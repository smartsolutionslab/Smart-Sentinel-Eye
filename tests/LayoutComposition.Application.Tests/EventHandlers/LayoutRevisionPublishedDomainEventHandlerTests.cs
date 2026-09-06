using System.Globalization;
using SmartSentinelEye.LayoutComposition.Application.EventHandlers;
using SmartSentinelEye.LayoutComposition.Application.Tests.Fakes;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.LayoutComposition.Domain.Layout.Events;
using SmartSentinelEye.Shared.Contracts.LayoutComposition;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Application.Tests.EventHandlers;

public class LayoutRevisionPublishedDomainEventHandlerTests
{
    private static readonly FabIdentifier Munich = FabIdentifier.From("munich");

    private static readonly DateTimeOffset FixedMoment =
        DateTimeOffset.Parse("2026-05-26T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Handle_publishes_V2_with_the_tile_set_and_broadcasts_a_lean_notification()
    {
        FakeEventBus bus = new();
        FakeLayoutLifecycleBroadcaster broadcaster = new();
        LayoutRevisionPublishedDomainEventHandler handler = new(bus, broadcaster);

        LayoutIdentifier layout = LayoutIdentifier.New();
        CameraIdentifier cameraA = CameraIdentifier.From(Guid.CreateVersion7());
        CameraIdentifier cameraB = CameraIdentifier.From(Guid.CreateVersion7());
        OverlayIdentifier overlay = OverlayIdentifier.From(Guid.CreateVersion7());
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IReadOnlyList<Tile> tiles =
        [
            new Tile(cameraA, Option<OverlayIdentifier>.Some(overlay), GridPosition.From(0, 0)),
            new Tile(cameraB, Option<OverlayIdentifier>.None, GridPosition.From(0, 1)),
        ];
        LayoutRevisionPublishedDomainEvent domainEvent = new(
            Munich, layout, LayoutRevisionNumber.One, LayoutName.From("Line-1"),
            GridDimensions.From(1, 2), tiles, FixedMoment, by);

        await handler.Handle(domainEvent, CancellationToken.None);

        bus.Published.ShouldHaveSingleItem();
        LayoutRevisionPublishedV2 v2 = bus.Published.Single().ShouldBeOfType<LayoutRevisionPublishedV2>();
        v2.Layout.ShouldBe(layout.Value);
        v2.RevisionNumber.ShouldBe(1);
        v2.Name.ShouldBe("Line-1");
        v2.GridRows.ShouldBe(1);
        v2.GridCols.ShouldBe(2);
        v2.PublishedBy.ShouldBe(by.Value);
        v2.Tiles.Count.ShouldBe(2);
        LayoutTileV2 first = v2.Tiles.Single(tile => tile.Camera == cameraA.Value);
        first.Overlay.ShouldBe(overlay.Value);
        first.Row.ShouldBe(0);
        first.Col.ShouldBe(0);
        v2.Tiles.Single(tile => tile.Camera == cameraB.Value).Overlay.ShouldBeNull();

        broadcaster.Published.ShouldHaveSingleItem();
        broadcaster.Published.Single().Layout.ShouldBe(layout);
    }

    // #2071. Twin of the archived handler's assertion: the V2's own metadata,
    // not the broadcast notification's fab. A row stored with fab = null is
    // readable by every operator of every fab (#1300), and only the publisher
    // knows which it is.
    [Fact]
    public async Task Stamps_the_publishing_fab_on_the_published_V2()
    {
        FakeEventBus bus = new();
        LayoutRevisionPublishedDomainEventHandler handler = new(bus, new FakeLayoutLifecycleBroadcaster());

        IReadOnlyList<Tile> tiles =
        [
            new Tile(
                CameraIdentifier.From(Guid.CreateVersion7()),
                Option<OverlayIdentifier>.None,
                GridPosition.From(0, 0)),
        ];
        LayoutRevisionPublishedDomainEvent domainEvent = new(
            Munich, LayoutIdentifier.New(), LayoutRevisionNumber.One, LayoutName.From("Line-1"),
            GridDimensions.From(1, 1), tiles, FixedMoment,
            OperatorIdentifier.From(Guid.CreateVersion7()));

        await handler.Handle(domainEvent, CancellationToken.None);

        LayoutRevisionPublishedV2 v2 = bus.Published.Single().ShouldBeOfType<LayoutRevisionPublishedV2>();
        v2.Metadata.Fab.ShouldBe("munich");
    }
}
