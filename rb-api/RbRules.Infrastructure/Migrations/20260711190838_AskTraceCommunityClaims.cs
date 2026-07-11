using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AskTraceCommunityClaims : Migration
    {
        // Hotfix: deze migratie was per ongeluk geregenereerd tegen een door
        // een merge beschadigde snapshot en hermaakte daardoor het volledige
        // claims-model uit 20260711163224_Claims — dat bestaat al op productie
        // (en zou ook op een verse database dubbel zijn). Alleen de echte
        // delta blijft over: de community_claims-kolom op ask_trace (#51).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "community_claims",
                table: "ask_trace",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "community_claims",
                table: "ask_trace");
        }
    }
}
