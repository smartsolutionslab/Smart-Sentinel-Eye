using System.Globalization;
using SmartSentinelEye.OverlayDesigner.Domain.Overlay;
using SmartSentinelEye.OverlayDesigner.Domain.Tests.Overlay.Builders;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.OverlayDesigner.Domain.Tests.Overlay;

/// <summary>
/// Spec 086 T002 — <c>Overlay.ArchivedAt</c> is set <b>iff</b> every revision in
/// the chain is Archived.
///
/// <para>
/// The marker is the whole reason this spec is more than a migration: the
/// predicate it replaces reads <c>overlay_revisions</c>, and a Postgres index
/// predicate may not. Which means the index is only as correct as this
/// invariant, and the invariant has no user-visible proxy of its own — an
/// aggregate that quietly stops maintaining it turns the partial unique index
/// into a total one, and nothing above the domain would say so.
/// </para>
///
/// <para>
/// Written against the property rather than through a repository on purpose:
/// the drift this guards against happens when a <em>new</em> mutator forgets
/// the recompute, and only the aggregate's own tests are in a position to
/// notice.
/// </para>
/// </summary>
public class OverlayChainArchivalTests
{
    private static readonly DateTimeOffset Minted =
        DateTimeOffset.Parse("2026-05-27T10:00:00Z", CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset Later =
        DateTimeOffset.Parse("2026-05-28T14:30:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void A_new_chain_is_not_archived()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();

        overlay.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public void Archiving_the_only_revision_archives_the_chain()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());

        overlay.ArchiveRevision(
            OverlayRevisionNumber.One, by, new OverlayBuilder.TestClock(Later));

        overlay.ArchivedAt.ShouldNotBeNull().Value.ShouldBe(Later);
    }

    /// <summary>
    /// Publish archives the prior Published revision, so a chain that is only
    /// ever published has an Archived revision in it from the second publish
    /// onwards. A marker that keyed off "any revision is Archived" rather than
    /// "every revision is Archived" would confiscate the name here.
    /// </summary>
    [Fact]
    public void Publishing_over_the_prior_Published_revision_leaves_the_chain_live()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new OverlayBuilder.TestClock(Minted);
        overlay.Publish(OverlayRevisionNumber.One, by, clock);
        Revision second = overlay.BranchDraft(by, clock);

        overlay.Publish(second.Number, by, clock);

        overlay.Revisions[0].State.ShouldBe(OverlayRevisionState.Archived);
        overlay.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public void Archiving_the_last_live_revision_archives_the_chain()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new OverlayBuilder.TestClock(Minted);
        overlay.Publish(OverlayRevisionNumber.One, by, clock);
        Revision second = overlay.BranchDraft(by, clock);
        overlay.Publish(second.Number, by, clock);

        overlay.ArchivedAt.ShouldBeNull();
        overlay.ArchiveRevision(second.Number, by, new OverlayBuilder.TestClock(Later));

        overlay.Revisions.ShouldAllBe(revision => revision.State == OverlayRevisionState.Archived);
        overlay.ArchivedAt.ShouldNotBeNull().Value.ShouldBe(Later);
    }

    /// <summary>
    /// Spec 037 / ADR-0121: a fully-archived chain is recoverable. The marker
    /// has to come back off, or the recovered chain would keep a name the index
    /// no longer defends and a second chain could take it.
    /// </summary>
    [Fact]
    public void Branching_a_fully_archived_chain_revives_it()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        overlay.ArchiveRevision(
            OverlayRevisionNumber.One, by, new OverlayBuilder.TestClock(Later));
        overlay.ArchivedAt.ShouldNotBeNull();

        overlay.BranchDraft(by, new OverlayBuilder.TestClock(Later));

        overlay.ArchivedAt.ShouldBeNull();
    }

    /// <summary>
    /// Revert turns a Published revision back into a Draft — the chain gains a
    /// live revision rather than losing one, so it stays unmarked.
    /// </summary>
    [Fact]
    public void Reverting_a_Published_revision_leaves_the_chain_live()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new OverlayBuilder.TestClock(Minted);
        overlay.Publish(OverlayRevisionNumber.One, by, clock);

        overlay.Revert(OverlayRevisionNumber.One, by, new OverlayBuilder.TestClock(Later));

        overlay.ArchivedAt.ShouldBeNull();
    }

    /// <summary>
    /// The invariant itself, asserted after every step of a full lifecycle
    /// rather than at one chosen moment. This is the test a future mutator that
    /// forgets the recompute has to get past.
    /// </summary>
    [Fact]
    public void The_marker_agrees_with_the_revisions_after_every_mutation()
    {
        Domain.Overlay.Overlay overlay = new OverlayBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new OverlayBuilder.TestClock(Later);
        Label edited = Label.From(
            "Rolling Mill B",
            NormalizedPosition.From(0.1m, 0.1m),
            NormalizedSize.From(0.2m, 0.05m),
            32);

        ShouldAgree(overlay);
        overlay.EditDraft(OverlayRevisionNumber.One, edited, clock);
        ShouldAgree(overlay);
        overlay.Publish(OverlayRevisionNumber.One, by, clock);
        ShouldAgree(overlay);
        Revision second = overlay.BranchDraft(by, clock);
        ShouldAgree(overlay);
        overlay.Publish(second.Number, by, clock);
        ShouldAgree(overlay);
        overlay.Revert(second.Number, by, clock);
        ShouldAgree(overlay);
        overlay.ArchiveRevision(second.Number, by, clock);
        ShouldAgree(overlay);
        overlay.BranchDraft(by, clock);
        ShouldAgree(overlay);
    }

    private static void ShouldAgree(Domain.Overlay.Overlay overlay)
    {
        bool everyRevisionArchived = overlay.Revisions.All(
            revision => revision.State == OverlayRevisionState.Archived);

        (overlay.ArchivedAt is not null).ShouldBe(
            everyRevisionArchived,
            $"states: {string.Join(", ", overlay.Revisions.Select(revision => revision.State))}");
    }
}
