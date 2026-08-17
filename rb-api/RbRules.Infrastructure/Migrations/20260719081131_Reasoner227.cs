using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Reasoner227 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reasoning_conflict",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pattern_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    subject_ref = table.Column<string>(type: "text", nullable: false),
                    counter_ref = table.Column<string>(type: "text", nullable: true),
                    memo = table.Column<string>(type: "text", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reasoning_conflict", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reasoning_conflict_channel",
                table: "reasoning_conflict",
                column: "channel");

            migrationBuilder.CreateIndex(
                name: "ix_reasoning_conflict_dedupe_key",
                table: "reasoning_conflict",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reasoning_conflict_kind",
                table: "reasoning_conflict",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ix_reasoning_conflict_status",
                table: "reasoning_conflict",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reasoning_conflict");
        }
    }
}
