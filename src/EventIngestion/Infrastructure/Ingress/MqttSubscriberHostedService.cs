using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Domain.DeadLetter;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.EventIngestion.Infrastructure.Ingress;

/// <summary>
/// MQTT subscriber for PLC + inference events (spec 006 FR-008).
/// Subscribes to <c>fab/+/+/+</c> at QoS 1, parses each delivery
/// into an <see cref="EventEnvelope"/>, and pushes it onto the
/// shared <see cref="IIngestChannel"/>. The persistence loop drains
/// the channel and runs the dedup + persist + publish.
///
/// <para>
/// When the channel is full the call to
/// <see cref="IIngestChannel.WriteAsync"/> blocks; that delays the
/// MQTTnet handler from returning, the broker stops getting ACKs,
/// queue depth absorbs the burst per spec FR-022.
/// </para>
///
/// <para>
/// Malformed deliveries (bad topic shape, malformed JSON, payload
/// over 64 KB) are captured in the <c>dead_letters</c> table per
/// spec FR-015 — audit-only, no fan-out.
/// </para>
/// </summary>
public sealed class MqttSubscriberHostedService(
    MosquittoConnectionFactory connectionFactory,
    IIngestChannel channel,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<MosquittoOptions> options,
    ILogger<MqttSubscriberHostedService> logger) : IHostedService, IDisposable
{
    private MqttConnection? connection;
    private CancellationTokenSource? loopCancellation;
    private Task? loop;

    // Deliveries this process rejected without being able to name their plant
    // (FR-012). Interlocked because MQTTnet dispatches handlers concurrently.
    private long unattributableDeadLetters;

    /// <summary>
    /// Attaches the message handler and <b>launches</b> the connect loop —
    /// deliberately without awaiting a first connection.
    ///
    /// <para>
    /// A plain <c>IMqttClient.ConnectAsync</c> throws when the broker is
    /// unreachable, and an exception escaping here does not fail a connection,
    /// it fails the whole host. Awaiting the first connect would therefore put
    /// event-ingestion into <c>FailedToStart</c> because mosquitto was slow —
    /// #2038 again, from the broker's side rather than Keycloak's. The loop
    /// retries on its own and there is nothing here worth waiting for.
    /// </para>
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        connection = await connectionFactory.CreateAsync(cancellationToken);
        connection.Client.ApplicationMessageReceivedAsync += OnMessageReceived;

        loopCancellation = new CancellationTokenSource();
        loop = new MqttConnectionLoop(connection, new MqttBackoff(), options.Value, logger)
            .RunAsync(loopCancellation.Token);

        logger.MqttSubscriberStarted(options.Value.SubscribeTopic);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            return;
        }

        IMqttClient stopping = connection.Client;
        stopping.ApplicationMessageReceivedAsync -= OnMessageReceived;

        if (loopCancellation is not null)
        {
            await loopCancellation.CancelAsync();
        }

        if (loop is not null)
        {
            // The loop swallows its own cancellation; this awaits it so the
            // disconnect below cannot race a connect the loop is still making.
            await loop;
        }

        await stopping.TryDisconnectAsync(
            MqttClientDisconnectOptionsReason.NormalDisconnection, "shutting down");
        stopping.Dispose();
        loopCancellation?.Dispose();

        connection = null;
        loop = null;
        loopCancellation = null;
        logger.MqttSubscriberStopped();
    }

    /// <summary>
    /// The safety net for a host that disposes without a clean
    /// <see cref="StopAsync"/> — after one, everything here is already null.
    /// </summary>
    public void Dispose()
    {
        loopCancellation?.Dispose();
        connection?.Client.Dispose();
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        // Spec 020 FR-001. Returning from this handler used to acknowledge the
        // delivery, which told the broker we had the event before anything had
        // been written — so it discarded its copy, and a failed write or a
        // restart lost an event we had already claimed. The acknowledgement now
        // waits for the write and travels with the envelope.
        args.AutoAcknowledge = false;

        string topic = args.ApplicationMessage.Topic;
        ReadOnlyMemory<byte> body = Body(args.ApplicationMessage);

        ParseResult result = TryParseEnvelope(topic, body);
        if (result.Envelope is null)
        {
            // A delivery that cannot be parsed will not parse on redelivery
            // either, so it is recorded and released here rather than left to
            // come back forever.
            //
            // Released only if the record was actually written. The capture
            // fails for the same reason an event write fails - the database is
            // away - and acknowledging anyway would discard the payload with
            // one log line, which is the defect this feature exists to close,
            // on the one path that has no second chance at it. Unacknowledged,
            // the broker brings it back and the capture is retried.
            if (await CaptureDeadLetterAsync(topic, body, result.Error ?? "unknown parse failure"))
            {
                await args.AcknowledgeAsync(CancellationToken.None);
            }

            return;
        }

        // WriteAsync blocks when the bounded channel is full — the handler stops
        // returning, the broker's in-flight window fills, and queue depth
        // absorbs the burst (FR-022). That backpressure now matters more, not
        // less: the window is what the acknowledgement is holding open.
        await channel.WriteAsync(
            new IngestDelivery(result.Envelope, new MqttDeliveryCompletion(args)),
            CancellationToken.None);
    }

    /// <summary>
    /// Reports a delivery's outcome by acknowledging it to the broker.
    ///
    /// <para>
    /// Both outcomes acknowledge, and that is deliberate. <c>Stored</c> releases
    /// the sender's copy because we now genuinely have it; <c>Abandoned</c>
    /// releases it because the delivery has been recorded in
    /// <c>dead_letters</c> and QoS 1 would otherwise redeliver it forever,
    /// filling the in-flight window and blocking every event behind it.
    /// </para>
    /// </summary>
    private sealed class MqttDeliveryCompletion(MqttApplicationMessageReceivedEventArgs args)
        : IIngestCompletion
    {
        public Task StoredAsync(CancellationToken cancellationToken) =>
            args.AcknowledgeAsync(cancellationToken);

        public Task AbandonedAsync(CancellationToken cancellationToken) =>
            args.AcknowledgeAsync(cancellationToken);
    }

    /// <summary>
    /// The delivery's bytes as one contiguous block.
    ///
    /// <para>
    /// MQTTnet 5 made <c>PayloadSegment</c> write-only; the readable payload is
    /// a <see cref="ReadOnlySequence{T}"/>, which a broker is free to hand over
    /// in several segments. The overwhelmingly common single-segment case is
    /// passed through without copying, and the rest is flattened rather than
    /// parsed segment-wise — one allocation on a path that would otherwise need
    /// a second JSON reader for a case MQTT payloads under 64 KB do not hit.
    /// </para>
    /// </summary>
    private static ReadOnlyMemory<byte> Body(MqttApplicationMessage message) =>
        message.Payload.IsSingleSegment ? message.Payload.First : message.Payload.ToArray();

    private static ParseResult TryParseEnvelope(string topic, ReadOnlyMemory<byte> body)
    {
        // Topic shape: fab/{fabId}/{source}/{deviceId}
        string[] segments = topic.Split('/');
        if (segments.Length != 4 || segments[0] != "fab")
        {
            return new ParseResult(null, $"Unexpected MQTT topic shape: '{topic}'.");
        }

        FabIdentifier fab;
        Source source;
        DeviceIdentifier device;
        MqttIngressPayload payload;
        try
        {
            fab = FabIdentifier.From(segments[1]);
            source = Source.From(segments[2]);
            device = DeviceIdentifier.From(segments[3]);

            payload = JsonSerializer.Deserialize<MqttIngressPayload>(body.Span)
                ?? throw new InvalidOperationException("payload is null");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        {
            return new ParseResult(null, $"envelope parse failed: {ex.Message}");
        }

        Payload payloadVo;
        try
        {
            payloadVo = Payload.From(payload.Payload.GetRawText());
        }
        catch (ArgumentException ex)
        {
            return new ParseResult(null, $"payload rejected: {ex.Message}");
        }

        // Guarded like the two blocks above. Unguarded, a field the value
        // objects reject threw straight out of the MQTT handler: MQTTnet never
        // ACKed, the broker stalled once its in-flight window filled, and the
        // delivery was never dead-lettered (FR-015) — so one bad message
        // wedged ingestion permanently, because QoS 1 redelivers it forever.
        EventEnvelope envelope;
        try
        {
            envelope = new(
                EventIdentifier.From(payload.EventId),
                fab,
                source,
                device,
                Kind.From(payload.Kind),
                OccurredAt.From(payload.OccurredAt),
                payloadVo);
        }
        catch (ArgumentException ex)
        {
            return new ParseResult(null, $"envelope rejected: {ex.Message}");
        }

        return new ParseResult(envelope, null);
    }

    /// <summary>
    /// The plant the delivery address establishes, or <c>null</c> when it
    /// establishes none (spec 018 FR-008, FR-010).
    ///
    /// <para>
    /// The two failure modes are not the same and must not be conflated. A
    /// malformed <em>payload</em> under a well-formed topic — the common case —
    /// has a plant, and its own operators can see it. Only a malformed
    /// <em>address</em> leaves the origin unknown. Treating every rejection as
    /// orphaned would hide the whole list while looking like correct scoping.
    /// </para>
    ///
    /// <para>
    /// Four segments do not by themselves yield a usable fab: the segment may be
    /// present and still not be a legal <see cref="FabIdentifier"/>. So this
    /// attempts the parse and falls back to null rather than assuming.
    /// </para>
    /// </summary>
    private static FabIdentifier? TryParseFab(string topic)
    {
        string[] segments = topic.Split('/');
        if (segments.Length != 4 || segments[0] != "fab")
        {
            return null;
        }

        try
        {
            return FabIdentifier.From(segments[1]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<bool> CaptureDeadLetterAsync(string topic, ReadOnlyMemory<byte> body, string error)
    {
        logger.RejectingMqttDelivery(topic, error);

        FabIdentifier? fab = TryParseFab(topic);
        if (fab is null)
        {
            // FR-012. The row is about to become visible to nobody (FR-011),
            // which is the fail-closed answer but also a real diagnostic gap.
            // The topic and the running count only — never the payload, which is
            // production data of unknown origin and the reason the row is
            // hidden in the first place.
            logger.UnattributableDeadLetter(topic, Interlocked.Increment(ref unattributableDeadLetters));
        }

        string raw = Encoding.UTF8.GetString(body.Span);
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IDeadLetterRepository deadLetters =
                scope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
            deadLetters.Add(DeadLetter.Capture(DeliveryTopic.From(topic), fab, RawPayload.From(raw), RejectionReason.From(error), clock));
            await deadLetters.SaveAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database outage must not bring the subscriber down, so this is
            // caught - but it is no longer "best effort and move on". The
            // caller leaves the delivery unacknowledged, so the broker keeps it
            // and the capture is retried rather than the payload being lost.
            logger.DeadLetterCaptureFailed(ex, topic, ex.Message);
            return false;
        }
    }

    private sealed record ParseResult(EventEnvelope? Envelope, string? Error);
}
