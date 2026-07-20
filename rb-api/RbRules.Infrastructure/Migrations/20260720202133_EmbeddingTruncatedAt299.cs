using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmbeddingTruncatedAt299 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "embedding_truncated_at",
                table: "rule_chunk",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "embedding_truncated_at",
                table: "knowledge_doc",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "embedding_truncated_at",
                table: "card",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding_truncated_at",
                table: "rule_chunk");

            migrationBuilder.DropColumn(
                name: "embedding_truncated_at",
                table: "knowledge_doc");

            migrationBuilder.DropColumn(
                name: "embedding_truncated_at",
                table: "card");
        }
    }
}
