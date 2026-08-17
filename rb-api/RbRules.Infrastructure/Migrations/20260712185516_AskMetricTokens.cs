using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AskMetricTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "input_tokens",
                table: "ask_metric",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_tokens",
                table: "ask_metric",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "ask_metric");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "ask_metric");
        }
    }
}
