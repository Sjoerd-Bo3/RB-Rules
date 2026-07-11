using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AskTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ask_trace",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    question = table.Column<string>(type: "text", nullable: false),
                    question_type = table.Column<string>(type: "text", nullable: true),
                    source_bias = table.Column<string>(type: "text", nullable: true),
                    mentions_card = table.Column<bool>(type: "boolean", nullable: false),
                    mechanic_matches = table.Column<string>(type: "text", nullable: true),
                    sections = table.Column<string>(type: "text", nullable: true),
                    context_cards = table.Column<string>(type: "text", nullable: true),
                    verified_rulings = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    had_image = table.Column<bool>(type: "boolean", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    ok = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ask_trace", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ask_trace_created_at",
                table: "ask_trace",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ask_trace");
        }
    }
}
