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

        await publisher.Publisher.StartAsync(CancellationToken.None);
        (await publisher.WaitUntilConnectedAsync()).ShouldBeTrue("the publisher never connected");

        publisher.Client.FailNextPublishAsNotConnected = true;

        await Should.NotThrowAsync(() => publisher.Publisher.PublishAsync(Topic, "{}", CancellationToken.None));

        publisher.Publisher.DroppedSamples.ShouldBe(
            1,
            $"a {nameof(MqttClientNotConnectedException)} raised by the race must be counted like any "
            + "other drop, not rethrown at the timeline and not logged per occurrence.");
    }

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

        public Task<bool> WaitForWarningAsync() =>
            WaitUntilAsync(() => Logger.Entries.Any(entry => entry.Level == LogLevel.Warning));

        public Task<bool> WaitUntilConnectedAsync() => WaitUntilAsync(() => Client.IsConnected);

        public async ValueTask DisposeAsync()
        {
            await Publisher.DisposeAsync();
            tokens.Dispose();
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
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
