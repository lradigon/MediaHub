using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobEncodingFps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "EncodingFps",
                table: "PipelineJobs",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncodingFps",
                table: "PipelineJobs");
        }
    }
}
