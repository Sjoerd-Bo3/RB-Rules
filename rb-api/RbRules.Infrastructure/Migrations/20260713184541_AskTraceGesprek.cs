using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AskTraceGesprek : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "answer",
                table: "ask_trace",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "history",
                table: "ask_trace",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "answer",
                table: "ask_trace");

            migrationBuilder.DropColumn(
                name: "history",
                table: "ask_trace");
        }
    }
}
