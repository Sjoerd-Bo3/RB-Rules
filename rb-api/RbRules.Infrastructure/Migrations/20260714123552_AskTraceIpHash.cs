using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AskTraceIpHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ip_hash",
                table: "ask_trace",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ask_trace_ip_hash_created_at",
                table: "ask_trace",
                columns: new[] { "ip_hash", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ask_trace_user_id_created_at",
                table: "ask_trace",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ask_trace_ip_hash_created_at",
                table: "ask_trace");

            migrationBuilder.DropIndex(
                name: "ix_ask_trace_user_id_created_at",
                table: "ask_trace");

            migrationBuilder.DropColumn(
                name: "ip_hash",
                table: "ask_trace");
        }
    }
}
