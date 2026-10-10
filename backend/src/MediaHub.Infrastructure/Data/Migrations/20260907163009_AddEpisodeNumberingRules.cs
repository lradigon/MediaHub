using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeNumberingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpisodeNumberingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseSeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    TmdbSeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    FirstTmdbEpisodeNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeNumberingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpisodeNumberingRules_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeNumberingRules_MediaItemId_ReleaseSeasonNumber",
                table: "EpisodeNumberingRules",
                columns: new[] { "MediaItemId", "ReleaseSeasonNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpisodeNumberingRules");
        }
    }
}
