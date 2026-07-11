using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RbRules.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AccountsQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "user_id",
                table: "ask_trace",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "ask_metric",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "user_id",
                table: "ask_metric",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "text", nullable: false),
                    blocked = table.Column<bool>(type: "boolean", nullable: false),
                    daily_quota = table.Column<int>(type: "integer", nullable: false),
                    daily_photo_quota = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_token",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_token", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_session",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_session_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ask_metric_user_id_created_at",
                table: "ask_metric",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_app_user_email",
                table: "app_user",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_token_email",
                table: "login_token",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_login_token_token_hash",
                table: "login_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_session_token_hash",
                table: "user_session",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_session_user_id",
                table: "user_session",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_token");

            migrationBuilder.DropTable(
                name: "user_session");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropIndex(
                name: "ix_ask_metric_user_id_created_at",
                table: "ask_metric");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "ask_trace");

            migrationBuilder.DropColumn(
                name: "model",
                table: "ask_metric");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "ask_metric");
        }
    }
}
