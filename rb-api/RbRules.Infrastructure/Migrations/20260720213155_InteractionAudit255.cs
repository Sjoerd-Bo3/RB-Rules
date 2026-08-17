using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InteractionAudit255 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "interaction_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    interaction_id = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    correct = table.Column<bool>(type: "boolean", nullable: false),
                    supported_by_evidence = table.Column<bool>(type: "boolean", nullable: false),
                    motivation = table.Column<string>(type: "text", nullable: true),
                    interaction_status_at_audit = table.Column<string>(type: "text", nullable: true),
                    audited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interaction_audit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_interaction_audit_audited_at",
                table: "interaction_audit",
                column: "audited_at");

            migrationBuilder.CreateIndex(
                name: "ix_interaction_audit_interaction_id_prompt_version",
                table: "interaction_audit",
                columns: new[] { "interaction_id", "prompt_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interaction_audit");
        }
    }
}
