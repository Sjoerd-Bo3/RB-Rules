using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CardPresentationFields270 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "effect_plain",
                table: "card",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "flags",
                table: "card",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "illustrator",
                table: "card",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_alt_text",
                table: "card",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_color_primary",
                table: "card",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_color_secondary",
                table: "card",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "image_height",
                table: "card",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "image_width",
                table: "card",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "might_bonus",
                table: "card",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_code",
                table: "card",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "effect_plain",
                table: "card");

            migrationBuilder.DropColumn(
                name: "flags",
                table: "card");

            migrationBuilder.DropColumn(
                name: "illustrator",
                table: "card");

            migrationBuilder.DropColumn(
                name: "image_alt_text",
                table: "card");

            migrationBuilder.DropColumn(
                name: "image_color_primary",
                table: "card");

            migrationBuilder.DropColumn(
                name: "image_color_secondary",
                table: "card");

            migrationBuilder.DropColumn(
                name: "image_height",
                table: "card");

            migrationBuilder.DropColumn(
                name: "image_width",
                table: "card");

            migrationBuilder.DropColumn(
                name: "might_bonus",
                table: "card");

            migrationBuilder.DropColumn(
                name: "public_code",
                table: "card");
        }
    }
}
