using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Infrastructure.Persistence;

public sealed class LayoutRepository(
    LayoutCompositionDbContext dbContext,
    ITransactionalCommit commit,
    IDomainEventDispatcher domainEventDispatcher) : ILayoutRepository
{
    public async Task<Option<Layout>> GetByIdentifierAsync(
        IReadOnlyList<FabIdentifier> fabs, LayoutIdentifier layout, CancellationToken cancellationToken)
    {
        Ensure.That(fabs).IsNotNull();

        // The fab filter is part of the lookup (FR-006). A layout in another
        // fab therefore comes back as None, exactly like an identifier that
        // matches nothing — and it does so before any caller can read a
        // precondition off the aggregate.
        Layout? found = await dbContext.Layouts.FirstOrDefaultAsync(
            candidate => candidate.Id == layout && fabs.Contains(candidate.Fab),
            cancellationToken);
        return found is null ? Option<Layout>.None : Option<Layout>.Some(found);
    }

    public async Task<Option<Layout>> GetByNameAsync(
        FabIdentifier fab, LayoutName name, CancellationToken cancellationToken)
    {
        Ensure.That(fab).IsNotNull();
        Ensure.That(name).IsNotNull();
        // FR-006 ignores archived chains, and the same predicate has to answer
        // here and in ux_layouts_fab_name_active. Not for the saved EXISTS over
        // layout_revisions: if the check and the index could differ, the gap
        // would be a create the handler admits and Postgres refuses, reaching
        // the caller as the generic RESOURCE_ALREADY_EXISTS where the specific
        // LAYOUT_NAME_TAKEN was earned.
        //
        // Fab first: a name is unique only within one (spec 017 FR-019), so
        // without it this returns another fab's layout — and the caller turns
        // that into a 409 that confirms the layout exists.
        Layout? found = await dbContext.Layouts
            .Where(candidate => candidate.Fab == fab)
            .Where(candidate => candidate.Name == name)
            .Where(candidate => candidate.ArchivedAt == null)
            .FirstOrDefaultAsync(cancellationToken);
        return found is null ? Option<Layout>.None : Option<Layout>.Some(found);
    }

    public void Add(Layout layout)
    {
        Ensure.That(layout).IsNotNull();

        dbContext.Layouts.Add(layout);
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        Layout[] tracked = dbContext.ChangeTracker
            .Entries<Layout>()
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
        foreach (Layout layout in tracked)
        {
            IDomainEvent[] events = layout.PendingEvents.ToArray();
            layout.ClearPendingEvents();
            await domainEventDispatcher.DispatchAsync(events, cancellationToken);
        }

        await commit.CommitAsync(cancellationToken);
    }
}
