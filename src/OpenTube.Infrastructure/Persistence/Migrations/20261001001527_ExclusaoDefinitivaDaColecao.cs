// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExclusaoDefinitivaDaColecao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O que estava só marcado como excluído some de verdade. Os vídeos ficam no acervo,
            // soltos, que era o efeito da exclusão antiga. A capa no storage, se havia, fica órfã.
            migrationBuilder.Sql(
                """
                DELETE FROM access_grants AS g
                 USING collections AS c
                 WHERE g.target_type = 1
                   AND g.target_id = c.id
                   AND c.deleted_at IS NOT NULL;

                DELETE FROM invitations AS i
                 USING collections AS c
                 WHERE i.target_type = 1
                   AND i.target_id = c.id
                   AND c.deleted_at IS NOT NULL;

                DELETE FROM collections
                 WHERE deleted_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
