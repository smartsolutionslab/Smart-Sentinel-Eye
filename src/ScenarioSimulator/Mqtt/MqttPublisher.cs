using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using SmartSentinelEye.ScenarioSimulator.Configuration;
using SmartSentinelEye.ScenarioSimulator.Keycloak;

namespace SmartSentinelEye.ScenarioSimulator.Mqtt;

/// <summary>
/// Publishes billet sensor samples to mosquitto as the simulated PLC/inference
/// device (ADR-0111 M2). Authenticates with the <c>scenario-simulator</c>
/// Keycloak JWT (username == <c>azp</c>, the go-auth plugin's requirement).
/// Dev-only.
///
/// <para>
/// <b>The reconnect loop below is hand-written and deliberately duplicates
/// EventIngestion's <c>MqttConnectionLoop</c>.</b> MQTTnet 5 removed
/// <c>MQTTnet.Extensions.ManagedClient</c>, which used to own reconnection; the
/// two callers live in assemblies with no common reference, so sharing would
/// mean putting an MQTT client into <c>Shared.Kernel</c>, which all nine bounded
/// contexts reference. Do not "fix" the duplication by extracting a shared
/// client (spec 079, ADR-0036).
/// </para>
///
/// <para>
/// <b>Duplicated deliberately is not the same as duplicated correctly.</b> The
/// two loops had drifted apart while both doc comments said they had not: this
/// one wrapped the mint and the CONNECT in one try, so a Keycloak blink skipped
/// the connect entirely and was reported as a publish failure to "(connect)",
/// and it logged nothing when it backed off. Both are now the subscriber's
/// shape — mint and connect in separate tries, the connect result read, a
/// retry line, and a backoff cleared only by a connection that held.
/// </para>
///
/// <para>
/// What still differs, and why: the subscriber resubscribes after every connect
/// and this publisher has no subscriptions; this one counts dropped samples the
/// subscriber has no use for, because its deliveries are QoS 1 and stay with
/// the broker. <b>A change to one of these loops belongs in the other unless it
/// is on that list.</b>
/// </para>
///
/// <para>
/// <b>Samples published while the broker is away are dropped, not buffered.</b>
/// A buffered sample carries the <c>occurredAt</c> it was generated with, so
/// replaying a minute of backlog would push a burst of stale readings at a live
/// wall. For a simulator a gap in the timeline is the honest representation of
/// an outage — but a silent gap is not, so the drops are counted and reported in
/// exactly one warning when the broker returns.
/// </para>
/// </summary>
public sealed class MqttPublisher : IAsyncDisposable
{
    private const string Username = "scenario-simulator";

    private readonly KeycloakTokenProvider tokens;
    private readonly ILogger<MqttPublisher> logger;
    private readonly string host;
    private readonly int port;
    private readonly IMqttClient client;
    private readonly TokenHolder token = new();
    private readonly MqttBackoff backoff = new();

    private CancellationTokenSource? loopCancellation;
    private Task? loop;
    private MqttClientOptions? clientOptions;
    private TaskCompletionSource? dropped;

    private long droppedSamples;
    private long outageStartedAt;

    public MqttPublisher(
        IOptions<SimulatorOptions> options, KeycloakTokenProvider tokens, ILogger<MqttPublisher> logger)
        : this(options, tokens, logger, new MqttClientFactory().CreateMqttClient())
    {
    }

    /// <summary>
    /// Takes the client so the drop accounting can be asserted without a broker.
    /// What is worth testing here — a disconnected publish is counted rather than
    /// thrown, and the count is reported once — needs a connection state to
    /// control, not a network.
    /// </summary>
    internal MqttPublisher(
        IOptions<SimulatorOptions> options,
        KeycloakTokenProvider tokens,
        ILogger<MqttPublisher> logger,
        IMqttClient client)
    {
        this.tokens = tokens;
        this.logger = logger;
        this.client = client;
        (host, port) = ParseHost(options.Value.MqttHost);
    }

    /// <summary>Samples discarded since the last report.</summary>
    internal long DroppedSamples => Interlocked.Read(ref droppedSamples);

    /// <summary>
    /// Builds the client options and <b>launches</b> the connect loop, without
    /// awaiting a first connection: a plain client throws when the broker is
    /// unreachable, and the caller is a <c>BackgroundService</c> that would
    /// simply stop. The loop retries until it is cancelled. Idempotent.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (loop is not null)
        {
            return Task.CompletedTask;
        }

        // Pinned for the same reason MosquittoConnectionFactory pins it: MQTTnet 5
        // defaults to MQTT 5.0 where MQTTnet 4 defaulted to 3.1.1, and both our
        // clients are talking to a broker whose ACL and auth behaviour (ADR-0100)
        // were proven on 3.1.1. Nothing here needs MQTT 5, and a silent
        // protocol change is what cost the subscriber its persistent session.
        clientOptions = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId(Username)
            .WithTcpServer(host, port)
            .WithCredentials(new TokenCredentials(token))
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();

        loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loop = RunAsync(loopCancellation.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes one sample, or counts it as dropped.
    ///
    /// <para>
    /// <c>EnqueueAsync</c> used to buffer this; a plain client throws
    /// <see cref="MqttClientNotConnectedException"/> instead. The exception is
    /// caught <b>by its own type</b> rather than by the previous
    /// <c>Exception ex when (ex is not OperationCanceledException)</c> — a strict
    /// tightening, so every other publish failure now surfaces instead of being
    /// logged and forgotten.
    /// </para>
    /// </summary>
    public async Task PublishAsync(string topic, string payloadJson, CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            Drop();
            return;
        }

        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payloadJson)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await client.PublishAsync(message, cancellationToken);
        }
        catch (MqttClientNotConnectedException)
        {
            // The connection dropped between the check above and the publish.
            Drop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (loopCancellation is not null)
        {
            await loopCancellation.CancelAsync();
        }

        if (loop is not null)
        {
            await loop;
        }

        await client.TryDisconnectAsync(MqttClientDisconnectOptionsReason.NormalDisconnection, "shutting down");
        client.Dispose();
        loopCancellation?.Dispose();
        loop = null;
        loopCancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        client.DisconnectedAsync += OnDisconnectedAsync;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await AttemptAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal cancelled the loop. Shutting down is not a failure.
        }
        finally
        {
            client.DisconnectedAsync -= OnDisconnectedAsync;
        }
    }

    private async Task AttemptAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = backoff.Next();
        if (delay > TimeSpan.Zero)
        {
            logger.MqttPublisherRetryScheduled(delay.TotalSeconds, backoff.Attempt);
            await Task.Delay(delay, cancellationToken);
        }

        TaskCompletionSource drop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dropped = drop;

        if (!await ConnectAsync(cancellationToken))
        {
            return;
        }

        long connectedAt = Stopwatch.GetTimestamp();
        logger.MqttPublisherConnected($"{host}:{port}", Username);
        ReportDrops();

        await drop.Task.WaitAsync(cancellationToken);

        // Cleared here rather than on the CONNACK, for the reason the
        // subscriber's copy gives: a session takeover answers CONNACK and then
        // closes, and this client's id is fixed too.
        backoff.ResetIfHeld(Stopwatch.GetElapsedTime(connectedAt));
    }

    /// <summary>
    /// Mints the credential <b>after</b> the delay and immediately before the
    /// CONNECT, so no wait — however long the backoff has grown — sits between
    /// minting a token and presenting it.
    /// </summary>
    private async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Replaces v4's ConnectingFailedAsync re-mint and is stronger than it
            // was: every attempt presents a fresh credential, not only those that
            // follow a refusal.
            token.Value = await tokens.GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
        {
            // Keycloak is away, and the credential already in the slot may still
            // have life in it — so the attempt continues rather than being
            // abandoned. Until now a mint failure was caught with the CONNECT and
            // skipped it, and was reported as a publish failure to "(connect)":
            // the wrong channel for a token error, and a Keycloak blink that
            // outlasted the backoff.
            logger.MqttPublisherTokenFailed(exception.Message);
        }

        try
        {
            // The result code is the answer; the absence of an exception is not.
            // MQTTnet 5 dropped ThrowOnNonSuccessfulConnectResponse, so a refusal
            // returns normally — and taken for a connection it reset the backoff,
            // logged a connect, and then called ReportDrops, which declares an
            // outage over that never ended and clears the counter. The loop then
            // waited on a drop no connection could ever raise: a permanent hang,
            // with every later sample counted and never reported.
            MqttClientConnectResult result = await client.ConnectAsync(clientOptions!, cancellationToken);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                return true;
            }

            logger.MqttPublisherConnectFailed($"{host}:{port}", result.ResultCode.ToString());
            return false;
        }
        catch (Exception exception) when (!IsShutdown(exception, cancellationToken))
        {
            logger.MqttPublisherConnectFailed($"{host}:{port}", exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Whether an exception is this loop being stopped, rather than a failure to
    /// retry past. HttpClient's own timeout and the resilience pipeline both
    /// raise an <see cref="OperationCanceledException"/> from a token nothing
    /// here owns, so a filter that reads the type alone lets one out of the loop
    /// and into the catch that means "shutting down".
    /// </summary>
    private static bool IsShutdown(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        logger.MqttPublisherDisconnected($"{host}:{port}");
        dropped?.TrySetResult();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Counts a discarded sample. <b>Nothing is logged here</b>: the timeline
    /// emits many samples a second, so a line per drop would bury the one
    /// summary that explains the gap.
    /// </summary>
    private void Drop()
    {
        if (Interlocked.Increment(ref droppedSamples) == 1)
        {
            outageStartedAt = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// One warning per outage, on the connect that ends it, naming what the
    /// outage cost. Silent when nothing was dropped.
    /// </summary>
    private void ReportDrops()
    {
        long count = Interlocked.Exchange(ref droppedSamples, 0);
        if (count == 0)
        {
            return;
        }

        logger.MqttSamplesDropped(count, Stopwatch.GetElapsedTime(outageStartedAt).TotalSeconds);
    }

    private static (string Host, int Port) ParseHost(string mqttHost)
    {
        string value = mqttHost ?? string.Empty;
        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        string[] parts = value.Split(':');
        string host = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "localhost";
        int port = parts.Length > 1 && int.TryParse(parts[1], out int parsed) ? parsed : 1883;
        return (host, port);
    }

    private sealed class TokenHolder
    {
        public string Value { get; set; } = string.Empty;
    }

    // MQTTnet's credentials provider is synchronous; it reads the latest token
    // the connect loop mints, so every (re)connect presents a live JWT.
    private sealed class TokenCredentials(TokenHolder token) : IMqttClientCredentialsProvider
    {
        public string GetUserName(MqttClientOptions clientOptions) => Username;

        public byte[] GetPassword(MqttClientOptions clientOptions) => Encoding.UTF8.GetBytes(token.Value);
    }
}
