using SmartSentinelEye.OverlayDesigner.Domain.Overlay.Events;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.OverlayDesigner.Domain.Overlay;

/// <summary>
/// Aggregate root for a logical Overlay chain (spec 004). Mirrors the
/// LayoutComposition.Layout shape: 1..N revisions, at most one
/// Published at a time, branch-on-edit semantics. A partial unique
/// index in Postgres backs the at-most-one-Published invariant.
/// </summary>
public sealed class Overlay : AggregateRoot<OverlayIdentifier>
{
    private readonly List<Revision> revisions = [];

    public OverlayName Name { get; private set; } = null!;

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

    private Overlay() { }

    /// <summary>
    /// Mints a new logical Overlay chain with its first revision in
    /// <c>Draft</c>. No domain event is raised — drafts are not
    /// observable to kiosks until Publish.
    /// </summary>
    public static Overlay CreateDraft(
        OverlayName name,
        Label label,
        OperatorIdentifier createdBy,
        IClock clock)
    {
        Ensure.That(name).IsNotNull();
        Ensure.That(label).IsNotNull();
        Ensure.That(clock).IsNotNull();

        DateTimeOffset now = clock.UtcNow;
        Overlay overlay = new()
        {
            Id = OverlayIdentifier.New(),
            Name = name,
            Creation = Creation.From(CreatedAt.From(now), createdBy),
        };
        overlay.revisions.Add(
            Revision.NewDraft(OverlayRevisionNumber.One, label, now, createdBy));
        overlay.RecomputeArchival(now);
        return overlay;
    }

    /// <summary>
    /// Branches a new Draft revision off the chain's current Published
    /// revision; pre-fills the Label from the prior revision so the
    /// editor mutates a known-good baseline (spec 004 US4).
    ///
    /// <para>
    /// Spec 037 (ADR-0121): a chain with no Published and no Draft revision
    /// branches from its newest Archived revision instead. Archiving takes an
    /// overlay out of service, not out of reach — without this the chain kept
    /// its identifier and could never be edited or published again.
    /// A Published revision still wins whenever one exists.
    /// </para>
    /// </summary>
    public Revision BranchDraft(OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision baseRevision = CurrentPublishedOrNull() ?? NewestWhenFullyArchivedOrNull()
            ?? throw new InvalidOperationException(
                $"Overlay {Id} has a Draft revision already; BranchDraft needs a Published revision or a fully-archived chain.");

        DateTimeOffset now = clock.UtcNow;
        OverlayRevisionNumber next = MaxRevisionNumber().Next();
        Revision draft = Revision.Branch(next, baseRevision.Label, now, by);
        revisions.Add(draft);
        RecomputeArchival(now);
        return draft;
    }

    /// <summary>
    /// In-place Label edit on an existing Draft revision (spec 004 FR-005).
    /// </summary>
    public void EditDraft(
        OverlayRevisionNumber number, Label label, IClock clock)
    {
        Ensure.That(label).IsNotNull();
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        target.EditLabel(label);
        RecomputeArchival(clock.UtcNow);
    }

    /// <summary>
    /// Publishes a Draft revision. Atomically archives the previously-
    /// Published sibling in the same transaction (FR-003); raises
    /// <see cref="OverlayRevisionPublishedDomainEvent"/> and, when
    /// applicable, <see cref="OverlayRevisionArchivedDomainEvent"/>.
    /// </summary>
    public void Publish(OverlayRevisionNumber number, OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        Revision? prior = CurrentPublishedOrNull();
        DateTimeOffset now = clock.UtcNow;

        target.Publish(now);
        if (prior is not null && prior.Number != number)
        {
            prior.Archive(now);
            Raise(new OverlayRevisionArchivedDomainEvent(Id, prior.Number, now, by));
        }
        Raise(new OverlayRevisionPublishedDomainEvent(
            Id, number, Name, target.Label, now, by));
        RecomputeArchival(now);
    }

    /// <summary>
    /// Reverts a Published revision to Draft. Raises an Archived
    /// domain event so connected kiosks treat the revision as gone
    /// (the new Draft is invisible to kiosks until republished).
    /// </summary>
    public void Revert(OverlayRevisionNumber number, OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        DateTimeOffset now = clock.UtcNow;
        Revision target = RequireRevision(number);
        target.Revert();
        Raise(new OverlayRevisionArchivedDomainEvent(Id, number, now, by));
        RecomputeArchival(now);
    }

    /// <summary>
    /// Archives a Draft or Published revision. Idempotent on Archived: no event
    /// is raised and no revision changes state. The chain marker is still
    /// restated on that path — see <see cref="RecomputeArchival"/>, where a
    /// re-archive is the only repair a stale marker has.
    /// </summary>
    public void ArchiveRevision(
        OverlayRevisionNumber number, OperatorIdentifier by, IClock clock)
    {
        Ensure.That(clock).IsNotNull();
        Revision target = RequireRevision(number);
        DateTimeOffset now = clock.UtcNow;
        if (target.State == OverlayRevisionState.Archived)
        {
            RecomputeArchival(now);
            return;
        }

        bool wasObservable = target.State == OverlayRevisionState.Published;
        target.Archive(now);
        if (wasObservable)
        {
            Raise(new OverlayRevisionArchivedDomainEvent(Id, number, now, by));
        }

        RecomputeArchival(now);
    }

    /// <summary>
    /// Restates <see cref="ArchivedAt"/> from the revisions that decide it, on
    /// <b>every path through every mutator</b> rather than only the three that
    /// can currently move the answer. The paths that cannot cost one list scan;
    /// a mutator added later without the call costs the index its meaning.
    ///
    /// <para>
    /// The instant survives a re-entry: a chain that is already fully archived
    /// keeps the instant it acquired instead of taking the caller's clock.
    /// <see cref="BranchDraft"/> revives the chain and clears the marker, so a
    /// later re-archive legitimately takes the new instant.
    /// </para>
    ///
    /// <para>
    /// <see cref="ArchiveRevision"/>'s idempotent early return calls this too,
    /// and that is the point rather than symmetry. A fully-archived chain whose
    /// marker is unset reads as <i>live</i> through the repository's
    /// name lookup, so archiving stops releasing the name and a legitimate
    /// reuse is refused <c>409</c> for good: every other mutator refuses a
    /// fully-archived chain, and <see cref="BranchDraft"/> clears the marker
    /// rather than setting it. The state is not hypothetical — a chain archived
    /// by code predating the column, whether through a rolling deploy or a
    /// developer switching branches against one dev volume, arrives exactly so.
    /// Re-archiving heals it; nothing else can.
    /// </para>
    /// </summary>
    private void RecomputeArchival(DateTimeOffset now)
    {
        bool fullyArchived = revisions.All(
            revision => revision.State == OverlayRevisionState.Archived);
        ArchivedAt = fullyArchived ? (ArchivedAt ?? ArchivedAt.From(now)) : null;
    }

    private Revision? CurrentPublishedOrNull() =>
        revisions.SingleOrDefault(revision => revision.State == OverlayRevisionState.Published);

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
        revisions.All(revision => revision.State == OverlayRevisionState.Archived)
            ? revisions.MaxBy(revision => revision.Number.Value)
            : null;

    private OverlayRevisionNumber MaxRevisionNumber() =>
        OverlayRevisionNumber.From(revisions.Max(revision => revision.Number.Value));

    private Revision RequireRevision(OverlayRevisionNumber number) =>
        revisions.SingleOrDefault(revision => revision.Number == number)
            ?? throw new InvalidOperationException(
                $"Overlay {Id} has no revision {number}.");
}
