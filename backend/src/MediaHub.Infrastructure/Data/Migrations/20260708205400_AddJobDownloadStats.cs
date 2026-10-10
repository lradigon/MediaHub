using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobDownloadStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Seeders",
                table: "PipelineJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "PipelineJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Seeders",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "PipelineJobs");
        }
    }
}
