using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiProviderUsage325 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "cost_usd",
                table: "mining_run",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "input_tokens",
                table: "mining_run",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "llm_calls",
                table: "mining_run",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "llm_model_alias",
                table: "mining_run",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "llm_provider",
                table: "mining_run",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_tokens",
                table: "mining_run",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_unit",
                table: "mining_run",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_mining_run_llm_provider_started_at",
                table: "mining_run",
                columns: new[] { "llm_provider", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_mining_run_llm_provider_started_at",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "cost_usd",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "llm_calls",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "llm_model_alias",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "llm_provider",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "mining_run");

            migrationBuilder.DropColumn(
                name: "usage_unit",
                table: "mining_run");
        }
    }
}
