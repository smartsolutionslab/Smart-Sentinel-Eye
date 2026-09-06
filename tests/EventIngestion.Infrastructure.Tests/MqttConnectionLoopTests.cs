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

    /// <summary>
    /// Attempts that a 100 ms/400 ms backoff cannot reach in <see cref="SpinWindow"/>:
    /// the delays run 0, 100, 200, 400, 400 … so about five are due, and even a
    /// fix that only ever waited the first 100 ms could manage a dozen. Twenty is
    /// therefore comfortably above any loop that waits and far below one that does
    /// not — a spin makes thousands.
    /// </summary>
    private const int SpinCeiling = 20;

    /// <summary>
    /// Connects a 100 ms/400 ms backoff cannot reach in <see cref="FlapWindow"/>
    /// once it engages: the delays run 0, 100, 200, 400, 400 … and each cycle
    /// also spends <see cref="JustPastTheFloor"/> connected, so eight or so are
    /// due in three seconds. A backoff that resets every cycle instead runs at
    /// the hold period alone — about 27. Sixteen sits clear of both.
    /// </summary>
    private const int FlapCeiling = 16;

    private static readonly TimeSpan SpinWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Long enough for a failed-connect cycle to repeat several times against a
    /// 100 ms/400 ms backoff, and short enough to keep the suite quick.
    /// </summary>
    private static readonly TimeSpan StaleWindow = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan FlapWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A hold that clears <see cref="LoopUnderTest.Patient"/>'s 100 ms floor and
    /// nothing more. The margin is the whole point, and it is what this test
    /// pins: a hold anywhere above the floor used to count as a connection that
    /// held, so a peer flapping at <c>first + ε</c> reset the backoff on every
    /// cycle and reconnected at full speed — the takeover the guard was written
    /// to stop. <c>ResetIfHeld</c> now measures against twice the wait actually
    /// served, so this hold no longer clears it.
    /// </summary>
    private static readonly TimeSpan JustPastTheFloor = TimeSpan.FromMilliseconds(110);

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

    /// <summary>
    /// <b>A refusal is not a connection, and only the result code says which it
    /// was.</b> MQTTnet 4's builder set
    /// <c>ThrowOnNonSuccessfulConnectResponse = true</c>, so a rejected CONNECT
    /// arrived as an exception; in MQTTnet 5 the property is gone and the
    /// refusal comes back as <c>ResultCode</c> on a result that is otherwise
    /// indistinguishable from success. Confirmed against mosquitto 2.0.18 — a
    /// bad credential answers <c>NotAuthorized</c> with <c>IsConnected=false</c>
    /// and throws nothing.
    ///
    /// <para>
    /// The log line is the assertion because the log is what an operator has.
    /// EventIngestion registers no MQTT health check, so during an outage an
    /// Information line saying the subscriber is connected is not merely
    /// untidy — it is the only signal there is, saying the opposite of what
    /// happened.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_connect_is_not_announced_as_a_connection()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(client => client.RefuseEveryConnect());

        (await LoopUnderTest.WaitUntilAsync(() => loop.Client.ConnectAttempts >= 3)).ShouldBeTrue(
            "the loop stopped attempting, so there is nothing in the log to read");

        List<string> lines = [.. loop.Logger.Entries.Select(entry => entry.Message)];

        lines.ShouldNotContain(
            line => line.Contains("subscriber connected", StringComparison.Ordinal),
            "the broker answered NotAuthorized and the client is not connected. Announcing a "
            + "connection the loop does not have makes the only signal an operator gets during "
            + "an outage report the opposite of the truth.");
    }

    /// <summary>
    /// The same defect, in the form that costs CPU rather than trust: a refusal
    /// taken for a success resets the backoff, so the next attempt waits for
    /// nothing and the loop spins. Reachable and <b>permanent</b> — an
    /// <c>acl.txt</c> that grants no read on the subscribe topic never resolves
    /// itself, unlike the JWKS fetch the retry loop was designed around.
    /// </summary>
    [Fact]
    public async Task A_broker_that_refuses_every_connect_is_retried_with_a_delay_rather_than_a_spin()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(
            client => client.RefuseEveryConnect(), LoopUnderTest.Patient());

        bool spun = await LoopUnderTest.WaitUntilAsync(
            () => loop.Client.ConnectAttempts >= SpinCeiling, SpinWindow);

        spun.ShouldBeFalse(
            $"the loop reached {loop.Client.ConnectAttempts} attempts inside a {SpinWindow.TotalSeconds:F0}s window "
            + "against a 100 ms backoff, which allows about five. A refusal that resets the backoff "
            + "leaves nothing to wait for, so a permanent refusal becomes a hot loop against the "
            + "broker and a matching stream of error lines.");
    }

    /// <summary>
    /// <c>backoff.Reset()</c> runs immediately after CONNECT, before the
    /// connection has proved it can hold. A session takeover answers CONNACK and
    /// then closes — both clients use a fixed client id
    /// (<c>event-ingestion</c>, <c>scenario-simulator</c>), so a second pod, or a
    /// restart before the broker reaps the old session, produces exactly that.
    /// Every cycle then resets a backoff that never gets to apply.
    ///
    /// <para>
    /// This does not contradict
    /// <see cref="A_reconnect_after_a_success_does_not_inherit_the_previous_backoff"/>:
    /// one drop after a connection that worked must reconnect at once, and a
    /// <i>run</i> of connections that die on arrival must not. The difference is
    /// repetition, not the first reconnect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_connection_that_dies_on_arrival_is_retried_with_a_delay_rather_than_a_spin()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(
            client => client.DropEveryConnectionImmediately = true, LoopUnderTest.Patient());

        bool spun = await LoopUnderTest.WaitUntilAsync(
            () => loop.Client.ConnectAttempts >= SpinCeiling, SpinWindow);

        spun.ShouldBeFalse(
            $"the loop reached {loop.Client.ConnectAttempts} attempts inside a {SpinWindow.TotalSeconds:F0}s window "
            + "against a 100 ms backoff, which allows about five. Resetting the backoff on a "
            + "connection that has not lasted a single instant means a takeover loop reconnects "
            + "with no delay at all, for as long as the other client is there.");
    }

    /// <summary>
    /// <b>A refusal leaves a disconnect in flight, and the loop has nowhere to
    /// put it.</b> MQTTnet 5's <c>ConnectAsync</c> calls
    /// <c>DisconnectInternal</c> on a non-success CONNACK, and
    /// <c>DisconnectCore</c> dispatches the handler fire-and-forget
    /// (<c>Task.Run(...).RunInBackground(_logger)</c>, not awaited). So after
    /// every refusal a stale disconnect is on its way, and it can land after a
    /// later CONNECT has succeeded — completing that attempt's <c>dropped</c>
    /// task for a connection that is up and subscribed.
    ///
    /// <para>
    /// <b>The cost is not one spurious cycle.</b> The loop wakes, sees a drop
    /// that did not happen, and reconnects a client that is still connected —
    /// which <c>MqttClient.ThrowIfConnected</c> refuses. The attempt fails, the
    /// connection is never closed, so the next attempt fails the same way, and
    /// the one after that: a false <c>Error</c> saying the subscriber could not
    /// connect, forever, on the only outage signal EventIngestion has — there is
    /// no MQTT health check. Worse, <c>ResetIfHeld</c> never runs on the held
    /// connection, so the attempt counter climbs to the cap and the next
    /// <em>genuine</em> outage waits up to 30 s: a recovery regression on the
    /// leg this spec exists to protect.
    /// </para>
    ///
    /// <para>
    /// The allowance of one failed connect is deliberate. A single spurious wake
    /// at the refused-to-success transition is the bounded part of this, and is
    /// not what this asserts against.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stale_disconnect_does_not_start_a_failed_connect_cycle_against_a_live_connection()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(_ => { }, LoopUnderTest.Patient());

        (await LoopUnderTest.WaitUntilAsync(
            () => loop.Client.IsConnected && loop.Client.SubscribedTopics.Count == 1)).ShouldBeTrue(
            "the loop never reached a connected, subscribed client, so there is nothing to disturb");

        await loop.Client.RaiseStaleDisconnectAsync();

        // The whole window is observed rather than exited at the threshold: what
        // is in doubt is the rate, and a count that stopped at the bound it was
        // compared against would report the bound rather than the cycle.
        await Task.Delay(StaleWindow);

        loop.FailedConnects.ShouldBeLessThanOrEqualTo(
            1,
            $"\"could not connect\" errors written in {StaleWindow.TotalSeconds:F0}s while the client was "
            + "connected and subscribed. Each is ThrowIfConnected refusing to reconnect a live "
            + "connection, and nothing closes that connection — so the cycle does not end: a permanent "
            + "stream of false outage errors, and an attempt counter climbing to the cap that the next "
            + "real outage then waits out. One is allowed for the bounded spurious wake.");

        loop.Client.IsConnected.ShouldBeTrue(
            "arrange check — a stale disconnect is an event, not a disconnection. If the client is down, "
            + "this test is measuring an ordinary reconnect rather than the cycle it was written for.");
    }

    /// <summary>
    /// <b>The yardstick <c>ResetIfHeld</c> measures against is a constant, so a
    /// peer can sit just above it forever.</b> A connection held for
    /// <c>first + ε</c> clears the backoff every single time, which puts the next
    /// attempt back to no delay at all — so the backoff never engages, and the
    /// flap runs at the peer's period instead of at a growing one.
    ///
    /// <para>
    /// This is the guard's own scenario, not an exotic one. Its comment names a
    /// session takeover, and with a fixed client id two pods take the session
    /// off each other at roughly one another's reconnect period — which is what
    /// that period converges on: a little above the floor. At the production
    /// floor of 1 s it is a reconnect, a resubscribe and three log lines every
    /// second, indefinitely.
    /// </para>
    ///
    /// <para>
    /// Distinct from
    /// <see cref="A_connection_that_dies_on_arrival_is_retried_with_a_delay_rather_than_a_spin"/>,
    /// which covers a hold of zero — the one case the fixed yardstick does
    /// catch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_connection_held_just_past_the_floor_still_backs_off()
    {
        using LoopUnderTest loop = LoopUnderTest.Start(
            client => client.HoldEveryConnectionFor(JustPastTheFloor), LoopUnderTest.Patient());

        await Task.Delay(FlapWindow);

        loop.Client.ConnectAttempts.ShouldBeLessThan(
            FlapCeiling,
            $"connects made in {FlapWindow.TotalSeconds:F0}s against a peer holding each one for "
            + $"{JustPastTheFloor.TotalMilliseconds:F0} ms — a 100 ms floor exceeded by a hair. "
            + "ResetIfHeld clears the backoff on every one of them, so there is never a delay left to "
            + "wait: the takeover the guard was written for reconnects at full speed anyway, one cycle "
            + "per peer period, for as long as the other client is there.");
    }

    private sealed class LoopUnderTest : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly MqttTokenProvider tokens;
        private readonly Task running;

        private LoopUnderTest(
            FakeMqttClient client, MqttTokenProvider tokens, MqttConnection connection, MqttBackoff backoff)
        {
            Client = client;
            this.tokens = tokens;

            running = new MqttConnectionLoop(connection, backoff, OptionsValue(), Logger)
                .RunAsync(cancellation.Token);
        }

        public FakeMqttClient Client { get; }

        public RecordingLogger Logger { get; } = new();

        /// <summary>
        /// How many times the loop has said it could not connect. Counted from
        /// the log rather than from <c>ConnectAttempts</c> because a CONNECT
        /// refused by <c>ThrowIfConnected</c> never leaves the client, so it
        /// shows up only here.
        /// </summary>
        public int FailedConnects =>
            Logger.Entries.Count(entry => entry.Message.Contains("could not connect", StringComparison.Ordinal));

        /// <summary>
        /// Milliseconds rather than the production 1 s/30 s: what most of these
        /// tests assert is the loop's ordering, not the arithmetic. The shape of
        /// the production delay is asserted directly in <c>MqttBackoffTests</c>,
        /// where it costs nothing to wait for.
        /// </summary>
        public static MqttBackoff Brisk() =>
            new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4));

        /// <summary>
        /// Slow enough that a delay is distinguishable from none. A 1 ms wait and
        /// no wait at all look the same to a test that counts attempts over a
        /// window, so the tests that assert the loop <em>waits</em> use this.
        /// </summary>
        public static MqttBackoff Patient() =>
            new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400));

        public static LoopUnderTest Start(int refuseFirstConnects = 0) =>
            Start(client => client.RefuseNextConnects(refuseFirstConnects));

        public static LoopUnderTest Start(Action<FakeMqttClient> arrange, MqttBackoff? backoff = null)
        {
            FakeMqttClient client = new();
            arrange(client);

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

            return new LoopUnderTest(
                client, tokens, new MqttConnection(client, clientOptions, token, tokens), backoff ?? Brisk());
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
