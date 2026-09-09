using System.Globalization;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// **Where the handover falls, asked of the broker rather than inferred** (spec
/// 109 US3, T102).
///
/// <para>
/// NFR-001's leg is "RabbitMQ deliver-ack to audit row committed". No timestamp
/// pair spans it: <c>received_at - occurred_at</c> starts at an aggregate
/// mutation in another bounded context, so it is a <i>superset</i>, and the
/// requirement's own span is known only as an interval
/// (<see cref="IngestAttribution.RequirementSpanWidthMs"/>) whose width is the
/// cost of having no publisher-side stamp.
/// </para>
///
/// <para>
/// <b>The broker can answer what the timestamps cannot.</b> Under
/// <c>ProcessInParallelWithNativeAcks()</c> a delivery stays unacknowledged from
/// the moment it is handed over until the handler finishes — the same window
/// NFR-001 names, plus the ack itself. So the mean unacknowledged population
/// divided by the drain rate is Little's law over exactly that leg, read from
/// one process's counters with no clock crossing at all, and it is an upper
/// bound on the requirement's span.
/// <see cref="AuditHandoverPopulationTests"/> checks that premise before this
/// fact divides by it.
/// </para>
///
/// <para>
/// <b>The number is bimodal, and one run is not the feature's answer.</b> Phase
/// 5 drove this seven times across fourteen boots: on the <i>first</i> paced
/// drive of a boot it read 237.8 / 794.1 / 402.2 / 306.6 ms — deep, four times
/// out of four, with 64 to 115 deliveries in flight; three drives in it read
/// 16.8 / 11.7 / 51.8 ms. Phase 4b's own two runs, 10.1 and 19.6 ms, sat at the
/// bottom of the warm range and never sampled the cold half at all. So this
/// fact prints the drive position it was taken at, and the label below reads
/// <i>that run</i>: it is not a verdict on NFR-001 and it is not the finding.
/// </para>
///
/// <para>
/// <b>It reports; it asserts only that the run was fit to be read</b>: the
/// queues were empty before the drive, the events landed, the broker answered,
/// and the population was seen to move. Two readings follow from the number.
/// <b>Near zero</b> — the handover is effectively at handler entry, this run's
/// leg sits inside the budget, and what is slow end to end is queue wait
/// <i>before</i> delivery, which no budget in this repository covers.
/// <b>Deep</b> — the prefetch buffer holds deliveries unacknowledged while they
/// wait for a handler slot, and that wait is inside NFR-001's leg.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class AuditHandoverLegTests(AspireFixture aspire, ITestOutputHelper output)
{
    /// <summary>Concurrent writers, one variable each — see IngestRunShape.</summary>
    private const int Writers = 50;

    /// <summary>
    /// Events driven through the leg.
    ///
    /// <para>
    /// <b>Sized for the broker's stats interval, not for the pipeline.</b>
    /// RabbitMQ's management plugin refreshes queue totals on
    /// <c>collect_statistics_interval</c> — five seconds by default — so a run
    /// short enough to finish inside a couple of intervals can be sampled a
    /// hundred times and return the same cached zero. Three thousand paced to
    /// 100 ev/s is half a minute of drive, which is several refreshes; the same
    /// window <see cref="AuditHandoverPopulationTests"/> established the premise
    /// over.
    /// </para>
    /// </summary>
    private const int Events = 3_000;

    private const int EventsPerWriter = Events / Writers;

    /// <summary>NFR-001's budget, placed against the implied leg — never asserted.</summary>
    private const double BudgetMs = 50;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan QuiesceDeadline = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromMinutes(3);

    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Audit_handover_leg_implied_by_the_broker_population_at_100_events_per_second()
    {
        CancellationToken cancellationToken = CancellationToken.None;

        using HttpClient broker = await AuditQueueProbe.ClientAsync(aspire, cancellationToken);
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        // Quiescent first, so the window below holds this run's population and
        // nobody else's — a residue from a neighbouring fact would inflate L and
        // be indistinguishable from a deep buffer.
        int quiescent = await AuditQueueProbe.WaitForQuiescenceAsync(broker, QuiesceDeadline, cancellationToken);

        string warmName = await IngestSpanMeasurement.DefineAsync(variables, cancellationToken);
        string[] names = new string[Writers];
        for (int writer = 0; writer < Writers; writer++)
        {
            names[writer] = await IngestSpanMeasurement.DefineAsync(variables, cancellationToken);
        }

        await IngestSpanMeasurement.SetRepeatedlyAsync(
            variables, warmName, IngestRunShape.WarmupEvents, IngestSpanMeasurement.NoPacing, cancellationToken);

        HandoverRun run = new(broker, variables, context, names);
        HandoverReading reading = await MeasureAsync(run, cancellationToken);

        output.WriteLine(Describe(quiescent, reading));

        await VariableRequests.ArchiveAllAsync(variables, [warmName, .. names], cancellationToken);

        // **The queues were empty before the drive — asserted, not merely
        // printed.** A neighbouring Measurement fact that left deliveries
        // unacked inflates L, the quiesce deadline expires quietly, and this run
        // reports "DEEP — the wait is inside NFR-001's leg" off somebody else's
        // backlog. A false alarm on the requirement this spec exists to
        // re-measure is the most expensive wrong answer available here, and
        // phase 5's four genuine deep readings are what make it expensive.
        quiescent.ShouldBe(
            0,
            $"{quiescent} deliveries were still unacknowledged on {AuditQueueProbe.QueuePrefix}* after "
            + $"{QuiesceDeadline.TotalSeconds:F0}s of waiting, so the population sampled below is not "
            + "this run's, and neither is the leg implied from it");

        reading.Landed.ShouldBe(
            Events,
            $"the leg can only be read over events that reached the store; {reading.Landed} of {Events} "
            + $"arrived within {DrainDeadline.TotalSeconds:F0}s, so the drain rate below divides by a "
            + "window that never closed");

        reading.NonZeroSamples.ShouldBeGreaterThan(
            0,
            $"{Events} events were driven through the audit handlers and the broker was sampled "
            + $"{reading.Samples} times across the drive and the drain, and messages_unacknowledged never "
            + "rose above zero. That is the cached-zero reading the management plugin returns when "
            + "nobody looked while the population was up, not an observation that the leg is empty");
    }

    /// <summary>
    /// Drives the paced load, samples the broker throughout, and closes the
    /// window when the last row lands.
    ///
    /// <para>
    /// <b>The window covers the drive and the drain, and both figures are taken
    /// over the same window.</b> Little's law relates a time-average population
    /// to the throughput it was averaged against; an L from the drive and a λ
    /// from the whole run would be two windows and one division.
    /// </para>
    /// </summary>
    private static async Task<HandoverReading> MeasureAsync(HandoverRun run, CancellationToken cancellationToken)
    {
        using CancellationTokenSource sampling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<(long Total, int Samples, int NonZero, int Peak)> sampler = SampleAsync(run.Broker, sampling.Token);

        Func<Task> pace = IngestSpanMeasurement.PaceTo(IngestRunShape.SlotIntervalMs, cancellationToken);
        int position = IngestSpanMeasurement.PacedDrivesSinceBoot;

        DateTimeOffset started = DateTimeOffset.UtcNow;
        string[] identifiers = await Task.WhenAll(run.Names.Select(name =>
            IngestSpanMeasurement.SetRepeatedlyAsync(run.Variables, name, EventsPerWriter, pace, cancellationToken)));

        int landed = await WaitForRowsAsync(run.Context, identifiers, cancellationToken);
        TimeSpan window = DateTimeOffset.UtcNow - started;

        await sampling.CancelAsync();
        (long total, int samples, int nonZero, int peak) = await sampler;

        return new HandoverReading(
            Landed: landed,
            WindowSeconds: window.TotalSeconds,
            MeanUnacknowledged: samples == 0 ? 0 : (double)total / samples,
            PeakUnacknowledged: peak,
            Samples: samples,
            NonZeroSamples: nonZero,
            PacedDrivePosition: position);
    }

    /// <summary>
    /// Samples until cancelled, at a fixed interval so the plain mean of the
    /// samples is the time-average Little's law asks for. The read carries the
    /// sampling token, so a cancelled run has no request still outstanding.
    /// </summary>
    private static async Task<(long Total, int Samples, int NonZero, int Peak)> SampleAsync(
        HttpClient broker, CancellationToken cancellationToken)
    {
        long total = 0;
        int samples = 0;
        int nonZero = 0;
        int peak = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int unacknowledged = await AuditQueueProbe.UnacknowledgedAsync(broker, cancellationToken);
                total += unacknowledged;
                samples++;
                peak = Math.Max(peak, unacknowledged);
                nonZero += unacknowledged > 0 ? 1 : 0;

                await Task.Delay(SampleInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (total, samples, nonZero, peak);
    }

    private static async Task<int> WaitForRowsAsync(
        AuditObservabilityDbContext context, string[] identifiers, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DrainDeadline;
        int landed = await IngestSpanMeasurement.CountAsync(context, identifiers, cancellationToken);

        while (landed < Events && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            landed = await IngestSpanMeasurement.CountAsync(context, identifiers, cancellationToken);
        }

        return landed;
    }

    private static string Describe(int quiescent, HandoverReading reading) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         queues sampled                        : {AuditQueueProbe.QueuePrefix}*
         unacknowledged before the drive       : {quiescent}
         paced drive since boot                : #{reading.PacedDrivePosition} ({reading.Mode})
         events driven, paced                  : {Events} at {IngestRunShape.TargetRatePerSecond:F0} ev/s intended
         rows landed                           : {reading.Landed}
         window (drive + drain)                : {reading.WindowSeconds:F1} s
         achieved drain rate                   : {reading.DrainRatePerSecond:F1} ev/s
         broker samples                        : {reading.Samples} ({reading.NonZeroSamples} non-zero, peak {reading.PeakUnacknowledged})
         mean messages_unacknowledged (L)      : {reading.MeanUnacknowledged:F2}
         implied mean leg, L / λ (W)           : {reading.ImpliedLegMs:F1} ms
         NFR-001 budget                        : {BudgetMs:F0} ms
         this run reads                        : {Reading(reading)}
         """);

    /// <summary>
    /// The two outcomes T104 names, chosen on the implied leg rather than on the
    /// mean population — a mean of 60 means one thing at 100 ev/s and another at
    /// 3, and only the quotient is comparable with a budget in milliseconds.
    ///
    /// <para>
    /// <b>This labels the run, not the requirement.</b> Phase 5 got both labels
    /// out of the same unchanged code in one afternoon, so a single run's word
    /// here settles nothing.
    /// </para>
    /// </summary>
    private static string Reading(HandoverReading reading) => reading.ImpliedLegMs <= BudgetMs
        ? "NEAR ZERO for this run — the handover is at handler entry and this run's leg is inside the "
          + "budget; the seconds seen end to end are queue wait before delivery, which no budget in "
          + "this repository covers"
        : "DEEP for this run — the prefetch buffer held deliveries unacknowledged while they waited "
          + "for a handler slot, and that wait is inside NFR-001's leg; the next step would be a "
          + "per-row transport-receipt stamp, which spec 109 does not take";

    /// <summary>What one handover run drives against — bundled so the token stays last.</summary>
    private sealed record HandoverRun(
        HttpClient Broker,
        HttpClient Variables,
        AuditObservabilityDbContext Context,
        string[] Names);

    /// <summary>What one handover run produced, before anybody reads it.</summary>
    private sealed record HandoverReading(
        int Landed,
        double WindowSeconds,
        double MeanUnacknowledged,
        int PeakUnacknowledged,
        int Samples,
        int NonZeroSamples,
        int PacedDrivePosition)
    {
        /// <summary>λ — throughput over the same window the population was averaged across.</summary>
        public double DrainRatePerSecond => WindowSeconds <= 0 ? 0 : Landed / WindowSeconds;

        /// <summary>W = L / λ, in milliseconds.</summary>
        public double ImpliedLegMs =>
            DrainRatePerSecond <= 0 ? 0 : MeanUnacknowledged / DrainRatePerSecond * 1000;

        /// <summary>Whether this was the boot's first paced drive — see the class remarks.</summary>
        public string Mode => PacedDrivePosition <= 1 ? "cold — the first of this boot" : "warm";
    }
}
