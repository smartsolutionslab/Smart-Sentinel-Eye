namespace SmartSentinelEye.Integration.Tests;

/// <summary>
/// Guards the <c>E2ETests</c> switch the whole integration suite rests on.
/// Were it to stop reaching <c>builder.Configuration</c>, the AppHost would
/// silently boot its dev shape instead — persistent data volumes, pgAdmin and
/// the scenario simulator — so the suite would run against developer data
/// with nothing failing to say so.
///
/// <para>
/// Passes the arguments exactly as <see cref="Fixtures.AspireFixture"/> does,
/// because the switch travelling alone is not the case that matters. Builds
/// the application model only: <c>CreateAsync</c> starts no resources, so
/// this costs no containers and is safe beside a live <c>aspire run</c>.
/// </para>
///
/// <para>
/// Also guards the <c>ScenarioSimulator</c> switch (#2013), which is a
/// <em>different</em> switch on purpose. <c>E2ETests</c> means "this is the
/// integration fixture" and also removes the Vite apps; the end-to-end job
/// needs those and needs the simulator gone, so it cannot use <c>E2ETests</c>.
/// The separation is not a detail a reader can infer from the guard
/// expression, so it is asserted here.
/// </para>
///
/// <para>
/// <c>fixture-video</c> was named in that removal list and is no longer part
/// of it (spec 076, #198). It spans both lanes: the end-to-end job needs a
/// picture for the wall, and the integration lane now reads the same RTSP
/// source to observe a camera reaching <c>Healthy</c>. So the <c>E2ETests</c>
/// guard asserts both directions — the three dev-only resources absent, and
/// <c>fixture-video</c> present.
/// </para>
/// </summary>
[Trait("Category", "FixtureLogic")]
public class AppHostE2ESwitchTests
{
    private const string Workflow = ".github/workflows/ci.yml";
    private const string BootCommand = "dotnet run --project src/AppHost";
    private const string SimulatorArgument = "-- ScenarioSimulator=false";

    private static readonly string[] FixtureArguments =
    [
        "Parameters:PostgresUser=postgres",
        "Parameters:PostgresPassword=testpassword",
        "Parameters:KeycloakPassword=testkeycloak",
        "Parameters:RabbitMqPassword=testmessaging",
        "E2ETests=true",
    ];

    /// <summary>
    /// The shape the end-to-end job boots: parameters only, no <c>E2ETests</c>.
    /// A developer's <c>aspire run</c> sees the same model.
    /// </summary>
    private static readonly string[] RunModeArguments =
    [
        "Parameters:PostgresUser=postgres",
        "Parameters:PostgresPassword=testpassword",
        "Parameters:KeycloakPassword=testkeycloak",
        "Parameters:RabbitMqPassword=testmessaging",
    ];

    private static readonly string[] SimulatorDisabledArguments =
    [
        .. RunModeArguments,
        "ScenarioSimulator=false",
    ];

    [Fact]
    public async Task E2ETests_argument_excludes_the_dev_only_resources()
    {
        using IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SmartSentinelEye_AppHost>(FixtureArguments);

        IReadOnlyList<string> names = [.. builder.Resources.Select(resource => resource.Name)];

        names.ShouldNotContain("camera-sim");
        names.ShouldNotContain("scenario-simulator");
        names.ShouldNotContain("pgadmin");

        names.ShouldContain(
            "fixture-video",
            "the integration fixture reads this resource's RTSP path to observe a camera "
            + "reaching Healthy (spec 076, #198; the URL is AspireFixture.RtspTestSourceUrl), "
            + "so E2ETests must leave the video source in place — it is a both-lanes "
            + "resource, not a dev-only one.");
    }

    [Fact]
    public async Task E2ETests_argument_leaves_postgres_without_a_data_volume()
    {
        using IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SmartSentinelEye_AppHost>(FixtureArguments);

        IResource postgres = builder.Resources.Single(resource => resource.Name == "postgres");

        postgres.Annotations.OfType<ContainerMountAnnotation>()
            .Where(mount => mount.Type == ContainerMountType.Volume)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The end-to-end job boots a run-mode stack and needs the simulator gone,
    /// because the simulator seeds twelve cameras and three walls into the
    /// catalogue the Playwright specs share, so the kiosk picker's first entry
    /// and every camera dropdown carried data no spec put there (#2013).
    /// </summary>
    [Fact]
    public async Task ScenarioSimulator_argument_excludes_the_simulator_from_a_run_mode_stack()
    {
        IReadOnlyList<string> names = await ResourceNamesAsync(SimulatorDisabledArguments);

        names.ShouldNotContain("camera-sim");
        names.ShouldNotContain("scenario-simulator");
    }

    /// <summary>
    /// A developer types <c>aspire run</c> with no arguments and must keep the
    /// simulated fab. The switch defaults on; absent is not off.
    /// </summary>
    [Fact]
    public async Task A_run_mode_stack_without_the_argument_still_composes_the_simulator()
    {
        IReadOnlyList<string> names = await ResourceNamesAsync(RunModeArguments);

        names.ShouldContain("camera-sim");
        names.ShouldContain("scenario-simulator");
    }

    /// <summary>
    /// The standing proof that this change is not the one the issue proposed.
    /// Reusing <c>E2ETests</c> to silence the simulator would also delete the
    /// three Vite apps and <c>fixture-video</c> — the front ends under test and
    /// the only video source the wall spec has. If someone later folds the
    /// simulator back onto <c>E2ETests</c>, this is what says why they cannot.
    /// </summary>
    [Fact]
    public async Task The_simulator_argument_leaves_the_web_apps_and_the_fixture_video_in_place()
    {
        IReadOnlyList<string> names = await ResourceNamesAsync(SimulatorDisabledArguments);

        names.ShouldContain("management-web");
        names.ShouldContain("kiosk-web");
        names.ShouldContain("kiosk-wall");
        names.ShouldContain("fixture-video");
    }

    /// <summary>
    /// Proves the workflow file contains a string, and nothing more. It cannot
    /// prove the argument reached <c>builder.Configuration</c> — that evidence
    /// is a dashboard reading in phase 5, not a green suite.
    ///
    /// <para>
    /// It earns its place because the switch fails open: an unparseable or
    /// misspelled value leaves the simulator running, so a typo here silently
    /// restores the bug while every test above still passes. A defect in the
    /// workflow file is the one class of defect a file-reading guard catches.
    /// </para>
    ///
    /// <para>
    /// Splits at the first <c>&gt;</c> so the argument must sit in the command
    /// rather than after the redirection, where it would be a filename.
    /// </para>
    /// </summary>
    [Fact]
    public void The_e2e_boot_line_disables_the_scenario_simulator()
    {
        string command = BootLine().Split('>', 2)[0];

        command.ShouldContain(
            SimulatorArgument,
            Case.Sensitive,
            $"{Workflow} boots the end-to-end stack without '{SimulatorArgument}' ahead of the "
            + "redirection, so the run-mode default applies and the scenario simulator seeds the "
            + "catalogue the Playwright suite asserts against (#2013).");
    }

    private static async Task<IReadOnlyList<string>> ResourceNamesAsync(string[] arguments)
    {
        using IDistributedApplicationTestingBuilder builder =
            await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SmartSentinelEye_AppHost>(arguments);

        return [.. builder.Resources.Select(resource => resource.Name)];
    }

    /// <summary>
    /// A scan that matches nothing passes silently, and a passing guard that
    /// checked nothing is indistinguishable from one that holds — so the
    /// absence of the boot line is itself a failure.
    /// </summary>
    private static string BootLine()
    {
        string[] candidates =
        [
            .. System.IO.File
                .ReadAllLines(WorkflowPath())
                .Where(line => line.Contains(BootCommand, StringComparison.Ordinal)),
        ];

        candidates.ShouldHaveSingleItem(
            $"expected exactly one '{BootCommand}' line in {Workflow}, found {candidates.Length} — "
            + "the scan is broken, or the boot step moved.");

        return candidates[0];
    }

    /// <summary>
    /// Built with <see cref="System.IO.Path.Combine(string[])"/> rather than a
    /// literal path: a backslash separator is green on Windows and red on Linux
    /// CI, and this repository has been bitten by exactly that.
    /// </summary>
    private static string WorkflowPath() =>
        System.IO.Path.Combine([RepositoryRoot(), .. Workflow.Split('/')]);

    private static string RepositoryRoot()
    {
        System.IO.DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null
            && !System.IO.File.Exists(System.IO.Path.Combine(candidate.FullName, "SmartSentinelEye.slnx")))
        {
            candidate = candidate.Parent;
        }

        return candidate?.FullName
            ?? throw new InvalidOperationException(
                $"could not locate the repository root above {AppContext.BaseDirectory}");
    }
}
