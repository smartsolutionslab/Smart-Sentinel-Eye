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
/// <b>It reports; it returns no verdict on NFR-001.</b> What it asserts is that
/// the run was fit to be read: the events landed, the broker answered, and the
/// population was seen to move. Two readings follow from the number, and the
/// output states which one this run supports:
/// </para>
///
/// <para>
/// <b>Near zero</b> — the handover is effectively at handler entry, the
/// requirement's span is the floor of the interval, NFR-001 holds on its own
/// leg, and what is slow is the queue wait <i>before</i> delivery, which no
/// budget in this repository covers. <b>Deep</b> — the prefetch buffer holds
/// messages unacknowledged while they wait for a handler slot, that wait is
/// inside NFR-001's leg, and the requirement is genuinely missed.
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
        using HttpClient broker = await AuditQueueProbe.ClientAsync(aspire);
        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        // Quiescent first, so the window below holds this run's population and
        // nobody else's — a residue from a neighbouring fact would inflate L and
        // be indistinguishable from a deep buffer.
        int quiescent = await AuditQueueProbe.WaitForQuiescenceAsync(broker, QuiesceDeadline, CancellationToken.None);

        string warmName = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);
        string[] names = new string[Writers];
        for (int writer = 0; writer < Writers; writer++)
        {
            names[writer] = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);
        }

        await IngestSpanMeasurement.SetRepeatedlyAsync(
            variables, warmName, IngestRunShape.WarmupEvents, IngestSpanMeasurement.NoPacing, CancellationToken.None);

        HandoverReading reading = await MeasureAsync(broker, variables, context, names);

        output.WriteLine(Describe(quiescent, reading));

        await VariableRequests.ArchiveAllAsync(variables, [warmName, .. names], CancellationToken.None);

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
    private static async Task<HandoverReading> MeasureAsync(
        HttpClient broker, HttpClient variables, AuditObservabilityDbContext context, string[] names)
    {
        using CancellationTokenSource sampling = new();
        Task<(long Total, int Samples, int NonZero, int Peak)> sampler = SampleAsync(broker, sampling.Token);

        Func<Task> pace = IngestSpanMeasurement.PaceTo(IngestRunShape.SlotIntervalMs, CancellationToken.None);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        string[] identifiers = await Task.WhenAll(names.Select(name =>
            IngestSpanMeasurement.SetRepeatedlyAsync(variables, name, EventsPerWriter, pace, CancellationToken.None)));

        int landed = await WaitForRowsAsync(context, identifiers);
        TimeSpan window = DateTimeOffset.UtcNow - started;

        await sampling.CancelAsync();
        (long total, int samples, int nonZero, int peak) = await sampler;

        return new HandoverReading(
            Landed: landed,
            WindowSeconds: window.TotalSeconds,
            MeanUnacknowledged: samples == 0 ? 0 : (double)total / samples,
            PeakUnacknowledged: peak,
            Samples: samples,
            NonZeroSamples: nonZero);
    }

    /// <summary>
    /// Samples until cancelled, at a fixed interval so the plain mean of the
    /// samples is the time-average Little's law asks for.
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
            int unacknowledged = await AuditQueueProbe.UnacknowledgedAsync(broker, CancellationToken.None);
            total += unacknowledged;
            samples++;
            peak = Math.Max(peak, unacknowledged);
            if (unacknowledged > 0)
            {
                nonZero++;
            }

            try
            {
                await Task.Delay(SampleInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (total, samples, nonZero, peak);
    }

    private static async Task<int> WaitForRowsAsync(AuditObservabilityDbContext context, string[] identifiers)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DrainDeadline;
        int landed = await IngestSpanMeasurement.CountAsync(context, identifiers, CancellationToken.None);

        while (landed < Events && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            landed = await IngestSpanMeasurement.CountAsync(context, identifiers, CancellationToken.None);
        }

        return landed;
    }

    private static string Describe(int quiescent, HandoverReading reading) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         queues sampled                        : {AuditQueueProbe.QueuePrefix}*
         unacknowledged before the drive       : {quiescent}
         events driven, paced                  : {Events} at {IngestRunShape.TargetRatePerSecond:F0} ev/s intended
         rows landed                           : {reading.Landed}
         window (drive + drain)                : {reading.WindowSeconds:F1} s
         achieved drain rate                   : {reading.DrainRatePerSecond:F1} ev/s
         broker samples                        : {reading.Samples} ({reading.NonZeroSamples} non-zero, peak {reading.PeakUnacknowledged})
         mean messages_unacknowledged (L)      : {reading.MeanUnacknowledged:F2}
         implied mean leg, L / λ (W)           : {reading.ImpliedLegMs:F1} ms
         NFR-001 budget                        : {BudgetMs:F0} ms
         the reading this run supports         : {Reading(reading)}
         """);

    /// <summary>
    /// The two outcomes T104 names, chosen on the implied leg rather than on the
    /// mean population — a mean of 60 means one thing at 100 ev/s and another at
    /// 3, and only the quotient is comparable with a budget in milliseconds.
    /// </summary>
    private static string Reading(HandoverReading reading) => reading.ImpliedLegMs <= BudgetMs
        ? "NEAR ZERO — the handover is at handler entry, the requirement's span is the floor of the "
          + "interval, and the seconds seen end to end are queue wait before delivery, which no budget "
          + "in this repository covers"
        : "DEEP — the prefetch buffer holds deliveries unacknowledged while they wait for a handler "
          + "slot, that wait is inside NFR-001's leg, and the requirement is missed on its own terms; "
          + "the next step would be a per-row transport-receipt stamp, which spec 109 does not take";

    /// <summary>What one handover run produced, before anybody reads it.</summary>
    private sealed record HandoverReading(
        int Landed,
        double WindowSeconds,
        double MeanUnacknowledged,
        int PeakUnacknowledged,
        int Samples,
        int NonZeroSamples)
    {
        /// <summary>λ — throughput over the same window the population was averaged across.</summary>
        public double DrainRatePerSecond => WindowSeconds <= 0 ? 0 : Landed / WindowSeconds;

        /// <summary>W = L / λ, in milliseconds.</summary>
        public double ImpliedLegMs =>
            DrainRatePerSecond <= 0 ? 0 : MeanUnacknowledged / DrainRatePerSecond * 1000;
    }
}
