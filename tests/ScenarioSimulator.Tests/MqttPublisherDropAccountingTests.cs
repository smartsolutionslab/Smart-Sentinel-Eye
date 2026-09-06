using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet.Exceptions;
using SmartSentinelEye.ScenarioSimulator.Configuration;
using SmartSentinelEye.ScenarioSimulator.Keycloak;
using SmartSentinelEye.ScenarioSimulator.Mqtt;
using SmartSentinelEye.ScenarioSimulator.Tests.Fakes;

namespace SmartSentinelEye.ScenarioSimulator.Tests;

/// <summary>
/// Spec 079 US2 (#1139) — what the simulator does with a sample it cannot send.
///
/// <para>
/// <b>Dropping is the chosen answer, and the counter is what makes it a choice
/// rather than a swallow.</b> <c>EnqueueAsync</c> buffered; MQTTnet 5 has no
/// managed client to buffer with, and buffering by hand would be worse than
/// dropping rather than merely more work — a replayed sample carries the
/// <c>occurredAt</c> it was generated with, so a minute of backlog would arrive
/// at a live wall as a burst of stale readings. A gap in a simulated timeline is
/// the honest picture of an outage. A <i>silent</i> gap is not.
/// </para>
/// </summary>
public class MqttPublisherDropAccountingTests
{
    private const string Topic = "fab/hamburg/plc/dev-1";

    /// <summary>US2-AC1.</summary>
    [Fact]
    public async Task A_sample_published_while_disconnected_is_counted_and_nothing_is_thrown()
    {
        await using PublisherUnderTest publisher = PublisherUnderTest.Create();

        await Should.NotThrowAsync(() => publisher.Publisher.PublishAsync(Topic, "{}", CancellationToken.None));

        publisher.Publisher.DroppedSamples.ShouldBe(
            1,
            "a sample the broker never saw must be counted. MqttClientNotConnectedException reaching "
            + "the timeline would stop the run; a drop nobody counted would leave the gap unexplained.");
        publisher.Client.Published.ShouldBeEmpty();
    }

    /// <summary>
    /// US2-AC2 — the whole point of the counter. The timeline emits many samples
    /// a second, so a line per drop would bury the one line that explains the
    /// gap. The connect that ends the outage reports it, once.
    /// </summary>
    [Fact]
    public async Task An_outage_is_reported_once_on_reconnect_not_once_per_dropped_sample()
    {
        await using PublisherUnderTest publisher = PublisherUnderTest.Create();

        // Held open rather than timed: the outage window is exactly the interval
        // between these two lines, so nothing here races the reconnect.
        publisher.Client.GateConnect();
        await publisher.Publisher.StartAsync(CancellationToken.None);

        for (int i = 0; i < 20; i++)
        {
            await publisher.Publisher.PublishAsync(Topic, "{}", CancellationToken.None);
        }

        publisher.Logger.Entries.ShouldBeEmpty(
            "nothing may be logged per dropped sample — 20 lines here is the noise this counter "
            + "exists to replace.");

        publisher.Client.AllowConnect();

        (await publisher.WaitForWarningAsync()).ShouldBeTrue(
            "the broker came back and nobody was told what the outage cost");

        List<string> warnings =
            [.. publisher.Logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];

        warnings.Count.ShouldBe(1, "exactly one warning per outage, not one per sample");
        warnings[0].ShouldContain("20 sample(s) dropped");
        publisher.Publisher.DroppedSamples.ShouldBe(0, "the count is cleared by the report it produced");
    }

    /// <summary>
    /// US2-AC3. The connection can drop between the <c>IsConnected</c> check and
    /// the publish. That is caught <b>by its own type</b> — the previous
    /// <c>catch (Exception ex) when (ex is not OperationCanceledException)</c> is
    /// narrowed, not widened, so any other publish failure now surfaces.
    /// </summary>
    [Fact]
    public async Task A_disconnect_racing_the_publish_is_counted_rather_than_swallowed_broadly()
    {
        await using PublisherUnderTest publisher = PublisherUnderTest.Create();

        await publisher.StartAndSettleAsync();

        publisher.Client.FailNextPublishAsNotConnected = true;

        await Should.NotThrowAsync(() => publisher.Publisher.PublishAsync(Topic, "{}", CancellationToken.None));

        publisher.Publisher.DroppedSamples.ShouldBe(
            1,
            $"a {nameof(MqttClientNotConnectedException)} raised by the race must be counted like any "
            + "other drop, not rethrown at the timeline and not logged per occurrence.");
    }

    /// <summary>
    /// <b>A refusal is not a connection, and here that costs more than a wrong
    /// log line.</b> MQTTnet 4's builder set
    /// <c>ThrowOnNonSuccessfulConnectResponse = true</c>, so a rejected CONNECT
    /// arrived as an exception; in MQTTnet 5 the property is gone and a refusal
    /// returns a result otherwise indistinguishable from success.
    ///
    /// <para>
    /// Taken for a connection it announces one and then runs
    /// <c>ReportDrops</c> — which <c>Interlocked.Exchange</c>es the counter to
    /// zero and writes the one warning an outage gets. So samples already lost
    /// are declared recovered by a connection that does not exist, and the loop
    /// then waits on a drop no connection could ever raise: every later sample
    /// counted, none ever reported, until the process is restarted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_connect_neither_announces_a_connection_nor_ends_the_outage()
    {
        await using PublisherUnderTest publisher = PublisherUnderTest.Create();
        publisher.Client.RefuseEveryConnect();

        for (int i = 0; i < 3; i++)
        {
            await publisher.Publisher.PublishAsync(Topic, "{}", CancellationToken.None);
        }

        publisher.Publisher.DroppedSamples.ShouldBe(3, "arrange: three samples were lost before the loop ran");

        await publisher.Publisher.StartAsync(CancellationToken.None);

        (await PublisherUnderTest.WaitUntilAsync(
            () => publisher.Client.ConnectAttempts >= 2 || publisher.Logger.Entries.Any(entry => Announces(entry.Message)))).ShouldBeTrue(
            "the loop neither retried nor said anything, so there is nothing to read");

        List<string> lines = [.. publisher.Logger.Entries.Select(entry => entry.Message)];

        lines.ShouldNotContain(
            line => line.Contains("publisher connected", StringComparison.Ordinal),
            "the broker answered NotAuthorized and the client is not connected. The simulator "
            + "registers no MQTT health check either, so an Information line claiming a connection "
            + "is the only signal there is, saying the opposite of what happened.");

        publisher.Publisher.DroppedSamples.ShouldBe(
            3,
            "ReportDrops must not run on a CONNECT that was refused. It exchanges the counter to zero "
            + "and writes the one warning an outage gets, so a refusal taken for a connection declares "
            + "over an outage that never began — and the loop then blocks for good on a drop that no "
            + "connection can raise.");
    }

    /// <summary>
    /// A QoS 1 publish whose PUBACK never arrives raises
    /// <c>MqttCommunicationTimedOutException</c>, not
    /// <see cref="MqttClientNotConnectedException"/>. A catch naming only the
    /// latter lets it out of <c>PublishAsync</c> and into
    /// <c>BilletTimelineHostedService</c>, a <c>BackgroundService</c> catching
    /// nothing but <c>OperationCanceledException</c> — so .NET's default
    /// <c>BackgroundServiceExceptionBehavior.StopHost</c> stops the host. A
    /// broker that answers slowly is this spec's own scenario, so it must not be
    /// the thing that ends the simulator run.
    /// </summary>
    [Fact]
    public async Task A_publish_whose_acknowledgement_never_arrives_is_counted_rather_than_stopping_the_host()
    {
        await using PublisherUnderTest publisher = PublisherUnderTest.Create();

        await publisher.StartAndSettleAsync();

        publisher.Client.FailNextPublishAsTimedOut = true;

        await Should.NotThrowAsync(
            () => publisher.Publisher.PublishAsync(Topic, "{}", CancellationToken.None),
            $"a {nameof(MqttCommunicationTimedOutException)} escaping here faults the timeline's "
            + "BackgroundService, and the default StopHost behaviour then stops the simulator.");

        publisher.Publisher.DroppedSamples.ShouldBe(
            1,
            "the broker never acknowledged the sample, so it is gone — a drop like any other, "
            + "counted and reported once when the outage ends.");
    }

    private static bool Announces(string message) =>
        message.Contains("publisher connected", StringComparison.Ordinal);

    private sealed class PublisherUnderTest : IAsyncDisposable
    {
        private readonly KeycloakTokenProvider tokens;

        private PublisherUnderTest(FakeMqttClient client, RecordingLogger<MqttPublisher> logger, KeycloakTokenProvider tokens)
        {
            Client = client;
            Logger = logger;
            this.tokens = tokens;

            Publisher = new MqttPublisher(
                Options.Create(new SimulatorOptions
                {
                    MqttHost = "mosquitto.test:1883",
                    KeycloakUrl = "https://keycloak.test",
                    ClientSecret = "a-secret",
                }),
                tokens,
                logger,
                client);
        }

        public FakeMqttClient Client { get; }

        public RecordingLogger<MqttPublisher> Logger { get; }

        public MqttPublisher Publisher { get; }

        public static PublisherUnderTest Create()
        {
            FakeMqttClient client = new();
            KeycloakTokenProvider tokens = new(
                new FakeHttpClientFactory(new HttpClient(new StubKeycloak())),
                Options.Create(new SimulatorOptions { KeycloakUrl = "https://keycloak.test" }),
                TimeProvider.System,
                NullLogger<KeycloakTokenProvider>.Instance);

            return new PublisherUnderTest(client, new RecordingLogger<MqttPublisher>(), tokens);
        }

        /// <summary>
        /// Starts the loop and waits until it is <b>past</b> everything a
        /// connect sets off — <c>ReportDrops</c> included, which
        /// <c>Interlocked.Exchange</c>es the drop counter to zero.
        ///
        /// <para>
        /// <b>Waiting on <c>IsConnected</c> is not enough.</b> The fake yields
        /// before answering CONNECT, because the real client cannot complete a
        /// network round trip synchronously, so the loop resumes on a pool
        /// thread somewhere after the flag is set. A test that arms a publish
        /// failure the moment the flag turns true can therefore count its drop
        /// and then have the loop clear the counter underneath it — observed as
        /// a two-in-three failure rate, not a rare race.
        /// </para>
        ///
        /// <para>
        /// So one sample is dropped first, giving the connect an outage to
        /// report, and the warning that reports it is the signal that the loop
        /// has gone past. The counter is back to zero when this returns.
        /// </para>
        /// </summary>
        public async Task StartAndSettleAsync()
        {
            Client.GateConnect();
            await Publisher.StartAsync(CancellationToken.None);

            await Publisher.PublishAsync(Topic, "{}", CancellationToken.None);

            Client.AllowConnect();

            (await WaitForWarningAsync()).ShouldBeTrue("the publisher never connected");
            Publisher.DroppedSamples.ShouldBe(0, "the settling sample was reported and the count cleared");
        }

        public Task<bool> WaitForWarningAsync() =>
            WaitUntilAsync(() => Logger.Entries.Any(entry => entry.Level == LogLevel.Warning));

        public async ValueTask DisposeAsync()
        {
            await Publisher.DisposeAsync();
            tokens.Dispose();
        }

        public static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
            }

            return condition();
        }
    }

    private sealed class StubKeycloak : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"a-token","expires_in":300,"token_type":"Bearer"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
