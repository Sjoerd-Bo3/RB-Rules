using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InteractionMiningWatermark249 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "interactions_mined_at",
                table: "card",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "interactions_mined_by_run_id",
                table: "card",
                type: "character varying(26)",
                maxLength: 26,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_card_interactions_mined_at",
                table: "card",
                column: "interactions_mined_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_card_interactions_mined_at",
                table: "card");

            migrationBuilder.DropColumn(
                name: "interactions_mined_at",
                table: "card");

            migrationBuilder.DropColumn(
                name: "interactions_mined_by_run_id",
                table: "card");
        }
    }
}
