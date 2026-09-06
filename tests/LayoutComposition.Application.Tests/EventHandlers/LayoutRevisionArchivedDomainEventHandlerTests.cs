using System.Globalization;
using SmartSentinelEye.LayoutComposition.Application.EventHandlers;
using SmartSentinelEye.LayoutComposition.Application.Tests.Fakes;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.LayoutComposition.Domain.Layout.Events;
using SmartSentinelEye.Shared.Contracts.LayoutComposition;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Application.Tests.EventHandlers;

public class LayoutRevisionArchivedDomainEventHandlerTests
{
    private static readonly FabIdentifier Munich = FabIdentifier.From("munich");

    private static readonly DateTimeOffset FixedMoment =
        DateTimeOffset.Parse("2026-05-26T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Handle_publishes_integration_event_and_broadcasts_notification()
    {
        FakeEventBus bus = new();
        FakeLayoutLifecycleBroadcaster broadcaster = new();
        LayoutRevisionArchivedDomainEventHandler handler = new(bus, broadcaster);

        LayoutIdentifier layout = LayoutIdentifier.New();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        LayoutRevisionArchivedDomainEvent domainEvent = new(
            Munich, layout, LayoutRevisionNumber.One, FixedMoment, by);

        await handler.Handle(domainEvent, CancellationToken.None);

        bus.Published.ShouldHaveSingleItem();
        LayoutRevisionArchivedV1 v1 = bus.Published.Single().ShouldBeOfType<LayoutRevisionArchivedV1>();
        v1.Layout.ShouldBe(layout.Value);
        v1.RevisionNumber.ShouldBe(1);
        v1.ArchivedAt.ShouldBe(FixedMoment);
        v1.ArchivedBy.ShouldBe(by.Value);

        broadcaster.Archived.ShouldHaveSingleItem();
        broadcaster.Archived.Single().Layout.ShouldBe(layout);
    }

    // #2071. The fab the revision belongs to reaches the audit row only if the
    // publisher stamps it: a stored row records fab = null and nothing more, so
    // the read side cannot tell "legitimately cross-fab" from "the publisher
    // forgot" and hands the row to every operator of every fab (#1300).
    [Fact]
    public async Task Stamps_the_archiving_fab_on_the_published_V1()
    {
        FakeEventBus bus = new();
        LayoutRevisionArchivedDomainEventHandler handler = new(bus, new FakeLayoutLifecycleBroadcaster());

        LayoutRevisionArchivedDomainEvent domainEvent = new(
            Munich, LayoutIdentifier.New(), LayoutRevisionNumber.One, FixedMoment,
            OperatorIdentifier.From(Guid.CreateVersion7()));

        await handler.Handle(domainEvent, CancellationToken.None);

        LayoutRevisionArchivedV1 v1 = bus.Published.Single().ShouldBeOfType<LayoutRevisionArchivedV1>();
        v1.Metadata.Fab.ShouldBe("munich");
    }
}
