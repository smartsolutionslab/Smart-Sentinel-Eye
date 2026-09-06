using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Ingress;

/// <summary>
/// Keeps the subscriber connected: mint, connect, subscribe, wait for the drop,
/// back off, repeat — until cancelled.
///
/// <para>
/// <b>This is hand-written on purpose and is deliberately duplicated.</b>
/// MQTTnet 5 removed <c>MQTTnet.Extensions.ManagedClient</c>, which used to own
/// the reconnect. The Scenario Simulator's <c>MqttPublisher</c> carries its own
/// copy of the same shape rather than sharing this one: the two live in
/// assemblies with no common reference, so sharing would mean putting an MQTT
/// client into <c>Shared.Kernel</c>, which all nine bounded contexts reference.
/// Do not "fix" the duplication by extracting a shared client (spec 079,
/// ADR-0036).
/// </para>
///
/// <para>
/// <b>The copy differs only where the two jobs differ</b>, and the exhaustive
/// list is in <c>MqttPublisher</c>'s own comment: it has no subscription to
/// renew, and it counts dropped samples this one has no use for. Everything
/// else — mint, connect, read the result, back off, clear the backoff only for
/// a connection that held — is the same in both, and a change here belongs
/// there. They had drifted once while both comments claimed they had not.
/// </para>
/// </summary>
internal sealed class MqttConnectionLoop
{
    private readonly MqttConnection connection;
    private readonly MqttBackoff backoff;
    private readonly MosquittoOptions options;
    private readonly ILogger logger;

    public MqttConnectionLoop(
        MqttConnection connection, MqttBackoff backoff, MosquittoOptions options, ILogger logger)
    {
        Ensure.That(connection).IsNotNull();
        Ensure.That(backoff).IsNotNull();
        Ensure.That(options).IsNotNull();
        Ensure.That(logger).IsNotNull();

        this.connection = connection;
        this.backoff = backoff;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is cancelled. <b>There is
    /// no attempt limit.</b> After a mosquitto restart the go-auth plugin
    /// re-fetches the realm JWKS and refuses CONNECT until it has, so a loop
    /// that gave up would turn a few seconds of JWKS fetch into an ingestion
    /// outage lasting until someone restarted the pod — the very defect this
    /// loop exists to prevent.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        IMqttClient client = connection.Client;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await AttemptAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync cancelled the loop. Shutting down is not a failure.
        }
    }

    /// <summary>
    /// One attempt owns one <see cref="DropSignal"/>, subscribed <b>before</b>
    /// the CONNECT and unsubscribed when the attempt ends.
    ///
    /// <para>
    /// <b>The handler used to be attached once for the life of the loop</b>, over
    /// a field the attempt replaced — so a disconnect belonging to attempt N
    /// completed attempt N+1's wait. That is not a rare interleaving: MQTTnet 5
    /// calls <c>DisconnectInternal</c> on a non-success CONNACK and dispatches
    /// the handler fire-and-forget (<c>Task.Run(…).RunInBackground(_logger)</c>),
    /// so a stale disconnect is in flight after every refusal, by design. Owned
    /// per attempt, it has nothing left to complete.
    /// </para>
    /// </summary>
    private async Task AttemptAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        TimeSpan delay = backoff.Next();
        if (delay > TimeSpan.Zero)
        {
            logger.MqttSubscriberRetryScheduled(delay.TotalSeconds, backoff.Attempt);
            await Task.Delay(delay, cancellationToken);
        }

        DropSignal drop = new(client, logger);
        client.DisconnectedAsync += drop.OnDisconnectedAsync;

        try
        {
            await HoldConnectionAsync(client, drop, cancellationToken);
        }
        finally
        {
            client.DisconnectedAsync -= drop.OnDisconnectedAsync;
        }
    }

    private async Task HoldConnectionAsync(
        IMqttClient client, DropSignal drop, CancellationToken cancellationToken)
    {
        if (!await ConnectAsync(client, cancellationToken))
        {
            return;
        }

        long connectedAt = Stopwatch.GetTimestamp();
        logger.MqttSubscriberConnected(Broker(), options.Username);

        if (!await SubscribeAsync(client, cancellationToken))
        {
            return;
        }

        await drop.WaitAsync(cancellationToken);

        // Cleared here rather than on the CONNACK. A session takeover answers
        // CONNACK and then closes — both clients connect with a fixed client id,
        // so a second pod, or a restart before the broker reaps the old session,
        // is exactly that — and a reset on arrival clears a backoff that never
        // gets to apply, so the takeover becomes a hot reconnect loop.
        backoff.ResetIfHeld(Stopwatch.GetElapsedTime(connectedAt));
    }

    /// <summary>
    /// Mints the credential <b>after</b> the delay and immediately before the
    /// CONNECT, so no wait — however long the backoff has grown — sits between
    /// minting a token and presenting it.
    /// </summary>
    private async Task<bool> ConnectAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        try
        {
            await connection.MintCredentialAsync(cancellationToken);
        }
        catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
        {
            // Keycloak is away. The credential already in the slot may still
            // have life in it, so the attempt continues rather than being
            // abandoned — the worst case is a refusal the backoff already
            // handles, and giving up here would make a Keycloak blink outlast it.
            logger.MqttReconnectTokenFailed(exception.Message);
        }

        try
        {
            // **The result code is the answer; the absence of an exception is
            // not.** MQTTnet 4's builder set
            // ThrowOnNonSuccessfulConnectResponse = true, so a rejected CONNECT
            // arrived here as a throw. In MQTTnet 5 the property is gone and a
            // refusal returns normally — verified against mosquitto 2.0.18,
            // where a bad credential answers NotAuthorized with
            // IsConnected=false and throws nothing. Discarding the result made
            // every refusal an Information line saying the subscriber was
            // connected, which during an outage is the only signal an operator
            // has and says the opposite of what happened.
            MqttClientConnectResult result =
                await client.ConnectAsync(connection.Options, cancellationToken);

            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                return true;
            }

            logger.MqttSubscriberConnectFailed(Broker(), result.ResultCode.ToString());
            return false;
        }
        catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
        {
            logger.MqttSubscriberConnectFailed(Broker(), exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Whether an exception is this loop being stopped, rather than a failure to
    /// retry past.
    ///
    /// <para>
    /// <b>The type alone does not say.</b> HttpClient's own timeout and the
    /// standard resilience pipeline both raise an
    /// <see cref="OperationCanceledException"/> from a token nothing here owns,
    /// so a filter reading <c>is not OperationCanceledException</c> lets one
    /// straight out of the loop and into the bare catch above — which means
    /// "shutting down", exits, and leaves the subscriber deaf until the pod is
    /// restarted. Reproduced with the real token provider against a Keycloak
    /// that accepted the socket and never answered; masked in production only
    /// because the resilience handler's 30 s timeout raises a
    /// <c>TimeoutRejectedException</c> first.
    /// </para>
    /// </summary>
    private static bool IsShutdown(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Subscribes after <b>every</b> successful connect, unconditionally.
    ///
    /// <para>
    /// The previous commit subscribed once per process, and
    /// <c>MqttResubscribeAfterBrokerOutageIntegrationTests</c> was observed
    /// failing against exactly that: two connects, one SUBSCRIBE, and an event
    /// published after the broker returned that never arrived in 90 s across 18
    /// publish attempts. The client was connected, <c>IsConnected</c> was true,
    /// the log carried a connect line, and the broker discarded every publish
    /// because it held no filter for this client.
    /// </para>
    ///
    /// <para>
    /// <b>Not conditional on <c>MqttClientConnectResult.IsSessionPresent</c>.</b>
    /// A SUBSCRIBE on a session that already holds the filter is idempotent at
    /// the broker, so unconditional costs one packet and cannot be wrong;
    /// a condition can, and its failure mode is silence.
    /// </para>
    /// </summary>
    private async Task<bool> SubscribeAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        string topic = options.SubscribeTopic;
        try
        {
            await client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
            logger.MqttSubscriberResubscribed(topic);
            return true;
        }
        catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
        {
            // Connected but not listening is the silent failure this loop exists
            // to avoid, so the connection is dropped rather than kept: the next
            // attempt is a clean connect that subscribes again.
            logger.MqttSubscriberSubscribeFailed(topic, exception.Message);
            await client.TryDisconnectAsync(
                MqttClientDisconnectOptionsReason.NormalDisconnection, "subscribe failed");
            return false;
        }
    }

    private string Broker() => $"{options.Host}:{options.Port}";

    /// <summary>
    /// One attempt's wait for its own connection to go.
    ///
    /// <para>
    /// <b>The event is the nudge; the client's state is the authority.</b> A
    /// disconnect event does not prove this connection ended — MQTTnet 5
    /// dispatches the handler fire-and-forget from the
    /// <c>DisconnectInternal</c> a refused CONNECT performs, so one can arrive
    /// while a later connection is up and subscribed. Taken for a drop it makes
    /// the loop reconnect a live client, which
    /// <c>MqttClient.ThrowIfConnected</c> refuses — and nothing closes that
    /// connection, so the refusal repeats: a permanent stream of false "could
    /// not connect" errors on the only outage signal EventIngestion has, and an
    /// attempt counter climbing to the cap that the next genuine outage then
    /// waits out.
    /// </para>
    ///
    /// <para>
    /// Reading <c>IsConnected</c> here is not the polling the loop avoids — it
    /// is asked once, on an event, and it is decisive:
    /// <c>DisconnectIsPendingOrFinished</c> moves the connection status off
    /// <c>Connected</c> before <c>DisconnectCore</c> runs, and
    /// <c>DisconnectCore</c> sets it to <c>Disconnected</c> before it builds the
    /// event args. A genuine drop therefore cannot reach this handler with the
    /// client still reporting a connection.
    /// </para>
    /// </summary>
    private sealed class DropSignal(IMqttClient client, ILogger logger)
    {
        private readonly TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(CancellationToken cancellationToken) => dropped.Task.WaitAsync(cancellationToken);

        public Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            logger.MqttSubscriberDisconnected(args.Reason.ToString());

            if (!client.IsConnected)
            {
                dropped.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Capped, jittered exponential backoff: nothing before the first attempt, then
/// 1 s, 2 s, 4 s, 8 s, 16 s, capped at 30 s, each multiplied by a factor in
/// [0.8, 1.2].
///
/// <para>
/// The cap is 30 s rather than something larger because a mosquitto restart
/// makes early refusals expected — go-auth re-fetches the realm JWKS — and a
/// longer cap would turn seconds of that into a minutes-long ingestion gap. The
/// jitter is de-synchronisation, not a policy knob: at the 250-camera target,
/// every client that dropped at the same moment would otherwise retry at the
/// same moment, making the recovery the second outage.
/// </para>
/// </summary>
internal sealed class MqttBackoff(TimeSpan first, TimeSpan cap)
{
    private int attempts;
    private TimeSpan servedDelay;

    public MqttBackoff()
        : this(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
    {
    }

    /// <summary>Attempts made since the last <see cref="Reset"/>, for the log.</summary>
    public int Attempt => attempts;

    /// <summary>How long to wait before the next attempt.</summary>
    public TimeSpan Next()
    {
        if (attempts++ == 0)
        {
            servedDelay = TimeSpan.Zero;
            return servedDelay;
        }

        double seconds = Math.Min(first.TotalSeconds * Math.Pow(2, attempts - 2), cap.TotalSeconds);
        servedDelay = TimeSpan.FromSeconds(seconds * Jitter());
        return servedDelay;
    }

    /// <summary>Puts the next wait back to nothing, after a connect that worked.</summary>
    public void Reset()
    {
        attempts = 0;
        servedDelay = TimeSpan.Zero;
    }

    /// <summary>
    /// Clears the debt for a connection that <b>held</b>, and leaves it alone for
    /// one that merely arrived.
    ///
    /// <para>
    /// <b>The yardstick is twice the wait this attempt actually served</b> —
    /// twice the floor when it served none, which is every attempt that follows
    /// a reset. It used to be the floor itself, and a constant yardstick is
    /// exactly what a takeover defeats: two clients sharing a fixed client id
    /// take the session off each other at roughly one another's reconnect
    /// period, and that period converges on a little <em>above</em> the floor,
    /// so every cycle cleared the backoff and the flap ran at full speed for as
    /// long as the other client was there — the very thing this guard was
    /// written to prevent.
    /// </para>
    ///
    /// <para>
    /// Measuring against the served wait alone is not enough either, because the
    /// wait is jittered over [0.8, 1.2]: a peer holding at 1.1 × the floor
    /// outlasts three quarters of that band. Doubling puts the yardstick clear
    /// of the whole band, so a takeover run escalates to the cap instead of
    /// resetting, while a connection that genuinely outlives two of its own
    /// waits still clears the debt.
    /// </para>
    /// </summary>
    public void ResetIfHeld(TimeSpan held)
    {
        TimeSpan yardstick = (servedDelay > TimeSpan.Zero ? servedDelay : first) * 2;

        if (held >= yardstick)
        {
            Reset();
        }
    }

    // Not a security decision: this de-synchronises retries between clients, it
    // does not generate anything anyone could guess their way into.
#pragma warning disable CA5394
    private static double Jitter() => 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
}
