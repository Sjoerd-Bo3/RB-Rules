using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeDocs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "primer_docs",
                table: "ask_trace",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_doc",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    section_refs = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_doc", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_doc_kind_topic",
                table: "knowledge_doc",
                columns: new[] { "kind", "topic" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_doc_status",
                table: "knowledge_doc",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_doc");

            migrationBuilder.DropColumn(
                name: "primer_docs",
                table: "ask_trace");
        }
    }
}
