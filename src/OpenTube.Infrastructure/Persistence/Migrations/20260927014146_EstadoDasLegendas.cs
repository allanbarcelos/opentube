// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EstadoDasLegendas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "content_updated_at",
                table: "video_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error",
                table: "video_assets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "source",
                table: "video_assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "video_assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                table: "video_assets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Legendas que já existiam têm conteúdo e estão prontas. As geradas pela
            // transcrição antiga são reconhecidas pelo rótulo que ela gravava.
            migrationBuilder.Sql("""
                UPDATE video_assets SET status_changed_at = created_at;
                UPDATE video_assets
                   SET content_updated_at = created_at,
                       language = lower(language),
                       source = CASE WHEN label = 'Legenda automática' THEN 1 ELSE 0 END
                 WHERE kind = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_video_assets_caption_language",
                table: "video_assets",
                columns: new[] { "video_id", "language" },
                unique: true,
                filter: "kind = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_video_assets_caption_language",
                table: "video_assets");

            migrationBuilder.DropColumn(
                name: "content_updated_at",
                table: "video_assets");

            migrationBuilder.DropColumn(
                name: "error",
                table: "video_assets");

            migrationBuilder.DropColumn(
                name: "source",
                table: "video_assets");

            migrationBuilder.DropColumn(
                name: "status",
                table: "video_assets");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                table: "video_assets");
        }
    }
}
