// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AcessoSemSenha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_attempts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scope = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "auth_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    email_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_codes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_attempts_scope_occurred_at",
                table: "auth_attempts",
                columns: new[] { "scope", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_expires_at",
                table: "auth_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_user_id_revoked_at",
                table: "auth_sessions",
                columns: new[] { "user_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_email_created_at",
                table: "login_codes",
                columns: new[] { "email", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_expires_at",
                table: "login_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_token_hash",
                table: "login_codes",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_attempts");

            migrationBuilder.DropTable(
                name: "auth_sessions");

            migrationBuilder.DropTable(
                name: "login_codes");
        }
    }
}
