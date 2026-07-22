using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageMetering328 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_tariff",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    model = table.Column<string>(type: "text", nullable: false),
                    input_usd_per_m_tok = table.Column<decimal>(type: "numeric", nullable: false),
                    output_usd_per_m_tok = table.Column<decimal>(type: "numeric", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_tariff", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_event",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    origin = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    ok = table.Column<bool>(type: "boolean", nullable: false),
                    tariff_version = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_tariff_model_effective_from",
                table: "ai_tariff",
                columns: new[] { "model", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_event_created_at",
                table: "ai_usage_event",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_event_user_id_created_at",
                table: "ai_usage_event",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_tariff");

            migrationBuilder.DropTable(
                name: "ai_usage_event");
        }
    }
}
