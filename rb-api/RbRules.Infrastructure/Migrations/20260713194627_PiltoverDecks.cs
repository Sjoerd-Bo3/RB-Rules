using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PiltoverDecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deck",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    pa_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    domains = table.Column<string[]>(type: "text[]", nullable: false),
                    pa_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pa_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    views = table.Column<int>(type: "integer", nullable: false),
                    likes = table.Column<int>(type: "integer", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deck", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deck_card",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    deck_id = table.Column<long>(type: "bigint", nullable: false),
                    section = table.Column<string>(type: "text", nullable: false),
                    card_code = table.Column<string>(type: "text", nullable: false),
                    canonical_riftbound_id = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deck_card", x => x.id);
                    table.ForeignKey(
                        name: "fk_deck_card_deck_deck_id",
                        column: x => x.deck_id,
                        principalTable: "deck",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deck_pa_id",
                table: "deck",
                column: "pa_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deck_card_canonical_riftbound_id",
                table: "deck_card",
                column: "canonical_riftbound_id");

            migrationBuilder.CreateIndex(
                name: "ix_deck_card_deck_id",
                table: "deck_card",
                column: "deck_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deck_card");

            migrationBuilder.DropTable(
                name: "deck");
        }
    }
}
