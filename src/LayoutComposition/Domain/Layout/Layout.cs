using SmartSentinelEye.LayoutComposition.Domain.Layout.Events;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Domain.Layout;

/// <summary>
/// Aggregate root for a logical Layout chain (spec 003). One per logical
/// layout the operator sees by name; owns a collection of
/// <see cref="Revision"/> sub-entities. The chain invariant
/// <em>at-most-one-Published-revision-per-chain</em> (FR-002) is
/// enforced inside this aggregate's transaction; the partial unique
/// index in Postgres is a belt-and-braces backstop.
///
/// <para>
/// Spec 010 (ADR-0112): a revision now carries a multi-tile grid.
/// <see cref="ValidateGrid"/> is the single source of truth for the four
/// grid invariants (≥1 tile, no duplicate position, in-bounds, ≤4);
/// command handlers call it and map the first violation to a
/// <c>LAYOUT_GRID_*</c> <c>400</c> error before invoking a write method.
/// Illegal <em>state transitions</em> keep throwing
/// <see cref="InvalidOperationException"/> (programmer error).
/// </para>
/// </summary>
public sealed class Layout : AggregateRoot<LayoutIdentifier>
{
    private readonly List<Revision> revisions = [];

    /// <summary>
    /// The fab this layout belongs to (spec 017). Fixed when the chain is
    /// minted and never changed: there is no setter and no <c>MoveToFab</c>
    /// (FR-002).
    ///
    /// <para>
    /// A <see cref="Revision"/> deliberately has no fab of its own (FR-003).
    /// It belongs to this one, so the two cannot disagree — the guarantee is
    /// that there is nowhere to put a second value.
    /// </para>
    /// </summary>
    public FabIdentifier Fab { get; private set; } = null!;

    public LayoutName Name { get; private set; } = null!;

    public IReadOnlyList<Revision> Revisions => revisions;

    public Creation Creation { get; private set; } = null!;

    /// <summary>
    /// When every revision in this chain became Archived, and null while any of
    /// them is still live. The chain already knew this — it is the condition
    /// behind <see cref="NewestWhenFullyArchivedOrNull"/> — but it lived only in
    /// the revisions, and a Postgres index predicate may neither read another
    /// table nor call a non-immutable function. Writing the answer onto the
    /// parent row is what lets the name rule be a partial unique index rather
    /// than an application check nothing backs up (spec 086 §1.1).
    /// </summary>
    public ArchivedAt? ArchivedAt { get; private set; }

    private Layout() { }

    /// <summary>
    /// Validates a candidate grid + tile set against the four spec-010
    /// invariants (ADR-0112 §2), returning the first violation or
    /// <see cref="Option{T}.None"/> when valid. The single source of
    /// truth shared by create + edit so both paths reject identically.
    /// </summary>
    public static Option<GridViolation> ValidateGrid(GridDimensions grid, IReadOnlyList<Tile> tiles)
    {
        Ensure.That(tiles).IsNotNull();

        if (tiles.Count == 0)
        {
            return Option<GridViolation>.Some(GridViolation.Empty);
        }
        if (grid.Rows * grid.Cols > GridDimensions.MaxCells || tiles.Count > GridDimensions.MaxTiles)
        {
            return Option<GridViolation>.Some(GridViolation.TooLarge);
        }
        if (tiles.Any(tile => !grid.Contains(tile.Position)))
        {
            return Option<GridViolation>.Some(GridViolation.OutOfBounds);
        }
        if (tiles.Select(tile => tile.Position).Distinct().Count() != tiles.Count)
        {
            return Option<GridViolation>.Some(GridViolation.DuplicatePosition);
        }
        return Option<GridViolation>.None;
    }

    /// <summary>
    /// Mints a new logical Layout chain with its first revision in
    /// <c>Draft</c> state. No domain event is raised — drafts are not
    /// observable to kiosks; the first observable transition is Publish.
    /// The grid + tiles must already be valid (<see cref="ValidateGrid"/>).
    /// </summary>
    public static Layout CreateDraft(
        FabIdentifier fab,
        LayoutName name,
        GridDimensions grid,
        IReadOnlyList<Tile> tiles,
        OperatorIdentifier createdBy,
        IClock clock)
    {
        Ensure.That(fab).IsNotNull();
        Ensure.That(name).IsNotNull();
        Ensure.That(tiles).IsNotNull();
        Ensure.That(clock).IsNotNull();

        DateTimeOffset now = clock.UtcNow;
        Layout layout = new()
        {
            Id = LayoutIdentifier.New(),
            Fab = fab,
            Name = name,
            Creation = Creation.From(CreatedAt.From(now), createdBy),
        };
        layout.revisions.Add(
            Revision.NewDraft(LayoutRevisionNumber.One, grid, tiles, now, createdBy));
        layout.RecomputeArchival(now);
        return layout;
    }

    /// <summary>
    /// Branches a new Draft revision off the chain's current Published
    /// revision (spec 003 US4). Pre-fills the grid + tiles from the prior
    /// revision so the editor can mutate from a known-good baseline.
    ///
    /// <para>
    /// Spec 037 (ADR-0121): a chain with no Published and no Draft revision
    /// branches from its newest Archived revision instead. Archiving takes a
    /// layout out of service, not out of reach — without this the chain kept
    /// its identifier and could never be edited or published again.
    /// A Published revision still wins whenever one exists.
    /// </para>
    /// </summary>
    public Revision BranchDraft(OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision baseRevision = CurrentPublishedOrNull() ?? NewestWhenFullyArchivedOrNull()
            ?? throw new InvalidOperationException(
                $"Layout {Id} has a Draft revision already; BranchDraft needs a Published revision or a fully-archived chain.");

        LayoutRevisionNumber next = MaxRevisionNumber().Next();
        Revision draft = Revision.Branch(
            next, baseRevision.Grid, baseRevision.Tiles, clock.UtcNow, by);
        revisions.Add(draft);
        return draft;
    }

    /// <summary>
    /// In-place edit of an existing Draft revision (spec 003 FR-005,
    /// spec 010): atomically replaces its grid + tile set. The grid +
    /// tiles must already be valid (<see cref="ValidateGrid"/>). Drafts
    /// can be mutated without spawning further revisions.
    /// </summary>
    public void EditDraft(
        LayoutRevisionNumber number, GridDimensions grid, IReadOnlyList<Tile> tiles, IClock clock)
    {
        Ensure.That(tiles).IsNotNull();
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        target.ReplaceTiles(grid, tiles);
    }

    /// <summary>
    /// Publishes a Draft revision. Atomically archives the previously-
    /// Published sibling revision (FR-003), preserving the
    /// at-most-one-Published invariant. Raises both
    /// <see cref="LayoutRevisionPublishedDomainEvent"/> and (when
    /// applicable) <see cref="LayoutRevisionArchivedDomainEvent"/>.
    /// </summary>
    public void Publish(LayoutRevisionNumber number, OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        Revision? prior = CurrentPublishedOrNull();
        DateTimeOffset now = clock.UtcNow;

        target.Publish(now);
        if (prior is not null && prior.Number != number)
        {
            prior.Archive(now);
            Raise(new LayoutRevisionArchivedDomainEvent(Fab, Id, prior.Number, now, by));
        }
        Raise(new LayoutRevisionPublishedDomainEvent(
            Fab, Id, number, Name, target.Grid, target.Tiles, now, by));
    }

    /// <summary>
    /// Reverts a Published revision to Draft so the admin can edit it in
    /// place without spawning a new revision. Raises an Archived event
    /// for downstream subscribers so kiosks force-disconnect.
    /// </summary>
    public void Revert(LayoutRevisionNumber number, OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        target.Revert();
        Raise(new LayoutRevisionArchivedDomainEvent(Fab, Id, number, clock.UtcNow, by));
    }

    /// <summary>
    /// Archives a Draft or Published revision. Idempotent on Archived
    /// (no event raised, no state change).
    /// </summary>
    public void ArchiveRevision(
        LayoutRevisionNumber number, OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        if (target.State == LayoutRevisionState.Archived)
        {
            return;
        }

        bool wasObservable = target.State == LayoutRevisionState.Published;
        DateTimeOffset now = clock.UtcNow;
        target.Archive(now);
        if (wasObservable)
        {
            Raise(new LayoutRevisionArchivedDomainEvent(Fab, Id, number, now, by));
        }
    }

    /// <summary>
    /// Restates <see cref="ArchivedAt"/> from the revisions that decide it.
    ///
    /// <para>
    /// The instant is not preserved across a re-entry because it cannot be lost
    /// by one: no mutator both runs on a fully-archived chain and leaves it
    /// fully archived. ArchiveRevision returns early on an Archived revision,
    /// EditDraft refuses a non-Draft, and BranchDraft revives the chain.
    /// </para>
    /// </summary>
    private void RecomputeArchival(DateTimeOffset now)
    {
        bool fullyArchived = revisions.All(
            revision => revision.State == LayoutRevisionState.Archived);
        ArchivedAt = fullyArchived ? ArchivedAt.From(now) : null;
    }

    private Revision? CurrentPublishedOrNull() =>
        revisions.SingleOrDefault(revision => revision.State == LayoutRevisionState.Published);

    /// <summary>
    /// The newest revision, but only when **every** revision is Archived
    /// (spec 037 FR-001). Null otherwise.
    ///
    /// <para>
    /// The condition lives here rather than at the call site on purpose. Widened
    /// to "the newest revision, whatever its state", a chain holding only a Draft
    /// would branch from that draft and end up with two competing drafts — worse
    /// than the stranding this fixes. Written this way, widening it means
    /// deleting a method that says what it is for.
    /// </para>
    /// </summary>
    private Revision? NewestWhenFullyArchivedOrNull() =>
        revisions.All(revision => revision.State == LayoutRevisionState.Archived)
            ? revisions.MaxBy(revision => revision.Number.Value)
            : null;

    private LayoutRevisionNumber MaxRevisionNumber() =>
        LayoutRevisionNumber.From(revisions.Max(revision => revision.Number.Value));

    private Revision RequireRevision(LayoutRevisionNumber number) =>
        revisions.SingleOrDefault(revision => revision.Number == number)
            ?? throw new InvalidOperationException(
                $"Layout {Id} has no revision {number}.");
}
