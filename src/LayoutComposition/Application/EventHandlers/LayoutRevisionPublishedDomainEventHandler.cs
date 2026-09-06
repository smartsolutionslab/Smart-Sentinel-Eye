using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.LayoutComposition.Domain.Layout.Events;
using SmartSentinelEye.Shared.Contracts;
using SmartSentinelEye.Shared.Contracts.LayoutComposition;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Application.EventHandlers;

/// <summary>
/// Translates the in-process <see cref="LayoutRevisionPublishedDomainEvent"/>
/// into:
/// <list type="number">
/// <item>the cross-context <see cref="LayoutRevisionPublishedV2"/>
/// integration event carrying the full tile set + grid (via the
/// Wolverine outbox, ADR-0088), and</item>
/// <item>a best-effort, <em>lean</em> SignalR broadcast via
/// <see cref="ILayoutLifecycleBroadcaster"/> (no tile set — the picker
/// re-queries on receipt, ADR-0112 §3 / plan T010).</item>
/// </list>
/// Broadcast failures are swallowed by the broadcaster impl; the
/// kiosk's reconnect-and-reconcile path is the safety net (spec 003
/// FR-012).
/// </summary>
public sealed class LayoutRevisionPublishedDomainEventHandler(
    IEventBus events,
    ILayoutLifecycleBroadcaster broadcaster)
    : IDomainEventHandler<LayoutRevisionPublishedDomainEvent>
{
    public async Task Handle(LayoutRevisionPublishedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        Ensure.That(domainEvent).IsNotNull();

        var (fab, layout, revisionNumber, name, grid, domainTiles, publishedAt, publishedBy) = domainEvent;

        IReadOnlyList<LayoutTileV2> tiles = domainTiles
            .Select(tile => new LayoutTileV2(
                Camera: tile.Camera.Value,
                Overlay: tile.Overlay.Match(overlay => (Guid?)overlay.Value, () => null),
                Row: tile.Position.Row,
                Col: tile.Position.Col))
            .ToList();

        await events.PublishAsync(
            new LayoutRevisionPublishedV2(
                Layout: layout.Value,
                RevisionNumber: revisionNumber.Value,
                Name: name.Value,
                Tiles: tiles,
                GridRows: grid.Rows,
                GridCols: grid.Cols,
                PublishedAt: publishedAt,
                PublishedBy: publishedBy.Value,
                Metadata: new EventMetadata(Guid.CreateVersion7(), publishedAt, fab.Value, publishedBy.Value)),
            cancellationToken);

        await broadcaster.PublishedAsync(
            new LayoutRevisionPublishedNotification(
                fab,
                layout,
                revisionNumber,
                name,
                publishedAt),
            cancellationToken);
    }
}
