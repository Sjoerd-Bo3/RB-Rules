using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CardInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_interaction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    card_a_id = table.Column<string>(type: "text", nullable: false),
                    card_b_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_interaction", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_interaction_card_a_id",
                table: "card_interaction",
                column: "card_a_id");

            migrationBuilder.CreateIndex(
                name: "ix_card_interaction_card_a_id_card_b_id",
                table: "card_interaction",
                columns: new[] { "card_a_id", "card_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_card_interaction_card_b_id",
                table: "card_interaction",
                column: "card_b_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_interaction");
        }
    }
}
