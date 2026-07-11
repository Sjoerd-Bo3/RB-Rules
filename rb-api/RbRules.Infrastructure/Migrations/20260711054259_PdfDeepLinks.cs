using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PdfDeepLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "page",
                table: "rule_chunk",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "file_url",
                table: "document",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "page",
                table: "rule_chunk");

            migrationBuilder.DropColumn(
                name: "file_url",
                table: "document");
        }
    }
}
