using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BronTypeLlm188 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_kind",
                table: "source",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_kind_source",
                table: "source",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_kind",
                table: "source");

            migrationBuilder.DropColumn(
                name: "content_kind_source",
                table: "source");
        }
    }
}
