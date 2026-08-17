using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalEntities225 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "canonical_entity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    canonical_label = table.Column<string>(type: "text", nullable: false),
                    alt_labels = table.Column<string[]>(type: "text[]", nullable: false),
                    definition = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    merged_into_id = table.Column<long>(type: "bigint", nullable: true),
                    created_by_run_id = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    embedding_dim = table.Column<int>(type: "integer", nullable: true),
                    embedding_content_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    merged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_canonical_entity", x => x.id);
                    table.ForeignKey(
                        name: "fk_canonical_entity_canonical_entity_merged_into_id",
                        column: x => x.merged_into_id,
                        principalTable: "canonical_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "merge_candidate",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_a_id = table.Column<long>(type: "bigint", nullable: false),
                    entity_b_id = table.Column<long>(type: "bigint", nullable: false),
                    verdict = table.Column<string>(type: "text", nullable: false),
                    signal_count = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merge_candidate", x => x.id);
                    table.ForeignKey(
                        name: "fk_merge_candidate_canonical_entity_entity_a_id",
                        column: x => x.entity_a_id,
                        principalTable: "canonical_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_merge_candidate_canonical_entity_entity_b_id",
                        column: x => x.entity_b_id,
                        principalTable: "canonical_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merge_decision",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_entity_id = table.Column<long>(type: "bigint", nullable: false),
                    target_entity_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    memo = table.Column<string>(type: "text", nullable: false),
                    moved_alt_labels = table.Column<string[]>(type: "text[]", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reverted = table.Column<bool>(type: "boolean", nullable: false),
                    reverted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merge_decision", x => x.id);
                    table.ForeignKey(
                        name: "fk_merge_decision_canonical_entity_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "canonical_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_merge_decision_canonical_entity_target_entity_id",
                        column: x => x.target_entity_id,
                        principalTable: "canonical_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_canonical_entity_kind",
                table: "canonical_entity",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ix_canonical_entity_kind_canonical_label",
                table: "canonical_entity",
                columns: new[] { "kind", "canonical_label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_canonical_entity_merged_into_id",
                table: "canonical_entity",
                column: "merged_into_id");

            migrationBuilder.CreateIndex(
                name: "ix_canonical_entity_status",
                table: "canonical_entity",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_merge_candidate_entity_a_id_entity_b_id",
                table: "merge_candidate",
                columns: new[] { "entity_a_id", "entity_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_merge_candidate_entity_b_id",
                table: "merge_candidate",
                column: "entity_b_id");

            migrationBuilder.CreateIndex(
                name: "ix_merge_candidate_status",
                table: "merge_candidate",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_merge_decision_source_entity_id",
                table: "merge_decision",
                column: "source_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_merge_decision_target_entity_id",
                table: "merge_decision",
                column: "target_entity_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "merge_candidate");

            migrationBuilder.DropTable(
                name: "merge_decision");

            migrationBuilder.DropTable(
                name: "canonical_entity");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
