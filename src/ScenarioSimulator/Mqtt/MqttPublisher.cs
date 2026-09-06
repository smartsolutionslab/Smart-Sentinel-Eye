using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
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
/// client (spec 079, ADR-0036). The needs differ anyway: the subscriber
/// resubscribes after every connect and this publisher has no subscriptions,
/// and this one counts dropped samples the subscriber has no use for.
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

        clientOptions = new MqttClientOptionsBuilder()
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
        catch (OperationCanceledException)
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
            await Task.Delay(delay, cancellationToken);
        }

        TaskCompletionSource drop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dropped = drop;

        try
        {
            // Minted after the delay and immediately before the CONNECT, so no
            // wait sits between minting a token and presenting it. This replaces
            // v4's ConnectingFailedAsync re-mint and is stronger than it was:
            // every attempt presents a fresh credential, not only those that
            // follow a refusal.
            token.Value = await tokens.GetAccessTokenAsync(cancellationToken);
            await client.ConnectAsync(clientOptions!, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.MqttPublishFailed("(connect)", exception.Message);
            return;
        }

        backoff.Reset();
        logger.MqttPublisherConnected($"{host}:{port}", Username);
        ReportDrops();

        await drop.Task.WaitAsync(cancellationToken);
    }

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
