using SmartSentinelEye.Identity.Application.KeycloakAdmin;
using SmartSentinelEye.Identity.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.Identity.Infrastructure.Tests.KeycloakAdmin;

/// <summary>
/// Spec 092 US1, the acceptance scenario <b>"the steady state says nothing"</b>
/// — the one behaviour change on this branch that shipped with no test.
///
/// <para>
/// <b>Why this is load-bearing and not tidiness.</b> The sweep used to log
/// <c>SweptKioskPrivileges(0, 0)</c> on every pass. Now that it runs on every
/// Identity start, and every realm that exists is empty of enrolled kiosks
/// (spec 092 §"The verdict"), that would be a line per restart saying nothing
/// happened — which trains an operator to skip the one that matters.
/// <c>StreamFabAttributionService</c> made the same call and wrote down the same
/// reason.
/// </para>
///
/// <para>
/// <b>And phase 5's evidence depends on it.</b> The only proof the pass runs at
/// boot is this log line, read out of a running Identity API with a residue
/// planted first. Invert the guard and every start emits the line whether or not
/// the sweep found anything, and phase 5's observation stops meaning anything —
/// with nothing on the branch to catch that. Hence a capturing logger: the five
/// pre-existing <c>KioskPrivilegeSweepTests</c> pass <c>NullLogger</c>, so no
/// assertion anywhere could see this.
/// </para>
///
/// <para>
/// <b>Colour: red, for the first of the two.</b> Silencing a log line is a
/// behaviour change, not a refactor, so characterisation would have been the
/// wrong obligation (ADR-0139, constitution §Testing).
/// <c>A_pass_that_finds_no_kiosk_says_nothing</c> was observed failing by
/// counterfactual — the guard replaced by <c>if (true)</c> — and the verbatim
/// output is quoted in the PR body. <c>A_pass_that_finds_a_kiosk_says_so_once</c>
/// passes on both sides of that counterfactual and is not the red: it is here so
/// the silence cannot be reached by deleting the line altogether, which would
/// satisfy the first test and destroy phase 5's only evidence.
/// </para>
///
/// <para>
/// The change itself shipped earlier on this branch typed <c>refactor</c>, which
/// was the wrong colour; this file is what phase 4a owed it, added at phase 6.
/// </para>
/// </summary>
public class KioskPrivilegeSweepSteadyStateTests
{
    /// <summary>The generator names the event after the log method.</summary>
    private const string CompletionLine = "SweptKioskPrivileges";

    [Fact]
    public async Task A_pass_that_finds_no_kiosk_says_nothing()
    {
        EnrolledKiosksKeycloakAdminClient keycloak = new();
        CapturingLogger<KioskPrivilegeSweep> logger = new();

        await new KioskPrivilegeSweep(keycloak, logger).SweepAsync(CancellationToken.None);

        keycloak.EnumerationAttempts.ShouldBe(
            1,
            "a pass that never asked the provider is silent for the wrong reason, and would "
            + "satisfy the assertion below without the guard existing at all");

        logger.Named(CompletionLine).ShouldBeEmpty(
            "this is what every Identity start in every environment that exists today looks "
            + "like — no client anywhere carries sse.kind. A completion line on each of them "
            + "trains an operator to skip the one that reports a residue, which is the only "
            + "evidence that the sweep ran at boot at all (spec 092 phase 5, step 8).");
    }

    [Fact]
    public async Task A_pass_that_finds_a_kiosk_says_so_once_and_names_the_count()
    {
        EnrolledKiosksKeycloakAdminClient keycloak = new("kiosk-residue-a");
        CapturingLogger<KioskPrivilegeSweep> logger = new();

        await new KioskPrivilegeSweep(keycloak, logger).SweepAsync(CancellationToken.None);

        IReadOnlyList<LoggedEntry> completions = logger.Named(CompletionLine);

        completions.Count.ShouldBe(
            1,
            "silencing the empty pass must not silence the pass that found something: the line "
            + "phase 5 reads out of a running Identity API is this one, and it is emitted once "
            + "per pass, not once per kiosk");

        completions[0].Message.Contains("1 of 1", StringComparison.Ordinal).ShouldBeTrue(
            "the count is what makes the line evidence. A line reporting zero would not be "
            + "distinguishable from the steady state that phase 5 plants a residue precisely "
            + $"to escape. The line read: {completions[0].Message}");
    }
}
