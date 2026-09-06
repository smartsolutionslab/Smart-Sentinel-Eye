using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.MigrationRunner.Tests.Fakes;

namespace SmartSentinelEye.MigrationRunner.Tests;

/// <summary>
/// Issue #2062, deliverable B. <c>MigrationRunner</c> starts as soon as
/// Keycloak's <c>/health/ready</c> answers, and that is not the same fact as
/// "the realm's group tree is queryable" — the integration fixture polls the
/// realm's discovery document separately for exactly that reason
/// (<c>AspireFixture.WaitForKeycloakRealmAsync</c>), and MigrationRunner has no
/// equivalent.
///
/// <para>
/// The chain these tests drive: a group path that is not there yet answers
/// <b>404</b>; <c>HttpKeycloakAdminClient.GetSubGroupNamesAsync</c> maps that to
/// an empty list; <see cref="KeycloakProvisionedFabSource"/> treats an empty
/// list as fatal. An answer of "not yet" therefore ends the run, and every
/// service gated on <c>migrations</c> with it.
/// </para>
///
/// <para>
/// <b>These tests claim nothing about the cause of the 134 exit in
/// CI.</b> That remains unexplained and unreproduced. What is asserted here is
/// only that "the group tree has not answered yet" and "this realm has no fabs"
/// must not be the same outcome, which is true independently of what aborted
/// that particular run.
/// </para>
///
/// <para>
/// The existing rules in <see cref="KeycloakProvisionedFabSourceTests"/> are
/// untouched and must stay green: a realm that never produces a usable fab
/// still fails the run (FR-011), and a badly named group is still skipped
/// rather than fatal (FR-005). Waiting is what happens before that verdict,
/// not instead of it.
/// </para>
/// </summary>
public class KeycloakProvisionedFabSourceWaitTests
{
    /// <summary>
    /// Caps the test, not the behaviour. Nothing here asserts how long the
    /// production wait may run; this only stops an unbounded implementation
    /// from hanging the suite instead of failing it.
    /// </summary>
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Waits_for_the_fab_groups_to_appear_rather_than_failing_on_the_first_empty_answer()
    {
        EventuallyPopulatedKeycloakAdminClient keycloak = new(emptyAnswers: 2, thenNames: ["munich"]);
        KeycloakProvisionedFabSource source =
            new(keycloak, NullLogger<KeycloakProvisionedFabSource>.Instance);
        using CancellationTokenSource guard = new(TestGuard);

        IReadOnlyList<FabIdentifier> fabs = await source.GetFabsAsync(guard.Token);

        fabs.Select(fab => fab.Value).ShouldBe(["munich"]);
    }

    /// <summary>
    /// Two empty answers, not one: a single re-ask would satisfy the test above
    /// while still losing the run to a realm that takes a moment longer. The
    /// wait has to be a poll of the same question.
    /// </summary>
    [Fact]
    public async Task Asks_the_group_tree_again_rather_than_settling_for_its_first_answer()
    {
        EventuallyPopulatedKeycloakAdminClient keycloak = new(emptyAnswers: 2, thenNames: ["munich"]);
        KeycloakProvisionedFabSource source =
            new(keycloak, NullLogger<KeycloakProvisionedFabSource>.Instance);
        using CancellationTokenSource guard = new(TestGuard);

        await source.GetFabsAsync(guard.Token);

        // Two that answered nothing, then the one that answered. Stopping on
        // the first usable answer is the other half of this: a wait that keeps
        // polling after it has what it needs delays every service behind it.
        keycloak.Calls.ShouldBe(3);
    }

    /// <summary>
    /// A silent pause and a hang look identical from outside the process, and
    /// this one sits in front of nine services. Information or above rather
    /// than Debug for the reason <c>Log.PostgresWarning</c> already records
    /// (#1394): at Debug the default filter hides it and the message may as
    /// well not exist.
    /// </summary>
    [Fact]
    public async Task Says_that_it_is_waiting_rather_than_going_quiet()
    {
        EventuallyPopulatedKeycloakAdminClient keycloak = new(emptyAnswers: 2, thenNames: ["munich"]);
        CapturingLogger<KeycloakProvisionedFabSource> logger = new();
        KeycloakProvisionedFabSource source = new(keycloak, logger);
        using CancellationTokenSource guard = new(TestGuard);

        await source.GetFabsAsync(guard.Token);

        logger.Entries.ShouldContain(entry => entry.Level >= LogLevel.Information);
    }

    /// <summary>
    /// A realm whose <c>/fabs</c> group has not been imported yet, followed by
    /// one where it has. The empty answers are what a 404 arrives as, once
    /// <c>HttpKeycloakAdminClient</c> has translated it.
    /// </summary>
    private sealed class EventuallyPopulatedKeycloakAdminClient(int emptyAnswers, string[] thenNames)
        : IKeycloakAdminClient
    {
        private int calls;

        public int Calls => calls;

        public Task<IReadOnlyList<string>> GetSubGroupNamesAsync(
            string parentPath, CancellationToken cancellationToken)
        {
            int answered = Interlocked.Increment(ref calls);
            IReadOnlyList<string> names = answered <= emptyAnswers ? [] : thenNames;
            return Task.FromResult(names);
        }

        public Task<KeycloakClientCredentials> CreateClientAsync(
            KeycloakClientRepresentation representation, string fabGroupPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<KeycloakClientCredentials> ReadClientSecretAsync(
            string clientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetEnrolledKioskClientIdsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StripInheritedRealmRolesAsync(
            string clientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<KeycloakClientCredentials> RotateClientSecretAsync(
            string clientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DisableClientAsync(string clientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
