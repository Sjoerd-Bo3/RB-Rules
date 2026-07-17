using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IgnoreSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ignore_reason",
                table: "source",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ignored_at",
                table: "source",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ignore_reason",
                table: "source");

            migrationBuilder.DropColumn(
                name: "ignored_at",
                table: "source");
        }
    }
}
