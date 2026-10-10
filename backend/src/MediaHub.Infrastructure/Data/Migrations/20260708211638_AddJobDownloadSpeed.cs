using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobDownloadSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DownloadSpeed",
                table: "PipelineJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DownloadSpeed",
                table: "PipelineJobs");
        }
    }
}
