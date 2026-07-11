using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <summary>Expression-GIN-index voor het full-text-kanaal van /ask
    /// (review-fix #43): de query gebruikt to_tsvector('english', text) —
    /// deze index matcht die expressie exact, dus Postgres scant niet langer
    /// de hele rule_chunk-tabel per vraag.</summary>
    public partial class RuleChunkFtsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_rule_chunk_text_tsv " +
                "ON rule_chunk USING GIN (to_tsvector('english', text));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_rule_chunk_text_tsv;");
        }
    }
}
