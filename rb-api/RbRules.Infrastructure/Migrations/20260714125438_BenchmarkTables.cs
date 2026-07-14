using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BenchmarkTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "benchmark_question",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    external_key = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<string[]>(type: "text[]", nullable: false),
                    correct_index = table.Column<int>(type: "integer", nullable: true),
                    explanation = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_benchmark_question", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_run",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    label = table.Column<string>(type: "text", nullable: true),
                    question_count = table.Column<int>(type: "integer", nullable: false),
                    keyed_count = table.Column<int>(type: "integer", nullable: false),
                    correct_count = table.Column<int>(type: "integer", nullable: false),
                    score_percent = table.Column<double>(type: "double precision", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_benchmark_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_result",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<long>(type: "bigint", nullable: false),
                    question_id = table.Column<long>(type: "bigint", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    chosen_index = table.Column<int>(type: "integer", nullable: true),
                    correct = table.Column<bool>(type: "boolean", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_benchmark_result", x => x.id);
                    table.ForeignKey(
                        name: "fk_benchmark_result_benchmark_question_question_id",
                        column: x => x.question_id,
                        principalTable: "benchmark_question",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_benchmark_result_benchmark_run_run_id",
                        column: x => x.run_id,
                        principalTable: "benchmark_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_question_external_key",
                table: "benchmark_question",
                column: "external_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_result_question_id",
                table: "benchmark_result",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_result_run_id",
                table: "benchmark_result",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_run_started_at",
                table: "benchmark_run",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_result");

            migrationBuilder.DropTable(
                name: "benchmark_question");

            migrationBuilder.DropTable(
                name: "benchmark_run");
        }
    }
}
