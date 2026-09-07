using Shouldly;
using Xunit;

namespace SmartSentinelEye.Integration.Tests.Fixtures;

/// <summary>
/// #2066 — reaching the end of <c>InitializeAsync</c> means every wait
/// <i>returned</i>; it does not mean every resource is <i>alive</i>. DCP
/// publishes <c>Running</c> when a process launches, so a service that crashes
/// a second later has already satisfied its wait. The fixture therefore reads
/// each waited-for resource's state once, after the waits, and refuses to hand
/// the tests a stack in which one of them is dead. Pure decision logic, so
/// these run without Docker or the fixture, alongside
/// <see cref="AspireFixtureMigrationGateTests"/>.
///
/// <para>
/// <b>Why the assertion throws an <c>InvalidOperationException</c> rather than
/// any <c>OperationCanceledException</c> subtype:</b> every diagnostic this
/// family has built — #2061's cause line, #2038's snapshot section, #2064's
/// migrations message — hangs off the <c>catch (… OperationCanceledException
/// or TaskCanceledException)</c> in <c>AspireFixture.InitializeAsync</c>. No
/// timeout, no catch, no report. The whole apparatus is bypassed by the
/// failure mode it was built for, because that failure mode never times out.
/// An OCE subtype thrown here would be reclassified as "did not start within 8
/// minutes" and vanish the same way.
/// </para>
///
/// <para>
/// <b>These are supporting evidence, not the gate.</b> Delete the one call site
/// and every test here stays green — the limit <c>plan.md</c> §"The honest
/// limits" records, now sharper than it was, because the withdrawn design had
/// twelve call sites to delete and this has one. The gate is the runtime
/// observation in <c>spec.md</c> §9: with <c>identity</c> provoked to die the
/// filtered test <i>passed, twice</i>. What these hold is the two things a boot
/// cannot show cheaply — the states that must <i>not</i> fire (<c>Unknown</c>
/// is the fail-open guard) and the shape of the message.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class AspireFixtureStartupGateTests
{
    private const string CameraCatalogLog = "Unhandled exception. System.InvalidOperationException";

    [Fact]
    public void Finished_is_a_fatal_state_for_a_long_running_resource()
    {
        // #1918's exact shape: a service that is never meant to end, ending.
        // `migrations` is the one resource for which `Finished` is success, and
        // it is excluded from the inspected set for that reason (FR-003) —
        // none of the twelve sampled here is one-shot.
        AspireFixture.IsFatalStartupState("Finished").ShouldBeTrue();
    }

    [Fact]
    public void Exited_is_a_fatal_state()
    {
        // The container spelling of the same death. `ContainerState` has no
        // `Finished` and `ExecutableState` has no `Exited`, so both words are
        // needed: three of the twelve inspected resources are containers
        // (keycloak, mediamtx, fixture-video), which the issue said they were
        // not.
        AspireFixture.IsFatalStartupState("Exited").ShouldBeTrue();
    }

    [Fact]
    public void FailedToStart_is_a_fatal_state()
    {
        // The only state with a confirmed occurrence: #2062's nine services
        // reached it after `migrations` exited 134. That family is now stopped
        // at 1m38s by #2064's migrations gate, so this state reaches the sample
        // only where a dependency failed some other way. Kept because the
        // vocabulary has to be complete, not because the case was observed
        // here.
        AspireFixture.IsFatalStartupState("FailedToStart").ShouldBeTrue();
    }

    [Fact]
    public void RuntimeUnhealthy_is_a_fatal_state()
    {
        // Container-only, and in Aspire's own stop set
        // (`ResourceNotificationService.IsContinuableState`). Not observable
        // here — provoking it means killing the Docker engine mid-boot — so
        // this test is the whole of its coverage.
        AspireFixture.IsFatalStartupState("RuntimeUnhealthy").ShouldBeTrue();
    }

    [Fact]
    public void Terminated_is_a_fatal_state_although_Aspire_has_no_constant_for_it()
    {
        // `KnownResourceStates.TerminalStates` is exactly
        // { Finished, FailedToStart, Exited } — no `Terminated`. The state
        // exists only as `Aspire.Hosting.Dcp.Model.ExecutableState.Terminated`,
        // which is `internal`, and reaches the snapshot because
        // `ResourceSnapshotBuilder.ToSnapshot(Executable, …)` copies the state
        // text verbatim. So it can only ever be a string literal, on both sides.
        AspireFixture.IsFatalStartupState("Terminated").ShouldBeTrue();
    }

    [Fact]
    public void Running_is_not_a_fatal_state()
    {
        // `Running` is what all twelve report at the sample on a healthy boot —
        // each one's own wait matched it minutes earlier. Matching it here would
        // fail every integration run on every machine, which is this change's
        // whole blast radius in one assertion.
        AspireFixture.IsFatalStartupState("Running").ShouldBeFalse();
    }

    [Fact]
    public void Unknown_is_not_a_fatal_state()
    {
        // The fail-open guard, and the one exclusion that is a judgement rather
        // than a reading: `Unknown` means "no longer tracked". It carries no
        // exit code and makes no claim, so reading it as death would invent one
        // and abort a boot that was fine.
        AspireFixture.IsFatalStartupState("Unknown").ShouldBeFalse();
    }

    [Fact]
    public void NotStarted_is_not_a_fatal_state()
    {
        // A container with `Spec.Start != true` — the dev-time rebuilders.
        // `IsHealthy` already treats it as benign; disagreeing here would make
        // the fixture abort on resources it deliberately ignores elsewhere.
        AspireFixture.IsFatalStartupState("NotStarted").ShouldBeFalse();
    }

    [Fact]
    public void Waiting_is_not_a_fatal_state()
    {
        // The state the nine dependents sit in while `migrations` runs. It
        // cannot legitimately reach the sample at all: a resource still
        // `Waiting` has not satisfied its own `Running` wait, so the boot is
        // blocked several hundred lines earlier and never gets here. An
        // unexpected state is exactly where fail-open is the right default —
        // read it as death and a sequencing surprise becomes a failed suite.
        AspireFixture.IsFatalStartupState("Waiting").ShouldBeFalse();
    }

    [Fact]
    public void A_null_state_is_not_a_fatal_state()
    {
        // `Snapshot.State` is nullable and `State?.Text` is what the sample
        // reads, so a null is representable at the call site even though every
        // one of the twelve has published a snapshot by then. A missing state
        // makes no claim; manufacturing a death out of one would be the same
        // invention `Unknown` is excluded for.
        AspireFixture.IsFatalStartupState(null).ShouldBeFalse();
    }

    [Fact]
    public void State_matching_ignores_case()
    {
        // `Aspire.StringComparers.ResourceState` is case-insensitive and
        // `internal`, so it cannot be borrowed; `OrdinalIgnoreCase` is how the
        // migrations gate matches state text exactly rather than by inspection
        // (#2064), and the sample compares it the same way.
        AspireFixture.IsFatalStartupState("finished").ShouldBeTrue();
        AspireFixture.IsFatalStartupState("FAILEDTOSTART").ShouldBeTrue();
        AspireFixture.IsFatalStartupState("terminated").ShouldBeTrue();
        AspireFixture.IsFatalStartupState("exited").ShouldBeTrue();
        AspireFixture.IsFatalStartupState("runtimeunhealthy").ShouldBeTrue();
    }

    [Fact]
    public void The_message_names_the_resource_the_state_and_the_exit_code()
    {
        string message = AspireFixture.FormatResourceDeathMessage(
            "camera-catalog",
            "FailedToStart",
            134,
            CameraCatalogLog);

        message.ShouldContain("camera-catalog");
        message.ShouldContain("FailedToStart");
        message.ShouldContain("134");
    }

    [Fact]
    public void The_message_says_so_when_no_exit_code_was_recorded()
    {
        // The container case, and the reason the parameter is `int?`: the
        // container branch of `ResourceSnapshotBuilder` maps `ExitCode == -1`
        // to null, so a dead container legitimately arrives with no code.
        // Rendering that through an `int`-shaped formatter yields
        // "with exit code  —"; rendering it as 0 would claim a clean exit.
        string message = AspireFixture.FormatResourceDeathMessage(
            "mediamtx",
            "Exited",
            null,
            "(no logs captured)");

        message.ShouldContain("mediamtx");
        message.ShouldContain("Exited");
        message.ShouldContain("no exit code recorded");
        message.ShouldNotContain("exit code 0");
    }

    [Fact]
    public void The_message_puts_the_cause_before_the_log()
    {
        // An ordering claim, holdable only over the assembled string — the
        // reason this formatter is pure and separate from the throw, like the
        // four before it. A reader scrolling a Kestrel stack trace should have
        // met the verdict already.
        string message = AspireFixture.FormatResourceDeathMessage(
            "camera-catalog",
            "FailedToStart",
            134,
            CameraCatalogLog);

        message.IndexOf("FailedToStart", StringComparison.Ordinal)
            .ShouldBeLessThan(message.IndexOf(CameraCatalogLog, StringComparison.Ordinal));
        message.IndexOf("134", StringComparison.Ordinal)
            .ShouldBeLessThan(message.IndexOf(CameraCatalogLog, StringComparison.Ordinal));
    }

    [Fact]
    public void The_message_says_the_fixture_refused_to_report_a_healthy_boot()
    {
        // The verdict sentence, and the one claim this file's rewrite had to
        // correct. The withdrawn wording — "stopped here rather than spending
        // the remaining budget", inherited from
        // `FormatMigrationFailureMessage` — is false for this check: it runs
        // *after* all twelve waits, so it spends the whole boot and only then
        // refuses to return. It saves no time; what it changes is the verdict.
        // A reader whose ~3-minute run failed must not be told the fixture
        // aborted early, and must be told what would otherwise have happened —
        // a green report over a dead resource, which phase 4a observed twice.
        string message = AspireFixture.FormatResourceDeathMessage(
            "identity",
            "Finished",
            0,
            "(no logs captured)");

        message.ShouldContain("rather than reporting a healthy boot over a dead resource");
        message.ShouldNotContain("spending the remaining budget");
    }

    [Fact]
    public void Two_dead_resources_are_both_reported()
    {
        // FR-008. In #2062 nine resources died on one boot, so reporting only
        // the first one found names an arbitrary member of the set and hides
        // the rest — the misattribution this family exists to remove.
        //
        // The sweep that collects them is private and needs a boot. What is
        // holdable here is the property that lets it report all of them: each
        // message is self-contained — its own resource, its own state, its own
        // exit code, its own log — and none of it is phrased as a singular
        // global verdict that a second occurrence would contradict. A formatter
        // that hard-coded a name, or wrote "the resource that died", would pass
        // every test above and produce nonsense the first time two died.
        string first = AspireFixture.FormatResourceDeathMessage(
            "camera-catalog",
            "FailedToStart",
            134,
            CameraCatalogLog);

        string second = AspireFixture.FormatResourceDeathMessage(
            "identity",
            "Exited",
            null,
            "SCRATCH: deliberate identity failure.");

        first.ShouldNotContain("identity");
        second.ShouldNotContain("camera-catalog");

        string report = first + Environment.NewLine + second;

        report.ShouldContain("camera-catalog");
        report.ShouldContain("FailedToStart");
        report.ShouldContain("134");
        report.ShouldContain(CameraCatalogLog);
        report.ShouldContain("identity");
        report.ShouldContain("Exited");
        report.ShouldContain("no exit code recorded");
        report.ShouldContain("SCRATCH: deliberate identity failure.");
    }

    [Fact]
    public void The_message_does_not_list_every_resource()
    {
        // At the sample the resources that are fine are all `Running` and say
        // nothing about the failure, so a forty-five-line state list would
        // print forty-four rows of noise — what #2061 removed, and the reason
        // `FormatMigrationFailureMessage` is scoped to one resource.
        string message = AspireFixture.FormatResourceDeathMessage(
            "camera-catalog",
            "FailedToStart",
            134,
            CameraCatalogLog);

        message.ShouldNotContain("Resource states:");
        message.ShouldNotContain("keycloak");
        message.ShouldNotContain("postgres");
    }
}
