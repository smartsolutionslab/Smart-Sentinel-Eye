using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// The historic audit-ingest span, kept for comparability and nothing else
/// (spec 109 US2).
///
/// <para>
/// Split out of <see cref="NFR001_AuditIngestLatencyTests"/> so that the file
/// holding the two paced facts stays readable: this one is mostly a record of
/// how the figure got where it is, and that record is worth keeping intact
/// rather than trimming to fit beside them.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class AuditIngestHistoricSpanTests(AspireFixture aspire, ITestOutputHelper output)
{
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
    /// already removed.
    /// </para>
    ///
    /// <para>
    /// <b>Every figure above, and the conclusion that used to close this note —
    /// "what is open in 1956 is no longer code" — was read off an instrument
    /// wrong at both ends</b> (spec 109). Wrong rate: this run is one sequential
    /// writer, whose rate is a property of when it runs rather than of the
    /// pipeline: 45–58 ev/s measured cold at phase 4a, 80–95 ev/s when it runs
    /// third in a boot. Neither is the 100 ev/s NFR-001 names, and a shape whose
    /// rate depends on its position in the run cannot carry a verdict about a
    /// sustained rate at all. Wrong leg: <c>received_at - occurred_at</c> is a
    /// <i>superset</i> of "deliver-ack → row committed", starting at an
    /// aggregate mutation in another bounded context, so every p99 quoted on
    /// #1956 is the <b>ceiling</b> of an interval whose floor nobody reported
    /// (<see cref="IngestAttribution.RequirementSpanWidthMs"/>).
    /// </para>
    ///
    /// <para>
    /// <b>So this fact no longer returns a verdict on NFR-001, and its shape is
    /// why it still exists.</b> One sequential writer, <c>NoPacing</c> — kept
    /// unchanged, because that shape is the only thing that makes its figure
    /// comparable with the ones already quoted on the issue. It reports the
    /// end-to-end span and the rate it achieved, and nothing more. The verdict
    /// question is asked at the rate the requirement names, and answered as an
    /// interval, by
    /// <see cref="NFR001_AuditIngestLatencyTests.Requirement_span_at_100_events_per_second_is_an_interval_not_a_verdict"/>.
    /// </para>
    /// </summary>
    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Ingest_span_from_publish_to_row_at_the_unpaced_historic_shape()
    {
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        // Two variables, not one. The warm-up's rows would otherwise sit in the
        // same result set as the measured ones with no way to tell them apart
        // after the fact, and the percentile would include the cold path the
        // warm-up exists to exclude.
        string warmName = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);
        string measureName = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);

        await IngestSpanMeasurement.SetRepeatedlyAsync(variables, warmName, IngestRunShape.WarmupEvents, IngestSpanMeasurement.NoPacing, CancellationToken.None);

        // **The rate this shape actually achieves, measured rather than
        // assumed.** It is the reason the figure below is not a verdict: a
        // sequential writer is capped by its own round trip, well under the
        // 100 ev/s NFR-001 is a claim about.
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string measureIdentifier = await IngestSpanMeasurement.SetRepeatedlyAsync(variables, measureName, IngestRunShape.MeasuredEvents, IngestSpanMeasurement.NoPacing, CancellationToken.None);
        TimeSpan drove = DateTimeOffset.UtcNow - started;

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
        output.WriteLine(
            $"achieved rate: {IngestRunShape.MeasuredEvents / drove.TotalSeconds:F1} ev/s "
            + $"(one sequential writer, unpaced) against the {IngestRunShape.TargetRatePerSecond:F0} ev/s "
            + "NFR-001 names — so this span is not a verdict on it");
        output.WriteLine(
            "the span above is occurred → received, a superset of NFR-001's deliver-ack → commit; "
            + "for the requirement's own interval see "
            + nameof(NFR001_AuditIngestLatencyTests.Requirement_span_at_100_events_per_second_is_an_interval_not_a_verdict));

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
    }
}
