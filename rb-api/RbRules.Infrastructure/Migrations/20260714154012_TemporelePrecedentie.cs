using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TemporelePrecedentie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "source",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "source",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_from",
                table: "erratum",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "published_at",
                table: "source");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "source");

            migrationBuilder.DropColumn(
                name: "effective_from",
                table: "erratum");
        }
    }
}
