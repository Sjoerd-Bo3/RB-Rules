using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EvalIndustrialization231 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eval_baseline",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ring = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    query_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    metric = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    mean = table.Column<double>(type: "double precision", nullable: false),
                    std_dev = table.Column<double>(type: "double precision", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    git_sha = table.Column<string>(type: "text", nullable: true),
                    prompt_contract_hash = table.Column<string>(type: "text", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_baseline", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_run",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    ring = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    git_sha = table.Column<string>(type: "text", nullable: true),
                    llm_model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    gating_failure_count = table.Column<int>(type: "integer", nullable: false),
                    shadow_count = table.Column<int>(type: "integer", nullable: false),
                    memo = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_run", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eval_baseline_ring_query_type_metric",
                table: "eval_baseline",
                columns: new[] { "ring", "query_type", "metric" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_run_created_at",
                table: "eval_run",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_eval_run_ring_passed",
                table: "eval_run",
                columns: new[] { "ring", "passed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_baseline");

            migrationBuilder.DropTable(
                name: "eval_run");
        }
    }
}
