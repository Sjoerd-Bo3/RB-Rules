using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReifiedInteractions226 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "interaction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agent_ref = table.Column<string>(type: "text", nullable: false),
                    patient_ref = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    governed_by_ref = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    created_by_run_id = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interaction", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interaction_decision",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    interaction_id = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    memo = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interaction_decision", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rejection_tombstone",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dedupe_key = table.Column<string>(type: "text", nullable: false),
                    agent_ref = table.Column<string>(type: "text", nullable: false),
                    patient_ref = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    restore_path = table.Column<string>(type: "text", nullable: false),
                    lifted = table.Column<bool>(type: "boolean", nullable: false),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rejection_tombstone", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interaction_condition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    interaction_id = table.Column<long>(type: "bigint", nullable: false),
                    on_kind = table.Column<string>(type: "text", nullable: false),
                    subject_role = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<string>(type: "text", nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interaction_condition", x => x.id);
                    table.ForeignKey(
                        name: "fk_interaction_condition_interaction_interaction_id",
                        column: x => x.interaction_id,
                        principalTable: "interaction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_interaction_agent_ref",
                table: "interaction",
                column: "agent_ref");

            migrationBuilder.CreateIndex(
                name: "ix_interaction_agent_ref_patient_ref_kind",
                table: "interaction",
                columns: new[] { "agent_ref", "patient_ref", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_interaction_patient_ref",
                table: "interaction",
                column: "patient_ref");

            migrationBuilder.CreateIndex(
                name: "ix_interaction_status",
                table: "interaction",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_interaction_condition_interaction_id",
                table: "interaction_condition",
                column: "interaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_interaction_decision_interaction_id",
                table: "interaction_decision",
                column: "interaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_rejection_tombstone_dedupe_key",
                table: "rejection_tombstone",
                column: "dedupe_key");

            migrationBuilder.CreateIndex(
                name: "ix_rejection_tombstone_lifted",
                table: "rejection_tombstone",
                column: "lifted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interaction_condition");

            migrationBuilder.DropTable(
                name: "interaction_decision");

            migrationBuilder.DropTable(
                name: "rejection_tombstone");

            migrationBuilder.DropTable(
                name: "interaction");
        }
    }
}
