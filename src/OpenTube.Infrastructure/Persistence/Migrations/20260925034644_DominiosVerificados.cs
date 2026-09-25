using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DominiosVerificados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verified_domains",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    verification_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    entry_slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entry_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    allowed_emails = table.Column<string[]>(type: "text[]", nullable: false),
                    contact_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verified_domains", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_verified_domains_entry_slug",
                table: "verified_domains",
                column: "entry_slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verified_domains_name",
                table: "verified_domains",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verified_domains");
        }
    }
}
