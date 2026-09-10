using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin;

/// <summary>
/// Spec 121 (issues #2165 and #2207) — <b>an explicit "off" must reach Keycloak
/// as <c>false</c>, not as an omission.</b>
///
/// <para>
/// <c>HttpKeycloakAdminClient</c>'s <c>JsonSerializerOptions</c> carried
/// <c>DefaultIgnoreCondition = WhenWritingDefault</c>, which drops every property
/// equal to its type's default — so every <c>false</c> boolean. Keycloak then
/// applies its own default, which for <c>standardFlowEnabled</c> is <c>true</c>.
/// The call sites read <c>StandardFlowEnabled: false</c> and are correct; the
/// value never left the process.
/// </para>
///
/// <para>
/// These assertions read the request body the real client produced. Nothing is
/// substituted for the class under test, because the defect is in a private
/// static field of it.
/// </para>
/// </summary>
public class KeycloakAdminSerializationTests
{
    private const string Realm = "smart-sentinel-eye";

    [Fact]
    public async Task An_explicit_off_is_sent_rather_than_omitted()
    {
        StubKeycloakHandler keycloak = new(CreateFlow);

        await CreateAsync(keycloak);

        Dictionary<string, JsonElement> body = BodyOf(keycloak.Only("POST", "/clients"));

        // The control: a flag whose value is not the default has always been
        // sent, so an assertion that can see this one is reading the body
        // rather than a broken stub.
        body.ShouldContainKey("serviceAccountsEnabled");
        body["serviceAccountsEnabled"].GetBoolean().ShouldBeTrue();

        body.ShouldContainKey(
            "standardFlowEnabled",
            "the handler passes StandardFlowEnabled: false, but WhenWritingDefault drops it, "
            + "so Keycloak sees an absent field and applies its own default — which is true "
            + "(issues #2165, #2207).");
        body["standardFlowEnabled"].GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_flags_Keycloak_only_agrees_with_by_coincidence_are_sent_too()
    {
        StubKeycloakHandler keycloak = new(CreateFlow);

        await CreateAsync(keycloak);

        Dictionary<string, JsonElement> body = BodyOf(keycloak.Only("POST", "/clients"));

        // #2165 measured both of these as stored false. They are stored false
        // because Keycloak's default agrees, not because the value was sent —
        // so a changed default would flip them and nothing would notice.
        body.ShouldContainKey("directAccessGrantsEnabled",
            "stored false today only because Keycloak's default agrees; not sent (#2165).");
        body["directAccessGrantsEnabled"].GetBoolean().ShouldBeFalse();

        body.ShouldContainKey("publicClient",
            "the same coincidence: a confidential client that never says so (#2207).");
        body["publicClient"].GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Disabling_a_client_sends_the_flag_that_disables_it()
    {
        StubKeycloakHandler keycloak = new(DisableFlow);

        await ClientOver(keycloak).DisableClientAsync("kiosk-a", CancellationToken.None);

        Dictionary<string, JsonElement> body = BodyOf(keycloak.Only("PUT", $"/clients/{ClientUuid}"));

        // DisableClientAsync carries its whole intent in one false boolean, so
        // the same condition empties the body outright. A PUT of {} updates
        // nothing: the aggregate is marked Disabled and the Keycloak client
        // stays enabled, still able to mint tokens.
        body.ShouldContainKey(
            "enabled",
            "the revocation PUT serialises to {} — Keycloak applies only the fields a "
            + "representation carries, so the client is never disabled at all.");
        body["enabled"].GetBoolean().ShouldBeFalse();
    }

    private const string ClientUuid = "11111111-1111-1111-1111-111111111111";

    private static Task<KeycloakClientCredentials> CreateAsync(StubKeycloakHandler keycloak) =>
        ClientOver(keycloak).CreateClientAsync(
            Representation(), "/fabs/munich", CancellationToken.None);

    private static HttpKeycloakAdminClient ClientOver(StubKeycloakHandler keycloak) =>
        new(new HttpClient(keycloak) { BaseAddress = new Uri("http://keycloak.test/") },
            Options.Create(new KeycloakAdminOptions { Realm = Realm }),
            NullLogger<HttpKeycloakAdminClient>.Instance);

    /// <summary>The kiosk payload, flag for flag — <c>EnrollKioskCommandHandler.cs:31-45</c>.</summary>
    private static KeycloakClientRepresentation Representation() =>
        new(ClientId: "kiosk-a",
            Name: "Kiosk kiosk-a",
            ServiceAccountsEnabled: true,
            StandardFlowEnabled: false,
            DirectAccessGrantsEnabled: false,
            PublicClient: false,
            DefaultClientScopes: ["sse.streams.view"],
            OptionalClientScopes: [],
            Attributes: new Dictionary<string, string> { ["sse.kind"] = "kiosk" });

    private static Dictionary<string, JsonElement> BodyOf(RecordedRequest request) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Body)
        ?? throw new InvalidOperationException($"{request.Method} {request.PathAndQuery} sent no body.");

    private static string? CreateFlow(StubKeycloakHandler keycloak, RecordedRequest request) => request switch
    {
        // The existence probe answers empty; the post-create re-probe answers the row.
        { PathAndQuery: var path } when path.Contains("clientId=", StringComparison.Ordinal) =>
            keycloak.CountOf("clientId=") == 1 ? "[]" : ClientRows,
        { PathAndQuery: var path } when path.EndsWith("/service-account-user", StringComparison.Ordinal) =>
            """{"id":"22222222-2222-2222-2222-222222222222"}""",
        { PathAndQuery: var path } when path.Contains("/group-by-path/", StringComparison.Ordinal) =>
            """{"id":"33333333-3333-3333-3333-333333333333","name":"munich","path":"/fabs/munich"}""",
        { PathAndQuery: var path } when path.EndsWith("/role-mappings/realm", StringComparison.Ordinal) => "[]",
        { PathAndQuery: var path } when path.EndsWith("/client-secret", StringComparison.Ordinal) =>
            """{"type":"secret","value":"a-secret"}""",
        _ => null,
    };

    private static string? DisableFlow(StubKeycloakHandler _, RecordedRequest request) =>
        request.PathAndQuery.Contains("clientId=", StringComparison.Ordinal) ? ClientRows : null;

    private const string ClientRows =
        $$"""[{"id":"{{ClientUuid}}","clientId":"kiosk-a"}]""";
}
