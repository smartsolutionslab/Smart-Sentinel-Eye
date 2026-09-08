using Microsoft.Extensions.Logging;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.Shared.Kernel;
using EventAggregate = SmartSentinelEye.EventIngestion.Domain.Event.Event;

namespace SmartSentinelEye.EventIngestion.Application.Commands.Handlers;

/// <summary>
/// The batched write behind the broker ingress (spec 020 FR-010). Same three
/// steps as <see cref="IngestEventCommandHandler"/> — is it already here, build
/// it, store it — taken once for the whole batch rather than once per event.
///
/// <para>
/// Deliberately <b>not</b> an <c>ICommandHandler&lt;,&gt;</c>: nothing dispatches
/// it, the persistence loop calls it directly, and giving it the interface would
/// advertise a general batch-ingest capability that the HTTP paths must not use.
/// They store one event and answer for it (FR-001), which is the opposite trade.
/// </para>
///
/// <para>
/// All-or-nothing on purpose. One envelope the database refuses fails the whole
/// insert, and the caller falls back to storing them one at a time — which is
/// where FR-009 is kept. Salvaging the batch here would mean guessing which row
/// Postgres objected to.
/// </para>
/// </summary>
public sealed class IngestEventBatchCommandHandler(
    IEventRepository events,
    IClock clock,
    ILogger<IngestEventBatchCommandHandler> logger)
{
    /// <summary>
    /// Stores what needs storing, and says which envelopes a domain rule
    /// refused.
    ///
    /// <para>
    /// The refusals are reported rather than folded into the success, because
    /// the caller owes them a dead letter before it releases the sender's copy
    /// (FR-008). Returning "all of them, acknowledge away" was the first
    /// version and it discarded a future-skewed event with nothing but a
    /// warning — the silent loss this feature exists to close, on its own fast
    /// path.
    /// </para>
    /// </summary>
    public async Task<IngestEventBatchResult> HandleAsync(
        IngestEventBatchCommand command, CancellationToken cancellationToken)
    {
        Ensure.That(command).IsNotNull();

        IReadOnlyList<EventEnvelope> envelopes = command.Envelopes;
        if (envelopes.Count == 0)
        {
            return new IngestEventBatchResult([]);
        }

        // FR-002, once for the batch. Redelivery became the ordinary way an
        // interruption ends with spec 020, so this is the common case now and
        // not an edge one.
        IReadOnlySet<EventIdentifier> already = await events.ExistingAsync(
            [.. envelopes.Select(envelope => (envelope.Fab, envelope.Identifier))],
            cancellationToken);

        // Seeded with what is already stored, then grown as the batch is built,
        // so the same check answers both "already here" and "already in this
        // batch". Without the second, a delivery that arrived twice before
        // either copy was stored would violate the unique constraint and fail
        // the whole batch — sending 199 healthy events down the slow path for a
        // duplicate the idempotency rule was supposed to absorb.
        HashSet<EventIdentifier> seen = [.. already];
        List<EventEnvelope> refused = [];
        Dictionary<Source, long> storedBySource = [];

        foreach (EventEnvelope envelope in envelopes)
        {
            if (!seen.Add(envelope.Identifier))
            {
                logger.IdempotentReDelivery(envelope.Identifier, envelope.Fab);
                continue;
            }

            Option<EventAggregate> built = Build(envelope);
            if (built.HasValue)
            {
                events.Add(built.Value);
                storedBySource[envelope.Source] = storedBySource.GetValueOrDefault(envelope.Source) + 1;
            }
            else
            {
                refused.Add(envelope);
            }
        }

        await events.SaveAsync(cancellationToken);

        RecordVolume(storedBySource);
        return new IngestEventBatchResult(refused);
    }

    /// <summary>
    /// Counts what the commit actually stored (spec 103 FR-005/FR-006), one
    /// measurement per source rather than one per envelope.
    ///
    /// <para>
    /// Called only after <c>SaveAsync</c> has returned. The insert is
    /// all-or-nothing, so a throwing save must contribute nothing before the
    /// caller retries the same envelopes one at a time — and spec 020 made that
    /// retry the ordinary way an interruption ends, so counting during the build
    /// loop would inflate the common case rather than an edge one.
    /// </para>
    ///
    /// <para>
    /// Grouped by the envelope's own source because a drained batch routinely
    /// mixes <c>plc</c> and <c>inference</c>; folding them into one bucket would
    /// need an <c>mqtt</c> token <see cref="Source"/> does not have.
    /// </para>
    /// </summary>
    private static void RecordVolume(Dictionary<Source, long> storedBySource)
    {
        foreach ((Source source, long stored) in storedBySource)
        {
            IngestVolume.Record(source, stored);
        }
    }

    /// <summary>
    /// Builds the aggregate, or nothing if this envelope can never be built.
    /// The future-skew rule (spec 006 FR-014) is the only way this fails, and it
    /// fails the same way every time — so the envelope is left out of the insert
    /// here rather than failing the batch and sending the other 199 down the
    /// slow path once per retry, for ever.
    /// </summary>
    private Option<EventAggregate> Build(EventEnvelope envelope)
    {
        try
        {
            return Option<EventAggregate>.Some(EventAggregate.Ingest(
                envelope.Identifier,
                envelope.Fab,
                envelope.Source,
                envelope.Device,
                envelope.Kind,
                envelope.OccurredAt,
                envelope.Payload,
                clock));
        }
        catch (ArgumentException)
        {
            logger.BatchEnvelopeRejected(
                envelope.Identifier,
                envelope.Source,
                envelope.Device,
                IngestEventFailures.OccurredAtTooFarInFuture(envelope.OccurredAt.Value).Code);
            return Option<EventAggregate>.None;
        }
    }
}
