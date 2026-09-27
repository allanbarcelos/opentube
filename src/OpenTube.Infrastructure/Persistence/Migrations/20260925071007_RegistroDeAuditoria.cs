// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegistroDeAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor_email",
                table: "audit_entries",
                column: "actor_email");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_at",
                table: "audit_entries",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity_type_entity_id",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries");
        }
    }
}
