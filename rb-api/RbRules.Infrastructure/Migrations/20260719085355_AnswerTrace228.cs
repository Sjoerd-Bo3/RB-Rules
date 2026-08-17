using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnswerTrace228 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "answer_trace",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    question_type = table.Column<string>(type: "text", nullable: false),
                    retrieval_mode = table.Column<string>(type: "text", nullable: false),
                    fallback_reason = table.Column<string>(type: "text", nullable: true),
                    beta = table.Column<double>(type: "double precision", nullable: false),
                    primary_channel = table.Column<string>(type: "text", nullable: false),
                    gate_memo = table.Column<string>(type: "text", nullable: true),
                    graph_epoch = table.Column<string>(type: "text", nullable: true),
                    llm_model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    embedding_rev = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answer_trace", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "answer_trace_support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    answer_trace_id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    citation_n = table.Column<int>(type: "integer", nullable: false),
                    subject_ref = table.Column<string>(type: "text", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    trust_weight_at_query = table.Column<double>(type: "double precision", nullable: false),
                    widget_marker = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answer_trace_support", x => x.id);
                    table.ForeignKey(
                        name: "fk_answer_trace_support_answer_trace_answer_trace_id",
                        column: x => x.answer_trace_id,
                        principalTable: "answer_trace",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_answer_trace_created_at",
                table: "answer_trace",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_answer_trace_primary_channel",
                table: "answer_trace",
                column: "primary_channel");

            migrationBuilder.CreateIndex(
                name: "ix_answer_trace_support_answer_trace_id",
                table: "answer_trace_support",
                column: "answer_trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_answer_trace_support_subject_ref",
                table: "answer_trace_support",
                column: "subject_ref");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_trace_support");

            migrationBuilder.DropTable(
                name: "answer_trace");
        }
    }
}
