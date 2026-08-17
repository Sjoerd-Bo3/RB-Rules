using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SourceFeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "feed_id",
                table: "source",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "source_feed",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auto_approve = table.Column<bool>(type: "boolean", nullable: false),
                    category_filter = table.Column<string>(type: "text", nullable: true),
                    cadence = table.Column<string>(type: "text", nullable: false),
                    last_checked = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_hash = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_feed", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_feed_id",
                table: "source",
                column: "feed_id");

            migrationBuilder.AddForeignKey(
                name: "fk_source_source_feeds_feed_id",
                table: "source",
                column: "feed_id",
                principalTable: "source_feed",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_source_source_feeds_feed_id",
                table: "source");

            migrationBuilder.DropTable(
                name: "source_feed");

            migrationBuilder.DropIndex(
                name: "ix_source_feed_id",
                table: "source");

            migrationBuilder.DropColumn(
                name: "feed_id",
                table: "source");
        }
    }
}
