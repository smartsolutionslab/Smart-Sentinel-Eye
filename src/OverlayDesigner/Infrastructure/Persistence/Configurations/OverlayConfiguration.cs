using Microsoft.EntityFrameworkCore;
using SmartSentinelEye.Shared.Kernel.Primitives;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSentinelEye.OverlayDesigner.Domain.Overlay;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.OverlayDesigner.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="Overlay"/> aggregate (spec 004).
///
/// <para>
/// Mirrors LayoutComposition's LayoutConfiguration: revisions are an
/// owned collection mapped to <c>overlay_revisions</c>; the partial
/// unique index on <c>state = 'Published'</c> backs the aggregate's
/// at-most-one-Published invariant as a belt-and-braces guard.
/// </para>
///
/// <para>
/// The <see cref="Label"/> value object is flattened across six columns
/// rather than mapped as a separate owned entity — kiosks need to render
/// every Published revision without joins, and Label has no identity of
/// its own.
/// </para>
/// </summary>
public sealed class OverlayConfiguration : IEntityTypeConfiguration<Overlay>
{
    public void Configure(EntityTypeBuilder<Overlay> builder)
    {
        Ensure.That(builder).IsNotNull();

        builder.ToTable("overlays");
        builder.HasKey(overlay => overlay.Id);

        builder.Property(overlay => overlay.Id)
            .HasColumnName("overlay_id")
            .HasConversion(id => id.Value, value => OverlayIdentifier.From(value))
            .ValueGeneratedNever();

        builder.Property(overlay => overlay.Name)
            .HasColumnName("name")
            .HasMaxLength(OverlayName.MaximumLength)
            .HasConversion(name => name.Value, value => OverlayName.From(value))
            .IsRequired();

        // Owned reference onto the two columns the pair occupied. The
        // Navigation(...).IsRequired() line keeps them NOT NULL (#2022).
        builder.OwnsOne(overlay => overlay.Creation, creation =>
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
        builder.Navigation(overlay => overlay.Creation).IsRequired();

        builder.Property(overlay => overlay.Version)
            .HasColumnName("version")
            .HasConversion(version => version.Value, value => AggregateVersion.From(value))
            .IsConcurrencyToken()
            .IsRequired();

        // The chain's own archival, materialised onto the parent row. The
        // predicate it summarises - every revision Archived - lives on
        // overlay_revisions, and a Postgres index predicate may not read another
        // table, so the only way the database can enforce the name rule is to
        // have the answer on the row the name is on (spec 086 §1.1).
        builder.Property(overlay => overlay.ArchivedAt)
            .HasColumnName("archived_at")
            .HasConversion(at => at!.Value, value => ArchivedAt.From(value))
            .IsRequired(false);

        builder.HasIndex(overlay => overlay.Name)
            .HasDatabaseName("ix_overlays_name");

        builder.OwnsMany(overlay => overlay.Revisions, revisions =>
        {
            revisions.ToTable("overlay_revisions");
            revisions.WithOwner().HasForeignKey("overlay_id");
            revisions.HasKey(revision => revision.Id);

            revisions.Property(revision => revision.Id)
                .HasColumnName("revision_id")
                .HasConversion(id => id.Value, value => OverlayRevisionIdentifier.From(value))
                .ValueGeneratedNever();

            revisions.Property(revision => revision.Number)
                .HasColumnName("revision_number")
                .HasConversion(number => number.Value, value => OverlayRevisionNumber.From(value))
                .IsRequired();

            revisions.Property(revision => revision.State)
                .HasColumnName("state")
                .HasMaxLength(16)
                .HasConversion(state => state.Value, value => OverlayRevisionState.From(value))
                .IsRequired();

            revisions.OwnsOne(revision => revision.Label, label =>
            {
                label.Property(labelValue => labelValue.Text)
                    .HasColumnName("label_text")
                    .HasMaxLength(Label.MaximumTextLength)
                    .IsRequired();

                // One level deeper than anything else here: a composite value
                // object owned by a composite value object owned by an owned
                // collection. The four columns stay where they were — the
                // owned-reference default would name them Position_X and make
                // them nullable, which is #2022's shape, so both the column name
                // and the Navigation(...).IsRequired() below are load-bearing.
                label.OwnsOne(labelValue => labelValue.Position, position =>
                {
                    position.Property(value => value.X).HasColumnName("label_x").IsRequired();
                    position.Property(value => value.Y).HasColumnName("label_y").IsRequired();
                });
                label.Navigation(labelValue => labelValue.Position).IsRequired();

                label.OwnsOne(labelValue => labelValue.Size, size =>
                {
                    size.Property(value => value.Width).HasColumnName("label_width").IsRequired();
                    size.Property(value => value.Height).HasColumnName("label_height").IsRequired();
                });
                label.Navigation(labelValue => labelValue.Size).IsRequired();

                label.Property(labelValue => labelValue.FontSizePx).HasColumnName("label_font_size_px").IsRequired();
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

            revisions.HasIndex("overlay_id", nameof(Revision.Number))
                .HasDatabaseName("ux_overlay_revisions_number")
                .IsUnique();

            revisions.HasIndex("overlay_id")
                .HasDatabaseName("ux_overlay_revisions_one_published")
                .IsUnique()
                .HasFilter("state = 'Published'");
        });

        builder.Ignore(overlay => overlay.PendingEvents);
    }
}
