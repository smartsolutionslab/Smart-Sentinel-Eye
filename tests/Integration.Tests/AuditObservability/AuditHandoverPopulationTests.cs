using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmartSentinelEye.AuditObservability.Infrastructure;
using SmartSentinelEye.Integration.Tests.Fixtures;
using Xunit.Abstractions;

namespace SmartSentinelEye.Integration.Tests.AuditObservability;

/// <summary>
/// **The assumption behind US3's reading, checked before anything divides by
/// it** (spec 109 US3, T101).
///
/// <para>
/// The audit listeners run <c>ProcessInParallelWithNativeAcks()</c>
/// (<c>WolverineDefaults.cs</c>), so a delivery stays <b>unacknowledged at the
/// broker for the whole handler</b>. If that holds, RabbitMQ's
/// <c>messages_unacknowledged</c> on the audit queues is exactly the population
/// of messages inside NFR-001's leg — handed over, not yet committed — and
/// Little's law reads the leg's mean time straight off the broker with no clock
/// crossing at all.
/// </para>
///
/// <para>
/// <b>That is an inference, drawn from ADR-0126's crash test rather than
/// observed.</b> ADR-0126's own correction came from a count that answered a
/// different question from the one asked of it, so this fact asks the count
/// directly: zero when the audit service is quiescent, non-zero while handlers
/// are mid-flight. If it does not behave this way, every number downstream of it
/// is meaningless and US3 stops here.
/// </para>
///
/// <para>
/// Evidence comes from the broker itself. A refusal names the address and the
/// status it received rather than reporting a mean of zero samples — an
/// unreachable management API and an idle queue are indistinguishable in a
/// number, and only one of them is an answer.
/// </para>
/// </summary>
[Collection(AspireCollection.Name)]
public class AuditHandoverPopulationTests(AspireFixture aspire, ITestOutputHelper output)
{
    /// <summary>
    /// Queues are named <c>{moduleQueuePrefix}.{eventType.FullName}</c>, and the
    /// audit module's prefix is its context name. Read from the module rather
    /// than spelled here: <c>wolverine_audit</c> is the <i>outbox schema</i>, not
    /// the queue prefix, and the two are easy to confuse.
    /// </summary>
    private static readonly string AuditQueuePrefix = AuditObservabilityInfrastructureModule.ContextName + ".";

    /// <summary>How long to wait for the audit queues to fall idle.</summary>
    private static readonly TimeSpan QuiesceDeadline = TimeSpan.FromSeconds(90);

    /// <summary>Writers driving the burst, one variable each — see IngestRunShape.</summary>
    private const int BurstWriters = 50;

    /// <summary>
    /// Events per writer.
    ///
    /// <para>
    /// <b>Sized for the broker's stats interval, not for the pipeline.</b>
    /// RabbitMQ's management plugin refreshes queue totals on
    /// <c>collect_statistics_interval</c> — five seconds by default — so a burst
    /// that starts and finishes inside one interval can be sampled a hundred
    /// times and return the same cached zero. Sixty per writer keeps the
    /// handlers loaded across several intervals, which is the difference between
    /// "the count stayed at zero" and "nobody looked while it was not".
    /// </para>
    /// </summary>
    private const int BurstEventsPerWriter = 60;

    /// <summary>
    /// How long sampling continues after the last write is issued.
    ///
    /// <para>
    /// The publishers finish before the audit side drains, and the population
    /// inside NFR-001's leg is largest during the drain. Stopping at the last
    /// <c>PUT</c> samples the wrong window.
    /// </para>
    /// </summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(20);

    /// <summary>The interval between broker samples.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    [Trait("Category", "Measurement")]
    [Fact]
    public async Task Unacknowledged_deliveries_on_the_audit_queues_are_zero_when_quiescent_and_not_when_handlers_run()
    {
        using HttpClient broker = await BrokerClientAsync();

        (int quiescent, string[] queues) = await WaitForQuiescenceAsync(broker);
        output.WriteLine($"audit queues seen: {(queues.Length == 0 ? "(none)" : string.Join(", ", queues))}");
        output.WriteLine($"quiescent  : messages_unacknowledged = {quiescent}");

        using HttpClient variables = await aspire.CreateAdminClientAsync("system-variables");

        string[] names = new string[BurstWriters];
        for (int writer = 0; writer < BurstWriters; writer++)
        {
            names[writer] = await IngestSpanMeasurement.DefineAsync(variables, CancellationToken.None);
        }

        using CancellationTokenSource sampling = new();
        Task<(int Peak, int Samples, int NonZero)> sampler = SampleAsync(broker, sampling.Token);

        await Task.WhenAll(names.Select(name => IngestSpanMeasurement.SetRepeatedlyAsync(
            variables, name, BurstEventsPerWriter, IngestSpanMeasurement.NoPacing, CancellationToken.None)));

        sampling.CancelAfter(DrainWindow);
        (int peak, int samples, int nonZero) = await sampler;

        output.WriteLine(
            $"mid-flight : {samples} samples over the drive and {DrainWindow.TotalSeconds:F0}s of drain, "
            + $"{nonZero} of them non-zero, peak messages_unacknowledged = {peak}");

        await VariableRequests.ArchiveAllAsync(variables, names, CancellationToken.None);

        quiescent.ShouldBe(
            0,
            $"with the audit service idle every delivery has been settled, so {AuditQueuePrefix}* should "
            + $"hold nothing unacknowledged; {quiescent} were still outstanding after "
            + $"{QuiesceDeadline.TotalSeconds:F0}s. A count that does not empty is not the in-flight "
            + "population, and Little's law over it would divide by the wrong thing");

        peak.ShouldBeGreaterThan(
            0,
            $"{BurstWriters * BurstEventsPerWriter} events were driven through the audit handlers, the "
            + $"broker was sampled {samples} times across the drive and the drain that followed it, and "
            + "messages_unacknowledged never rose above zero. Under native acks a delivery is held "
            + "unacknowledged for the whole handler, so a flat zero means the count is not measuring "
            + "what ADR-0126 describes, and Little's law over it would divide by the wrong thing");
    }

    /// <summary>
    /// Polls until the audit queues report nothing unacknowledged, and answers
    /// what it last saw together with the queue names it was reading.
    /// </summary>
    private static async Task<(int Unacknowledged, string[] Queues)> WaitForQuiescenceAsync(HttpClient broker)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + QuiesceDeadline;
        (int unacknowledged, string[] queues) = await ReadAsync(broker);

        while (unacknowledged > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            (unacknowledged, queues) = await ReadAsync(broker);
        }

        return (unacknowledged, queues);
    }

    /// <summary>Samples the count until cancelled, and answers the peak it saw.</summary>
    private static async Task<(int Peak, int Samples, int NonZero)> SampleAsync(HttpClient broker, CancellationToken cancellationToken)
    {
        int peak = 0;
        int samples = 0;
        int nonZero = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            (int unacknowledged, _) = await ReadAsync(broker);
            peak = Math.Max(peak, unacknowledged);
            samples++;
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

        return (peak, samples, nonZero);
    }

    /// <summary>
    /// Sums <c>messages_unacknowledged</c> across the audit queues.
    ///
    /// <para>
    /// A refusal throws naming the address and the status. Answering zero would
    /// be indistinguishable from an idle broker, and the whole reading is built
    /// on telling those two apart.
    /// </para>
    /// </summary>
    private static async Task<(int Unacknowledged, string[] Queues)> ReadAsync(HttpClient broker)
    {
        using HttpResponseMessage response = await broker.GetAsync("/api/queues");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the RabbitMQ management API at {broker.BaseAddress}api/queues answered "
                + $"{(int)response.StatusCode} {response.ReasonPhrase}. A sampler that reported zero "
                + "here would be reporting an unreachable broker as an empty queue.");
        }

        JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        int unacknowledged = 0;
        List<string> queues = [];
        foreach (JsonElement queue in payload.EnumerateArray())
        {
            string name = queue.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith(AuditQueuePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            queues.Add(name[(name.LastIndexOf('.') + 1)..]);
            if (queue.TryGetProperty("messages_unacknowledged", out JsonElement outstanding))
            {
                unacknowledged += outstanding.GetInt32();
            }
        }

        return (unacknowledged, [.. queues.Order()]);
    }

    /// <summary>
    /// The management plugin, with the credentials the AppHost parameterised
    /// rather than guessed: a 401 here reads exactly like "no queues".
    /// </summary>
    private async Task<HttpClient> BrokerClientAsync()
    {
        Uri management = aspire.App.GetEndpoint("rabbitmq", "management");
        string connection = await aspire.App.GetConnectionStringAsync("rabbitmq") ?? "";
        string userInfo = new Uri(connection).UserInfo;

        HttpClient client = new() { BaseAddress = management };
        client.DefaultRequestHeaders.Authorization = new(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(userInfo))));

        return client;
    }
}
