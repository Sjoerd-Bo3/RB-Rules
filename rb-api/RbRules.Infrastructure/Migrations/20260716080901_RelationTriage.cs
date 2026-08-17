using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RelationTriage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "recommendation",
                table: "relation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recommendation_reason",
                table: "relation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recommended_at",
                table: "relation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_relation_recommendation",
                table: "relation",
                column: "recommendation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_relation_recommendation",
                table: "relation");

            migrationBuilder.DropColumn(
                name: "recommendation",
                table: "relation");

            migrationBuilder.DropColumn(
                name: "recommendation_reason",
                table: "relation");

            migrationBuilder.DropColumn(
                name: "recommended_at",
                table: "relation");
        }
    }
}
