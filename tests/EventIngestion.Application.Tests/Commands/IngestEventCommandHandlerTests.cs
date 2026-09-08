using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSentinelEye.EventIngestion.Application.Commands;
using SmartSentinelEye.EventIngestion.Application.Commands.Handlers;
using SmartSentinelEye.EventIngestion.Application.Ingress;
using SmartSentinelEye.EventIngestion.Application.Tests.Fakes;
using SmartSentinelEye.EventIngestion.Domain.Event;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.EventIngestion.Application.Tests.Commands;

[Collection(IngestVolumeCollection.Name)]
public class IngestEventCommandHandlerTests
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

    private static IngestEventCommandHandler Handler(InMemoryEventRepository repository) =>
        new(repository, new FakeClock(Now),
            NullLogger<IngestEventCommandHandler>.Instance);

    [Fact]
    public async Task Happy_path_persists_the_event_and_raises_a_domain_event_carrying_the_envelope()
    {
        InMemoryEventRepository repo = new();
        IngestEventCommandHandler handler = new(
            repo, new FakeClock(Now),
            NullLogger<IngestEventCommandHandler>.Instance);

        EventEnvelope envelope = BuildEnvelope();
        Result<EventIdentifier, IngestEventError> result =
            await handler.HandleAsync(new IngestEventCommand(envelope), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(envelope.Identifier);
        repo.Events.ShouldHaveSingleItem().Id.ShouldBe(envelope.Identifier);
    }

    [Fact]
    public async Task Duplicate_event_returns_EventAlreadyIngested_and_does_not_double_insert()
    {
        EventIdentifier identifier = EventIdentifier.New();
        InMemoryEventRepository repo = new();
        IngestEventCommandHandler handler = new(
            repo, new FakeClock(Now),
            NullLogger<IngestEventCommandHandler>.Instance);

        Result<EventIdentifier, IngestEventError> first = await handler.HandleAsync(
            new IngestEventCommand(BuildEnvelope(identifier)), CancellationToken.None);
        first.IsSuccess.ShouldBeTrue();

        Result<EventIdentifier, IngestEventError> second = await handler.HandleAsync(
            new IngestEventCommand(BuildEnvelope(identifier)), CancellationToken.None);

        second.IsSuccess.ShouldBeFalse();
        second.Error.ShouldBeOfType<IngestEventError.EventAlreadyIngested>();
        repo.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OccurredAt_more_than_5_minutes_in_the_future_returns_typed_error()
    {
        InMemoryEventRepository repo = new();
        IngestEventCommandHandler handler = new(
            repo, new FakeClock(Now),
            NullLogger<IngestEventCommandHandler>.Instance);

        EventEnvelope envelope = BuildEnvelope(occurredAt: Now.AddMinutes(6));

        Result<EventIdentifier, IngestEventError> result =
            await handler.HandleAsync(new IngestEventCommand(envelope), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<IngestEventError.OccurredAtTooFarInFuture>();
        repo.Events.ShouldBeEmpty();
    }

    /// <summary>
    /// Spec 103 scenarios 1 and 2. This handler is the funnel both HTTP write
    /// paths reach storage through, so the tag has to come from the envelope
    /// rather than from the route — which is the only reason a single counter
    /// can serve two endpoints.
    /// </summary>
    [Theory]
    [InlineData("manual")]
    [InlineData("webhook")]
    public async Task A_stored_event_is_counted_once_under_its_source(string token)
    {
        using RecordedIngestVolume recorded = new();
        Source source = Source.From(token);
        InMemoryEventRepository repo = new();

        await Handler(repo).HandleAsync(
            new IngestEventCommand(BuildEnvelope(source: source)), CancellationToken.None);

        recorded.For(source).ShouldHaveSingleItem().Count.ShouldBe(1);
    }

    /// <summary>
    /// Spec 103 scenario 5. A redelivered event is the same event; counting it
    /// twice reports a burst that never happened. Asserted as a total rather
    /// than as an absence, so a counter that never increments at all cannot
    /// pass this test by doing nothing.
    /// </summary>
    [Fact]
    public async Task A_duplicate_re_delivery_is_not_counted()
    {
        using RecordedIngestVolume recorded = new();
        EventIdentifier identifier = EventIdentifier.New();
        InMemoryEventRepository repo = new();

        await Handler(repo).HandleAsync(
            new IngestEventCommand(BuildEnvelope(identifier)), CancellationToken.None);
        await Handler(repo).HandleAsync(
            new IngestEventCommand(BuildEnvelope(identifier)), CancellationToken.None);

        recorded.TotalFor(Source.Plc).ShouldBe(1, "the redelivery was counted as a second arrival");
    }

    /// <summary>
    /// Spec 103 scenario 7. A refusal is not an arrival: the future-skew branch
    /// returns before anything is stored, and the count is taken after the
    /// store commits.
    /// </summary>
    [Fact]
    public async Task An_event_refused_for_future_skew_is_not_counted()
    {
        using RecordedIngestVolume recorded = new();
        InMemoryEventRepository repo = new();

        await Handler(repo).HandleAsync(
            new IngestEventCommand(BuildEnvelope(occurredAt: Now.AddMinutes(6))),
            CancellationToken.None);

        recorded.Measurements.ShouldBeEmpty("a refused envelope never reached the store");
    }
}
