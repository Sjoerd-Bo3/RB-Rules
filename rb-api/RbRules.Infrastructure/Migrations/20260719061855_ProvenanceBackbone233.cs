using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProvenanceBackbone233 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "embedding_content_hash",
                table: "rule_chunk",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_content_hash",
                table: "knowledge_doc",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_content_hash",
                table: "correction",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_model",
                table: "correction",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_content_hash",
                table: "claim",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_content_hash",
                table: "card",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mining_run",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    llm_model = table.Column<string>(type: "text", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    vocab_snapshot = table.Column<string>(type: "text", nullable: true),
                    git_sha = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    candidates = table.Column<int>(type: "integer", nullable: false),
                    verified = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mining_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assertion",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    fact_kind = table.Column<string>(type: "text", nullable: false),
                    mining_run_id = table.Column<string>(type: "text", nullable: false),
                    derived_from_ref = table.Column<string>(type: "text", nullable: false),
                    derived_from_document_id = table.Column<long>(type: "bigint", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    embedding_dim = table.Column<int>(type: "integer", nullable: true),
                    verifier = table.Column<string>(type: "text", nullable: true),
                    evidence_span = table.Column<string>(type: "text", nullable: true),
                    verdict = table.Column<string>(type: "text", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    asserted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assertion", x => x.id);
                    table.ForeignKey(
                        name: "fk_assertion_document_derived_from_document_id",
                        column: x => x.derived_from_document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_assertion_mining_run_mining_run_id",
                        column: x => x.mining_run_id,
                        principalTable: "mining_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assertion_derived_from_document_id",
                table: "assertion",
                column: "derived_from_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_assertion_fact_kind_subject",
                table: "assertion",
                columns: new[] { "fact_kind", "subject" });

            migrationBuilder.CreateIndex(
                name: "ix_assertion_mining_run_id",
                table: "assertion",
                column: "mining_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_mining_run_kind",
                table: "mining_run",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ix_mining_run_started_at",
                table: "mining_run",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assertion");

            migrationBuilder.DropTable(
                name: "mining_run");

            migrationBuilder.DropColumn(
                name: "embedding_content_hash",
                table: "rule_chunk");

            migrationBuilder.DropColumn(
                name: "embedding_content_hash",
                table: "knowledge_doc");

            migrationBuilder.DropColumn(
                name: "embedding_content_hash",
                table: "correction");

            migrationBuilder.DropColumn(
                name: "embedding_model",
                table: "correction");

            migrationBuilder.DropColumn(
                name: "embedding_content_hash",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "embedding_content_hash",
                table: "card");
        }
    }
}
