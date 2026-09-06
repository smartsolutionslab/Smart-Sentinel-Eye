using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartSentinelEye.EventIngestion.Domain.Event;

namespace SmartSentinelEye.EventIngestion.Infrastructure;

[ExcludeFromCodeCoverage]
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Persistence loop started.")]
    public static partial void PersistenceLoopStarted(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Persistence loop stopping (cancellation).")]
    public static partial void PersistenceLoopStopping(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest failed for {Identifier} ({Source}/{Device}): {Code}.")]
    public static partial void IngestFailed(this ILogger logger, EventIdentifier identifier, Source source, DeviceIdentifier device, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest dispatch faulted for {Identifier} in fab {Fab}; the envelope is dropped and the loop continues.")]
    public static partial void IngestDispatchFaulted(this ILogger logger, EventIdentifier identifier, FabIdentifier fab, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT subscriber started; subscribed to topic '{Topic}' at QoS 1.")]
    public static partial void MqttSubscriberStarted(this ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT subscriber stopped.")]
    public static partial void MqttSubscriberStopped(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT subscriber connected to '{Broker}' as '{Username}'.")]
    public static partial void MqttSubscriberConnected(this ILogger logger, string broker, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT subscriber disconnected: {Reason}.")]
    public static partial void MqttSubscriberDisconnected(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "MQTT subscriber could not connect to '{Broker}': {Error}.")]
    public static partial void MqttSubscriberConnectFailed(this ILogger logger, string broker, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not refresh the MQTT token before reconnect: {Error}.")]
    public static partial void MqttReconnectTokenFailed(this ILogger logger, string error);

    // Distinct from MqttSubscriberConnected on purpose (spec 079 US3-AC1). A
    // subscriber that reconnected without resubscribing is connected, healthy
    // and receiving nothing; a connect line with no subscribe line beside it is
    // the whole of that defect, and this is what makes it readable from the log.
    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT subscriber subscribed to '{Topic}' at QoS 1.")]
    public static partial void MqttSubscriberResubscribed(this ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "MQTT subscriber could not subscribe to '{Topic}': {Error}. "
            + "Dropping the connection so the next attempt subscribes again.")]
    public static partial void MqttSubscriberSubscribeFailed(this ILogger logger, string topic, string error);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MQTT subscriber retrying in {DelaySeconds:F1}s (attempt {Attempt}).")]
    public static partial void MqttSubscriberRetryScheduled(this ILogger logger, double delaySeconds, int attempt);

    // Warning, not Error: the subscriber is degraded rather than broken — it
    // starts, the connect loop retries with a capped backoff, and the token is
    // minted again before every attempt. An Error here would page someone for a
    // condition that resolves itself as soon as Keycloak answers.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not mint the MQTT token at startup: {Error}. "
            + "Starting anyway; the subscriber will connect once Keycloak answers.")]
    public static partial void InitialMqttTokenFailed(this ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting MQTT delivery on '{Topic}': {Error}.")]
    public static partial void RejectingMqttDelivery(this ILogger logger, string topic, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to capture dead letter for topic '{Topic}': {Message}.")]
    public static partial void DeadLetterCaptureFailed(this ILogger logger, Exception exception, string topic, string message);

    // Spec 018 FR-012. Such a delivery is visible to nobody (FR-011), so this
    // line is the only trace an operator gets: invisible is acceptable,
    // invisible and unnoticed is not. The topic and the count, never the
    // payload.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected delivery on '{Topic}' names no fab; it is visible to no operator. {Count} such deliveries since start.")]
    public static partial void UnattributableDeadLetter(this ILogger logger, string topic, long count);

    [LoggerMessage(Level = LogLevel.Information, Message = "No per-fab partitions under 'events' yet; skipping rollover. Add a fab via 'CREATE TABLE events_<fabId> PARTITION OF events FOR VALUES IN (...)'.")]
    public static partial void NoFabPartitions(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ensured partition {Partition} (FROM {From} TO {To}).")]
    public static partial void EnsuredPartition(this ILogger logger, string partition, string from, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ensured event storage {Partition} for fab {Fab}.")]
    public static partial void EnsuredFabPartition(this ILogger logger, string partition, FabIdentifier fab);

    // Spec 019 FR-008. Says the cause, not just that something failed: no
    // events_<fab> partition exists, so nothing this fab sends can be stored
    // until provisioning runs. The envelope is still dropped — what a loop
    // should do with an envelope it cannot persist is #1546, and answering it
    // here for one cause would leave the loop with two behaviours to reconcile.
    [LoggerMessage(Level = LogLevel.Error, Message = "No event storage for fab {Fab}; {Identifier} is dropped. A partition for this fab is missing — provisioning has not run since it was added.")]
    public static partial void NoStorageForFab(this ILogger logger, EventIdentifier identifier, FabIdentifier fab, Exception exception);

    // Spec 020 FR-006. The pair matters more than either line: an interruption
    // with no recovery beside it reads as loss, and a recovery nobody can see is
    // indistinguishable from one that never happened. Both carry the count,
    // because "some events were held up" is not an answer anybody can act on.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest interrupted: {Count} event(s) could not be stored and are being retried. Nothing has been acknowledged, so nothing is lost yet.")]
    public static partial void IngestInterrupted(this ILogger logger, int count);

    // Debug, not Warning. During an outage every batch fails and this would be
    // one line per batch per retry; the interruption itself is reported once by
    // IngestInterrupted, which is where an operator should be looking.
    [LoggerMessage(Level = LogLevel.Debug, Message = "A batch of {Count} could not be stored together; storing them one at a time to find the row that cannot.")]
    public static partial void BatchFellBackToSingles(this ILogger logger, int count, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest recovered: {Count} event(s) that had been failing are now stored.")]
    public static partial void IngestRecovered(this ILogger logger, int count);

    // FR-007/FR-008. The delivery is released after this, so this line is the
    // last chance anybody has to know it existed — hence Error, and hence the
    // dead letter that is written before it.
    [LoggerMessage(Level = LogLevel.Error, Message = "Giving up on {Identifier} in fab {Fab} after {Window} of failing writes; recorded as a dead letter and released so it stops being redelivered.")]
    public static partial void IngestAbandoned(this ILogger logger, EventIdentifier identifier, FabIdentifier fab, TimeSpan window);

    // Recording the failure failed too — which during an outage is the ordinary
    // case, since both writes go to the same database. The delivery stays
    // unacknowledged and keeps being retried, so this is a diagnostic rather
    // than a loss.
    // The sender was not told the outcome. It still holds its copy, so the
    // event is safe — what is at risk is a duplicate on redelivery, which the
    // idempotency rule absorbs. Guarded because an unguarded acknowledgement
    // that throws faults the loop and stops the host: spec 018's defect.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not acknowledge {Identifier} to its sender; it will be redelivered and deduplicated.")]
    public static partial void AcknowledgementFailed(this ILogger logger, EventIdentifier identifier, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record {Identifier} as a dead letter; it stays unacknowledged and will be retried.")]
    public static partial void IngestAbandonFailed(this ILogger logger, EventIdentifier identifier, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying EventIngestion EF Core migrations.")]
    public static partial void ApplyingMigrations(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "EventIngestion migrations applied.")]
    public static partial void MigrationsApplied(this ILogger logger);
}
