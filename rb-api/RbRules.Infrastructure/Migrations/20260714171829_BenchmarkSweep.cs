using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BenchmarkSweep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "benchmark_run",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "run_index",
                table: "benchmark_run",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "sweep_id",
                table: "benchmark_run",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_run_sweep_id",
                table: "benchmark_run",
                column: "sweep_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_benchmark_run_sweep_id",
                table: "benchmark_run");

            migrationBuilder.DropColumn(
                name: "model",
                table: "benchmark_run");

            migrationBuilder.DropColumn(
                name: "run_index",
                table: "benchmark_run");

            migrationBuilder.DropColumn(
                name: "sweep_id",
                table: "benchmark_run");
        }
    }
}
