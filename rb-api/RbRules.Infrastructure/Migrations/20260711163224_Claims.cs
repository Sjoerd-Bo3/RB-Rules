using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Claims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claims_mined_at",
                table: "document",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "claim",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    topic_type = table.Column<string>(type: "text", nullable: false),
                    topic_ref = table.Column<string>(type: "text", nullable: false),
                    statement = table.Column<string>(type: "text", nullable: false),
                    corroboration = table.Column<int>(type: "integer", nullable: false),
                    trust_score = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    official_status = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "claim_source",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    claim_id = table.Column<long>(type: "bigint", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    quote_excerpt = table.Column<string>(type: "text", nullable: true),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_source", x => x.id);
                    table.ForeignKey(
                        name: "fk_claim_source_claim_claim_id",
                        column: x => x.claim_id,
                        principalTable: "claim",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_claim_source_source_source_id",
                        column: x => x.source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_claim_embedding",
                table: "claim",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_status",
                table: "claim",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_claim_topic_type_topic_ref",
                table: "claim",
                columns: new[] { "topic_type", "topic_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_source_claim_id_source_id",
                table: "claim_source",
                columns: new[] { "claim_id", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_source_source_id",
                table: "claim_source",
                column: "source_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claim_source");

            migrationBuilder.DropTable(
                name: "claim");

            migrationBuilder.DropColumn(
                name: "claims_mined_at",
                table: "document");
        }
    }
}
