using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelegramTrackSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramTrackSelections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    FileJson = table.Column<string>(type: "text", nullable: true),
                    AudioIndexesJson = table.Column<string>(type: "text", nullable: false),
                    SubtitleIndexesJson = table.Column<string>(type: "text", nullable: false),
                    Heading = table.Column<string>(type: "text", nullable: true),
                    Page = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramTrackSelections", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TelegramTrackSelections",
                columns: new[] { "Id", "AudioIndexesJson", "FileJson", "Heading", "JobId", "MessageId", "Page", "SessionId", "SubtitleIndexesJson" },
                values: new object[] { 1, "[]", null, null, null, null, 0, new Guid("00000000-0000-0000-0000-000000000000"), "[]" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramTrackSelections");
        }
    }
}
