using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgenticAskTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "agentic",
                table: "ask_trace",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "brain_steps",
                table: "ask_trace",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "agentic",
                table: "ask_metric",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agentic",
                table: "ask_trace");

            migrationBuilder.DropColumn(
                name: "brain_steps",
                table: "ask_trace");

            migrationBuilder.DropColumn(
                name: "agentic",
                table: "ask_metric");
        }
    }
}
