// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExclusaoDefinitivaDeVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O que estava só marcado como excluído some de verdade. Os arquivos no storage,
            // se havia, ficam órfãos: esta migração não alcança o bucket.
            migrationBuilder.Sql(
                """
                DELETE FROM playback_events AS e
                 USING videos AS v
                 WHERE e.video_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM playback_sessions AS s
                 USING videos AS v
                 WHERE s.video_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM video_daily_stats AS s
                 USING videos AS v
                 WHERE s.video_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM video_retention_buckets AS b
                 USING videos AS v
                 WHERE b.video_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM support_threads AS t
                 USING videos AS v
                 WHERE t.video_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM processing_jobs AS j
                 USING videos AS v
                 WHERE j.target_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM access_grants AS g
                 USING videos AS v
                 WHERE g.target_type = 0
                   AND g.target_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM invitations AS i
                 USING videos AS v
                 WHERE i.target_type = 0
                   AND i.target_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM collection_videos AS cv
                 USING videos AS v
                 WHERE cv.video_id = v.id
                   AND v.deleted_at IS NOT NULL;

                DELETE FROM videos
                 WHERE deleted_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
