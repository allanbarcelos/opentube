// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversasDeSuporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "support_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    timestamp_seconds = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_by_user_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_by_admin_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_threads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    from_admin = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_support_messages_support_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "support_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_support_messages_thread_id_created_at",
                table: "support_messages",
                columns: new[] { "thread_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_support_threads_status_last_message_at",
                table: "support_threads",
                columns: new[] { "status", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_support_threads_user_id",
                table: "support_threads",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_threads_video_id_user_id",
                table: "support_threads",
                columns: new[] { "video_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "support_messages");

            migrationBuilder.DropTable(
                name: "support_threads");
        }
    }
}
