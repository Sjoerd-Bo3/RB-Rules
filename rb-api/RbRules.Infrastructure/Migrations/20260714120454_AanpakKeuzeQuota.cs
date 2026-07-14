using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AanpakKeuzeQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "escalated_by",
                table: "ask_trace",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "escalated_by",
                table: "ask_metric",
                type: "text",
                nullable: true);

            // Backfill: bestaande accounts krijgen hetzelfde Grondig-tegoed
            // als nieuwe accounts (C#-default 5) — 0 zou de aanpak-keuze voor
            // hen stil uitzetten.
            migrationBuilder.AddColumn<int>(
                name: "daily_agentic_quota",
                table: "app_user",
                type: "integer",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "escalated_by",
                table: "ask_trace");

            migrationBuilder.DropColumn(
                name: "escalated_by",
                table: "ask_metric");

            migrationBuilder.DropColumn(
                name: "daily_agentic_quota",
                table: "app_user");
        }
    }
}
