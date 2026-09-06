using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using SmartSentinelEye.EventIngestion.Infrastructure.Ingress;
using SmartSentinelEye.EventIngestion.Infrastructure.Tests.Fakes;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Tests;

/// <summary>
/// Spec 079 (#1139) — the four behaviours MQTTnet 5 stopped supplying when it
/// removed <c>MQTTnet.Extensions.ManagedClient</c>: reconnect, resubscribe,
/// per-attempt credential minting, and a retry that never gives up.
///
/// <para>
/// <b>This is the fast lane, not the proof.</b> It drives our loop against our
/// own fake, so it cannot see a broker that accepts a connection and silently
/// discards every publish because nobody is subscribed. That is what
/// <c>MqttResubscribeAfterBrokerOutageIntegrationTests</c> is for. This one
/// catches a regression in milliseconds and without Docker; both are required.
/// </para>
/// </summary>
public class MqttConnectionLoopTests
{
    private const string Topic = "fab/+/+/+";

    [Fact]
    public async Task The_loop_reconnects_after_the_connection_drops()
    {
        using LoopUnderTest loop = LoopUnderTest.Start();

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.ConnectAttempts == 1)).ShouldBeTrue(
            "the loop never made a first connection");

        await loop.Client.DropAsync();

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.ConnectAttempts >= 2)).ShouldBeTrue(
            "the connection dropped and nothing reconnected. Nothing outside this loop will: "
            + "the managed client that used to do it does not exist in MQTTnet 5, and no health "
            + "check would report a subscriber that simply stopped.");
    }

    /// <summary>
    /// Gap 2, and the one this whole slice exists for. A reconnect that does not
    /// resubscribe leaves the subscriber connected, healthy and receiving
    /// nothing — the broker comes back holding no session, so the filter is gone
    /// with it.
    /// </summary>
    [Fact]
    public async Task Every_connect_subscribes_again_not_only_the_first()
    {
        using LoopUnderTest loop = LoopUnderTest.Start();

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.SubscribedTopics.Count == 1)).ShouldBeTrue(
            "the loop never subscribed at all");

        await loop.Client.DropAsync();

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.SubscribedTopics.Count >= 2)).ShouldBeTrue(
            "the loop reconnected but subscribed only once. The broker restarted with no session, "
            + "so it holds no filter for this client: connected, IsConnected true, a connect line "
            + "in the log, and not one delivery ever again.");

        loop.Client.SubscribedTopics.ShouldAllBe(topic => topic == Topic);
    }

    /// <summary>
    /// Gap 4. <c>ConnectingFailedAsync</c> — the v4 event that re-minted after a
    /// refusal — does not exist on a plain client. Minting before <em>every</em>
    /// attempt is stronger than re-minting after a failed one, and it closes
    /// #2038 by construction: a subscriber cannot re-present the same dead
    /// credential forever if it never presents the same credential twice.
    /// </summary>
    [Fact]
    public async Task Each_attempt_presents_a_freshly_minted_credential()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(refuseFirstConnects: 2);

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.ConnectAttempts >= 3)).ShouldBeTrue(
            "the loop gave up before the third attempt");

        List<string> presented = [.. loop.Client.PresentedCredentials.Take(3)];

        presented.ShouldBe(["token-1", "token-2", "token-3"],
            "each attempt must mint again. Reusing the credential is exactly #2038: a subscriber "
            + "that never achieved a first connection re-presenting the same dead JWT forever, "
            + "recoverable only by a restart.");
    }

    /// <summary>
    /// Assumption A3: after a mosquitto restart the go-auth plugin re-fetches the
    /// realm JWKS and refuses CONNECT until it has. A loop with an attempt limit
    /// would turn that into a permanent outage.
    /// </summary>
    [Fact]
    public async Task The_loop_keeps_retrying_with_no_attempt_limit()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(refuseFirstConnects: 12);

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.IsConnected)).ShouldBeTrue(
            "the loop stopped retrying after a run of refusals. Early refusals are expected "
            + "rather than exceptional — a loop that gives up makes a JWKS fetch a permanent gap.");

        loop.Client.ConnectAttempts.ShouldBeGreaterThan(12);
    }

    /// <summary>
    /// The connect that ends an outage resets the wait, so the next drop
    /// reconnects at once instead of inheriting the delay the outage grew to.
    /// </summary>
    [Fact]
    public async Task A_reconnect_after_a_success_does_not_inherit_the_previous_backoff()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(refuseFirstConnects: 4);

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.IsConnected)).ShouldBeTrue(
            "the loop never connected");

        int before = loop.Client.ConnectAttempts;
        await loop.Client.DropAsync();

        (await LoopUnderTest.WaitUntilAsync(
            () => loop.Client.ConnectAttempts > before, TimeSpan.FromMilliseconds(500))).ShouldBeTrue(
            "the reconnect was still waiting out the backoff the outage had grown. A delay is "
            + "for repeated failures, not for the first attempt after a connection that worked.");
    }

    private sealed class LoopUnderTest : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly MqttTokenProvider tokens;
        private readonly Task running;

        private LoopUnderTest(FakeMqttClient client, MqttTokenProvider tokens, MqttConnection connection)
        {
            Client = client;
            this.tokens = tokens;

            // Milliseconds rather than the production 1 s/30 s: what these tests
            // assert is the loop's ordering, not the arithmetic. The shape of the
            // production delay is asserted directly in MqttBackoffTests, where it
            // costs nothing to wait for.
            running = new MqttConnectionLoop(
                    connection,
                    new MqttBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4)),
                    OptionsValue(),
                    NullLogger.Instance)
                .RunAsync(cancellation.Token);
        }

        public FakeMqttClient Client { get; }

        public static LoopUnderTest Start(int refuseFirstConnects = 0)
        {
            FakeMqttClient client = new();
            client.RefuseNextConnects(refuseFirstConnects);

            IOptions<MosquittoOptions> options = Options.Create(OptionsValue());
            TokenHolder token = new();
            MqttTokenProvider tokens = new(
                new StubHttpClientFactory(new HttpClient(new CountingKeycloak())),
                options,
                TimeProvider.System,
                NullLogger<MqttTokenProvider>.Instance);

            MqttClientOptions clientOptions = new MqttClientOptionsBuilder()
                .WithClientId(options.Value.ClientId)
                .WithTcpServer(options.Value.Host, options.Value.Port)
                .WithCredentials(new TokenCredentials(options.Value.Username, token))
                .Build();

            return new LoopUnderTest(client, tokens, new MqttConnection(client, clientOptions, token, tokens));
        }

        public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? within = null)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(10));
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

        public void Dispose()
        {
            cancellation.Cancel();
            running.GetAwaiter().GetResult();
            cancellation.Dispose();
            tokens.Dispose();
            Client.Dispose();
        }

        private static MosquittoOptions OptionsValue() => new()
        {
            Host = "mosquitto.test",
            Port = 1883,
            KeycloakUrl = "https://keycloak.test",
            ClientSecret = "a-secret",
        };
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Hands back <c>token-1</c>, <c>token-2</c>, … so a reused credential is
    /// visible rather than merely uncounted. <c>expires_in: 0</c> defeats
    /// <c>ClientCredentialsTokenProvider</c>'s 80 %-of-lifetime cache, which
    /// would otherwise answer every attempt from the first mint and make this
    /// test unable to tell minting from remembering.
    /// </summary>
    private sealed class CountingKeycloak : HttpMessageHandler
    {
        private int minted;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int number = Interlocked.Increment(ref minted);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"token-{{number}}","expires_in":0,"token_type":"Bearer"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
