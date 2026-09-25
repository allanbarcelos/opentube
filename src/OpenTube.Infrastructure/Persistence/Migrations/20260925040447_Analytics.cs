using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Esta é a única tabela que cresce sem parar e a única que um dia precisará ser
            // descartada por idade, então nasce particionada por mês. O EF Core não descreve
            // particionamento, e por isso ela é criada em SQL.
            migrationBuilder.Sql("""
                CREATE TABLE playback_events (
                    id            uuid NOT NULL,
                    at            timestamptz NOT NULL,
                    session_id    uuid NOT NULL,
                    video_id      uuid NOT NULL,
                    user_id       uuid NULL,
                    type          integer NOT NULL,
                    position      double precision NOT NULL,
                    from_seconds  double precision NULL,
                    to_seconds    double precision NULL,
                    detail        character varying(200) NULL,
                    CONSTRAINT pk_playback_events PRIMARY KEY (at, id)
                ) PARTITION BY RANGE (at);
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION opentube_ensure_event_partition(p_month date) RETURNS void AS $$
                DECLARE
                    inicio date := date_trunc('month', p_month)::date;
                    fim    date := (date_trunc('month', p_month) + interval '1 month')::date;
                    nome   text := 'playback_events_' || to_char(inicio, 'YYYY_MM');
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_class WHERE relname = nome) THEN
                        RETURN;
                    END IF;

                    EXECUTE format(
                        'CREATE TABLE %I PARTITION OF playback_events FOR VALUES FROM (%L) TO (%L)',
                        nome, inicio, fim);
                    EXECUTE format('CREATE INDEX %I ON %I (video_id, at)', nome || '_video_at', nome);
                    EXECUTE format('CREATE INDEX %I ON %I (session_id)', nome || '_session', nome);
                END
                $$ LANGUAGE plpgsql;
                """);

            // Partição de escape: sem ela, um evento com data fora de todas as faixas faria a
            // gravação falhar, e perder um evento por causa de um relógio adiantado no cliente
            // seria o pior dos mundos.
            migrationBuilder.Sql(
                "CREATE TABLE playback_events_padrao PARTITION OF playback_events DEFAULT;");

            migrationBuilder.Sql("""
                DO $$
                DECLARE mes date;
                BEGIN
                    FOR mes IN
                        SELECT generate_series(
                            date_trunc('month', now() - interval '1 month'),
                            date_trunc('month', now() + interval '2 months'),
                            interval '1 month')::date
                    LOOP
                        PERFORM opentube_ensure_event_partition(mes);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "playback_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymous_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    device = table.Column<int>(type: "integer", nullable: false),
                    operating_system = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    browser = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    referrer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    max_quality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    furthest_position = table.Column<double>(type: "double precision", nullable: false),
                    watched_seconds = table.Column<double>(type: "double precision", nullable: false),
                    completed = table.Column<bool>(type: "boolean", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "video_daily_stats",
                columns: table => new
                {
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    views = table.Column<int>(type: "integer", nullable: false),
                    unique_viewers = table.Column<int>(type: "integer", nullable: false),
                    watch_seconds = table.Column<double>(type: "double precision", nullable: false),
                    completions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_daily_stats", x => new { x.video_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "video_retention_buckets",
                columns: table => new
                {
                    video_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_index = table.Column<int>(type: "integer", nullable: false),
                    viewers = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_video_retention_buckets", x => new { x.video_id, x.bucket_index });
                });

            migrationBuilder.CreateTable(
                name: "playback_intervals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_seconds = table.Column<double>(type: "double precision", nullable: false),
                    end_seconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playback_intervals", x => x.id);
                    table.ForeignKey(
                        name: "fk_playback_intervals_playback_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "playback_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_playback_intervals_session_id",
                table: "playback_intervals",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_grant_id",
                table: "playback_sessions",
                column: "grant_id");

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_last_seen_at",
                table: "playback_sessions",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_user_id_started_at",
                table: "playback_sessions",
                columns: new[] { "user_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_playback_sessions_video_id_started_at",
                table: "playback_sessions",
                columns: new[] { "video_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_video_daily_stats_day",
                table: "video_daily_stats",
                column: "day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS playback_events CASCADE;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS opentube_ensure_event_partition(date);");

            migrationBuilder.DropTable(
                name: "playback_intervals");

            migrationBuilder.DropTable(
                name: "video_daily_stats");

            migrationBuilder.DropTable(
                name: "video_retention_buckets");

            migrationBuilder.DropTable(
                name: "playback_sessions");
        }
    }
}
