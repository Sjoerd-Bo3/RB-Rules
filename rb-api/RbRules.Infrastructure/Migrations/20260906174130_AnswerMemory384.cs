using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnswerMemory384 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "answer_memory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    question_normalized = table.Column<string>(type: "text", nullable: false),
                    question_type = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    embedding_content_hash = table.Column<string>(type: "text", nullable: true),
                    answer = table.Column<string>(type: "text", nullable: false),
                    citations_json = table.Column<string>(type: "text", nullable: false),
                    source_snapshot = table.Column<string>(type: "text", nullable: false),
                    retrieval_set = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    trust = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hit_count = table.Column<int>(type: "integer", nullable: false),
                    last_hit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    retracted_reason = table.Column<string>(type: "text", nullable: true),
                    retracted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answer_memory", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_answer_memory_created_at",
                table: "answer_memory",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_answer_memory_embedding",
                table: "answer_memory",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_answer_memory_trust",
                table: "answer_memory",
                column: "trust");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answer_memory");
        }
    }
}
