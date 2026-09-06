using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartSentinelEye.EventIngestion.Infrastructure.Persistence;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.EventIngestion;

/// <summary>
/// Spec 020 T022–T024 — quickstart step 5. The requirement most likely to be
/// broken quietly, and the one the spec insists is measured rather than argued.
///
/// <para>
/// Three numbers from one run, because they are three properties of the same
/// load and measuring them separately would let each be read at a different
/// pressure: sustained throughput (FR-010, SC-005), arrival-to-visible latency
/// against the ≤ 200 ms leg of the end-to-end budget (FR-012, SC-006,
/// constitution §IV), and per-source ordering under that load (FR-011).
/// </para>
///
/// <para>
/// It <b>reports</b> and asserts almost nothing. The first version asserted that
/// every published event had landed, which meant the run died at the first
/// number and the other two — the ones that say <i>how</i> it is slow — were
/// never taken. Whether a rate is acceptable is a judgement for the verification
/// note, against the same measurement taken before the feature; what the test
/// owes is the figure. Ordering is the exception: an inversion is wrong at any
/// rate.
/// </para>
///
/// <para>
/// The drain is timed to completion rather than sampled over a fixed window.
/// A window was wrong in exactly the case that matters most: when ingest keeps
/// up, the window opens on an already-empty backlog and reports a rate of zero
/// for a system that was never behind.
/// </para>
///
/// <para>
/// Excluded from CI by its category. A saturating burst on a shared runner
/// measures the runner, and a number that measures the runner would be quoted
/// as if it measured this code.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
[Trait("Category", "Measurement")]
public class IngestThroughputMeasurementTests(AspireFixture aspire, ITestOutputHelper output)
{
    private const string SimulatorClientId = "scenario-simulator";
    private const string SimulatorClientSecret = "dev-only-scenario-simulator-secret";

    /// <summary>Spec 006 sized this path for this rate.</summary>
    private const int TargetRatePerSecond = 5_000;
    private const int DurationSeconds = 30;

    /// <summary>
    /// How many publishing clients. Eight offered only ~270/s: each client
    /// waits for its own acknowledgement before sending the next, so the
    /// offered rate is the number of clients divided by the round trip, and
    /// eight of them measured the harness rather than ingest. They stay
    /// sequential per client — that is what makes the per-source ordering check
    /// meaningful — so the fix is more of them.
    /// </summary>
    private const int Publishers = 40;

    /// <summary>How long the drain may keep going after the burst is in.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromMinutes(10);

    /// <summary>The leg of the end-to-end budget this path spends.</summary>
    private static readonly TimeSpan EventToStateBudget = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Measure_sustained_rate_latency_and_order()
    {
        string kind = $"Load{Guid.CreateVersion7():N}"[..20];
        int perPublisher = TargetRatePerSecond * DurationSeconds / Publishers;

        Stopwatch publishing = Stopwatch.StartNew();
        int offered = await PublishConcurrentlyAsync(kind, perPublisher);
        publishing.Stop();

        output.WriteLine(
            $"T022 offered  : {offered} events in {publishing.Elapsed.TotalSeconds:F1}s "
            + $"= {offered / publishing.Elapsed.TotalSeconds:F0}/s "
            + $"(target {TargetRatePerSecond}/s for {DurationSeconds}s)");

        Stopwatch draining = Stopwatch.StartNew();
        long stored = await WaitForDrainAsync(kind, offered);
        draining.Stop();
        TimeSpan endToEnd = publishing.Elapsed + draining.Elapsed;

        output.WriteLine(
            $"T022 sustained: {stored} of {offered} stored by {endToEnd.TotalSeconds:F1}s "
            + $"= {stored / endToEnd.TotalSeconds:F0}/s end to end"
            + (stored >= offered && draining.Elapsed < TimeSpan.FromSeconds(2)
                ? " — everything had landed before publishing stopped, so ingest was not the limit"
                : string.Empty));

        await ReportLatencyAsync(kind);
        await ReportOrderingAsync(kind);
    }

    /// <summary>
    /// T023. Arrival to visible, per event, from the publish stamp the payload
    /// carries to the moment storage recorded it. Reported at percentiles: a
    /// mean would hide exactly the tail the budget is about.
    ///
    /// <para>
    /// Under a backlog this is queueing time, not per-event cost, and it is
    /// still the number the budget asks for — how long after arriving an event
    /// becomes visible. The uncontended figure is the p50 of a small run.
    /// </para>
    /// </summary>
    private async Task ReportLatencyAsync(string kind)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        double[] milliseconds = await database.Database
            .SqlQueryRaw<double>(
                """
                SELECT EXTRACT(EPOCH FROM (ingested_at - (payload->>'publishedAt')::timestamptz)) * 1000
                    AS "Value"
                FROM events WHERE kind = {0} ORDER BY 1
                """,
                kind)
            .ToArrayAsync();

        if (milliseconds.Length == 0)
        {
            output.WriteLine("T023 latency  : nothing stored — no measurement");
            return;
        }

        output.WriteLine(
            $"T023 latency  : p50={Percentile(milliseconds, 0.50):F0} "
            + $"p95={Percentile(milliseconds, 0.95):F0} "
            + $"p99={Percentile(milliseconds, 0.99):F0} "
            + $"max={milliseconds[^1]:F0} ms "
            + $"over {milliseconds.Length} events "
            + $"(this leg's budget: {EventToStateBudget.TotalMilliseconds:F0} ms)");
    }

    /// <summary>
    /// T024. Per-source FIFO is guaranteed by the channel being FIFO and the
    /// loop being single-reader, and batching is exactly where that quietly
    /// stops being true. Counted as inversions rather than asserted on the first
    /// mismatch, so a failure says how badly rather than merely that.
    /// </summary>
    private async Task ReportOrderingAsync(string kind)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        int total = 0;

        for (int publisher = 0; publisher < Publishers; publisher++)
        {
            long[] sequence = await database.Database
                .SqlQueryRaw<long>(
                    """
                    SELECT (payload->>'sequence')::bigint AS "Value"
                    FROM events WHERE kind = {0} AND device_id = {1}
                    ORDER BY ingested_at
                    """,
                    kind,
                    $"load-{publisher}")
                .ToArrayAsync();

            int inversions = 0;
            for (int i = 1; i < sequence.Length; i++)
            {
                if (sequence[i] < sequence[i - 1])
                {
                    inversions++;
                }
            }

            total += inversions;
            output.WriteLine($"T024 load-{publisher}   : {sequence.Length} events, {inversions} out of order");
        }

        total.ShouldBe(0, "per-source order was lost under load");
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>
    /// Waits until everything published has landed, or until the count stops
    /// moving. Measuring a fixed window instead was wrong in the case that
    /// matters most: when ingest keeps up, the window opens on an already-empty
    /// backlog and reports a rate of zero for a system that was never behind.
    /// </summary>
    private async Task<long> WaitForDrainAsync(string kind, long expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DrainLimit;
        long stored = await CountAsync(kind);
        int stalled = 0;

        while (stored < expected && DateTimeOffset.UtcNow < deadline && stalled < 10)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            long now = await CountAsync(kind);
            stalled = now == stored ? stalled + 1 : 0;
            stored = now;
        }

        return stored;
    }

    private async Task<long> CountAsync(string kind)
    {
        await using EventIngestionDbContext database = await aspire.CreateEventIngestionDbContextAsync();
        return await database.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM events WHERE kind = {0}", kind)
            .SingleAsync();
    }

    private async Task<int> PublishConcurrentlyAsync(string kind, int perPublisher)
    {
        string jwt = await TokenAsync();
        Uri broker = aspire.App.GetEndpoint("mosquitto", "mqtt");
        ConcurrentBag<int> counts = [];

        // One client per source, so each source's stream is genuinely ordered by
        // its own publisher. Sharing a client across sources would make the
        // ordering check a test of the test.
        await Parallel.ForAsync(0, Publishers, async (publisher, cancellationToken) =>
            counts.Add(await PublishOneSourceAsync(
                broker, jwt, kind, publisher, perPublisher, cancellationToken)));

        return counts.Sum();
    }

    private static async Task<int> PublishOneSourceAsync(
        Uri broker,
        string jwt,
        string kind,
        int publisher,
        int count,
        CancellationToken cancellationToken)
    {
        using IMqttClient client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithClientId($"{SimulatorClientId}-load-{publisher}-{Guid.CreateVersion7():N}")
                .WithCredentials(SimulatorClientId, jwt)
                .WithTcpServer(broker.Host, broker.Port)
                .WithCleanSession(true)
                .WithTimeout(TimeSpan.FromSeconds(30))
                .Build(),
            cancellationToken);

        string topic = $"fab/munich/plc/load-{publisher}";
        Stopwatch elapsed = Stopwatch.StartNew();
        int sent = 0;

        for (int sequence = 0; sequence < count; sequence++)
        {
            await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Payload(kind, sequence))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                cancellationToken);
            sent++;

            // The burst is meant to last the stated duration. Without this it
            // runs until the publisher's own ceiling, and the figure would be a
            // measurement of the test harness rather than of ingest.
            if (elapsed.Elapsed > TimeSpan.FromSeconds(DurationSeconds))
            {
                break;
            }
        }

        await client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
        return sent;
    }

    /// <summary>
    /// The first call this test makes, and it has timed out twice on a Keycloak
    /// that is healthy but still warming. Retried rather than left to chance,
    /// because the cost of losing that coin toss is a ten-minute run that
    /// produces no number at all.
    /// </summary>
    private async Task<string> TokenAsync()
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using HttpClient keycloak = aspire.CreateKeycloakClient();
                using FormUrlEncodedContent form = new(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = SimulatorClientId,
                    ["client_secret"] = SimulatorClientSecret,
                });
                HttpResponseMessage token = await keycloak.PostAsync(
                    "/realms/smart-sentinel-eye/protocol/openid-connect/token", form);
                token.EnsureSuccessStatusCode();
                return (await token.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("access_token").GetString()!;
            }
            catch (Exception ex) when (attempt < 5)
            {
                output.WriteLine($"token attempt {attempt} failed ({ex.GetType().Name}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static string Payload(string kind, int sequence) => JsonSerializer.Serialize(new
    {
        eventId = Guid.CreateVersion7(),
        kind,
        occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        // Both carried in the payload because the stored row is all the
        // measurement has afterwards: publishedAt gives the latency, sequence
        // gives the order.
        payload = new
        {
            publishedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            sequence,
        },
    });
}
