using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CardMechanics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "effects",
                table: "card",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "mechanics",
                table: "card",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "triggers",
                table: "card",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "effects",
                table: "card");

            migrationBuilder.DropColumn(
                name: "mechanics",
                table: "card");

            migrationBuilder.DropColumn(
                name: "triggers",
                table: "card");
        }
    }
}
