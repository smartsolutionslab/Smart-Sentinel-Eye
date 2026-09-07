using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.Shared.Kernel.Primitives;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSentinelEye.LayoutComposition.Domain.Layout;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.LayoutComposition.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="Layout"/> aggregate (spec 003).
///
/// <para>
/// Revisions are an owned collection mapped to <c>layout_revisions</c>;
/// the aggregate boundary stays inside the chain so the
/// at-most-one-Published invariant lives in a single transaction. A
/// partial unique index in Postgres backs the invariant as a
/// belt-and-braces guard (FR-002).
/// </para>
///
/// <para>
/// FR-006 (name unique across non-archived chains, per fab since spec 017
/// FR-019) is enforced twice: by <c>CreateLayoutDraftCommandHandler</c> through
/// the repository's <c>GetByNameAsync</c>, which produces an answer an operator
/// can act on, and by <c>ux_layouts_fab_name_active</c>, which guarantees it
/// against two writers who both read nothing. The index needs
/// <c>layouts.archived_at</c> to exist at all: the rule is about the chain's
/// revisions, and an index predicate can read only its own row (spec 086 §1.1).
/// The column is the aggregate's answer written down, not a cache —
/// <c>Layout.RecomputeArchival</c> restates it after every mutation.
/// </para>
/// </summary>
public sealed class LayoutConfiguration : IEntityTypeConfiguration<Layout>
{
    public void Configure(EntityTypeBuilder<Layout> builder)
    {
        Ensure.That(builder).IsNotNull();

        builder.ToTable("layouts");
        builder.HasKey(layout => layout.Id);

        builder.Property(layout => layout.Id)
            .HasColumnName("layout_id")
            .HasConversion(id => id.Value, value => LayoutIdentifier.From(value))
            .ValueGeneratedNever();

        builder.Property(layout => layout.Fab)
            .HasColumnName("fab")
            .HasMaxLength(FabIdentifier.MaximumLength)
            .HasConversion(fab => fab.Value, value => FabIdentifier.From(value))
            .IsRequired();

        builder.Property(layout => layout.Name)
            .HasColumnName("name")
            .HasMaxLength(LayoutName.MaximumLength)
            .HasConversion(name => name.Value, value => LayoutName.From(value))
            .IsRequired();

        // Owned reference onto the two columns the pair occupied. The
        // Navigation(...).IsRequired() line keeps them NOT NULL (#2022).
        builder.OwnsOne(layout => layout.Creation, creation =>
        {
            creation.Property(value => value.At)
                .HasColumnName("created_at")
                .HasConversion(at => at.Value, value => CreatedAt.From(value))
                .IsRequired();

            creation.Property(value => value.By)
                .HasColumnName("created_by")
                .HasConversion(by => by.Value, value => OperatorIdentifier.From(value))
                .IsRequired();
        });
        builder.Navigation(layout => layout.Creation).IsRequired();

        builder.Property(layout => layout.Version)
            .HasColumnName("version")
            .HasConversion(version => version.Value, value => AggregateVersion.From(value))
            .IsConcurrencyToken()
            .IsRequired();

        // The chain's own archival, materialised onto the parent row. The
        // predicate it summarises - every revision Archived - lives on
        // layout_revisions, and a Postgres index predicate may not read another
        // table, so the only way the database can enforce the name rule is to
        // have the answer on the row the name is on (spec 086 §1.1).
        builder.Property(layout => layout.ArchivedAt)
            .HasColumnName("archived_at")
            .HasConversion(at => at!.Value, value => ArchivedAt.From(value))
            .IsRequired(false);

        // FR-006 and FR-019 together, in the database rather than only in
        // CreateLayoutDraftCommandHandler: one live chain per name per fab.
        // Keyed on (fab, name) and not on name alone — a name is unique only
        // within one fab, and the narrower key would refuse a second fab's wall
        // its own North Wall.
        //
        // Replaces ix_layouts_fab_name, a plain btree that read like a
        // constraint without being one. The comment it replaces deferred this on
        // the grounds that the data might already violate it; spec 086 §6
        // measured that and the migration refuses with the collisions listed
        // rather than assuming the measurement travels.
        //
        // Partial, and deliberately so. A total unique index would satisfy every
        // race test and quietly turn archiving into permanent confiscation of
        // the word — the reuse clause FR-006 spells out.
        builder.HasIndex(layout => new { layout.Fab, layout.Name })
            .HasDatabaseName("ux_layouts_fab_name_active")
            .IsUnique()
            .HasFilter("archived_at IS NULL");

        // Supports the listing filter, the only query the column alone
        // participates in.
        builder.HasIndex(layout => layout.Fab)
            .HasDatabaseName("ix_layouts_fab");

        builder.OwnsMany(layout => layout.Revisions, revisions =>
        {
            revisions.ToTable("layout_revisions");
            revisions.WithOwner().HasForeignKey("layout_id");
            revisions.HasKey(revision => revision.Id);

            revisions.Property(revision => revision.Id)
                .HasColumnName("revision_id")
                .HasConversion(id => id.Value, value => LayoutRevisionIdentifier.From(value))
                .ValueGeneratedNever();

            revisions.Property(revision => revision.Number)
                .HasColumnName("revision_number")
                .HasConversion(number => number.Value, value => LayoutRevisionNumber.From(value))
                .IsRequired();

            revisions.Property(revision => revision.State)
                .HasColumnName("state")
                .HasMaxLength(16)
                .HasConversion(state => state.Value, value => LayoutRevisionState.From(value))
                .IsRequired();

            // GridDimensions is a struct value object flattened across two
            // columns on layout_revisions via an owned reference (mirrors the
            // OverlayDesigner Label pattern).
            revisions.OwnsOne(revision => revision.Grid, grid =>
            {
                grid.Property(dimensions => dimensions.Rows).HasColumnName("grid_rows").IsRequired();
                grid.Property(dimensions => dimensions.Cols).HasColumnName("grid_cols").IsRequired();
            });

            // Tiles are a nested owned collection on layout_revision_tiles
            // (ADR-0112 §3). A tile has no identity of its own, so the
            // composite key is (revision_id, row, col) — the tile's GridPosition,
            // flattened into the two scalars mapped below.
            revisions.OwnsMany(revision => revision.Tiles, tiles =>
            {
                tiles.ToTable("layout_revision_tiles");
                tiles.WithOwner().HasForeignKey("revision_id");

                // Field-backed, not CLR properties: the tile exposes only its
                // GridPosition, so the two scalars the key needs are mapped onto
                // its private fields rather than published as ints on a domain
                // type (constitution §II). The names must be the field names
                // exactly — EF refuses a field-only property whose name differs,
                // so these are "row"/"col" and not the PascalCase they replaced.
                tiles.Property<int>("row").HasColumnName("row").IsRequired();
                tiles.Property<int>("col").HasColumnName("col").IsRequired();
                tiles.HasKey("revision_id", "row", "col");
                tiles.Ignore(tile => tile.Position);
                tiles.Ignore(tile => tile.Overlay);

                tiles.Property(tile => tile.Camera)
                    .HasColumnName("camera_id")
                    .HasConversion(camera => camera.Value, value => CameraIdentifier.From(value))
                    .IsRequired();

                tiles.Property(tile => tile.OverlayValue)
                    .HasColumnName("overlay_id")
                    .HasConversion(
                        overlay => overlay.HasValue ? overlay.Value.Value : (Guid?)null,
                        value => value.HasValue ? OverlayIdentifier.From(value.Value) : (OverlayIdentifier?)null)
                    .IsRequired(false);

                // The column has existed since spec 010 but nothing has ever
                // queried by it. Spec 017's overlay-frame scoping asks "which
                // fabs have a published layout carrying this overlay" on every
                // overlay publish or archive; without this the join scans
                // every tile in the product.
                tiles.HasIndex(tile => tile.OverlayValue)
                    .HasDatabaseName("ix_layout_revision_tiles_overlay_id");
            });

            // The nested case: a composite inside an owned collection, one
            // level deeper than anything else in this feature. The columns
            // land on the revisions table exactly as the pair did.
            revisions.OwnsOne(revision => revision.Creation, creation =>
            {
                creation.Property(value => value.At)
                    .HasColumnName("created_at")
                    .HasConversion(at => at.Value, value => CreatedAt.From(value))
                    .IsRequired();

                creation.Property(value => value.By)
                    .HasColumnName("created_by")
                    .HasConversion(by => by.Value, value => OperatorIdentifier.From(value))
                    .IsRequired();
            });
            revisions.Navigation(revision => revision.Creation).IsRequired();

            revisions.Property(revision => revision.PublishedAt)
                .HasColumnName("published_at")
            .HasConversion(v => v!.Value, value => PublishedAt.From(value))
                .IsRequired(false);

            revisions.Property(revision => revision.ArchivedAt)
                .HasColumnName("archived_at")
            .HasConversion(v => v!.Value, value => ArchivedAt.From(value))
                .IsRequired(false);

            revisions.HasIndex("layout_id", nameof(Revision.Number))
                .HasDatabaseName("ux_layout_revisions_number")
                .IsUnique();

            // Belt-and-braces: at most one Published revision per chain.
            // The aggregate enforces this in-memory; the partial unique
            // index makes a buggy code path fail loudly at COMMIT instead
            // of silently leaving two Published rows.
            revisions.HasIndex("layout_id")
                .HasDatabaseName("ux_layout_revisions_one_published")
                .IsUnique()
                .HasFilter("state = 'Published'");
        });

        builder.Ignore(layout => layout.PendingEvents);
    }
}
