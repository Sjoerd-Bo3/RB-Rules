using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChangeConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "consolidated_with_id",
                table: "change",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_consolidated_with_id",
                table: "change",
                column: "consolidated_with_id");

            migrationBuilder.AddForeignKey(
                name: "fk_change_change_consolidated_with_id",
                table: "change",
                column: "consolidated_with_id",
                principalTable: "change",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_change_change_consolidated_with_id",
                table: "change");

            migrationBuilder.DropIndex(
                name: "ix_change_consolidated_with_id",
                table: "change");

            migrationBuilder.DropColumn(
                name: "consolidated_with_id",
                table: "change");
        }
    }
}
