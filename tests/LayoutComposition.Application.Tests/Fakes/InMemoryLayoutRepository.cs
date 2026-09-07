using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Application.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ILayoutRepository"/> for handler tests.
/// SaveAsync clears pending events to mimic the real
/// dispatcher-after-Save flow.
/// </summary>
public sealed class InMemoryLayoutRepository : ILayoutRepository
{
    private readonly List<Layout> _layouts = [];

    public IReadOnlyList<Layout> Layouts => _layouts;

    public Task<Option<Layout>> GetByIdentifierAsync(
        IReadOnlyList<FabIdentifier> fabs, LayoutIdentifier layout, CancellationToken cancellationToken)
    {
        Ensure.That(fabs).IsNotNull();
        // Fab as part of the lookup, mirroring the real repository (FR-006).
        Layout? found = _layouts.SingleOrDefault(
            candidate => candidate.Id == layout && fabs.Contains(candidate.Fab));
        return Task.FromResult(found is null ? Option<Layout>.None : Option<Layout>.Some(found));
    }

    public Task<Option<Layout>> GetByNameAsync(
        FabIdentifier fab, LayoutName name, CancellationToken cancellationToken)
    {
        Ensure.That(fab).IsNotNull();
        Ensure.That(name).IsNotNull();
        // Fab first, mirroring the real repository: a name is unique only
        // within one (spec 017 FR-019). Archived-ness likewise mirrors it and
        // now reads the chain's own marker rather than its revisions (spec 086)
        // — left on the old predicate this fake would keep the Application
        // suite green against a rule production no longer applies.
        Layout? found = _layouts.SingleOrDefault(candidate =>
            candidate.Fab == fab &&
            candidate.Name == name &&
            candidate.ArchivedAt is null);
        return Task.FromResult(found is null ? Option<Layout>.None : Option<Layout>.Some(found));
    }

    public void Add(Layout layout)
    {
        Ensure.That(layout).IsNotNull();
        _layouts.Add(layout);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        foreach (Layout layout in _layouts)
        {
            layout.ClearPendingEvents();
        }
        return Task.CompletedTask;
    }
}
