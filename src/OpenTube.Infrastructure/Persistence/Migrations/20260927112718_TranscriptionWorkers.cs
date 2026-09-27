// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TranscriptionWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transcription_workers",
                columns: table => new
                {
                    worker_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    available = table.Column<bool>(type: "boolean", nullable: false),
                    engine = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcription_workers", x => x.worker_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transcription_workers");
        }
    }
}
