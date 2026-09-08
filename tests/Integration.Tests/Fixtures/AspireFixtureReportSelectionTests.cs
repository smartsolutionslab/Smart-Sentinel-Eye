using Shouldly;
using Xunit;

namespace SmartSentinelEye.Integration.Tests.Fixtures;

/// <summary>
/// #1918 — the startup-timeout report has to name the resource that failed.
/// Pure decision logic, so these run without Docker or the fixture.
///
/// <para>
/// The trait is how `backend` selects these (ci.yml) rather than leaving them
/// to the 30-minute `integration` job. It is a trait and not a name filter
/// because a name filter has to be extended for every new class, and the next
/// one was missed the day it was written (#2064).
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class AspireFixtureReportSelectionTests
{
    [Fact]
    public void A_service_that_exited_during_startup_is_reported()
    {
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation"] = "Finished",
            ["camera-catalog"] = "Running",
        };

        AspireFixture.SelectResourcesToReport(states, new(StringComparer.Ordinal)).ShouldBe(["automation"]);
    }

    [Fact]
    public void A_one_shot_job_that_finished_is_not_reported()
    {
        Dictionary<string, string> states = new(StringComparer.Ordinal) { ["migrations"] = "Finished" };

        AspireFixture.SelectResourcesToReport(states, new(StringComparer.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void A_one_shot_that_finished_with_a_captured_null_exit_code_is_not_reported()
    {
        // The dominant shape, not an edge case: the state capture assigns
        // `_exitCodes[name] = evt.Snapshot.ExitCode` for every resource it
        // observes, so a resource with no exit code has a *present null*, not
        // an absent key. Reading that as a non-zero exit would report every
        // healthy resource and drop `migrations` from the failure section.
        Dictionary<string, string> states = new(StringComparer.Ordinal) { ["migrations"] = "Finished" };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal) { ["migrations"] = null };

        AspireFixture.SelectResourcesToReport(states, exitCodes).ShouldBeEmpty();
    }

    [Fact]
    public void A_resource_with_a_captured_null_exit_code_is_named_without_inventing_one()
    {
        // #1930's observation, spelled out: `automation` ends `Finished` while
        // the others reach `Running`, and its exit code was never captured.
        //
        // This assertion is inverted (spec 098). It used to require
        // `FormatLikelyCause` to stay *empty* here, and the reason it gave was
        // a wording problem, not a suppression judgement: reading the null as
        // non-zero would print "automation exited with code " — a cause line
        // with no code in it. That finding is kept, and the new sentence
        // honours it by not using the phrase at all: it says the resource
        // reached a state and that no exit code was recorded, so there is
        // never a number-shaped hole to fill.
        //
        // The selection half is unchanged — the failure section already names
        // `automation`, and a cause line must point at a section that exists.
        // The two `Running` entries are the ordinary case alongside it.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation"] = "Finished",
            ["api-gateway"] = "Running",
            ["camera-catalog"] = "Running",
        };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal)
        {
            ["automation"] = null,
            ["api-gateway"] = null,
            ["camera-catalog"] = null,
        };

        AspireFixture.SelectResourcesToReport(states, exitCodes).ShouldBe(["automation"]);

        string cause = AspireFixture.FormatLikelyCause(states, exitCodes);

        cause.ShouldStartWith("Likely cause: ");
        cause.ShouldContain("automation");
        cause.ShouldContain("Finished");
        cause.ShouldContain("no exit code");
        cause.ShouldNotContain("exited with code");
        cause.ShouldNotContain("api-gateway");
        cause.ShouldNotContain("camera-catalog");
    }

    [Fact]
    public void A_long_running_resource_that_finished_with_exit_code_zero_is_named_as_a_cause()
    {
        // #1930's other branch — the one the issue's own analysis singles out:
        // "exit 0 → graceful stop. Nothing in the app asks for one, so look at
        // the orchestrator." Exit code 0 changes *where you look next*; it does
        // not change *what broke*. A long-running service sitting in a terminal
        // state while the boot timed out waiting for it is the cause of that
        // timeout, whatever number it exited with.
        //
        // The non-zero wording must not be reused: "a non-zero exit is a
        // failure" is a false sentence over exit code 0, and #2061's cause line
        // is the sentence readers already recognise.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation"] = "Finished",
            ["api-gateway"] = "Running",
            ["camera-catalog"] = "Running",
        };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal)
        {
            ["automation"] = 0,
            ["api-gateway"] = null,
            ["camera-catalog"] = null,
        };

        string cause = AspireFixture.FormatLikelyCause(states, exitCodes);

        cause.ShouldStartWith("Likely cause: ");
        cause.ShouldContain("automation");
        cause.ShouldContain("Finished");
        cause.ShouldContain("exit code 0");
        cause.ShouldNotContain("a non-zero exit");
        cause.ShouldNotContain("api-gateway");
        cause.ShouldNotContain("camera-catalog");
    }

    [Fact]
    public void A_one_shot_that_ended_is_named_without_being_called_long_running()
    {
        // `IsHealthy` spells the one-shot exemption for `Finished` only, so a
        // `migrations` in `Exited` is unhealthy and reaches the cause line —
        // which then said "a long-running resource that ends…" about the one
        // resource in the stack that is known not to be. Naming it is right;
        // the boot was waiting on it. The predicate never asked whether it was
        // long-running, so the sentence must not answer.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["migrations"] = "Exited",
            ["camera-catalog"] = "Running",
        };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal) { ["migrations"] = 0 };

        string cause = AspireFixture.FormatLikelyCause(states, exitCodes);

        cause.ShouldContain("migrations reached Exited with exit code 0");
        cause.ShouldNotContain("long-running");
    }

    [Fact]
    public void A_rebuilder_that_ended_is_not_named_as_the_likely_cause()
    {
        // `The_real_failure_is_not_buried_under_idle_rebuilders` covers
        // `NotStarted`, which is the only state `IsHealthy` exempts a rebuilder
        // in. A rebuilder that *ends* is therefore unhealthy, in an ended
        // state, and — without the clause in `FormatLikelyCause` — the first
        // sentence of the report: "automation-rebuilder reached Finished and no
        // exit code was recorded". That is #1918's failure buried under idle
        // rebuilders, arriving through the one line #2061 built to be trusted.
        //
        // The rebuilder stays in the failure section; only the sentence drops
        // it. Dropping it from the section is what #1918 argued against.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation"] = "Finished",
            ["automation-rebuilder"] = "Finished",
            ["camera-catalog"] = "Running",
        };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal)
        {
            ["automation"] = 0,
            ["automation-rebuilder"] = null,
        };

        string cause = AspireFixture.FormatLikelyCause(states, exitCodes);

        cause.ShouldStartWith("Likely cause: ");
        cause.ShouldContain("automation reached Finished");
        cause.ShouldNotContain("-rebuilder");

        AspireFixture.SelectResourcesToReport(states, exitCodes)
            .ShouldBe(["automation", "automation-rebuilder"]);
    }

    [Fact]
    public void No_cause_is_claimed_when_only_a_rebuilder_ended()
    {
        // The damaging half: with nothing else to name, an ended rebuilder is
        // the *whole* cause line, and a report that claims a cause it does not
        // have is worse than one that claims none — which is the property
        // `FormatLikelyCause` returns `string.Empty` to keep.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation-rebuilder"] = "Finished",
            ["camera-catalog"] = "Running",
        };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal)
        {
            ["automation-rebuilder"] = null,
        };

        AspireFixture.FormatLikelyCause(states, exitCodes).ShouldBeEmpty();
    }

    [Fact]
    public void Rebuilders_that_never_started_are_not_reported()
    {
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["api-gateway-rebuilder"] = "NotStarted",
            ["automation-rebuilder"] = "NotStarted",
        };

        AspireFixture.SelectResourcesToReport(states, new(StringComparer.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void The_real_failure_is_not_buried_under_idle_rebuilders()
    {
        // The exact shape of the run that motivated this: one service exited,
        // eleven rebuilders idle. The old predicate reported the eleven and
        // omitted the one.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation"] = "Finished",
            ["migrations"] = "Finished",
            ["api-gateway"] = "Running",
            ["api-gateway-rebuilder"] = "NotStarted",
            ["automation-rebuilder"] = "NotStarted",
            ["camera-catalog-rebuilder"] = "NotStarted",
        };

        AspireFixture.SelectResourcesToReport(states, new(StringComparer.Ordinal)).ShouldBe(["automation"]);
    }

    [Fact]
    public void A_resource_that_failed_outright_is_still_reported()
    {
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["identity"] = "FailedToStart",
            ["keycloak"] = "Running",
        };

        AspireFixture.SelectResourcesToReport(states, new(StringComparer.Ordinal)).ShouldBe(["identity"]);
    }

    [Fact]
    public void An_exited_resource_reports_its_exit_code()
    {
        // `Finished` alone cannot distinguish a clean shutdown from a death.
        // The exit code is the fact that decides it, and its absence is why
        // #1918's one occurrence could not be diagnosed afterwards.
        Dictionary<string, string> states = new(StringComparer.Ordinal)
        {
            ["automation"] = "Finished",
            ["camera-catalog"] = "Running",
        };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal)
        {
            ["automation"] = 139,
            ["camera-catalog"] = null,
        };

        string report = AspireFixture.FormatResourceStates(states, exitCodes);

        report.ShouldContain("automation: Finished (exit code 139)");
        report.ShouldContain("camera-catalog: Running");
        report.ShouldNotContain("camera-catalog: Running (exit");
    }

    [Fact]
    public void A_one_shot_that_died_is_reported()
    {
        // Run 33623647778: the AppHost never came up, nine services were
        // `FailedToStart`, and the report named all nine and none of them was
        // the cause. `migrations` had exited 134 (SIGABRT) — printed in the
        // state list two inches above, and excluded from the failure section
        // because finishing is how a one-shot succeeds, whatever the code.
        //
        // Three of the thirty-five entries carry the assertion — `migrations`
        // (the one-shot that died), any one `FailedToStart` service, and any
        // one `-rebuilder`. The rest is the run's real scenery, kept so the
        // fixture is the shape that was actually observed.
        AspireFixture.SelectResourcesToReport(
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis()).ShouldContain("migrations");
    }

    [Fact]
    public void The_report_names_the_one_shot_that_exited_and_its_code()
    {
        // Run 33623647778's failed-resource section listed nine services and
        // omitted `migrations`, which had exited 134. The section whose whole
        // job is to say which resource broke did not contain the one that did.
        string report = AspireFixture.FormatFailedResourceReport(
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis(),
            new(StringComparer.Ordinal));

        report.ShouldContain("migrations (Finished, exit code 134");
    }

    [Fact]
    public void A_resource_that_ran_and_died_is_distinguishable_from_one_that_never_launched()
    {
        string report = AspireFixture.FormatFailedResourceReport(
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis(),
            new(StringComparer.Ordinal));

        report.ShouldContain("ran and died");
        report.ShouldContain("audit-observability (FailedToStart");
        report.ShouldContain("never reached a running state");
        report.ShouldNotContain("audit-observability (FailedToStart, exit code");
    }

    [Fact]
    public void The_report_names_a_likely_cause_when_a_resource_exited_non_zero()
    {
        string cause = AspireFixture.FormatLikelyCause(
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis());

        // The label as well as the facts: "migrations 134" would satisfy the
        // three assertions below and would not be a sentence naming a cause.
        cause.ShouldStartWith("Likely cause: ");
        cause.ShouldContain("migrations");
        cause.ShouldContain("exited");
        cause.ShouldContain("134");
    }

    [Fact]
    public void The_timeout_message_names_the_cause_before_it_lists_the_states()
    {
        // FR-005: the cause comes first, so the reader is not asked to scan
        // forty-five state lines before learning what broke. Ordering is a
        // property of the assembled message, and until it was assembled in one
        // place, deleting the cause line from the `throw` left every test green
        // while the line disappeared from the report a human reads.
        string message = AspireFixture.FormatTimeoutMessage(
            TimeSpan.FromMinutes(8),
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis(),
            "(no failed resources)",
            "(no logs captured)");

        message.ShouldContain("Likely cause:");
        message.IndexOf("Likely cause:", StringComparison.Ordinal)
            .ShouldBeLessThan(message.IndexOf("Resource states:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_timeout_message_carries_every_section_of_the_report()
    {
        string message = AspireFixture.FormatTimeoutMessage(
            TimeSpan.FromMinutes(8),
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis(),
            "---- migrations (Finished, exit code 134 — the process ran and died) ----",
            "the last camera-catalog line");

        message.ShouldContain("did not start within 8 minutes");
        message.ShouldContain("Resource states:");
        message.ShouldContain("migrations: Finished (exit code 134)");
        message.ShouldContain("Failed-resource logs:");
        message.ShouldContain("---- migrations (Finished, exit code 134 — the process ran and died) ----");
        message.ShouldContain("Last camera-catalog logs:");
        message.ShouldContain("the last camera-catalog line");
    }

    [Fact]
    public void No_cause_is_claimed_when_the_one_shot_exited_cleanly()
    {
        // #1918's case. Finishing with 0 is how a one-shot succeeds, so there
        // is nothing to name and the report must not invent a suspect.
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal) { ["migrations"] = 0 };

        AspireFixture.FormatLikelyCause(StatesFromTheRunThatMotivatedThis(), exitCodes).ShouldBeEmpty();
    }

    [Fact]
    public void A_resource_that_crashed_and_came_back_is_not_named_as_a_cause()
    {
        // The cause line and the failure section have to agree. A resource
        // that died and restarted inside the watch window ends the window
        // `Running` — healthy, so the failure section leaves it out — while
        // still carrying the non-zero code from the earlier observation.
        // Naming it at the top of a report that then never mentions it again
        // sends the reader looking for a section that is not there.
        Dictionary<string, string> states = new(StringComparer.Ordinal) { ["camera-catalog"] = "Running" };
        Dictionary<string, int?> exitCodes = new(StringComparer.Ordinal) { ["camera-catalog"] = 137 };

        AspireFixture.SelectResourcesToReport(states, exitCodes).ShouldBeEmpty();
        AspireFixture.FormatLikelyCause(states, exitCodes).ShouldBeEmpty();
    }

    [Fact]
    public void No_cause_is_claimed_when_no_resource_states_were_captured()
    {
        AspireFixture.FormatLikelyCause(
            new(StringComparer.Ordinal),
            new(StringComparer.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void Idle_rebuilders_stay_out_of_the_failed_resource_report()
    {
        string report = AspireFixture.FormatFailedResourceReport(
            StatesFromTheRunThatMotivatedThis(),
            ExitCodesFromTheRunThatMotivatedThis(),
            new(StringComparer.Ordinal));

        report.ShouldNotContain("-rebuilder");
    }

    private static Dictionary<string, string> StatesFromTheRunThatMotivatedThis() =>
        new(StringComparer.Ordinal)
        {
            ["audit-observability"] = "FailedToStart",
            ["automation"] = "FailedToStart",
            ["camera-catalog"] = "FailedToStart",
            ["event-ingestion"] = "FailedToStart",
            ["identity"] = "FailedToStart",
            ["layout-composition"] = "FailedToStart",
            ["overlay-designer"] = "FailedToStart",
            ["stream-distribution"] = "FailedToStart",
            ["system-variables"] = "FailedToStart",

            ["migrations"] = "Finished",

            ["api-gateway-rebuilder"] = "NotStarted",
            ["audit-observability-rebuilder"] = "NotStarted",
            ["automation-rebuilder"] = "NotStarted",
            ["camera-catalog-rebuilder"] = "NotStarted",
            ["event-ingestion-rebuilder"] = "NotStarted",
            ["identity-rebuilder"] = "NotStarted",
            ["layout-composition-rebuilder"] = "NotStarted",
            ["migrations-rebuilder"] = "NotStarted",
            ["overlay-designer-rebuilder"] = "NotStarted",
            ["stream-distribution-rebuilder"] = "NotStarted",
            ["system-variables-rebuilder"] = "NotStarted",

            ["api-gateway"] = "Running",
            ["audit-db"] = "Running",
            ["automation-db"] = "Running",
            ["camera-catalog-db"] = "Running",
            ["event-ingestion-db"] = "Running",
            ["fixture-video"] = "Running",
            ["identity-db"] = "Running",
            ["keycloak"] = "Running",
            ["layout-composition-db"] = "Running",
            ["mediamtx"] = "Running",
            ["overlay-designer-db"] = "Running",
            ["postgres"] = "Running",
            ["rabbitmq"] = "Running",
            ["stream-distribution-db"] = "Running",
            ["system-variables-db"] = "Running",
        };

    private static Dictionary<string, int?> ExitCodesFromTheRunThatMotivatedThis() =>
        new(StringComparer.Ordinal) { ["migrations"] = 134 };
}
