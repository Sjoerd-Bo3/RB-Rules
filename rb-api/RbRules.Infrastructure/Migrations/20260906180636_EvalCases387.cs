using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EvalCases387 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "results_json",
                table: "eval_run",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "samples_json",
                table: "eval_run",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "eval_case",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    query_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    superseded_by_erratum = table.Column<string>(type: "text", nullable: true),
                    gold_support_json = table.Column<string>(type: "text", nullable: false),
                    expected_citations_json = table.Column<string>(type: "text", nullable: false),
                    forbidden_claims_json = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    origin_ref = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_case", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eval_case_status",
                table: "eval_case",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_case");

            migrationBuilder.DropColumn(
                name: "results_json",
                table: "eval_run");

            migrationBuilder.DropColumn(
                name: "samples_json",
                table: "eval_run");
        }
    }
}
