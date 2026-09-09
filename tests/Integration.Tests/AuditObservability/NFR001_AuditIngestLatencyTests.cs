using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// Spec 009 NFR-001 (T072): audit ingest latency, p99 ≤ 50 ms from publish to
/// the row being written. Warms 100 events, measures 1 000.
///
/// <para>
/// <b>The generator is a repeated variable value set</b>, and the choice is
/// load-bearing. It publishes <c>SystemVariableValueChangedV1</c>, whose
/// <c>Metadata.OccurredAt</c> is the domain event's <c>changedAt</c> — stamped
/// as the aggregate mutates, so the figure spans the publish path rather than
/// the caller's wall clock. A plant-floor event would have been the easier
/// generator and the wrong one: <c>FabEventIngestedV1</c> carries the *device's*
/// timestamp, so its rows measure the whole MQTT chain. Measured on a dev stack
/// those run p50 30 ms / p99 63 ms, against p50 10 ms / p99 24 ms for events
/// stamped at publish — the same budget, the wrong leg, and a failure that would
/// say nothing about audit ingest.
/// </para>
///
/// <para>
/// It also keeps the blast radius small. The variable is referenced by no
/// overlay, so <c>VariableValueChangedDomainEventHandler</c> takes its
/// no-overlays branch and publishes nothing further; and unlike a camera
/// registration there is no stream provisioning behind each event.
/// </para>
///
/// <para>
/// <b>What the figure spans, stated because the NFR is narrower than anything
/// that can be measured.</b> NFR-001's words are "RabbitMQ deliver-ack to audit
/// row committed". No timestamp pair exists for that leg. What is available is
/// <c>received_at - occurred_at</c>: aggregate mutation → outbox → RabbitMQ →
/// the audit handler stamping <c>ReceivedAt</c> in <c>AuditEvent.From</c>, which
/// happens just before <c>SaveAsync</c> rather than after the commit. So this is
/// a superset of the leg NFR-001 names, short by the final insert — the honest
/// approximation, and a pass here implies a pass on the narrower leg.
/// </para>
///
/// <para>
/// Latency is computed <b>in SQL</b> rather than by polling from the client, so
/// the measurement carries no HTTP round trip of its own — the approach spec
/// 020's throughput test takes, for the same reason.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class NFR001_AuditIngestLatencyTests(AspireFixture aspire, ITestOutputHelper output)
{
    // The run shape lives in IngestRunShape, read by this run and by the
    // run-mode run alike (spec 054). Two constants that happened to match would
    // satisfy a reader and drift the moment one was edited.
    private const double P99BudgetMs = 50;

    /// <summary>
    /// The log level the services under measurement are running at.
    ///
    /// <para>
    /// <b>This is a condition of the measurement, not a detail of the harness.</b>
    /// Measured on this stack: at Debug the run sustains ~80 ev/s, at Warning
    /// ~174–244. The logging is the bottleneck at Debug — which is why that
    /// figure is the stable one and the quiet figure is not.
    /// </para>
    ///
    /// <para>
    /// Read from the environment because that is what the fixture propagates to
    /// the services it boots. Absent means nothing overrode the appsettings, so
    /// the level was inherited rather than chosen for this run. What the
    /// appsettings pin is not this file's to know: as of spec 081 (2026-09-06)
    /// the eleven Development files sit at <c>Information</c> with
    /// <c>Microsoft.EntityFrameworkCore.Database.Command</c> at <c>Warning</c>,
    /// and that pairing has no measured throughput figure (#2133) — which is why
    /// the level travels with its provenance rather than being read for its
    /// spelling. The fallback names the level only; provenance is
    /// <see cref="ServiceLogLevelWasChosen"/>'s to carry, and a string that
    /// tries to carry both is the shape spec 081 exists to unwind.
    /// </para>
    /// </summary>
    private static string ServiceLogLevel =>
        string.IsNullOrWhiteSpace(ChosenServiceLogLevel) ? "Information" : ChosenServiceLogLevel;

    /// <summary>
    /// The level somebody set for this run, or absent if nobody did.
    ///
    /// <para>
    /// Read once and asked two questions, because the value and its provenance
    /// are different facts: <see cref="ServiceLogLevel"/> reports what the
    /// services are at, and <see cref="ServiceLogLevelWasChosen"/> reports
    /// whether that was a decision or an inheritance.
    /// </para>
    /// </summary>
    private static string? ChosenServiceLogLevel =>
        Environment.GetEnvironmentVariable("Logging__LogLevel__Default");

    /// <summary>
    /// Whether the level above was chosen for this run rather than inherited.
    ///
    /// <para>
    /// <b>Blank is not a choice.</b> <c>Logging__LogLevel__Default=</c> yields
    /// <c>""</c>, not <c>null</c>, and an empty or whitespace value binds to no
    /// level — the services stay on whatever the appsettings pin. Read for
    /// nullness alone this would have reported a choice, and certified a run
    /// under the very configuration the guard refuses.
    /// </para>
    /// </summary>
    private static bool ServiceLogLevelWasChosen =>
        !string.IsNullOrWhiteSpace(ChosenServiceLogLevel);

    /// <summary>
    /// **Where the span goes** (spec 053 US1). Excluded from CI like its
    /// neighbour, and for the same reason: it is a measurement, not a check.
    ///
    /// <para>
    /// <b>It asserts almost nothing about the pipeline on purpose.</b> The
    /// output is a breakdown for someone deciding what to do about a
    /// requirement, and a test that failed when the pipeline was slow would be
    /// reporting the thing already known. What it does assert is that the
    /// breakdown is <i>trustworthy</i>: the parts cover the span, every row
    /// carried its stamps, and the clocks are close enough for the one part
    /// that crosses them to mean anything.
    /// </para>
    ///
    /// <para>
    /// Needs the measurement switch on — with it off the parts are absent and
    /// the run says so rather than reporting zeros.
    /// </para>
    /// </summary>
    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Where_the_ingest_span_goes()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        IngestSpanResult result = await IngestSpanMeasurement.RunAsync(
            variables,
            context,
            environment: "Aspire test fixture",
            endpoint: variables.BaseAddress?.ToString() ?? "unknown",
            logLevel: ServiceLogLevel,
            logLevelWasChosen: ServiceLogLevelWasChosen,
            CancellationToken.None);

        // **The conditions first, before anything that can fail.** A refused run
        // still has to say what it was refused for; spec 053's guards behaved
        // this way and that is why its failures were informative rather than
        // merely red.
        output.WriteLine(result.Conditions.Describe());
        output.WriteLine(DrivePosition(result));
        output.WriteLine("--- typical event (medians over every row) ---");
        output.WriteLine(result.Typical.Describe());
        output.WriteLine("--- tail band (rows at or above the p99 of the total) ---");
        output.WriteLine(result.Tail.Describe());
        output.WriteLine($"this process vs the shared server: {result.Offset}");
        output.WriteLine($"  clock standing: {result.Verdict.Standing} — {result.Verdict.Reason}");
        output.WriteLine(
            "  the write leg crosses host and container clocks and is bounded by that offset; "
            + "'in handler' is stamped twice by one process and is exact whatever the clocks do");

        result.Conditions.RowsMeasured.ShouldBe(IngestRunShape.MeasuredEvents);

        result.Typical.EveryRowStamped.ShouldBeTrue(
            $"{result.Typical.RowsMissingStamps} rows arrived without the measurement stamps; "
            + "turn the switch on before reading anything above");

        // **The apparatus check, asserted on both bands.** Each row's parts sum
        // to that row's own span by construction, so the median per-row residual
        // is exactly zero unless the stamps genuinely disagree. Asserting on the
        // reported medians instead would be wrong: medians do not add, so a
        // healthy apparatus can leave a gap between the printed parts and the
        // printed total.
        result.Typical.PartsCoverEveryRow.ShouldBeTrue(
            $"the median row leaves {result.Typical.PerRowResidualMs:F3} ms between its span and its "
            + "parts; consecutive stamps cannot do that, so the stamps disagree with the timestamps "
            + "that bracket them");

        result.Tail.PartsCoverEveryRow.ShouldBeTrue(
            $"the median tail row leaves {result.Tail.PerRowResidualMs:F3} ms between its span and its parts");

        result.Verdict.IsEstablished.ShouldBeTrue(
            $"{result.Verdict.Reason} — the write leg subtracts a host-process stamp from a container "
            + $"one, and at {result.Typical.WriteMs:F1} ms it is the same size as that disagreement, "
            + "so it cannot be reported as measured");

        result.Conditions.LoggingIsVerbose.ShouldBeFalse(
            $"the services are logging at '{result.Conditions.LogLevel}', "
            + $"{(result.Conditions.LogLevelWasChosen ? "chosen for this run" : "inherited from the appsettings")}; "
            + "Debug and Trace put the logging in front of the pipeline at ~80 ev/s against a target "
            + "of 100, and an inherited level has no measured throughput figure at all (#2133) — set "
            + "Logging__LogLevel__Default=Warning for a measurement run");

        result.Conditions.RateWasMet.ShouldBeTrue(
            $"the run drove {result.Conditions.AchievedRatePerSecond:F1} ev/s against a target of "
            + $"{IngestRunShape.TargetRatePerSecond:F0}; NFR-001 is a claim about that rate sustained, "
            + "and a breakdown taken at another rate answers another question");
    }

    /// <summary>
    /// **The requirement's span, at the rate the requirement names** (spec 109
    /// US2). Excluded from CI like its neighbours: the fixture is bistable at
    /// 100 ev/s (ADR-0136) and a bistable figure does not belong in a gate.
    ///
    /// <para>
    /// <b>It reports an interval and asserts no verdict.</b> NFR-001's span is
    /// "RabbitMQ deliver-ack to audit row committed", and the hand-over falls
    /// <i>inside</i> "before handler" with no publisher-side stamp to divide it
    /// (<see cref="IngestAttribution.RequirementSpanWidthMs"/>). So the budget is
    /// <i>placed</i> against the floor-to-ceiling interval and the reader is told
    /// where it fell. Asserting <c>p99 &lt; 50</c> against the ceiling is the
    /// assertion that produced every superseded verdict on #1956; asserting it
    /// against the floor is the same error mirrored, and turns the fact green for
    /// the wrong reason.
    /// </para>
    ///
    /// <para>
    /// <b>What it does assert is that the run was fit to be read</b>: that it
    /// drove at the rate the requirement names, that every row carried its
    /// stamps, and that the parts cover each row. A number taken at another rate
    /// answers another question — which is exactly what
    /// <see cref="AuditIngestHistoricSpanTests.Ingest_span_from_publish_to_row_at_the_unpaced_historic_shape"/>
    /// above was doing while it still returned a verdict — at 45-58 ev/s measured
    /// cold, and 80-95 ev/s when it ran third in a boot.
    /// </para>
    /// </summary>
    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Requirement_span_at_100_events_per_second_is_an_interval_not_a_verdict()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        // **The paced shape, and it is the fact's whole subject.** Fifty writers
        // drawing their slots from one counter, paced to
        // `IngestRunShape.TargetRatePerSecond` — the drive
        // `IngestSpanMeasurement.RunAsync` already carries, taken from here so
        // that this figure and `Where_the_ingest_span_goes`' are comparable by
        // construction rather than by prose. The single sequential writer this
        // fact was first wired to sustains 45-58 ev/s cold, which is the rate the
        // guard below refused at phase 4a.
        IngestSpanResult result = await IngestSpanMeasurement.RunAsync(
            variables,
            context,
            environment: "Aspire test fixture",
            endpoint: variables.BaseAddress?.ToString() ?? "unknown",
            logLevel: ServiceLogLevel,
            logLevelWasChosen: ServiceLogLevelWasChosen,
            CancellationToken.None);

        IngestRunConditions conditions = result.Conditions;
        IngestAttribution typical = result.Typical;
        IngestAttribution tail = result.Tail;

        // The conditions before anything that can fail, so a refused run still
        // says what it was refused for.
        output.WriteLine(conditions.Describe());
        output.WriteLine(DrivePosition(result));
        output.WriteLine("--- typical event (medians over every row) ---");
        output.WriteLine(typical.Describe());
        output.WriteLine(BudgetPlacement("typical", typical));
        output.WriteLine("--- tail band (rows at or above the p99 of the total) ---");
        output.WriteLine(tail.Describe());
        output.WriteLine(BudgetPlacement("tail", tail));

        // **The rate first.** NFR-001 is a claim about a sustained 100 ev/s, and
        // a breakdown taken at another rate answers another question — so
        // nothing printed above is worth reading until this holds.
        conditions.RateWasMet.ShouldBeTrue(
            $"the run drove {conditions.AchievedRatePerSecond:F1} ev/s against a target of "
            + $"{IngestRunShape.TargetRatePerSecond:F0}; NFR-001 is a claim about that rate sustained, "
            + "and a verdict taken at another rate is not a verdict about NFR-001");

        typical.EveryRowStamped.ShouldBeTrue(
            $"{typical.RowsMissingStamps} rows arrived without the measurement stamps, so the "
            + "requirement span has neither a floor nor a ceiling; turn the switch on");

        typical.PartsCoverEveryRow.ShouldBeTrue(
            $"the median row leaves {typical.PerRowResidualMs:F3} ms between its span and its parts; "
            + "consecutive stamps cannot do that, so the stamps disagree with the timestamps that "
            + "bracket them");

        conditions.RowsMeasured.ShouldBe(
            IngestRunShape.MeasuredEvents,
            $"{conditions.RowsMeasured} of {IngestRunShape.MeasuredEvents} events reached the store, so "
            + "the interval above is a percentile over a population that never arrived");

        // **The clock, and it is load-bearing rather than tidy.** Both ends of
        // the requirement span include WriteMs, which subtracts a host-process
        // stamp from a container one. Where_the_ingest_span_goes refuses to
        // report that leg when the offset is the same size as it; without the
        // same refusal here BudgetPlacement can print "above the whole interval"
        // off a floor that is clock error. Phase 5 saw two negative floors.
        result.Verdict.IsEstablished.ShouldBeTrue(
            $"{result.Verdict.Reason} — the write leg subtracts a host-process stamp from a container "
            + $"one, and at {typical.WriteMs:F1} ms it is the same size as that disagreement, so the "
            + "interval it bounds cannot be placed against a budget");

        conditions.LoggingIsVerbose.ShouldBeFalse(
            $"the services are logging at '{conditions.LogLevel}', "
            + $"{(conditions.LogLevelWasChosen ? "chosen for this run" : "inherited from the appsettings")}; "
            + "Debug and Trace put the logging in front of the pipeline at ~80 ev/s against a target "
            + "of 100, and an inherited level has no measured throughput figure at all (#2133) — set "
            + "Logging__LogLevel__Default=Warning for a measurement run");
    }

    /// <summary>
    /// Which paced drive of this test process the figure came from.
    ///
    /// <para>
    /// <b>Printed because it is a condition of the measurement.</b> Phase 5 found
    /// the first paced drive of a boot slower than the second in 7 of 7
    /// within-boot pairs, by 3.2x to 143x, and found 2 of 10 first drives already
    /// fast — a tendency, not a law, and therefore exactly the sort of thing a
    /// reader has to be told rather than left to infer from a hundredfold swing
    /// between two runs of the same unchanged code. The warm-up before each drive
    /// is unpaced and does not touch it; reporting the position beats warming it
    /// away, which would hide which mode the number was taken in.
    /// </para>
    /// </summary>
    private static string DrivePosition(IngestSpanResult result) => string.Create(
        CultureInfo.InvariantCulture,
        $"paced drive since boot               : #{result.PacedDrivePosition} "
        + $"({(result.PacedDrivePosition <= 1 ? "cold — the first of this boot" : "warm")})");

    /// <summary>
    /// Where the budget falls against the requirement span's interval —
    /// **reported, never asserted**.
    ///
    /// <para>
    /// Three placements, and only two of them are answers. A budget below the
    /// whole interval means the span exceeds it on any reading; above the whole
    /// interval means it is inside on any reading; and inside the interval means
    /// this run cannot say, which is the state every figure on #1956 was quoted
    /// from without noticing.
    /// </para>
    /// </summary>
    private static string BudgetPlacement(string band, IngestAttribution attribution)
    {
        string placement = "inside the interval — this run cannot say whether NFR-001 was met or missed";

        if (P99BudgetMs < attribution.RequirementSpanFloorMs)
        {
            placement = "below the whole interval — the span exceeds the budget on any reading";
        }

        if (P99BudgetMs > attribution.RequirementSpanCeilingMs)
        {
            placement = "above the whole interval — the span is inside the budget on any reading";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
               NFR-001 budget, {band,-21}: {P99BudgetMs:F0} ms
               against the interval                : {attribution.RequirementSpanFloorMs:F1} to {attribution.RequirementSpanCeilingMs:F1} ms (width {attribution.RequirementSpanWidthMs:F1} ms)
               the budget falls                    : {placement}
             """);
    }
}
