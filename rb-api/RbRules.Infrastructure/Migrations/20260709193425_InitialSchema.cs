using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "card",
                columns: table => new
                {
                    riftbound_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: true),
                    supertype = table.Column<string>(type: "text", nullable: true),
                    rarity = table.Column<string>(type: "text", nullable: true),
                    domains = table.Column<string[]>(type: "text[]", nullable: false),
                    energy = table.Column<int>(type: "integer", nullable: true),
                    might = table.Column<int>(type: "integer", nullable: true),
                    power = table.Column<int>(type: "integer", nullable: true),
                    set_id = table.Column<string>(type: "text", nullable: true),
                    set_label = table.Column<string>(type: "text", nullable: true),
                    collector_number = table.Column<int>(type: "integer", nullable: true),
                    text_plain = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card", x => x.riftbound_id);
                });

            migrationBuilder.CreateTable(
                name: "card_set",
                columns: table => new
                {
                    set_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    published_on = table.Column<DateOnly>(type: "date", nullable: true),
                    card_count = table.Column<int>(type: "integer", nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_set", x => x.set_id);
                });

            migrationBuilder.CreateTable(
                name: "correction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scope = table.Column<string>(type: "text", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    question = table.Column<string>(type: "text", nullable: true),
                    provenance = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correction", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "push_subscription",
                columns: table => new
                {
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    p256dh = table.Column<string>(type: "text", nullable: false),
                    auth = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_subscription", x => x.endpoint);
                });

            migrationBuilder.CreateTable(
                name: "run_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    trust_tier = table.Column<short>(type: "smallint", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    parser = table.Column<string>(type: "text", nullable: false),
                    cadence = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_hash = table.Column<string>(type: "text", nullable: true),
                    last_checked = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "change",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    change_type = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    meaning = table.Column<string>(type: "text", nullable: true),
                    diff = table.Column<string>(type: "text", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_source_source_id",
                        column: x => x.source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conflict",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    topic = table.Column<string>(type: "text", nullable: false),
                    source_a_id = table.Column<string>(type: "text", nullable: true),
                    source_b_id = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    winner_source_id = table.Column<string>(type: "text", nullable: true),
                    explanation = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflict", x => x.id);
                    table.ForeignKey(
                        name: "fk_conflict_source_source_a_id",
                        column: x => x.source_a_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_conflict_source_source_b_id",
                        column: x => x.source_b_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_conflict_source_winner_source_id",
                        column: x => x.winner_source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "document",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_source_source_id",
                        column: x => x.source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rule_chunk",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    section_code = table.Column<string>(type: "text", nullable: true),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_chunk", x => x.id);
                    table.ForeignKey(
                        name: "fk_rule_chunk_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_embedding",
                table: "card",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_card_name",
                table: "card",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_card_set_id",
                table: "card",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_detected_at",
                table: "change",
                column: "detected_at");

            migrationBuilder.CreateIndex(
                name: "ix_change_source_id",
                table: "change",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_source_a_id",
                table: "conflict",
                column: "source_a_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_source_b_id",
                table: "conflict",
                column: "source_b_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_winner_source_id",
                table: "conflict",
                column: "winner_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_correction_status",
                table: "correction",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_document_source_id_retrieved_at",
                table: "document",
                columns: new[] { "source_id", "retrieved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_chunk_document_id",
                table: "rule_chunk",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_rule_chunk_embedding",
                table: "rule_chunk",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_chunk_source_id",
                table: "rule_chunk",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_log_created_at",
                table: "run_log",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card");

            migrationBuilder.DropTable(
                name: "card_set");

            migrationBuilder.DropTable(
                name: "change");

            migrationBuilder.DropTable(
                name: "conflict");

            migrationBuilder.DropTable(
                name: "correction");

            migrationBuilder.DropTable(
                name: "push_subscription");

            migrationBuilder.DropTable(
                name: "rule_chunk");

            migrationBuilder.DropTable(
                name: "run_log");

            migrationBuilder.DropTable(
                name: "document");

            migrationBuilder.DropTable(
                name: "source");
        }
    }
}
