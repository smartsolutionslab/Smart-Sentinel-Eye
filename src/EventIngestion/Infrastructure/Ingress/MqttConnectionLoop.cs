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
/// </summary>
internal sealed class MqttConnectionLoop
{
    private readonly MqttConnection connection;
    private readonly MqttBackoff backoff;
    private readonly MosquittoOptions options;
    private readonly ILogger logger;

    /// <summary>
    /// Completed by the disconnect handler, so the loop waits for a real drop
    /// instead of polling <c>IsConnected</c>. Replaced before every attempt and
    /// <b>before</b> the CONNECT, so a connection that drops the instant it is
    /// established still wakes the loop that is about to wait on it.
    /// </summary>
    private TaskCompletionSource? dropped;

    private bool subscribed;

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
        client.DisconnectedAsync += OnDisconnectedAsync;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await AttemptAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync cancelled the loop. Shutting down is not a failure.
        }
        finally
        {
            client.DisconnectedAsync -= OnDisconnectedAsync;
        }
    }

    private async Task AttemptAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        TimeSpan delay = backoff.Next();
        if (delay > TimeSpan.Zero)
        {
            logger.MqttSubscriberRetryScheduled(delay.TotalSeconds, backoff.Attempt);
            await Task.Delay(delay, cancellationToken);
        }

        TaskCompletionSource drop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dropped = drop;

        if (!await ConnectAsync(client, cancellationToken))
        {
            return;
        }

        // Reset only after a connect that worked, so a drop reconnects at once
        // and only repeated failures back off.
        backoff.Reset();
        logger.MqttSubscriberConnected(Broker(), options.Username);

        if (!await SubscribeAsync(client, cancellationToken))
        {
            return;
        }

        await drop.Task.WaitAsync(cancellationToken);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keycloak is away. The credential already in the slot may still
            // have life in it, so the attempt continues rather than being
            // abandoned — the worst case is a refusal the backoff already
            // handles, and giving up here would make a Keycloak blink outlast it.
            logger.MqttReconnectTokenFailed(exception.Message);
        }

        try
        {
            await client.ConnectAsync(connection.Options, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.MqttSubscriberConnectFailed(Broker(), exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Subscribes once, at the first connect. <b>A reconnect leaves the
    /// subscription behind</b>: the broker comes back holding no session, so the
    /// client is connected, healthy and receiving nothing. That is the defect
    /// spec 079 exists to close, and this shape is here only long enough for
    /// <c>MqttResubscribeAfterBrokerOutageIntegrationTests</c> to be observed
    /// failing against it (spec 079 T016).
    /// </summary>
    private async Task<bool> SubscribeAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        if (subscribed)
        {
            return true;
        }

        string topic = options.SubscribeTopic;
        try
        {
            await client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
            subscribed = true;
            logger.MqttSubscriberResubscribed(topic);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
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

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        logger.MqttSubscriberDisconnected(args.Reason.ToString());
        dropped?.TrySetResult();
        return Task.CompletedTask;
    }

    private string Broker() => $"{options.Host}:{options.Port}";
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
            return TimeSpan.Zero;
        }

        double seconds = Math.Min(first.TotalSeconds * Math.Pow(2, attempts - 2), cap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * Jitter());
    }

    /// <summary>Puts the next wait back to nothing, after a connect that worked.</summary>
    public void Reset() => attempts = 0;

    // Not a security decision: this de-synchronises retries between clients, it
    // does not generate anything anyone could guess their way into.
#pragma warning disable CA5394
    private static double Jitter() => 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
}
