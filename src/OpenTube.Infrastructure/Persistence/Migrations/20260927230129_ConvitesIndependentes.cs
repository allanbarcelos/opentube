// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConvitesIndependentes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "invitation_id",
                table: "access_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    target_type = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_after_first_use = table.Column<TimeSpan>(type: "interval", nullable: true),
                    max_views = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_grants_invitation_id",
                table: "access_grants",
                column: "invitation_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_target_type_target_id",
                table: "invitations",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_access_grants_invitations_invitation_id",
                table: "access_grants",
                column: "invitation_id",
                principalTable: "invitations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_access_grants_invitations_invitation_id",
                table: "access_grants");

            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropIndex(
                name: "ix_access_grants_invitation_id",
                table: "access_grants");

            migrationBuilder.DropColumn(
                name: "invitation_id",
                table: "access_grants");
        }
    }
}
