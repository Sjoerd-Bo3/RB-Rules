using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InteractionExtractProvenance323 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "input_tokens",
                table: "mining_run",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_tokens",
                table: "mining_run",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "extract_batch_position",
                table: "interaction",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "extract_model",
                table: "interaction",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "extract_batch_position",
                table: "interaction");

            migrationBuilder.DropColumn(
                name: "extract_model",
                table: "interaction");
        }
    }
}
