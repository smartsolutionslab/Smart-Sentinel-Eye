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

    public async Task DeleteAsync(string clientId, CancellationToken cancellationToken)
    {
        using HttpClient admin = await AuthorisedAdminClientAsync(cancellationToken);

        JsonElement clients = await ReadJsonAsync(
            admin, $"admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}", cancellationToken);
        foreach (JsonElement client in clients.EnumerateArray())
        {
            await admin.DeleteAsync(
                $"admin/realms/{Realm}/clients/{client.GetProperty("id").GetString()}", cancellationToken);
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
        KeycloakAdminTokenProvider tokens = new(
            new FakeHttpClientFactory(aspire.CreateKeycloakClient()),
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
