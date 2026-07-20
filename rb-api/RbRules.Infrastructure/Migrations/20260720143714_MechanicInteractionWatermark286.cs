using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MechanicInteractionWatermark286 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "interactions_mined_at",
                table: "canonical_entity",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "interactions_mined_by_run_id",
                table: "canonical_entity",
                type: "character varying(26)",
                maxLength: 26,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_canonical_entity_interactions_mined_at",
                table: "canonical_entity",
                column: "interactions_mined_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_canonical_entity_interactions_mined_at",
                table: "canonical_entity");

            migrationBuilder.DropColumn(
                name: "interactions_mined_at",
                table: "canonical_entity");

            migrationBuilder.DropColumn(
                name: "interactions_mined_by_run_id",
                table: "canonical_entity");
        }
    }
}
