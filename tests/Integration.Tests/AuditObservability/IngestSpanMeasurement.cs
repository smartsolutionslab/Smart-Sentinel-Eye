using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.AuditObservability.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>What one measurement run produced, before anybody asserts on it.</summary>
public sealed record IngestSpanResult(
    IngestAttribution Typical,
    IngestAttribution Tail,
    ClockOffset Offset,
    AttributionVerdict Verdict,
    IngestRunConditions Conditions,
    int PacedDrivePosition);

/// <summary>
/// The audit-ingest measurement run, extracted so that the fixture run and the
/// run-mode run are <b>the same code</b> (spec 054).
///
/// <para>
/// <b>This exists because the two runs are meant to be compared.</b> Two
/// implementations held in agreement by prose drift the moment one is edited, and
/// the drift is invisible in the output — two numbers in a table that look
/// comparable and are not. One implementation, reading one
/// <see cref="IngestRunShape"/>, makes that failure inexpressible rather than
/// unlikely.
/// </para>
///
/// <para>
/// <b>It asserts nothing.</b> The callers assert, because they report separately
/// and a refused run still has to say what it was refused for. What this returns
/// is the breakdown and the conditions it was taken under; judging them is the
/// caller's job.
/// </para>
///
/// <para>
/// It takes its client and its database context as arguments rather than reaching
/// for a fixture. That is the whole structural point: the fixture boots its own
/// stack, which is exactly what makes it not run mode.
/// </para>
/// </summary>
public static class IngestSpanMeasurement
{
    private static int pacedDrives;

    /// <summary>How long to wait for the last measured row to reach the store.</summary>
    internal static readonly TimeSpan IngestDeadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The prefix every variable a run defines carries, so that a sweep of the
    /// residue can be sure it is matching what a run creates rather than a
    /// spelling somebody copied (#2004).
    /// </summary>
    internal const string VariablePrefix = "nfr";

    /// <summary>
    /// The gate for runs that are not rate-controlled: the warm-up, and the
    /// historic NFR-001 run whose figures are compared against recorded ones and
    /// so must keep the shape they were recorded at.
    /// </summary>
    public static readonly Func<Task> NoPacing = () => Task.CompletedTask;

    /// <summary>
    /// How many paced drives this test process has started, counted so a run can
    /// print <b>which one it was</b>.
    ///
    /// <para>
    /// <b>Position is a condition of the measurement here, not trivia.</b>
    /// Phase 5 drove fourteen boots and found the first paced drive of a boot
    /// slower than the second in 7 of 7 within-boot pairs, by 3.2x to 143x — and
    /// found 2 of 10 first drives already fast, so it is a tendency and not a
    /// law. A reader comparing two intervals that differ hundredfold otherwise
    /// has nothing printed to attribute the difference to.
    /// </para>
    ///
    /// <para>
    /// Counted rather than warmed away. The warm-up before each drive is
    /// unpaced, so it does not exercise what the effect is about; making it
    /// paced would bury the mode the figure was taken in rather than report it.
    /// </para>
    /// </summary>
    public static int PacedDrivesSinceBoot => Volatile.Read(ref pacedDrives);

    /// <summary>
    /// A gate that admits one write per slot, drawn from <b>one counter shared by
    /// every writer</b>, so the rate belongs to the run rather than to each
    /// writer.
    ///
    /// <para>
    /// Slots are counted from a single stopwatch rather than slept between
    /// writes: a per-write delay accumulates each write's own service time and
    /// the run drifts steadily below the rate it claims to be holding.
    /// </para>
    /// </summary>
    public static Func<Task> PaceTo(double slotIntervalMs, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref pacedDrives);

        Stopwatch pacing = Stopwatch.StartNew();
        long issued = 0;

        return async () =>
        {
            long slot = Interlocked.Increment(ref issued) - 1;
            double dueMs = slot * slotIntervalMs;
            double waitMs = dueMs - pacing.Elapsed.TotalMilliseconds;

            if (waitMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
            }
        };
    }

    /// <summary>
    /// Drives the load, waits for it to land, and divides the span.
    ///
    /// <para>
    /// <paramref name="environment"/> and <paramref name="endpoint"/> are carried
    /// into the conditions rather than inferred, because the run genuinely cannot
    /// tell which stack answered — see <see cref="IngestRunConditions.Endpoint"/>.
    /// </para>
    /// </summary>
    public static async Task<IngestSpanResult> RunAsync(
        HttpClient variables,
        AuditObservabilityDbContext context,
        string environment,
        string endpoint,
        string logLevel,
        bool logLevelWasChosen,
        CancellationToken cancellationToken)
    {
        Ensure.That(variables).IsNotNull();
        Ensure.That(context).IsNotNull();

        string warmName = await DefineAsync(variables, cancellationToken);
        await SetRepeatedlyAsync(variables, warmName, IngestRunShape.WarmupEvents, NoPacing, cancellationToken);

        // **Concurrent writers, because one is not a load.** A single sequential
        // caller is capped by its own round trip — measured at ~15 ev/s, far
        // below the knee this requirement lives at. The writers take a variable
        // each: the version travels in an `If-Match`, so two callers on one
        // variable collide on optimistic concurrency rather than generate load.
        string[] measureNames = new string[IngestRunShape.Writers];
        for (int writer = 0; writer < IngestRunShape.Writers; writer++)
        {
            measureNames[writer] = await DefineAsync(variables, cancellationToken);
        }

        // **Paced to the rate, not driven flat out.** Every writer draws its slot
        // from one counter, so the pacing is global rather than per-writer.
        Func<Task> pace = PaceTo(IngestRunShape.SlotIntervalMs, cancellationToken);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        string[] measured = await Task.WhenAll(
            measureNames.Select(name =>
                SetRepeatedlyAsync(variables, name, IngestRunShape.EventsPerWriter, pace, cancellationToken)));
        TimeSpan drove = DateTimeOffset.UtcNow - started;

        int landed = await WaitForRowsAsync(context, measured, cancellationToken);
        double achieved = IngestRunShape.MeasuredEvents / drove.TotalSeconds;

        // **Both bands, because the requirement is a p99 and the median is not
        // it.** The tail band is the rows at or above the p99 of the total: each
        // row's parts sum to its own total exactly, so the band's parts still
        // divide the band's span rather than being three unrelated percentiles
        // added together.
        IngestAttribution typical = await AttributionAsync(context, measured, tailOnly: false, cancellationToken);
        IngestAttribution tail = await AttributionAsync(context, measured, tailOnly: true, cancellationToken);

        ClockOffset offset = await ClockOffsetProbe.MeasureBestOfAsync(
            context.Database.GetConnectionString()!, readings: 20, cancellationToken);

        AttributionVerdict verdict = AttributionVerdict.For(
            RelativeSkew.Between(offset, new ClockOffset(TimeSpan.Zero, TimeSpan.Zero)));

        IngestRunConditions conditions = new(
            Environment: environment,
            Endpoint: endpoint,
            IntendedRatePerSecond: IngestRunShape.TargetRatePerSecond,
            AchievedRatePerSecond: achieved,
            LogLevel: logLevel,
            LogLevelWasChosen: logLevelWasChosen,
            MeasurementSwitchOn: typical.RowsMeasured > 0 && typical.RowsMissingStamps == 0,
            RowsMeasured: landed,
            RowsMissingStamps: typical.RowsMissingStamps);

        // **The run takes its own variables away again** (#2004). Fifty-one per
        // run, kept forever by a run-mode stack, and 94% of that table was this.
        //
        // After every query above, so nothing measured here is measured with
        // these writes in flight. They are 51 more events on top of the 1100 the
        // run just published — under 5%, and none of them inside the window any
        // percentile is taken over.
        //
        // Archiving rather than deleting because archiving is the only removal
        // the domain offers, and since #2015 it is enough: an archived variable
        // leaves the listing an operator sees.
        //
        // **A run that throws still leaves its variables behind.** Wrapping the
        // drive to catch that would re-indent the whole body and mix a refactor
        // into a fix; the residue this issue is about is the one successful runs
        // deposit, over and over.
        await VariableRequests.ArchiveAllAsync(variables, [warmName, .. measureNames], cancellationToken);

        return new IngestSpanResult(typical, tail, offset, verdict, conditions, PacedDrivesSinceBoot);
    }

    /// <summary>
    /// How many of a run's rows carry the measurement stamps, which is how a run
    /// reports the switch state it <b>ran under</b> rather than the one somebody
    /// meant to set (spec 053).
    /// </summary>
    internal static async Task<int> StampedCountAsync(AuditObservabilityDbContext context, string[] identifiers, CancellationToken cancellationToken)
    {
        List<long> counted = await context.Database
            .SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM audit_events "
                + "WHERE event_kind = 'SystemVariableValueChangedV1' AND resource_identifier = ANY({0}) "
                + "AND handler_entered_at IS NOT NULL",
                (object)identifiers)
            .ToListAsync(cancellationToken);

        return (int)counted[0];
    }

    internal static async Task<string> DefineAsync(HttpClient variables, CancellationToken cancellationToken)
    {
        string name = $"{VariablePrefix}{Guid.NewGuid():N}"[..16];

        // Deliberately no initialValue. Setting a variable to the value it
        // already holds is a no-op: it answers 200, raises nothing, and leaves
        // the version where it was — correct of the domain, and fatal here,
        // because the next write's If-Match would carry a version that never
        // came to exist. Defining without a value means every write below is a
        // real change.
        HttpResponseMessage defined = await variables.PostAsJsonAsync("/system-variables", new
        {
            name,
            type = "Number",
            truthyLabel = (string?)null,
            falsyLabel = (string?)null,
        }, cancellationToken);

        defined.EnsureSuccessStatusCode();
        return name;
    }

    /// <summary>
    /// Sets the variable <paramref name="times"/> times, tracking the version
    /// locally: each set bumps it by exactly one, so re-reading between writes
    /// would add an HTTP round trip per iteration and establish nothing. Returns
    /// the variable's identifier, which is what the audit row carries.
    ///
    /// <para>
    /// The starting version is <b>read</b> rather than assumed to be zero.
    /// Defining with an <c>initialValue</c> performs a value set of its own, so
    /// the variable is already at version 1 before this loop begins — assuming
    /// zero costs a 409 on the first write.
    /// </para>
    /// </summary>
    internal static async Task<string> SetRepeatedlyAsync(
        HttpClient variables, string name, int times, Func<Task> pace, CancellationToken cancellationToken)
    {
        int first = await ReadVersionAsync(variables, name, cancellationToken);

        for (int version = first; version < first + times; version++)
        {
            await pace();

            using HttpRequestMessage request = new(
                HttpMethod.Put,
                $"/system-variables/{name}/value?fabId=munich");
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
            request.Content = JsonContent.Create(new { value = (version + 1).ToString(CultureInfo.InvariantCulture) });

            using HttpResponseMessage response = await variables.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // **A 401 here is not an optimistic-concurrency failure**, and
                // saying so matters: the token is minted once and held for the
                // whole run, so a long drive can outlive it. Reporting version
                // numbers for an expired token sends the reader after If-Match,
                // which is the one thing that cannot be wrong here — the version
                // is tracked locally and increments by one.
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(
                        $"set #{version - first + 1} of {times} on '{name}' was refused with 401. "
                        + "The access token is minted once at the start of the run and held "
                        + "throughout; a drive longer than the token's lifetime outlives it. This is "
                        + "not an If-Match problem.");
                }

                string detail = await response.Content.ReadAsStringAsync(cancellationToken);
                int actual = await ReadVersionAsync(variables, name, cancellationToken);
                throw new InvalidOperationException(
                    $"set #{version - first + 1} of {times} on '{name}' sent If-Match \"{version}\" "
                    + $"and got {(int)response.StatusCode}; the variable now reads version {actual}. {detail}");
            }
        }

        return await ReadIdentifierAsync(variables, name, cancellationToken);
    }

    internal static async Task<string> ReadIdentifierAsync(HttpClient variables, string name, CancellationToken cancellationToken) =>
        (await ReadAsync(variables, name, cancellationToken)).GetProperty("variableIdentifier").GetString()!;

    internal static async Task<int> ReadVersionAsync(HttpClient variables, string name, CancellationToken cancellationToken) =>
        (await ReadAsync(variables, name, cancellationToken)).GetProperty("version").GetInt32();

    internal static async Task<JsonElement> ReadAsync(HttpClient variables, string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage read = await variables.GetAsync($"/system-variables/{name}?fabId=munich", cancellationToken);
        read.EnsureSuccessStatusCode();

        return await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    internal static async Task<int> WaitForRowsAsync(AuditObservabilityDbContext context, string[] identifiers, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + IngestDeadline;
        int landed = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            landed = await CountAsync(context, identifiers, cancellationToken);
            if (landed >= IngestRunShape.MeasuredEvents)
            {
                return landed;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return landed;
    }

    internal static async Task<int> CountAsync(AuditObservabilityDbContext context, string[] identifiers, CancellationToken cancellationToken)
    {
        List<long> counted = await context.Database
            .SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM audit_events "
                + "WHERE event_kind = 'SystemVariableValueChangedV1' AND resource_identifier = ANY({0})",
                (object)identifiers)
            .ToListAsync(cancellationToken);

        return (int)counted[0];
    }

    /// <summary>
    /// The span divided, read in the same query that produces the total.
    ///
    /// <para>
    /// <b>One query, one row per event, so the parts cannot drift from the
    /// figure they divide.</b> Medians rather than means throughout: a single
    /// stalled event moves a mean enough to invent a part that is not there,
    /// and the percentiles beside it are already order statistics.
    /// </para>
    ///
    /// <para>
    /// Rows without the measurement stamps are counted rather than filtered
    /// away — a run that quietly measured nine hundred of a thousand events
    /// would report the nine hundred as though they were the population.
    /// </para>
    /// </summary>
    internal static async Task<IngestAttribution> AttributionAsync(
        AuditObservabilityDbContext context, string[] identifiers, bool tailOnly, CancellationToken cancellationToken)
    {
        // The tail band is "rows at or above the p99 of the total". Selecting rows
        // rather than taking the p99 of each part separately is the whole point:
        // three independent p99s belong to three different events and adding them
        // divides nothing. Every row's parts sum to that row's own total, so a
        // band of rows still has a span to divide.
        object[] arguments = [identifiers, tailOnly];

        List<double> parts = await context.Database
            .SqlQueryRaw<double>(
                "WITH samples AS ("
                // **No COALESCE to zero.** An unstamped row has no parts, and
                // giving it zeros while keeping its real total drags every median
                // toward zero and manufactures a remainder that looks like a
                // pipeline property. The percentiles below are filtered to
                // stamped rows; the counts are not, so a partly-stamped run is
                // still visible rather than silently narrowed to what worked.
                + "SELECT "
                + "EXTRACT(EPOCH FROM (received_at - occurred_at)) * 1000 AS total, "
                + "EXTRACT(EPOCH FROM (handler_entered_at - occurred_at)) * 1000 AS before_handler, "
                + "EXTRACT(EPOCH FROM (received_at - handler_entered_at)) * 1000 AS in_handler, "
                + "EXTRACT(EPOCH FROM (written_at - received_at)) * 1000 AS write_leg, "
                + "(handler_entered_at IS NULL OR written_at IS NULL) AS stamps_missing "
                + "FROM audit_events "
                + "WHERE event_kind = 'SystemVariableValueChangedV1' AND resource_identifier = ANY({0})"
                + "), cut AS ("
                + "SELECT percentile_cont(0.99) WITHIN GROUP (ORDER BY total) AS threshold FROM samples"
                // Every percentile over the same population — stamped rows — so
                // the parts and the total they divide describe one set of events.
                // COALESCEd on the outside, not the inside: with no stamped rows
                // at all these are NULL, and a zero that `EveryRowStamped` will
                // immediately contradict beats a mapping exception.
                + ") SELECT unnest(ARRAY["
                + "COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY total) "
                + "FILTER (WHERE NOT stamps_missing), 0), "
                + "COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY before_handler) "
                + "FILTER (WHERE NOT stamps_missing), 0), "
                + "COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY in_handler) "
                + "FILTER (WHERE NOT stamps_missing), 0), "
                + "COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY write_leg) "
                + "FILTER (WHERE NOT stamps_missing), 0), "
                + "count(*)::float8, "
                + "count(*) FILTER (WHERE stamps_missing)::float8, "
                + "COALESCE(percentile_cont(0.50) WITHIN GROUP "
                + "(ORDER BY total - before_handler - in_handler) "
                + "FILTER (WHERE NOT stamps_missing), 0)]) "
                + "AS \"Value\" "
                + "FROM samples, cut WHERE (NOT {1}) OR samples.total >= cut.threshold",
                arguments)
            .ToListAsync(cancellationToken);

        return new IngestAttribution(
            TotalMs: parts[0],
            BeforeHandlerMs: parts[1],
            InHandlerMs: parts[2],
            WriteMs: parts[3],
            RowsMeasured: (int)parts[4],
            RowsMissingStamps: (int)parts[5],
            PerRowResidualMs: parts[6]);
    }

    internal static async Task<(double P50, double P99, double Max)> PercentilesAsync(
        AuditObservabilityDbContext context,
        string[] identifiers, CancellationToken cancellationToken)
    {
        List<double> percentiles = await context.Database
            .SqlQueryRaw<double>(
                "SELECT unnest(ARRAY["
                + "percentile_cont(0.50) WITHIN GROUP (ORDER BY delta), "
                + "percentile_cont(0.99) WITHIN GROUP (ORDER BY delta), "
                + "max(delta)]) AS \"Value\" FROM ("
                + "SELECT EXTRACT(EPOCH FROM (received_at - occurred_at)) * 1000 AS delta "
                + "FROM audit_events "
                + "WHERE event_kind = 'SystemVariableValueChangedV1' AND resource_identifier = ANY({0})"
                + ") samples",
                (object)identifiers)
            .ToListAsync(cancellationToken);

        return (percentiles[0], percentiles[1], percentiles[2]);
    }
}
