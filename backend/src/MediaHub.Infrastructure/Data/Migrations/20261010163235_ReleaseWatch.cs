using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaWatches",
                columns: table => new
                {
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitializedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaWatches", x => x.MediaItemId);
                    table.ForeignKey(
                        name: "FK_MediaWatches_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    DownloadUrl = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Coverage = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextNotificationAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotificationAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReleaseObservations_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxParallelSearches = table.Column<int>(type: "integer", nullable: false),
                    TelegramOffset = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "WatchSettings",
                columns: new[] { "Id", "Enabled", "IntervalMinutes", "MaxParallelSearches", "TelegramOffset" },
                values: new object[] { 1, false, 60, 3, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_MediaWatches_NextCheckAt",
                table: "MediaWatches",
                column: "NextCheckAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseObservations_Decision_NextNotificationAt",
                table: "ReleaseObservations",
                columns: new[] { "Decision", "NextNotificationAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseObservations_JobId",
                table: "ReleaseObservations",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseObservations_MediaItemId_Fingerprint",
                table: "ReleaseObservations",
                columns: new[] { "MediaItemId", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaWatches");

            migrationBuilder.DropTable(
                name: "ReleaseObservations");

            migrationBuilder.DropTable(
                name: "WatchSettings");
        }
    }
}
