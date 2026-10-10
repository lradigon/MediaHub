using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueTmdbMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
                migrationBuilder.Sql("""
                    DO $$
                    DECLARE duplicate_record record;
                    BEGIN
                        FOR duplicate_record IN
                            WITH ranked AS (
                                SELECT "Id",
                                       FIRST_VALUE("Id") OVER (
                                           PARTITION BY "TmdbId", "Type"
                                           ORDER BY CASE
                                               WHEN "Status" = 2 OR "StoragePath" IS NOT NULL OR EXISTS (
                                                   SELECT 1 FROM "CloudFiles" WHERE "CloudFiles"."MediaItemId" = "MediaItems"."Id")
                                               THEN 0 ELSE 1 END,
                                               "AddedAt", "Id") AS kept_id,
                                       ROW_NUMBER() OVER (
                                           PARTITION BY "TmdbId", "Type"
                                           ORDER BY CASE
                                               WHEN "Status" = 2 OR "StoragePath" IS NOT NULL OR EXISTS (
                                                   SELECT 1 FROM "CloudFiles" WHERE "CloudFiles"."MediaItemId" = "MediaItems"."Id")
                                               THEN 0 ELSE 1 END,
                                               "AddedAt", "Id") AS duplicate_rank
                                FROM "MediaItems"
                                WHERE "TmdbId" IS NOT NULL)
                            SELECT "Id" AS duplicate_id, kept_id
                            FROM ranked
                            WHERE duplicate_rank > 1
                        LOOP
                            UPDATE "PipelineJobs" SET "MediaItemId" = duplicate_record.kept_id
                            WHERE "MediaItemId" = duplicate_record.duplicate_id;
                            UPDATE "MediaRequests" SET "MediaItemId" = duplicate_record.kept_id
                            WHERE "MediaItemId" = duplicate_record.duplicate_id;
                            UPDATE "Seasons" SET "MediaItemId" = duplicate_record.kept_id
                            WHERE "MediaItemId" = duplicate_record.duplicate_id;
                            UPDATE "CloudFiles" SET "MediaItemId" = duplicate_record.kept_id
                            WHERE "MediaItemId" = duplicate_record.duplicate_id;
                            DELETE FROM "MediaItems" WHERE "Id" = duplicate_record.duplicate_id;
                        END LOOP;
                    END $$;
                    """);

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_TmdbId_Type",
                table: "MediaItems");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_TmdbId_Type",
                table: "MediaItems",
                columns: new[] { "TmdbId", "Type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItems_TmdbId_Type",
                table: "MediaItems");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_TmdbId_Type",
                table: "MediaItems",
                columns: new[] { "TmdbId", "Type" });
        }
    }
}
