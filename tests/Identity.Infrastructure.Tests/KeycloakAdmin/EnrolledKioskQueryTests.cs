using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartSentinelEye.Identity.Infrastructure.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin;

/// <summary>
/// Spec 124 (#2151) — <b>the two properties <c>KioskPrivilegeSweepTests</c>
/// names but cannot fail on</b>, asserted against the class that implements
/// them.
///
/// <para>
/// <c>KioskPrivilegeSweep</c> contains no filter and no idempotency: it strips
/// whatever <c>GetEnrolledKioskClientIdsAsync</c> hands it, once per pass. Both
/// guarantees live in <c>HttpKeycloakAdminClient</c> — which
/// <c>Identity.Application.Tests</c> deliberately cannot reference — so its
/// <c>FakeKeycloakAdminClient</c> ends up implementing the very behaviour the
/// assertions check, and no change to the production filter or the early exit
/// can turn either of them red (spec 087 F8; spec 092 §"Whether the existing
/// tests can fail").
/// </para>
///
/// <para>
/// The double here sits one layer lower, at the <see cref="HttpMessageHandler"/>
/// seam, for the reason <c>StubKeycloakHandler</c> already records for #2207: a
/// double standing in for the class under test cannot see a defect inside it.
/// </para>
/// </summary>
public class EnrolledKioskQueryTests
{
    private const string Realm = "smart-sentinel-eye";
    private const string KioskUuid = "11111111-1111-1111-1111-111111111111";
    private const string ServiceAccountUserId = "22222222-2222-2222-2222-222222222222";

    /// <summary>
    /// Three rows Keycloak really returns together: a kiosk this system
    /// enrolled, a person-shaped account carrying a different <c>sse.kind</c>,
    /// and one with no attributes at all — the realm's own built-in clients,
    /// which is what every realm we have actually contains (spec 092).
    /// </summary>
    private const string AllClients = """
        [
          {"id":"11111111-1111-1111-1111-111111111111","clientId":"kiosk-a",
           "attributes":{"sse.kind":"kiosk","sse.fab":"munich"}},
          {"id":"33333333-3333-3333-3333-333333333333","clientId":"someone-elses-account",
           "attributes":{"sse.kind":"operator-console"}},
          {"id":"44444444-4444-4444-4444-444444444444","clientId":"realm-management"}
        ]
        """;

    private static readonly string[] TheKioskAlone = ["kiosk-a"];

    [Fact]
    public async Task Only_a_client_this_system_stamped_as_a_kiosk_is_enrolled()
    {
        StubKeycloakHandler keycloak = new((_, _) => AllClients);

        IReadOnlyList<string> enrolled = await ClientOver(keycloak)
            .GetEnrolledKioskClientIdsAsync(CancellationToken.None);

        // The sweep strips every directly-assigned realm privilege from whatever
        // this returns. An account reached by mistake is not merely swept — it
        // is emptied, without an error, and the person it belongs to finds out
        // by losing access.
        enrolled.ShouldBe(
            TheKioskAlone,
            "the sweep applies no filter of its own, so this list is the whole of "
            + "the boundedness: an operator console in it would be stripped of "
            + "every realm role it holds");
    }

    [Fact]
    public async Task A_second_removal_sends_nothing_when_the_account_holds_no_realm_role()
    {
        // What a sweep meets on every start after the first: the kiosk was
        // stripped already and holds nothing directly.
        StubKeycloakHandler keycloak = new(StripFlow(assignedRoles: "[]"));

        await ClientOver(keycloak).StripInheritedRealmRolesAsync("kiosk-a", CancellationToken.None);

        keycloak.Requests.Select(request => request.Method).ShouldNotContain(
            "DELETE",
            "this is what makes the sweep safe to run on every Identity start — a "
            + "removal that always fired would put a write per kiosk per boot "
            + "against the realm, and an empty DELETE is not a no-op at Keycloak");
    }

    [Fact]
    public async Task A_first_removal_deletes_the_roles_the_account_actually_holds()
    {
        // The control. Without it the test above is satisfied by a client that
        // never deletes anything at all — including on the pass that matters,
        // which is the one that takes offline_access away.
        StubKeycloakHandler keycloak = new(StripFlow(assignedRoles: AssignedRoles));

        await ClientOver(keycloak).StripInheritedRealmRolesAsync("kiosk-a", CancellationToken.None);

        RecordedRequest removal = keycloak.Only("DELETE", "/role-mappings/realm");
        removal.Body.ShouldContain(
            "default-roles-smart-sentinel-eye",
            customMessage: "the delete only recognises the role objects the realm reports as "
            + "directly mapped to this account, so the body has to carry the ones just read back");
    }

    private const string AssignedRoles = """
        [{"id":"55555555-5555-5555-5555-555555555555","name":"default-roles-smart-sentinel-eye"}]
        """;

    private static Func<StubKeycloakHandler, RecordedRequest, string?> StripFlow(string assignedRoles) =>
        (_, request) => request switch
        {
            { PathAndQuery: var path } when path.Contains("clientId=", StringComparison.Ordinal) =>
                $$"""[{"id":"{{KioskUuid}}","clientId":"kiosk-a"}]""",
            { PathAndQuery: var path } when path.EndsWith("/service-account-user", StringComparison.Ordinal) =>
                $$"""{"id":"{{ServiceAccountUserId}}"}""",
            { Method: "GET", PathAndQuery: var path }
                when path.EndsWith("/role-mappings/realm", StringComparison.Ordinal) => assignedRoles,
            _ => null,
        };

    private static HttpKeycloakAdminClient ClientOver(StubKeycloakHandler keycloak) =>
        new(new HttpClient(keycloak) { BaseAddress = new Uri("http://keycloak.test/") },
            Options.Create(new KeycloakAdminOptions { Realm = Realm }),
            NullLogger<HttpKeycloakAdminClient>.Instance);
}
