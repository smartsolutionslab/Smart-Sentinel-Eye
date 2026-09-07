using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSentinelEye.LayoutComposition.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LayoutNameUniquePerLiveChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column arrived empty in LayoutChainArchivalMarker; the
            // aggregate only maintains it for chains it has touched since.
            //
            // A chain with no revisions cannot exist — CreateDraft always mints
            // one — so NOT EXISTS needs no extra guard against the empty chain,
            // which it would otherwise report as archived.
            migrationBuilder.Sql("""
                UPDATE layouts l
                SET archived_at = (
                    SELECT max(r.archived_at)
                    FROM layout_revisions r
                    WHERE r.layout_id = l.layout_id)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM layout_revisions r
                    WHERE r.layout_id = l.layout_id AND r.state <> 'Archived');
                """);

            // Spec 086 §6 measured zero collisions in one dev database, which
            // says nothing about any other. CreateIndex on a table that already
            // holds two live chains of one name in one fab reports a bare unique
            // violation naming the index — true, and useless to whoever has to
            // act on it.
            //
            // This refuses first and says which names collide, in which fab.
            // Deliberately NOT auto-reconciled: the fixes available to a
            // migration are renaming somebody's wall or archiving it, and both
            // change what an operator sees on a wall of live video. That is an
            // operator's decision, not a deploy step's.
            //
            // Runs after the backfill, because its predicate is the column the
            // backfill fills.
            migrationBuilder.Sql("""
                DO $$
                DECLARE collisions text;
                BEGIN
                    SELECT string_agg(format('fab %s: %s (%s chains)', fab, name, tally), '; ')
                    INTO collisions
                    FROM (
                        SELECT fab, name, count(*) AS tally
                        FROM layouts
                        WHERE archived_at IS NULL
                        GROUP BY fab, name
                        HAVING count(*) > 1
                    ) AS duplicates;

                    IF collisions IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Layout names must be unique per fab across live chains (spec 086), but these already collide: %',
                            collisions
                            USING HINT =
                                'Archive every revision of all but one chain in each group, then re-run the migration.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_layouts_fab_name",
                table: "layouts");

            migrationBuilder.CreateIndex(
                name: "ux_layouts_fab_name_active",
                table: "layouts",
                columns: new[] { "fab", "name" },
                unique: true,
                filter: "archived_at IS NULL");
        }

        /// <inheritdoc />
        /// <remarks>
        /// The backfill is not undone. Its values stay correct while the column
        /// exists, and the column is dropped by the Down of
        /// LayoutChainArchivalMarker, which is the migration that added it.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_layouts_fab_name_active",
                table: "layouts");

            migrationBuilder.CreateIndex(
                name: "ix_layouts_fab_name",
                table: "layouts",
                columns: new[] { "fab", "name" });
        }
    }
}
