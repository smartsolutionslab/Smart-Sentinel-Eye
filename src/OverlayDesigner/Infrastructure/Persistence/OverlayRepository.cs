using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.OverlayDesigner.Domain.Overlay;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.OverlayDesigner.Infrastructure.Persistence;

public sealed class OverlayRepository(
    OverlayDesignerDbContext dbContext,
    ITransactionalCommit commit,
    IDomainEventDispatcher domainEventDispatcher) : IOverlayRepository
{
    public async Task<Option<Overlay>> GetByIdentifierAsync(
        OverlayIdentifier overlay, CancellationToken cancellationToken)
    {
        Overlay? found = await dbContext.Overlays
            .FirstOrDefaultAsync(candidate => candidate.Id == overlay, cancellationToken);
        return found is null ? Option<Overlay>.None : Option<Overlay>.Some(found);
    }

    public async Task<Option<Overlay>> GetByNameAsync(
        OverlayName name, CancellationToken cancellationToken)
    {
        Ensure.That(name).IsNotNull();
        // FR-006 ignores archived chains, and the same predicate has to answer
        // here and in ux_overlays_name_active. Not for the saved EXISTS over
        // overlay_revisions: if the check and the index could differ, the gap
        // would be a create the handler admits and Postgres refuses, reaching
        // the caller as the generic RESOURCE_ALREADY_EXISTS where the specific
        // OVERLAY_NAME_TAKEN was earned.
        Overlay? found = await dbContext.Overlays
            .Where(candidate => candidate.Name == name)
            .Where(candidate => candidate.ArchivedAt == null)
            .FirstOrDefaultAsync(cancellationToken);
        return found is null ? Option<Overlay>.None : Option<Overlay>.Some(found);
    }

    public void Add(Overlay overlay)
    {
        Ensure.That(overlay).IsNotNull();
        dbContext.Overlays.Add(overlay);
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        Overlay[] tracked = dbContext.ChangeTracker
            .Entries<Overlay>()
            .Where(entry => entry.Entity.PendingEvents.Count > 0)
            .Select(entry => entry.Entity)
            .ToArray();

        // Dispatch first: the announcement is captured into the outbox, and the
        // commit below writes the rows and the messages in one transaction
        // (spec 021 FR-001). It used to be the other way round, and the gap
        // between the two was where an integration event went missing.
        //
        // Which means a handler now runs before the write is durable, and one
        // that throws fails the write rather than leaving the row behind. Every
        // handler on this path publishes and does nothing else - checked across
        // all twelve (research.md R2), not assumed.
        foreach (Overlay overlay in tracked)
        {
            IDomainEvent[] events = overlay.PendingEvents.ToArray();
            overlay.ClearPendingEvents();
            await domainEventDispatcher.DispatchAsync(events, cancellationToken);
        }

        await commit.CommitAsync(cancellationToken);
    }
}
