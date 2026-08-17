using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MechanicPredicates229 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mechanic_predicate",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    subject_entity_id = table.Column<long>(type: "bigint", nullable: false),
                    predicate = table.Column<string>(type: "text", nullable: false),
                    object_token = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    created_by_run_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mechanic_predicate", x => x.id);
                    table.ForeignKey(
                        name: "fk_mechanic_predicate_canonical_entity_subject_entity_id",
                        column: x => x.subject_entity_id,
                        principalTable: "canonical_entity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mechanic_predicate_status",
                table: "mechanic_predicate",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_mechanic_predicate_subject_entity_id",
                table: "mechanic_predicate",
                column: "subject_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_mechanic_predicate_subject_entity_id_predicate_object_token",
                table: "mechanic_predicate",
                columns: new[] { "subject_entity_id", "predicate", "object_token" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mechanic_predicate");
        }
    }
}
