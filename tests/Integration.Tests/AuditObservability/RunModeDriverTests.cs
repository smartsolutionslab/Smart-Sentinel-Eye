using System.Reflection;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Guards on the run-mode driver itself (spec 054 Phase 2).
///
/// <para>
/// <b>These are cheap, they need no stack, and each one stands in front of a
/// failure that would otherwise be silent.</b> The measurement they protect
/// produces a breakdown that reads identically whether it came from the intended
/// stack or another — so the guards are on the things that decide *which*, not on
/// the numbers.
/// </para>
/// </summary>
public class RunModeDriverTests
{
    /// <summary>
    /// **The mutation this exists for**: giving the run-mode class
    /// <c>[Collection(AspireCollection.Name)]</c>.
    ///
    /// <para>
    /// That attribute is what injects <c>AspireFixture</c>, and the fixture boots
    /// its own stack. A run-mode test that acquired one would drive load at a
    /// fixture, divide the span correctly, and publish the result labelled "run
    /// mode" — complete, well-formed and wrong, with nothing in the output to
    /// betray it.
    /// </para>
    ///
    /// <para>
    /// The mechanism is real but invisible, so it is asserted rather than relied
    /// upon. This is the only guard here whose failure produces a *plausible*
    /// answer rather than an obvious one.
    /// </para>
    /// </summary>
    [Fact]
    public void The_run_mode_test_cannot_acquire_the_fixture()
    {
        CollectionAttribute? collection = typeof(RunModeIngestAttributionTests)
            .GetCustomAttribute<CollectionAttribute>();

        collection.ShouldBeNull(
            "a collection attribute injects AspireFixture, which boots its own stack — the run-mode "
            + "measurement would then report a fixture's figures as run mode's");
    }

    /// <summary>
    /// The run-mode test must also take no fixture through its constructor, which
    /// is the other way one could arrive.
    /// </summary>
    [Fact]
    public void The_run_mode_test_takes_no_fixture_as_a_dependency()
    {
        bool takesFixture = typeof(RunModeIngestAttributionTests)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType.Name.Contains("AspireFixture", StringComparison.Ordinal));

        takesFixture.ShouldBeFalse("taking the fixture would boot a stack regardless of the attribute");
    }

    /// <summary>
    /// **Each of the three, not just the first.**
    ///
    /// <para>
    /// The mutation named is "a fallback address", and clearing only
    /// <c>SSE_RUNMODE_SYSTEM_VARIABLES</c> would miss two thirds of it: a default
    /// introduced for the Keycloak or audit-db variable survives untouched,
    /// because the first branch short-circuits before either is read.
    /// </para>
    ///
    /// <para>
    /// <b>Reads no real environment variable.</b> xUnit runs classes without a
    /// collection attribute in parallel, and this class and the measurement both
    /// lack one — so nulling the real variables here could refuse a correctly
    /// configured measurement running alongside, with a message naming a cause
    /// that was not true.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, "https://keycloak", "Host=db")]
    [InlineData("http://variables", null, "Host=db")]
    [InlineData("http://variables", "https://keycloak", null)]
    [InlineData("", "https://keycloak", "Host=db")]
    [InlineData("http://variables", "   ", "Host=db")]
    public void Any_absent_setting_yields_no_address_rather_than_a_default(
        string? systemVariables, string? keycloak, string? auditDb)
    {
        Option<RunModeStackAddress> configured =
            RunModeStackAddress.From(systemVariables, keycloak, auditDb);

        configured.HasValue.ShouldBeFalse(
            "with any part of the stack unconfigured there must be no address at all; a default "
            + "would let the run measure something else and call it run mode");
    }

    [Fact]
    public void A_fully_configured_stack_yields_an_address()
    {
        Option<RunModeStackAddress> configured =
            RunModeStackAddress.From("http://variables", "https://keycloak", "Host=db");

        configured.HasValue.ShouldBeTrue(
            "without this the refusal test above would pass on a function that always refuses");
    }

    /// <summary>
    /// The refusal has to say what it wanted, or whoever hits it is left guessing
    /// at three variable names and an address format.
    /// </summary>
    [Fact]
    public void The_refusal_names_what_it_needed()
    {
        string missing = RunModeStackAddress.Missing;

        missing.ShouldContain(RunModeStackAddress.SystemVariablesVariable);
        missing.ShouldContain(RunModeStackAddress.KeycloakVariable);
        missing.ShouldContain(RunModeStackAddress.AuditDbVariable);
        missing.ShouldContain("will not", Case.Insensitive);
        missing.ShouldContain("issuer", Case.Insensitive);
    }

    /// <summary>
    /// **Both runs read one shape** (spec 054 US2).
    ///
    /// <para>
    /// Two constants that happen to match satisfy a reader and drift the moment
    /// one is edited. This asserts there is one definition to read — that the run
    /// body both callers use takes its counts from <see cref="IngestRunShape"/>
    /// and not from either caller.
    /// </para>
    /// </summary>
    [Fact]
    public void Neither_run_can_supply_a_shape_of_its_own()
    {
        MethodInfo run = typeof(IngestSpanMeasurement)
            .GetMethod(nameof(IngestSpanMeasurement.RunAsync))
            .ShouldNotBeNull();

        // **The mutation this exists for**: giving the two runs separate shape
        // constants. They cannot, because the run body accepts no counts — it
        // reads IngestRunShape and nothing else can tell it otherwise. A numeric
        // parameter here would be the door through which the two runs drift
        // apart, so its absence is the guard.
        string[] numericParameters = run
            .GetParameters()
            .Where(parameter => parameter.ParameterType == typeof(int)
                || parameter.ParameterType == typeof(double)
                || parameter.ParameterType == typeof(long))
            .Select(parameter => parameter.Name!)
            .ToArray();

        numericParameters.ShouldBeEmpty(
            "the run body takes its counts and its rate from IngestRunShape; a numeric parameter "
            + $"would let one caller run a different shape than the other ({string.Join(", ", numericParameters)})");
    }

    [Fact]
    public void The_run_shape_is_one_definition_both_runs_read()
    {
        IngestRunShape.EventsPerWriter.ShouldBe(
            IngestRunShape.MeasuredEvents / IngestRunShape.Writers,
            "every writer sends the same count, so the measured total must divide exactly");

        (IngestRunShape.EventsPerWriter * IngestRunShape.Writers).ShouldBe(
            IngestRunShape.MeasuredEvents,
            "a shape that does not multiply back would drive a different number of events than the "
            + "population every percentile is taken over");

        IngestRunShape.SlotIntervalMs.ShouldBe(
            1000d / IngestRunShape.TargetRatePerSecond,
            0.0001,
            "the pacing interval is derived from the target rate, not set beside it");

        IngestRunShape.MinimumAcceptableRate.ShouldBeLessThan(IngestRunShape.TargetRatePerSecond);
        IngestRunShape.MaximumAcceptableRate.ShouldBeGreaterThan(IngestRunShape.TargetRatePerSecond);
    }

    /// <summary>
    /// The conditions block reports the endpoint, which is the only guard against
    /// attributing a figure to the wrong stack — and the only one no automated
    /// check can replace.
    /// </summary>
    [Fact]
    public void The_conditions_report_the_endpoint_reached()
    {
        IngestRunConditions conditions = new(
            Environment: "run mode (AppHost)",
            Endpoint: "system-variables http://localhost:1234/",
            IntendedRatePerSecond: 100,
            AchievedRatePerSecond: 99,
            LogLevel: "Warning",
            LogLevelWasChosen: true,
            MeasurementSwitchOn: true,
            RowsMeasured: 1_000,
            RowsMissingStamps: 0);

        string described = conditions.Describe();

        described.ShouldContain("http://localhost:1234/");
        described.ShouldContain("run mode (AppHost)");
        described.ShouldContain("endpoint reached");
    }

    /// <summary>
    /// A run below the rate tolerance must be refusable. Asserted on the
    /// condition rather than on the test, so the judgement lives where both runs
    /// read it.
    /// </summary>
    [Theory]
    [InlineData(60, false)]
    [InlineData(84, false)]
    [InlineData(99, true)]
    [InlineData(114, true)]
    [InlineData(140, false)]
    public void A_run_off_the_target_rate_is_not_reportable(double achieved, bool acceptable)
    {
        IngestRunConditions conditions = new(
            Environment: "run mode (AppHost)",
            Endpoint: "somewhere",
            IntendedRatePerSecond: IngestRunShape.TargetRatePerSecond,
            AchievedRatePerSecond: achieved,
            LogLevel: "Warning",
            LogLevelWasChosen: true,
            MeasurementSwitchOn: true,
            RowsMeasured: 1_000,
            RowsMissingStamps: 0);

        conditions.RateWasMet.ShouldBe(acceptable);
    }

    /// <summary>
    /// Verbose logging is a condition of the run, and both directions matter: at
    /// Debug this stack sustains ~80 ev/s, below the rate the requirement names.
    /// Every level here was chosen for the run; whether an inherited one is fit
    /// to measure through is
    /// <see cref="A_level_nobody_chose_for_this_run_is_not_fit_to_measure"/>.
    /// </summary>
    [Theory]
    [InlineData("Warning", false)]
    [InlineData("Information", false)]
    [InlineData("Debug", true)]
    [InlineData("Trace", true)]
    public void Verbose_logging_is_recognised_whatever_it_is_called(string level, bool verbose)
    {
        IngestRunConditions conditions = new(
            Environment: "run mode (AppHost)",
            Endpoint: "somewhere",
            IntendedRatePerSecond: 100,
            AchievedRatePerSecond: 99,
            LogLevel: level,
            LogLevelWasChosen: true,
            MeasurementSwitchOn: true,
            RowsMeasured: 1_000,
            RowsMissingStamps: 0);

        conditions.LoggingIsVerbose.ShouldBe(verbose);
    }

    /// <summary>
    /// A level nobody picked for this run is refused whatever it is.
    ///
    /// <para>
    /// <b>This is what the theory above used to say by accident.</b> Its
    /// <c>"Debug (from appsettings)"</c> datum passed because the string began
    /// with "Debug" — the appsettings' level of the day compiled into a literal,
    /// so the refusal of an inherited level rode on a spelling rather than on the
    /// fact. An inherited level is whatever the eleven Development files pin at
    /// the time, and no throughput figure exists for a pairing nobody has
    /// measured, so it is not fit to measure through however it is spelled.
    /// </para>
    ///
    /// <para>
    /// The chosen row is the control: the same level, picked deliberately for the
    /// run, is not refused. Without it this would pass for the wrong reason — a
    /// guard that refuses everything is not a guard.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Information", false, true)]
    [InlineData("Information", true, false)]
    public void A_level_nobody_chose_for_this_run_is_not_fit_to_measure(
        string level,
        bool chosen,
        bool notFitToMeasure)
    {
        IngestRunConditions conditions = new(
            Environment: "run mode (AppHost)",
            Endpoint: "somewhere",
            IntendedRatePerSecond: 100,
            AchievedRatePerSecond: 99,
            LogLevel: level,
            LogLevelWasChosen: chosen,
            MeasurementSwitchOn: true,
            RowsMeasured: 1_000,
            RowsMissingStamps: 0);

        conditions.LoggingIsVerbose.ShouldBe(
            notFitToMeasure,
            $"the run was at '{level}', {(chosen ? "chosen for this run" : "inherited from the appsettings")}");
    }
}
