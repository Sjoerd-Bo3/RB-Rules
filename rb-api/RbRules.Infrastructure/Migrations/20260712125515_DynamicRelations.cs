using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DynamicRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "relations_mined_at",
                table: "knowledge_doc",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "relation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_ref = table.Column<string>(type: "text", nullable: false),
                    to_ref = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: false),
                    provenance = table.Column<string>(type: "text", nullable: false),
                    trust = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relation", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relation_kind",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relation_kind", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_relation_from_ref_to_ref_kind",
                table: "relation",
                columns: new[] { "from_ref", "to_ref", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_relation_status",
                table: "relation",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_relation_kind_kind",
                table: "relation_kind",
                column: "kind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_relation_kind_status",
                table: "relation_kind",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relation");

            migrationBuilder.DropTable(
                name: "relation_kind");

            migrationBuilder.DropColumn(
                name: "relations_mined_at",
                table: "knowledge_doc");
        }
    }
}
