using System.Globalization;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// **What the shipped logging configuration sustains** (spec 127, #2133).
///
/// <para>
/// ADR-0135 records throughput for three logging arms and the repository ships a
/// fourth. Its 2026-08-31 refinement measures <c>Default: Debug</c> at 60.0 /
/// 79.1 / 82.5 ev/s and <c>Default: Warning</c> at 169.8 / 173.7 / 244.4, and
/// notes that pinning EF's command category alone reaches only ~103. Spec 081
/// then shipped <c>Information</c> <i>plus</i> that pin — a pair none of those
/// figures describes, sitting somewhere between ~103 and ~244 against a 100 ev/s
/// target. This fact measures it.
/// </para>
///
/// <para>
/// <b>It reports a rate and returns no verdict.</b> NFR-001 is a p99 latency and
/// is another question, answered as an interval by
/// <see cref="NFR001_AuditIngestLatencyTests.Requirement_span_at_100_events_per_second_is_an_interval_not_a_verdict"/>.
/// A test that failed when the rate was low would be asserting the thing this
/// measurement exists to inform, and the 100 ev/s target is not this spec's to
/// move.
/// </para>
///
/// <para>
/// <b>The shape is ADR-0135's throughput shape, taken from
/// <see cref="IngestRunShape"/> rather than respelled</b>: fifty concurrent
/// writers, one variable each, <see cref="IngestSpanMeasurement.NoPacing"/>,
/// over <see cref="IngestRunShape.MeasuredEvents"/> events after a warm-up. That
/// is the row ADR-0135's stability table calls "50 writers, unpaced", which is
/// where its 244.4 came from. Achieved rate is events over the drive's own
/// duration — publish-side throughput, the quantity logging volume acts on.
/// </para>
///
/// <para>
/// <b>Three drives in one boot, reported separately and never averaged.</b> The
/// first run after machine churn on this box looks exactly like a regression,
/// and ADR-0135 found the first paced drive of a boot slower than the second in
/// 7 of 7 within-boot pairs. An average over that is a number describing neither
/// mode.
/// </para>
///
/// <para>
/// <b>The arm is read back off the services' own logs.</b> Which arm a run was
/// taken under otherwise rests on which shell launched it — and an environment
/// variable that never reached the service processes would make all three arms
/// agree, for the one reason that would look like a finding. So each drive
/// reports what severities its driver actually emitted, and whether EF's
/// <c>Executed DbCommand</c> appeared.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class IngestThroughputTests(AspireFixture aspire, ITestOutputHelper output)
{
    /// <summary>Drives per boot. Three, matching the repetition ADR-0135's refinement used.</summary>
    private const int Drives = 3;

    /// <summary>
    /// The service the load is driven through, and the one whose log is read
    /// back. It is the publisher whose SQL the command category silences, so it
    /// is where the arm is visible.
    /// </summary>
    private const string Driver = "system-variables";

    /// <summary>The opening of EF Core's <c>CommandExecuted</c> message.</summary>
    private const string SqlMarker = "Executed DbCommand";

    /// <summary>The console formatter's severity prefixes, in level order.</summary>
    private static readonly string[] Severities = ["trce:", "dbug:", "info:", "warn:", "fail:", "crit:"];

    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Publish_throughput_at_the_unpaced_fifty_writer_shape()
    {
        LoggingArm arm = LoggingArm.Read();
        output.WriteLine(arm.Describe());
        output.WriteLine(
            $"shape: {IngestRunShape.Writers} writers, unpaced, {IngestRunShape.MeasuredEvents} events "
            + $"after {IngestRunShape.WarmupEvents} warm-up — ADR-0135's \"50 writers, unpaced\" row");

        using HttpClient variables = await aspire.CreateAdminClientAsync(Driver);

        await using AuditObservabilityDbContext context =
            await aspire.CreateAuditObservabilityDbContextAsync();

        List<Drive> drives = [];

        for (int position = 1; position <= Drives; position++)
        {
            Drive drive = await DriveOnceAsync(variables, context, CancellationToken.None);
            drives.Add(drive);

            output.WriteLine(drive.Describe(position));
            output.WriteLine(LogEvidence());
        }

        // **The conditions after the figures, and both before anything that can
        // fail.** A refused run still has to say what it was refused for, and
        // these figures are the whole output — spec 053's guards behaved this way
        // and that is why their failures were informative rather than merely red.
        arm.IsAttributable.ShouldBeTrue(
            $"the run inherited part of its logging configuration ({arm.Describe()}), so the figures "
            + $"above belong to no named arm. Set {LoggingArm.DefaultKey} and {LoggingArm.CommandKey} "
            + "for the arm you mean to measure; an inherited level is whatever the appsettings pin on "
            + "the day, which is the drift ADR-0135's amendment records");

        drives
            .Where(drive => drive.Landed < IngestRunShape.MeasuredEvents)
            .ShouldBeEmpty(
                $"a drive's events did not all reach the audit store within "
                + $"{IngestSpanMeasurement.IngestDeadline.TotalMinutes:F0} minutes, so the drive after "
                + "it ran against a backlog and measured that instead: "
                + string.Join(", ", drives.Select(drive => $"{drive.Landed}/{IngestRunShape.MeasuredEvents}")));
    }

    /// <summary>
    /// One warm-up, one unpaced fifty-writer drive, and the drain that follows
    /// it — so the next drive does not start against this one's backlog.
    /// </summary>
    private static async Task<Drive> DriveOnceAsync(
        HttpClient variables, AuditObservabilityDbContext context, CancellationToken cancellationToken)
    {
        // A separate warm-up variable, as everywhere else here: the warm-up's
        // rows would otherwise sit in the same result set with no way to tell
        // them apart.
        string warmName = await IngestSpanMeasurement.DefineAsync(variables, cancellationToken);
        await IngestSpanMeasurement.SetRepeatedlyAsync(
            variables, warmName, IngestRunShape.WarmupEvents, IngestSpanMeasurement.NoPacing, cancellationToken);

        string[] names = new string[IngestRunShape.Writers];
        for (int writer = 0; writer < IngestRunShape.Writers; writer++)
        {
            names[writer] = await IngestSpanMeasurement.DefineAsync(variables, cancellationToken);
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        string[] measured = await Task.WhenAll(names.Select(name => IngestSpanMeasurement.SetRepeatedlyAsync(
            variables, name, IngestRunShape.EventsPerWriter, IngestSpanMeasurement.NoPacing, cancellationToken)));
        TimeSpan drove = DateTimeOffset.UtcNow - started;

        DateTimeOffset draining = DateTimeOffset.UtcNow;
        int landed = await IngestSpanMeasurement.WaitForRowsAsync(context, measured, cancellationToken);
        TimeSpan drained = DateTimeOffset.UtcNow - draining;

        // The run takes its own variables away again (#2004), after every read
        // above so nothing measured here is measured with these writes in flight.
        await VariableRequests.ArchiveAllAsync(variables, [warmName, .. names], cancellationToken);

        return new Drive(drove, drained, landed);
    }

    /// <summary>
    /// What the driver's own log says about the arm — **the fact, against the
    /// intention that launched the run**.
    ///
    /// <para>
    /// Counts rather than a verdict, because the tail is a bounded window and an
    /// absence in it is weaker evidence than a presence. What it settles is the
    /// question that would silently void every figure: whether the chosen levels
    /// reached the service processes at all.
    /// </para>
    /// </summary>
    private string LogEvidence()
    {
        string[] lines = aspire
            .RecentLogs(Driver, lines: 400)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string counted = string.Join(
            ", ",
            Severities.Select(severity => $"{severity[..4]} {Count(lines, severity)}"));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
               driver log, last {lines.Length,3} lines   : {counted}
               '{SqlMarker}'          : {Count(lines, SqlMarker)}
             """);
    }

    private static int Count(string[] lines, string marker) =>
        lines.Count(line => line.Contains(marker, StringComparison.Ordinal));

    /// <summary>One unpaced drive: what it achieved, and what it cost to drain.</summary>
    private sealed record Drive(TimeSpan Drove, TimeSpan Drained, int Landed)
    {
        public double AchievedRatePerSecond => IngestRunShape.MeasuredEvents / Drove.TotalSeconds;

        public string Describe(int position) => string.Create(
            CultureInfo.InvariantCulture,
            $"""
             drive #{position} ({(position <= 1 ? "cold — the first of this boot" : "warm")})
               achieved                            : {AchievedRatePerSecond:F1} ev/s
               drove                               : {Drove.TotalSeconds:F1} s for {IngestRunShape.MeasuredEvents} events
               drained                             : {Drained.TotalSeconds:F1} s, {Landed} rows landed
             """);
    }
}
