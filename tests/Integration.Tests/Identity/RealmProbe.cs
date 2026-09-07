using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;
using SmartSentinelEye.Integration.Tests.Fixtures;

namespace SmartSentinelEye.Integration.Tests.Identity;

/// <summary>
/// Asks the running provider directly, over the Admin API, using the same
/// <c>identity-admin</c> service account Identity itself presents.
///
/// <para>
/// Deliberately not routed through <c>IKeycloakAdminClient</c>: a test that both
/// acts and observes through the code under test cannot tell a working sweep
/// from a lying client. Planting is done here too, and <b>directly</b> — a
/// client created through <c>POST /kiosks</c> is stripped by enrolment itself,
/// so it could never stand for the residue a failed enrolment leaves behind.
/// </para>
///
/// <para>
/// The helpers mirror the private ones in
/// <see cref="KioskInheritedPrivilegeIntegrationTests"/>. That file is spec 052's
/// and is left untouched; folding the two together is a tidy-up for whoever
/// touches it next.
/// </para>
/// </summary>
public sealed class RealmProbe(AspireFixture aspire)
{
    public const string Realm = "smart-sentinel-eye";
    public const string AdminClientId = "identity-admin";
    public const string AdminClientSecret = "dev-only-identity-admin-secret";

    /// <summary>The privilege that lets a grant outlive the session that issued it.</summary>
    public const string LongLivedCredentialPrivilege = "offline_access";

    /// <summary>
    /// Creates a client the way nothing in this system creates one — straight
    /// at the provider, so no enrolment step strips it on the way in.
    /// </summary>
    public async Task PlantAsync(
        string clientId,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        using HttpClient admin = await AuthorisedAdminClientAsync(cancellationToken);

        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"admin/realms/{Realm}/clients",
            new
            {
                clientId,
                enabled = true,
                publicClient = false,
                serviceAccountsEnabled = true,
                standardFlowEnabled = false,
                attributes,
            },
            cancellationToken);

        created.IsSuccessStatusCode.ShouldBeTrue(
            $"planting '{clientId}' failed with {(int)created.StatusCode}; without it the "
            + "assertions below prove nothing");
    }

    /// <summary>
    /// What the provider says the client's service account effectively holds —
    /// composites resolved, which is what actually decides whether a long-lived
    /// grant is issued.
    /// </summary>
    public async Task<IReadOnlyList<string>> EffectiveRealmRolesAsync(
        string clientId, CancellationToken cancellationToken)
    {
        using HttpClient admin = await AuthorisedAdminClientAsync(cancellationToken);

        JsonElement clients = await ReadJsonAsync(
            admin, $"admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}", cancellationToken);
        string uuid = clients.EnumerateArray().First().GetProperty("id").GetString()!;

        JsonElement serviceAccount = await ReadJsonAsync(
            admin, $"admin/realms/{Realm}/clients/{uuid}/service-account-user", cancellationToken);

        JsonElement roles = await ReadJsonAsync(
            admin,
            $"admin/realms/{Realm}/users/{serviceAccount.GetProperty("id").GetString()}/role-mappings/realm/composite",
            cancellationToken);

        return roles.EnumerateArray()
            .Select(role => role.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>
    /// Removes the probe, and <b>says so when it cannot</b>.
    ///
    /// <para>
    /// The unchecked <c>DeleteAsync</c> this replaces is the same shape as the
    /// defect issue #2166 reports in <c>TryDeleteClientAsync</c>: a DELETE
    /// answering 404, 409 or 500 leaves the client behind and nothing reports
    /// it. Here that would leave a client stamped <c>sse.kind=kiosk</c> in the
    /// realm the next test in this collection sweeps — a residue the suite
    /// planted itself, indistinguishable from one it is meant to find.
    /// </para>
    ///
    /// <para>
    /// Both call sites are <c>finally</c> blocks, so a throw here replaces a
    /// failure from the body. That is the accepted cost: a cleanup that failed
    /// silently is how a suite starts lying about the realm it runs against,
    /// and the message below names the client and the status.
    /// </para>
    /// </summary>
    public async Task DeleteAsync(string clientId, CancellationToken cancellationToken)
    {
        using HttpClient admin = await AuthorisedAdminClientAsync(cancellationToken);

        JsonElement clients = await ReadJsonAsync(
            admin, $"admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}", cancellationToken);
        foreach (JsonElement client in clients.EnumerateArray())
        {
            HttpResponseMessage deleted = await admin.DeleteAsync(
                $"admin/realms/{Realm}/clients/{client.GetProperty("id").GetString()}", cancellationToken);

            deleted.IsSuccessStatusCode.ShouldBeTrue(
                $"removing probe client '{clientId}' answered {(int)deleted.StatusCode}; it is still "
                + "in the realm, stamped as this suite planted it, and the next pass over this "
                + "realm will read it as residue it did not create");
        }
    }

    private async Task<HttpClient> AuthorisedAdminClientAsync(CancellationToken cancellationToken)
    {
        HttpClient http = aspire.CreateKeycloakClient();
        KeycloakAdminOptions options = new()
        {
            BaseUrl = http.BaseAddress!.ToString(),
            Realm = Realm,
            AdminClientId = AdminClientId,
            AdminClientSecret = AdminClientSecret,
        };

        // One client, not two: the mint happens before the bearer header is
        // attached below, so the token provider can borrow the same one the
        // caller will use and dispose. A second client here was created per
        // call and never disposed.
        KeycloakAdminTokenProvider tokens = new(
            new FakeHttpClientFactory(http),
            Options.Create(options),
            TimeProvider.System,
            NullLogger<KeycloakAdminTokenProvider>.Instance);

        string token = await tokens.GetAccessTokenAsync(cancellationToken);
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpClient admin, string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await admin.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }
}
