using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.LayoutComposition.Domain.Layout.Events;
using SmartSentinelEye.Shared.Contracts;
using SmartSentinelEye.Shared.Contracts.LayoutComposition;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Application.EventHandlers;

/// <summary>
/// Twin of <see cref="LayoutRevisionPublishedDomainEventHandler"/>:
/// publishes <see cref="LayoutRevisionArchivedV1"/> + broadcasts the
/// Archived notification to connected kiosks so they can force-
/// disconnect (FR-011).
/// </summary>
public sealed class LayoutRevisionArchivedDomainEventHandler(
    IEventBus events,
    ILayoutLifecycleBroadcaster broadcaster)
    : IDomainEventHandler<LayoutRevisionArchivedDomainEvent>
{
    public async Task Handle(LayoutRevisionArchivedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        Ensure.That(domainEvent).IsNotNull();

        var (fab, layout, revisionNumber, archivedAt, archivedBy) = domainEvent;

        await events.PublishAsync(
            new LayoutRevisionArchivedV1(
                Layout: layout.Value,
                RevisionNumber: revisionNumber.Value,
                ArchivedAt: archivedAt,
                ArchivedBy: archivedBy.Value,
                Metadata: new EventMetadata(Guid.CreateVersion7(), archivedAt, fab.Value, archivedBy.Value)),
            cancellationToken);

        await broadcaster.ArchivedAsync(
            new LayoutRevisionArchivedNotification(
                fab,
                layout,
                revisionNumber,
                archivedAt),
            cancellationToken);
    }
}
