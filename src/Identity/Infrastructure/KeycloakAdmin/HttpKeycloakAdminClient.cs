using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;

/// <summary>
/// Hand-rolled <see cref="IKeycloakAdminClient"/> implementation
/// against Keycloak's REST Admin API. No external SDK dependency.
/// The bearer token is attached per request by
/// <see cref="KeycloakAdminAuthorizationHandler"/> rather than by this class,
/// so nothing here touches a credential.
///
/// <para>
/// Idempotency notes:
/// <list type="bullet">
/// <item><c>CreateClientAsync</c> probes for an existing client
/// with the same <c>clientId</c> and throws
/// <see cref="KeycloakClientAlreadyExistsException"/> on hit, so
/// the handler can surface a typed 409 instead of an opaque 4xx.</item>
/// <item><c>DisableClientAsync</c> on an unknown client is a
/// silent no-op (we cannot un-create what was never created).</item>
/// </list>
/// </para>
/// </summary>
public sealed class HttpKeycloakAdminClient(
    HttpClient httpClient,
    IOptions<KeycloakAdminOptions> options,
    ILogger<HttpKeycloakAdminClient> logger) : IKeycloakAdminClient
{
    // WhenWritingNull, not WhenWritingDefault. The wider condition dropped every
    // property equal to its type's default — so every `false` — and an omitted
    // flag is not an off flag: Keycloak applies its own default, which for
    // `standardFlowEnabled` is `true`, and which left the disable PUT below
    // sending `{}` and the client enabled (issues #2165, #2207). Nothing this
    // client serialises has a nullable member, so narrowing rather than removing
    // costs nothing today and keeps absent meaning absent if one is added.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<KeycloakClientCredentials> CreateClientAsync(
        KeycloakClientRepresentation representation,
        string fabGroupPath,
        CancellationToken cancellationToken)
    {
        Ensure.That(representation).IsNotNull();

        string realm = options.Value.Realm;

        // Existence probe — Keycloak's create endpoint returns 409
        // on duplicate, but we want a typed exception either way.
        string? existing = await TryGetClientUuidAsync(realm, representation.ClientId, cancellationToken);
        if (existing is not null)
        {
            throw new KeycloakClientAlreadyExistsException(representation.ClientId);
        }

        using HttpRequestMessage create = new(HttpMethod.Post, $"admin/realms/{realm}/clients")
        {
            Content = JsonContent.Create(representation, options: JsonOptions),
        };
        using HttpResponseMessage createResponse = await httpClient.SendAsync(create, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KeycloakClientAlreadyExistsException(representation.ClientId);
        }
        createResponse.EnsureSuccessStatusCode();

        string? clientUuid = await TryGetClientUuidAsync(realm, representation.ClientId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Keycloak accepted POST /clients but no client with clientId='{representation.ClientId}' is visible.");

        try
        {
            // Attach the service-account user to the fab group so the
            // `groups` claim carries `/fabs/<fabId>` (FR-003).
            await AssignServiceAccountToGroupAsync(
                realm, clientUuid, fabGroupPath, cancellationToken);

            // **Take back what the realm gave it for free** (spec 052). Keycloak
            // grants every account created after import a default composite that
            // includes the privilege to mint credentials which never expire, so
            // each kiosk is born holding it. Removing it here rather than later
            // is what keeps the window one call wide.
            await StripInheritedRealmRolesAsync(realm, clientUuid, cancellationToken);

            // Read the just-minted secret.
            return await ReadClientSecretAsync(realm, clientUuid, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **The client exists but is not properly set up, so it must not
            // survive.** Leaving it behind fails the caller *and* blocks every
            // retry — the existence probe above would answer "already enrolled"
            // for a client that was never finished, while its account keeps the
            // privilege this step exists to remove.
            await TryDeleteClientAsync(
                realm, representation.ClientId, clientUuid, cancellationToken);
            throw;
        }
    }

    public async Task<KeycloakClientCredentials> RotateClientSecretAsync(
        string clientId, CancellationToken cancellationToken)
    {
        Ensure.That(clientId).IsNotNull().IsNotNullOrWhiteSpace();
        string realm = options.Value.Realm;

        string clientUuid = await TryGetClientUuidAsync(realm, clientId, cancellationToken)
            ?? throw new KeycloakClientNotFoundException(clientId);

        using HttpResponseMessage response = await httpClient
            .PostAsync($"admin/realms/{realm}/clients/{clientUuid}/client-secret",
                content: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        ClientCredentialPayload payload = await response.Content
            .ReadFromJsonAsync<ClientCredentialPayload>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Keycloak returned an empty rotate-secret response for clientId='{clientId}'.");
        return new KeycloakClientCredentials(payload.Value);
    }

    public async Task<KeycloakClientCredentials> ReadClientSecretAsync(
        string clientId, CancellationToken cancellationToken)
    {
        Ensure.That(clientId).IsNotNull().IsNotNullOrWhiteSpace();
        string realm = options.Value.Realm;

        string clientUuid = await TryGetClientUuidAsync(realm, clientId, cancellationToken)
            ?? throw new KeycloakClientNotFoundException(clientId);

        return await ReadClientSecretAsync(realm, clientUuid, cancellationToken);
    }

    public async Task DisableClientAsync(string clientId, CancellationToken cancellationToken)
    {
        Ensure.That(clientId).IsNotNull().IsNotNullOrWhiteSpace();
        string realm = options.Value.Realm;

        string? clientUuid = await TryGetClientUuidAsync(realm, clientId, cancellationToken);
        if (clientUuid is null)
        {
            logger.DisableClientNoOp(clientId);
            return;
        }

        using HttpRequestMessage update = new(HttpMethod.Put, $"admin/realms/{realm}/clients/{clientUuid}")
        {
            Content = JsonContent.Create(new { enabled = false }, options: JsonOptions),
        };
        using HttpResponseMessage response = await httpClient.SendAsync(update, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string?> TryGetClientUuidAsync(
        string realm, string clientId, CancellationToken cancellationToken)
    {
        string url = $"admin/realms/{realm}/clients?clientId={Uri.EscapeDataString(clientId)}";
        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        ClientRow[] rows = await response.Content
            .ReadFromJsonAsync<ClientRow[]>(JsonOptions, cancellationToken)
            ?? [];
        return rows.Length == 0 ? null : rows[0].Id;
    }

    private async Task AssignServiceAccountToGroupAsync(
        string realm, string clientUuid, string groupPath, CancellationToken cancellationToken)
    {
        // Fetch the service-account user behind the client.
        using HttpResponseMessage saResponse = await httpClient
            .GetAsync($"admin/realms/{realm}/clients/{clientUuid}/service-account-user", cancellationToken);
        saResponse.EnsureSuccessStatusCode();
        ServiceAccountUser? user = await saResponse.Content
            .ReadFromJsonAsync<ServiceAccountUser>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No service-account-user for Keycloak client {clientUuid}.");

        // Resolve the group id by path.
        string lookupUrl = $"admin/realms/{realm}/group-by-path/{groupPath.TrimStart('/')}";
        using HttpResponseMessage groupResponse = await httpClient.GetAsync(lookupUrl, cancellationToken);
        groupResponse.EnsureSuccessStatusCode();
        GroupRow? group = await groupResponse.Content
            .ReadFromJsonAsync<GroupRow>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Keycloak group '{groupPath}' not found; create it before registering clients in this fab.");

        using HttpResponseMessage joinResponse = await httpClient.PutAsync(
            $"admin/realms/{realm}/users/{user.Id}/groups/{group.Id}",
            content: null, cancellationToken);
        joinResponse.EnsureSuccessStatusCode();
    }

    private async Task<KeycloakClientCredentials> ReadClientSecretAsync(
        string realm, string clientUuid, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync($"admin/realms/{realm}/clients/{clientUuid}/client-secret", cancellationToken);
        response.EnsureSuccessStatusCode();
        ClientCredentialPayload payload = await response.Content
            .ReadFromJsonAsync<ClientCredentialPayload>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException(
                "Keycloak returned an empty client-secret response.");
        return new KeycloakClientCredentials(payload.Value);
    }

    /// <summary>
    /// Sub-groups one level under <paramref name="parentPath"/>, resolved
    /// through the same <c>group-by-path</c> lookup the service-account join
    /// already uses.
    ///
    /// <para>
    /// Newer Keycloak versions omit <c>subGroups</c> from the list response and
    /// require a second call per group, so an empty children array is followed
    /// up rather than believed. An unreachable realm throws (spec 019 FR-011) —
    /// it must never be reported as "no groups".
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSubGroupNamesAsync(
        string parentPath, CancellationToken cancellationToken)
    {
        Ensure.That(parentPath).IsNotNull().IsNotNullOrWhiteSpace();

        string realm = options.Value.Realm;

        using HttpResponseMessage response = await httpClient.GetAsync(
            $"admin/realms/{realm}/group-by-path/{parentPath.TrimStart('/')}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.FabGroupParentMissing(parentPath, realm);
            return [];
        }
        response.EnsureSuccessStatusCode();

        GroupRow parent = await response.Content
            .ReadFromJsonAsync<GroupRow>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Keycloak returned an empty body for group path '{parentPath}'.");

        if (parent.SubGroups is { Length: > 0 })
        {
            return [.. parent.SubGroups.Select(child => child.Name)];
        }

        // Paged explicitly, and read to exhaustion. Keycloak applies a server-side
        // default page size to this endpoint, so a single unpaged call silently
        // returns a prefix once there are enough sub-groups — and a fab missing
        // from that prefix is indistinguishable from a fab that does not exist.
        // For the caller that means no storage provisioned and no indication why,
        // which is the silent partial this feature exists to end.
        const int pageSize = 100;
        List<string> names = [];
        bool lastPageReached = false;
        while (!lastPageReached)
        {
            using HttpResponseMessage children = await httpClient.GetAsync(
                $"admin/realms/{realm}/groups/{parent.Id}/children?first={names.Count}&max={pageSize}",
                cancellationToken);
            children.EnsureSuccessStatusCode();

            GroupRow[] rows = await children.Content
                .ReadFromJsonAsync<GroupRow[]>(JsonOptions, cancellationToken) ?? [];

            names.AddRange(rows.Select(child => child.Name));
            lastPageReached = rows.Length < pageSize;
        }

        return names;
    }

    public async Task<IReadOnlyList<string>> GetEnrolledKioskClientIdsAsync(
        CancellationToken cancellationToken)
    {
        string realm = options.Value.Realm;

        using HttpResponseMessage response = await httpClient
            .GetAsync($"admin/realms/{realm}/clients", cancellationToken);
        response.EnsureSuccessStatusCode();

        ClientDetailRow[] rows = await response.Content
            .ReadFromJsonAsync<ClientDetailRow[]>(JsonOptions, cancellationToken) ?? [];

        // The same attribute enrolment stamps, read back. Deriving the set from
        // what enrolment writes is the point: a second naming convention would
        // drift, and a sweep that quietly matches less than it claims is worse
        // than no sweep.
        return rows
            .Where(row => row.Attributes is not null
                && row.Attributes.TryGetValue("sse.kind", out string? kind)
                && kind == "kiosk")
            .Select(row => row.ClientId)
            .ToArray();
    }

    public async Task StripInheritedRealmRolesAsync(
        string clientId, CancellationToken cancellationToken)
    {
        Ensure.That(clientId).IsNotNull().IsNotNullOrWhiteSpace();
        string realm = options.Value.Realm;

        string clientUuid = await TryGetClientUuidAsync(realm, clientId, cancellationToken)
            ?? throw new KeycloakClientNotFoundException(clientId);

        await StripInheritedRealmRolesAsync(realm, clientUuid, cancellationToken);
    }

    /// <summary>
    /// Removes the realm privileges an account inherited simply by being
    /// created, leaving it with only what it was given deliberately.
    ///
    /// <para>
    /// <b>The shape matters and is not obvious.</b> The assignment must be read
    /// back before it is removed: the delete only recognises role objects the
    /// realm reports as <i>directly</i> mapped to this account. A role fetched
    /// from the realm's own role list looks identical and produces a
    /// <c>404</c> — which reads exactly like a permissions problem and is not.
    /// </para>
    /// </summary>
    private async Task StripInheritedRealmRolesAsync(
        string realm, string clientUuid, CancellationToken cancellationToken)
    {
        using HttpResponseMessage saResponse = await httpClient
            .GetAsync($"admin/realms/{realm}/clients/{clientUuid}/service-account-user", cancellationToken);
        saResponse.EnsureSuccessStatusCode();
        ServiceAccountUser? user = await saResponse.Content
            .ReadFromJsonAsync<ServiceAccountUser>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No service-account-user for Keycloak client {clientUuid}.");

        using HttpResponseMessage assignedResponse = await httpClient
            .GetAsync($"admin/realms/{realm}/users/{user.Id}/role-mappings/realm", cancellationToken);
        assignedResponse.EnsureSuccessStatusCode();
        RealmRoleRow[] assigned = await assignedResponse.Content
            .ReadFromJsonAsync<RealmRoleRow[]>(JsonOptions, cancellationToken) ?? [];

        // Already stripped. Returning rather than sending an empty delete is
        // what makes a sweep safe to run on every startup.
        if (assigned.Length == 0)
        {
            return;
        }

        using HttpRequestMessage remove = new(
            HttpMethod.Delete,
            $"admin/realms/{realm}/users/{user.Id}/role-mappings/realm")
        {
            Content = JsonContent.Create(assigned, options: JsonOptions),
        };
        using HttpResponseMessage removeResponse = await httpClient.SendAsync(remove, cancellationToken);
        removeResponse.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Removes the client an interrupted enrolment left half-made. Best effort
    /// in that it never throws — the caller is already failing, and its error
    /// is the one that explains why compensation was needed at all — but not
    /// silent: what it could not remove is reported (spec 122, #2166).
    ///
    /// <para>
    /// <b>The startup sweep is not the backstop this used to name.</b>
    /// <c>KioskPrivilegeSweep</c> takes a residue's realm roles away and leaves
    /// the client, and its enrolled-kiosk query cannot tell a residue from a
    /// healthy kiosk. So a client that survives here survives every sweep, and
    /// the existence probe in <c>CreateClientAsync</c> keeps answering
    /// already-enrolled for it — that kiosk cannot be enrolled again until
    /// someone deletes the client by hand. Nothing else will.
    /// </para>
    /// </summary>
    private async Task TryDeleteClientAsync(
        string realm, string clientId, string clientUuid, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient
                .DeleteAsync($"admin/realms/{realm}/clients/{clientUuid}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // 404 is Keycloak reporting no such client, which is the outcome
            // this call wanted. Reporting a residue for it would send someone
            // hunting a client that is not there.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.HalfEnrolledClientWasAlreadyAbsent(clientId, clientUuid);
                return;
            }

            logger.HalfEnrolledClientSurvived(clientId, clientUuid, response.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.CouldNotRemoveHalfEnrolledClient(clientId, clientUuid, exception);
        }
    }

    private sealed record ClientRow(string Id, string ClientId);

    private sealed record ClientDetailRow(string Id, string ClientId, Dictionary<string, string>? Attributes);

    private sealed record RealmRoleRow(string Id, string Name);

    private sealed record ServiceAccountUser(string Id);

    // Name and SubGroups are new for spec 019's sub-group read; the
    // group-by-path lookup above uses Id alone and is unaffected by the extra
    // members, which simply stay null when Keycloak does not send them.
    private sealed record GroupRow(string Id, string Name, string Path, GroupRow[]? SubGroups);

    private sealed record ClientCredentialPayload(string Type, string Value);
}
