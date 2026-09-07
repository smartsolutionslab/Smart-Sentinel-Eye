using System.Globalization;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.LayoutComposition.Domain.Tests.Layout.Builders;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Domain.Tests.Layout;

/// <summary>
/// Spec 086 T011 — <c>Layout.ArchivedAt</c> is set <b>iff</b> every revision in
/// the chain is Archived. The OverlayDesigner twin of
/// <c>OverlayChainArchivalTests</c>, and the same reasoning applies: the marker
/// is what makes the name rule expressible as an index predicate, it has no
/// user-visible proxy of its own, and an aggregate that stops maintaining it
/// turns a partial unique index into a total one with nothing above the domain
/// to say so.
/// </summary>
public class LayoutChainArchivalTests
{
    private static readonly DateTimeOffset Minted =
        DateTimeOffset.Parse("2026-05-26T10:00:00Z", CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset Later =
        DateTimeOffset.Parse("2026-05-27T14:30:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void A_new_chain_is_not_archived()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();

        layout.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public void Archiving_the_only_revision_archives_the_chain()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());

        layout.ArchiveRevision(
            LayoutRevisionNumber.One, by, new LayoutBuilder.TestClock(Later));

        layout.ArchivedAt.ShouldNotBeNull().Value.ShouldBe(Later);
    }

    /// <summary>
    /// Publish archives the prior Published revision, so a chain that is only
    /// ever published carries an Archived revision from the second publish
    /// onwards. A marker keyed on "any revision Archived" rather than "every
    /// revision Archived" would confiscate this wall's name.
    /// </summary>
    [Fact]
    public void Publishing_over_the_prior_Published_revision_leaves_the_chain_live()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new LayoutBuilder.TestClock(Minted);
        layout.Publish(LayoutRevisionNumber.One, by, clock);
        Revision second = layout.BranchDraft(by, clock);

        layout.Publish(second.Number, by, clock);

        layout.Revisions[0].State.ShouldBe(LayoutRevisionState.Archived);
        layout.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public void Archiving_the_last_live_revision_archives_the_chain()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new LayoutBuilder.TestClock(Minted);
        layout.Publish(LayoutRevisionNumber.One, by, clock);
        Revision second = layout.BranchDraft(by, clock);
        layout.Publish(second.Number, by, clock);

        layout.ArchivedAt.ShouldBeNull();
        layout.ArchiveRevision(second.Number, by, new LayoutBuilder.TestClock(Later));

        layout.Revisions.ShouldAllBe(revision => revision.State == LayoutRevisionState.Archived);
        layout.ArchivedAt.ShouldNotBeNull().Value.ShouldBe(Later);
    }

    /// <summary>
    /// Spec 037 / ADR-0121: a fully-archived chain is recoverable. The marker
    /// has to come back off, or the recovered wall would keep a name the index
    /// no longer defends and a second chain in the same fab could take it.
    /// </summary>
    [Fact]
    public void Branching_a_fully_archived_chain_revives_it()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        layout.ArchiveRevision(
            LayoutRevisionNumber.One, by, new LayoutBuilder.TestClock(Later));
        layout.ArchivedAt.ShouldNotBeNull();

        layout.BranchDraft(by, new LayoutBuilder.TestClock(Later));

        layout.ArchivedAt.ShouldBeNull();
    }

    /// <summary>
    /// Revert turns a Published revision back into a Draft — the chain gains a
    /// live revision rather than losing one, so it stays unmarked.
    /// </summary>
    [Fact]
    public void Reverting_a_Published_revision_leaves_the_chain_live()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new LayoutBuilder.TestClock(Minted);
        layout.Publish(LayoutRevisionNumber.One, by, clock);

        layout.Revert(LayoutRevisionNumber.One, by, new LayoutBuilder.TestClock(Later));

        layout.ArchivedAt.ShouldBeNull();
    }

    /// <summary>
    /// The invariant itself, asserted after every step of a full lifecycle
    /// rather than at one chosen moment. This is the test a future mutator that
    /// forgets the recompute has to get past.
    /// </summary>
    [Fact]
    public void The_marker_agrees_with_the_revisions_after_every_mutation()
    {
        Domain.Layout.Layout layout = new LayoutBuilder().At(Minted).Build();
        OperatorIdentifier by = OperatorIdentifier.From(Guid.CreateVersion7());
        IClock clock = new LayoutBuilder.TestClock(Later);
        Tile[] edited = [new Tile(
            CameraIdentifier.From(Guid.CreateVersion7()),
            Option<OverlayIdentifier>.None,
            GridPosition.From(0, 0))];

        ShouldAgree(layout);
        layout.EditDraft(LayoutRevisionNumber.One, GridDimensions.Cell, edited, clock);
        ShouldAgree(layout);
        layout.Publish(LayoutRevisionNumber.One, by, clock);
        ShouldAgree(layout);
        Revision second = layout.BranchDraft(by, clock);
        ShouldAgree(layout);
        layout.Publish(second.Number, by, clock);
        ShouldAgree(layout);
        layout.Revert(second.Number, by, clock);
        ShouldAgree(layout);
        layout.ArchiveRevision(second.Number, by, clock);
        ShouldAgree(layout);
        layout.BranchDraft(by, clock);
        ShouldAgree(layout);
    }

    private static void ShouldAgree(Domain.Layout.Layout layout)
    {
        bool everyRevisionArchived = layout.Revisions.All(
            revision => revision.State == LayoutRevisionState.Archived);

        (layout.ArchivedAt is not null).ShouldBe(
            everyRevisionArchived,
            $"states: {string.Join(", ", layout.Revisions.Select(revision => revision.State))}");
    }
}
