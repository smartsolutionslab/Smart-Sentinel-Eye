using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Application.Tests.Fakes;

namespace SmartSentinelEye.Identity.Application.Tests.KeycloakAdmin;

/// <summary>
/// Spec 052 US1 — the realm hands every account it creates a privilege that
/// mints credentials which never expire, and this takes it back.
///
/// <para>
/// <b>The claim is about a running provider, not a file.</b> These cover the
/// logic; the claim itself is asserted end to end against the provider, because
/// the previous attempt's file-reading guard stayed green for a whole feature
/// while every enrolled kiosk held the privilege.
/// </para>
/// </summary>
public class KioskPrivilegeSweepTests
{
    private static readonly string[] BothKiosks = ["kiosk-a", "kiosk-b"];
    private static readonly string[] OneKiosk = ["kiosk-a"];
    private static readonly string[] TwoPassesOverOneKiosk = ["kiosk-a", "kiosk-a"];
    private static readonly string[] TheReachableOnes = ["kiosk-a", "kiosk-c"];
    private static readonly string[] TheUnreachableOne = ["kiosk-b"];

    private static KioskPrivilegeSweep SweepOver(FakeKeycloakAdminClient keycloak) =>
        new(keycloak, NullLogger<KioskPrivilegeSweep>.Instance);

    private static async Task<FakeKeycloakAdminClient> WithEnrolledKiosksAsync(params string[] clientIds)
    {
        FakeKeycloakAdminClient keycloak = new();
        foreach (string clientId in clientIds)
        {
            await keycloak.CreateClientAsync(
                KioskRepresentation(clientId), "/fabs/munich", CancellationToken.None);
        }

        keycloak.Stripped.Clear();
        return keycloak;
    }

    private static KeycloakClientRepresentation KioskRepresentation(string clientId) =>
        new(
            ClientId: clientId,
            Name: $"Kiosk {clientId}",
            ServiceAccountsEnabled: true,
            StandardFlowEnabled: false,
            DirectAccessGrantsEnabled: false,
            PublicClient: false,
            DefaultClientScopes: ["sse.cameras.read"],
            OptionalClientScopes: [],
            Attributes: new Dictionary<string, string>
            {
                ["sse.kind"] = "kiosk",
                ["sse.fab"] = "munich",
            });

    private static KeycloakClientRepresentation PersonLikeRepresentation(string clientId) =>
        new(
            ClientId: clientId,
            Name: clientId,
            ServiceAccountsEnabled: true,
            StandardFlowEnabled: true,
            DirectAccessGrantsEnabled: true,
            PublicClient: true,
            DefaultClientScopes: ["sse.management"],
            OptionalClientScopes: [],
            Attributes: new Dictionary<string, string> { ["sse.kind"] = "operator-console" });

    [Fact]
    public async Task Strips_every_kiosk_this_system_enrolled()
    {
        FakeKeycloakAdminClient keycloak = await WithEnrolledKiosksAsync("kiosk-a", "kiosk-b");

        KioskSweepOutcome outcome = await SweepOver(keycloak).SweepAsync(CancellationToken.None);

        keycloak.Stripped.ShouldBe(BothKiosks, ignoreOrder: true);
        outcome.StrippedCount.ShouldBe(2);
        outcome.Unreachable.ShouldBeEmpty();
    }

    /// <summary>
    /// **Asserted on the account, not on the run completing.**
    ///
    /// <para>
    /// The removal takes away <i>every</i> directly-assigned realm privilege. A
    /// sweep whose idea of "a kiosk" matched everything would strip a person's
    /// account to nothing — and it would not throw while doing it, so a test
    /// that only checked the sweep finished would pass on the way past.
    /// </para>
    ///
    /// <para>
    /// <b>The <c>ShouldNotContain</c> half cannot fail here, and this is where
    /// the property it names is actually proved.</b> <c>KioskPrivilegeSweep</c>
    /// contains no filter — it strips whatever
    /// <c>GetEnrolledKioskClientIdsAsync</c> returns — so the boundedness lives
    /// behind that seam, in <c>HttpKeycloakAdminClient</c>, which this assembly
    /// deliberately cannot reference. Removing the production filter outright
    /// leaves this test green (#2151, and spec 092 §"Whether the existing tests
    /// can fail"). The assertion stays because the invariant is real and this is
    /// where a reader looks for it; the falsifiable version of it is
    /// <c>Identity.Infrastructure.Tests</c>'
    /// <c>EnrolledKioskQueryTests.Only_a_client_this_system_stamped_as_a_kiosk_is_enrolled</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Does_not_touch_an_account_this_system_did_not_enrol()
    {
        FakeKeycloakAdminClient keycloak = new();
        await keycloak.CreateClientAsync(
            KioskRepresentation("kiosk-a"), "/fabs/munich", CancellationToken.None);
        await keycloak.CreateClientAsync(
            PersonLikeRepresentation("someone-elses-account"), "/fabs/munich", CancellationToken.None);
        keycloak.Stripped.Clear();

        await SweepOver(keycloak).SweepAsync(CancellationToken.None);

        keycloak.Stripped.ShouldContain("kiosk-a");
        keycloak.Stripped.ShouldNotContain(
            "someone-elses-account",
            "the sweep removes every direct realm privilege, so reaching an account it did not create would strip that account of everything");
    }

    /// <summary>
    /// What makes running this on every start acceptable. If a second sweep did
    /// more than the first, it could not be a startup step.
    ///
    /// <para>
    /// <b><c>Stripped</c> alone could not see that.</b> It is a set, so a sweep
    /// calling the removal three times per kiosk left this test green — measured
    /// by counterfactual in spec 092 and filed on #2151. <c>StripCalls</c> keeps
    /// the repeats, which is what makes "one removal per kiosk per pass" an
    /// assertion rather than a title.
    /// </para>
    ///
    /// <para>
    /// The <i>other</i> half of the claim — that the second removal is a no-op
    /// at Keycloak rather than an empty <c>DELETE</c> — is not visible at this
    /// seam either: it is <c>HttpKeycloakAdminClient</c>'s
    /// <c>if (assigned.Length == 0) return;</c>. See
    /// <c>Identity.Infrastructure.Tests</c>'
    /// <c>EnrolledKioskQueryTests.A_second_removal_sends_nothing_when_the_account_holds_no_realm_role</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sweeping_twice_does_no_more_than_sweeping_once()
    {
        FakeKeycloakAdminClient keycloak = await WithEnrolledKiosksAsync("kiosk-a");

        await SweepOver(keycloak).SweepAsync(CancellationToken.None);
        KioskSweepOutcome second = await SweepOver(keycloak).SweepAsync(CancellationToken.None);

        keycloak.Stripped.ShouldBe(OneKiosk);
        // Two passes, one removal each. A set cannot tell that from twenty.
        keycloak.StripCalls.ShouldBe(TwoPassesOverOneKiosk);
        second.Unreachable.ShouldBeEmpty();
    }

    /// <summary>
    /// One unreachable account must not leave the others holding the privilege —
    /// they are independent, and stopping would be the wrong trade.
    /// </summary>
    [Fact]
    public async Task Carries_on_past_a_kiosk_it_cannot_reach_and_names_it()
    {
        FakeKeycloakAdminClient keycloak = await WithEnrolledKiosksAsync("kiosk-a", "kiosk-b", "kiosk-c");
        keycloak.StripFailsFor.Add("kiosk-b");

        KioskSweepOutcome outcome = await SweepOver(keycloak).SweepAsync(CancellationToken.None);

        keycloak.Stripped.ShouldBe(TheReachableOnes, ignoreOrder: true);
        outcome.Unreachable.ShouldBe(TheUnreachableOne);
        outcome.StrippedCount.ShouldBe(2);
    }

    [Fact]
    public async Task Reports_nothing_to_do_when_no_kiosk_has_been_enrolled()
    {
        FakeKeycloakAdminClient keycloak = new();

        KioskSweepOutcome outcome = await SweepOver(keycloak).SweepAsync(CancellationToken.None);

        outcome.KioskCount.ShouldBe(0);
        outcome.Unreachable.ShouldBeEmpty();
    }
}
