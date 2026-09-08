using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.EventIngestion.Application.Commands;
using SmartSentinelEye.EventIngestion.Application.Commands.Handlers;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Application.Tests.Fakes;
using SmartSentinelEye.EventIngestion.Domain.Event;

namespace SmartSentinelEye.EventIngestion.Application.Tests.Commands;

/// <summary>
/// Spec 020 FR-010. The batch handler exists to take the two round trips once
/// rather than once per event, and the ways that goes wrong are all about which
/// envelopes it decides not to insert.
/// </summary>
[Collection(IngestVolumeCollection.Name)]
public class IngestEventBatchCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-05-28T08:14:33.040Z", CultureInfo.InvariantCulture);

    private static EventEnvelope BuildEnvelope(
        EventIdentifier? identifier = null,
        DateTimeOffset? occurredAt = null,
        Source? source = null) =>
        new(
            identifier ?? EventIdentifier.New(),
            FabIdentifier.From("munich"),
            source ?? Source.Plc,
            DeviceIdentifier.From("station-4"),
            Kind.From("PlcCycleStart"),
            OccurredAt.From(occurredAt ?? Now),
            Payload.From("{\"cycleId\":\"abc\"}"));

    private static IngestEventBatchCommandHandler Handler(InMemoryEventRepository repository) =>
        new(repository, new FakeClock(Now),
            NullLogger<IngestEventBatchCommandHandler>.Instance);

    private static IngestEventCommandHandler SingleHandler(InMemoryEventRepository repository) =>
        new(repository, new FakeClock(Now),
            NullLogger<IngestEventCommandHandler>.Instance);

    [Fact]
    public async Task Stores_every_envelope_in_the_batch()
    {
        InMemoryEventRepository repository = new();

        IngestEventBatchResult result = await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(), BuildEnvelope(), BuildEnvelope()]),
            CancellationToken.None);

        result.Refused.ShouldBeEmpty();
        repository.Events.Count.ShouldBe(3);
    }

    /// <summary>
    /// FR-002/FR-003. Redelivery is the ordinary way an interruption ends now,
    /// so this is the common case rather than an edge one.
    /// </summary>
    [Fact]
    public async Task An_event_already_stored_is_not_inserted_again_but_is_still_acknowledged()
    {
        EventIdentifier identifier = EventIdentifier.New();
        InMemoryEventRepository repository = new();

        await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(identifier)]), CancellationToken.None);

        IngestEventBatchResult result = await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(identifier)]), CancellationToken.None);

        repository.Events.Count.ShouldBe(1, "the redelivery was stored a second time");
        result.Refused.ShouldBeEmpty();
    }

    /// <summary>
    /// The duplicate that arrives <b>within one batch</b>, before either copy
    /// has been stored — so the database cannot answer for it and the existence
    /// query does not see it. Left alone it violates the unique constraint,
    /// fails the whole batch, and sends 199 healthy events down the slow path
    /// for a duplicate the idempotency rule was meant to absorb.
    /// </summary>
    [Fact]
    public async Task A_duplicate_inside_one_batch_is_inserted_once()
    {
        EventIdentifier identifier = EventIdentifier.New();
        InMemoryEventRepository repository = new();

        IngestEventBatchResult result = await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(identifier), BuildEnvelope(identifier)]),
            CancellationToken.None);

        repository.Events.Count.ShouldBe(1);
        result.Refused.ShouldBeEmpty("both deliveries are storable; the event is there");
    }

    /// <summary>
    /// A domain rule refuses this envelope and will refuse it identically for
    /// ever. It must not fail the batch — and it must be <i>reported</i>, so the
    /// caller can record it before releasing the sender's copy (FR-008).
    /// Folding it into the success was the first version, and it discarded the
    /// envelope with nothing but a warning.
    /// </summary>
    [Fact]
    public async Task An_envelope_no_rule_will_ever_accept_is_left_out_but_does_not_fail_the_batch()
    {
        InMemoryEventRepository repository = new();
        EventEnvelope healthy = BuildEnvelope();
        EventEnvelope skewed = BuildEnvelope(occurredAt: Now.AddDays(30));

        IngestEventBatchResult result = await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([skewed, healthy]), CancellationToken.None);

        repository.Events.ShouldHaveSingleItem().Id.ShouldBe(healthy.Identifier);
        result.Refused.ShouldHaveSingleItem().Identifier.ShouldBe(skewed.Identifier);
    }

    /// <summary>
    /// Spec 103 scenario 3 and FR-005. Three envelopes stored in one commit are
    /// <b>one</b> measurement carrying three, not three measurements — the batch
    /// path exists precisely so the per-event work happens once.
    /// </summary>
    [Fact]
    public async Task A_stored_batch_is_counted_once_per_stored_envelope()
    {
        using RecordedIngestVolume recorded = new();
        InMemoryEventRepository repository = new();

        await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(), BuildEnvelope(), BuildEnvelope()]),
            CancellationToken.None);

        recorded.For(Source.Plc).ShouldHaveSingleItem().Count.ShouldBe(3);
    }

    /// <summary>
    /// Spec 103 scenario 4. The persistence loop drains whatever the broker
    /// sent, so one batch routinely mixes <c>plc</c> and <c>inference</c>. They
    /// are two tag values, not one <c>mqtt</c> bucket — a vocabulary
    /// <see cref="Source"/> does not have.
    /// </summary>
    [Fact]
    public async Task Two_sources_in_one_batch_are_counted_under_their_own_tags()
    {
        using RecordedIngestVolume recorded = new();
        InMemoryEventRepository repository = new();

        await Handler(repository).HandleAsync(
            new IngestEventBatchCommand(
            [
                BuildEnvelope(source: Source.Plc),
                BuildEnvelope(source: Source.Plc),
                BuildEnvelope(source: Source.Inference),
            ]),
            CancellationToken.None);

        recorded.TotalFor(Source.Plc).ShouldBe(2);
        recorded.TotalFor(Source.Inference).ShouldBe(1);
    }

    /// <summary>
    /// Spec 103 scenario 6. The counter follows what was <i>stored</i>, not the
    /// batch's length — a duplicate inside one batch is inserted once, so it
    /// counts once.
    /// </summary>
    [Fact]
    public async Task A_duplicate_within_a_batch_is_counted_once()
    {
        using RecordedIngestVolume recorded = new();
        EventIdentifier identifier = EventIdentifier.New();
        InMemoryEventRepository repository = new();

        await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(identifier), BuildEnvelope(identifier)]),
            CancellationToken.None);

        recorded.TotalFor(Source.Plc).ShouldBe(1, "one row was inserted, so one event arrived");
    }

    /// <summary>
    /// Spec 103 scenario 7, on the batch path. The refused envelope is reported
    /// rather than stored, so only its healthy neighbour counts.
    /// </summary>
    [Fact]
    public async Task An_envelope_refused_for_future_skew_is_not_counted()
    {
        using RecordedIngestVolume recorded = new();
        InMemoryEventRepository repository = new();

        await Handler(repository).HandleAsync(
            new IngestEventBatchCommand([BuildEnvelope(occurredAt: Now.AddDays(30)), BuildEnvelope()]),
            CancellationToken.None);

        recorded.TotalFor(Source.Plc).ShouldBe(1, "a refusal is not an arrival");
    }

    /// <summary>
    /// Spec 103 scenario 11 and FR-006. The insert is all-or-nothing, so a
    /// throwing <c>SaveAsync</c> stored nothing and must contribute nothing.
    /// Counting during the build loop instead would inflate the figure on every
    /// retry — and spec 020 made retry the ordinary way an interruption ends.
    /// </summary>
    [Fact]
    public async Task A_batch_whose_save_throws_counts_nothing()
    {
        using RecordedIngestVolume recorded = new();
        InMemoryEventRepository repository = new() { SaveFailure = new InvalidOperationException("insert refused") };

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Handler(repository).HandleAsync(
                new IngestEventBatchCommand([BuildEnvelope(), BuildEnvelope()]),
                CancellationToken.None));

        recorded.Measurements.ShouldBeEmpty("nothing committed, so nothing arrived");
    }

    /// <summary>
    /// Spec 103 scenario 11, the whole sequence. The persistence loop answers a
    /// failed batch by storing the same envelopes one at a time, so the two
    /// handlers see every envelope twice between them — and the total must
    /// still be exactly one per event.
    /// </summary>
    [Fact]
    public async Task The_retry_after_a_failed_batch_counts_each_event_exactly_once()
    {
        using RecordedIngestVolume recorded = new();
        InMemoryEventRepository repository = new() { SaveFailure = new InvalidOperationException("insert refused") };
        EventEnvelope first = BuildEnvelope();
        EventEnvelope second = BuildEnvelope();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Handler(repository).HandleAsync(
                new IngestEventBatchCommand([first, second]), CancellationToken.None));

        repository.SaveFailure = null;
        await SingleHandler(repository).HandleAsync(new IngestEventCommand(first), CancellationToken.None);
        await SingleHandler(repository).HandleAsync(new IngestEventCommand(second), CancellationToken.None);

        recorded.TotalFor(Source.Plc).ShouldBe(2, "each event arrived once, however many attempts it took");
    }
}
