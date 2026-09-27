// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InicialNucleo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "processing_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    email_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "videos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    visibility = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    original_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    hls_prefix = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    thumbnail_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sprite_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    duration_seconds = table.Column<double>(type: "double precision", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    transcript = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_videos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "video_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_video_assets_videos_video_id",
                        column: x => x.video_id,
                        principalTable: "videos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_kind_target_id",
                table: "processing_jobs",
                columns: new[] { "kind", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_status_run_after",
                table: "processing_jobs",
                columns: new[] { "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email_domain",
                table: "users",
                column: "email_domain");

            migrationBuilder.CreateIndex(
                name: "ix_video_assets_video_id_kind",
                table: "video_assets",
                columns: new[] { "video_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_videos_created_at",
                table: "videos",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_videos_slug",
                table: "videos",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_videos_visibility_status",
                table: "videos",
                columns: new[] { "visibility", "status" });

            // Busca textual em português. A coluna é mantida por gatilho, e não como coluna
            // gerada, porque juntar as etiquetas exige uma função que o PostgreSQL não
            // considera imutável o bastante para uma coluna gerada.
            migrationBuilder.Sql("""
                ALTER TABLE videos ADD COLUMN search_vector tsvector;
                """);

            // Configuração de busca que ignora acentos: quem procura digita "orcamento" e
            // precisa encontrar "orçamento". Sem isto o dicionário português trata as duas
            // grafias como palavras diferentes e a busca volta vazia.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");

            migrationBuilder.Sql("""
                CREATE TEXT SEARCH CONFIGURATION portuguese_unaccent (COPY = portuguese);
                """);

            migrationBuilder.Sql("""
                ALTER TEXT SEARCH CONFIGURATION portuguese_unaccent
                ALTER MAPPING FOR hword, hword_part, word
                WITH unaccent, portuguese_stem;
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION videos_search_vector_update() RETURNS trigger AS $$
                BEGIN
                    NEW.search_vector :=
                        setweight(to_tsvector('portuguese_unaccent', coalesce(NEW.title, '')), 'A') ||
                        setweight(to_tsvector('portuguese_unaccent', coalesce(array_to_string(NEW.tags, ' '), '')), 'B') ||
                        setweight(to_tsvector('portuguese_unaccent', coalesce(NEW.description, '')), 'C') ||
                        setweight(to_tsvector('portuguese_unaccent', coalesce(NEW.transcript, '')), 'D');
                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER videos_search_vector_trigger
                BEFORE INSERT OR UPDATE OF title, description, tags, transcript ON videos
                FOR EACH ROW EXECUTE FUNCTION videos_search_vector_update();
                """);

            migrationBuilder.Sql("CREATE INDEX ix_videos_search_vector ON videos USING GIN (search_vector);");

            // Complementa a busca textual: encontra o título mesmo com erro de digitação,
            // situação em que o dicionário sozinho não acha nada.
            migrationBuilder.Sql("CREATE INDEX ix_videos_title_trgm ON videos USING GIN (title gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS videos_search_vector_trigger ON videos;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS videos_search_vector_update();");
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION IF EXISTS portuguese_unaccent;");

            migrationBuilder.DropTable(
                name: "processing_jobs");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "video_assets");

            migrationBuilder.DropTable(
                name: "videos");
        }
    }
}
