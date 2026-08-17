using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Governance230 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lifecycle_event",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subject_ref = table.Column<string>(type: "text", nullable: false),
                    fact_kind = table.Column<string>(type: "text", nullable: false),
                    from_state = table.Column<string>(type: "text", nullable: false),
                    to_state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    superseded_by_ref = table.Column<string>(type: "text", nullable: true),
                    restore_path = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    reverted = table.Column<bool>(type: "boolean", nullable: false),
                    reverted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lifecycle_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ontology_version",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    bump_kind = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ontology_version", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schema_proposal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    proposed_name = table.Column<string>(type: "text", nullable: false),
                    parent_type = table.Column<string>(type: "text", nullable: true),
                    official_card_count = table.Column<int>(type: "integer", nullable: false),
                    has_rule_section_evidence = table.Column<bool>(type: "boolean", nullable: false),
                    rule_section_ref = table.Column<string>(type: "text", nullable: true),
                    memo = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    bump_kind = table.Column<string>(type: "text", nullable: false),
                    migrated_in_version = table.Column<string>(type: "text", nullable: true),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    reviewed_by = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_proposal", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lifecycle_event_reverted",
                table: "lifecycle_event",
                column: "reverted");

            migrationBuilder.CreateIndex(
                name: "ix_lifecycle_event_subject_ref_created_at",
                table: "lifecycle_event",
                columns: new[] { "subject_ref", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_lifecycle_event_to_state",
                table: "lifecycle_event",
                column: "to_state");

            migrationBuilder.CreateIndex(
                name: "ix_ontology_version_applied_at",
                table: "ontology_version",
                column: "applied_at");

            migrationBuilder.CreateIndex(
                name: "ix_ontology_version_version",
                table: "ontology_version",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_schema_proposal_kind_proposed_name",
                table: "schema_proposal",
                columns: new[] { "kind", "proposed_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_schema_proposal_status",
                table: "schema_proposal",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lifecycle_event");

            migrationBuilder.DropTable(
                name: "ontology_version");

            migrationBuilder.DropTable(
                name: "schema_proposal");
        }
    }
}
