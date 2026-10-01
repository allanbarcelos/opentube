// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoEmUmaColecao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Um vídeo fica em uma coleção só. Se já havia mais de um vínculo, permanece o da
            // coleção ainda ativa que o recebeu por último; o aviso de "novo" da que perdeu some junto.
            migrationBuilder.Sql(
                """
                DELETE FROM collection_videos AS cv
                USING (
                    SELECT collection_id, video_id
                      FROM (
                          SELECT cv.collection_id,
                                 cv.video_id,
                                 ROW_NUMBER() OVER (
                                     PARTITION BY cv.video_id
                                     ORDER BY (c.deleted_at IS NULL) DESC, cv.added_at DESC, cv.collection_id
                                 ) AS ordem
                            FROM collection_videos AS cv
                            JOIN collections AS c ON c.id = cv.collection_id
                      ) AS ranqueados
                     WHERE ordem > 1
                ) AS extras
                WHERE cv.collection_id = extras.collection_id
                  AND cv.video_id = extras.video_id;

                DELETE FROM collection_video_seen AS visto
                WHERE NOT EXISTS (
                    SELECT 1
                      FROM collection_videos AS cv
                     WHERE cv.collection_id = visto.collection_id
                       AND cv.video_id = visto.video_id
                );
                """);

            migrationBuilder.DropIndex(
                name: "ix_collection_videos_video_id",
                table: "collection_videos");

            migrationBuilder.CreateIndex(
                name: "ix_collection_videos_video_id",
                table: "collection_videos",
                column: "video_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_collection_videos_video_id",
                table: "collection_videos");

            migrationBuilder.CreateIndex(
                name: "ix_collection_videos_video_id",
                table: "collection_videos",
                column: "video_id");
        }
    }
}
