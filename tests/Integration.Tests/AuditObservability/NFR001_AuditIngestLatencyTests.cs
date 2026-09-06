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
    /// spelling.
    /// </para>
    /// </summary>
    private static string ServiceLogLevel =>
        ChosenServiceLogLevel ?? "Information (from appsettings)";

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

    /// <summary>Whether the level above was chosen for this run rather than inherited.</summary>
    private static bool ServiceLogLevelWasChosen => ChosenServiceLogLevel is not null;

    /// <summary>How long to wait for the last measured row to reach the store.</summary>

    /// <summary>
    /// <b>Excluded from CI</b> by <c>Category!=Measurement</c> in `ci.yml`, which
    /// deviates from T072's checkpoint that "NFR-001 + NFR-002 land in CI".
    ///
    /// <para>
    /// It is excluded because it does not pass, and the budget is deliberately
    /// left at the NFR's 50 ms rather than tuned to whatever the fixture
    /// produces — a passing number obtained by moving the line would say the
    /// requirement is met when it is not. Measured on the fixture: 1 000 events
    /// at roughly 20 ev/s gave <b>p50 4 800 ms, p99 9 469 ms, max 9 586 ms</b>,
    /// and 100 events gave p50 4 624 ms, max 5 045 ms. Latency grows through the
    /// run, so the consumer is draining slower than the writes arrive.
    /// </para>
    ///
    /// <para>
    /// <b>Run mode under load has since been measured (2026-08-28), and the gap
    /// is not a fixture artefact.</b> Driving the same generator against the
    /// run-mode stack at sustained rates, each rate run twice: 24 ev/s → p50
    /// 31 ms / p99 142 ms; 48 ev/s → p50 37 ms / p99 258–280 ms; 68 ev/s → p50
    /// 52 ms / p99 342 ms; 86–95 ev/s → p50 3 066–4 936 ms / p99 6 350–6 730 ms;
    /// 158 ev/s → p50 9 870 ms / p99 14 221 ms. The 50 ms p99 is missed at every
    /// rate measured, near-idle included.
    /// </para>
    ///
    /// <para>
    /// <b>The delay is on the consume side, not the flush cadence.</b> Sampled
    /// through a 158 ev/s burst the publisher's
    /// <c>wolverine_outgoing_envelopes</c> held 0 rows at every sample while the
    /// audit queue on RabbitMQ backed up to 468 then 643. The consumer's ceiling
    /// is ~100 rows/s for that queue — exactly the rate NFR-001 names, with no
    /// headroom, which is why latency is stable to ~68 ev/s and collapses by ~86.
    /// Per message the audit side does a durable-inbox write plus the audit row's
    /// own <c>SaveAsync</c>, one transaction per row, on a single listener.
    /// </para>
    ///
    /// <para>
    /// <b>Parallel listeners were then taken (ADR-0124) and NFR-001 is still not
    /// met.</b> Four listeners per audit queue moved peak drain from ~100 to
    /// ~270 rows/s and the knee from ~75 ev/s to past 110: at ~30 ev/s p99 58 ms,
    /// at ~50–58 ev/s p99 62 ms, at ~85–115 ev/s p99 214–420 ms typical (two of
    /// six runs at ~100 ev/s spiked to 2 119 / 3 786 ms). So 100 ev/s is
    /// survivable and draining rather than collapsing to six or seven seconds,
    /// and the gap to the budget is ~5× where it was ~130×.
    /// </para>
    ///
    /// <para>
    /// <b>The second lever followed (ADR-0126): the audit listeners settle each
    /// delivery at the broker (<c>Mode=NativeAck</c>) rather than through the
    /// durable inbox.</b> Six runs at 99–113 ev/s gave p50 26–35 ms and p99
    /// 85–236 ms, against 29–63 ms / 283–333 ms for the same rates before — no
    /// overlap on p99. Not a durability trade: killing the audit service outright
    /// mid-burst put all 640 in-flight events back on the queue and every one was
    /// audited on restart.
    /// </para>
    ///
    /// <para>
    /// It does <b>not</b> stop Postgres being written per message, which an
    /// earlier version of this note claimed. The incoming-envelopes table still
    /// gains one <c>Handled</c> tombstone per event; what the mode removes is
    /// whatever the durable inbox does beyond that, and ADR-0126 does not
    /// quantify it.
    /// </para>
    ///
    /// <para>
    /// The budget therefore stays at 50 ms and this test stays excluded — the
    /// best p99 observed is 85 ms.
    /// </para>
    ///
    /// <para>
    /// <b>The third lever, batching audit writes, was built, measured and not
    /// adopted (ADR-0127).</b> At a sustained 100 ev/s it made both percentiles
    /// worse — p50 36–44 ms against 23–30 ms — because a batch window short
    /// enough to respect a 50 ms budget collects roughly one message at that
    /// rate. It is a large win under backlog, which ADR-0124 and ADR-0126 had
    /// already removed. So what is open in 1956 is no longer code: production
    /// topology, where audit gets its own pod and database node and none of
    /// this measured that, or moving NFR-001 to what the pipeline does.
    /// </para>
    /// </summary>
    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Ingest_p99_from_publish_to_row_stays_under_50ms()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        // Two variables, not one. The warm-up's rows would otherwise sit in the
        // same result set as the measured ones with no way to tell them apart
        // after the fact, and the percentile would include the cold path the
        // warm-up exists to exclude.
        string warmName = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);
        string measureName = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);

        await IngestSpanMeasurement.SetRepeatedlyAsync(variables, warmName, IngestRunShape.WarmupEvents, IngestSpanMeasurement.NoPacing, CancellationToken.None);
        string measureIdentifier = await IngestSpanMeasurement.SetRepeatedlyAsync(variables, measureName, IngestRunShape.MeasuredEvents, IngestSpanMeasurement.NoPacing, CancellationToken.None);

        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        int landed = await IngestSpanMeasurement.WaitForRowsAsync(context, [measureIdentifier], CancellationToken.None);
        landed.ShouldBe(
            IngestRunShape.MeasuredEvents,
            "every measured event must reach the audit store before its latency can be read; "
            + $"{landed} of {IngestRunShape.MeasuredEvents} arrived within {IngestSpanMeasurement.IngestDeadline.TotalSeconds:F0}s");

        (double p50, double p99, double max) = await IngestSpanMeasurement.PercentilesAsync(context, [measureIdentifier], CancellationToken.None);

        output.WriteLine(
            $"audit ingest over {IngestRunShape.MeasuredEvents} events: "
            + $"p50 = {p50:F1} ms, p99 = {p99:F1} ms, max = {max:F1} ms");

        // **The apparatus' own cost, and the reason this line exists** (spec 053).
        // The switch is service-side configuration read at startup, so no single
        // run can measure both states — the cost is the difference between two
        // runs. Pairing those two by remembering which shell had the variable
        // exported is exactly how a figure gets attributed to the wrong
        // configuration, so each run states the switch state it actually ran
        // under, read off the rows it produced rather than off an intention.
        int stamped = await IngestSpanMeasurement.StampedCountAsync(context, [measureIdentifier], CancellationToken.None);
        output.WriteLine(
            $"measurement switch: {(stamped > 0 ? "ON" : "OFF")} "
            + $"({stamped} of {landed} rows carry the stamps)");

        p99.ShouldBeLessThan(
            P99BudgetMs,
            $"NFR-001 allows p99 ≤ {P99BudgetMs} ms from publish to row; "
            + $"observed p50 = {p50:F1} ms, p99 = {p99:F1} ms, max = {max:F1} ms");
    }

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
}
