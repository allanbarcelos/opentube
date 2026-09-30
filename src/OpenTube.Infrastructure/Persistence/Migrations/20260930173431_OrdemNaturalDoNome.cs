// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTube.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrdemNaturalDoNome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Comparar o nome como texto põe "10" antes de "2". Cada sequência de dígitos
            // vira um número de largura fixa, então a ordem alfabética passa a respeitar o valor.
            // Dezoito posições cobrem qualquer número que caiba num título; um maior fica como está.
            migrationBuilder.Sql("""
                CREATE FUNCTION opentube_natural_sort_key(p_text text) RETURNS text AS $$
                DECLARE
                    resultado text := '';
                    restante text;
                    trecho text;
                BEGIN
                    restante := lower(unaccent(coalesce(p_text, '')));
                    WHILE restante <> '' LOOP
                        IF restante ~ '^[0-9]' THEN
                            trecho := substring(restante FROM '^[0-9]+');
                            resultado := resultado || lpad(trecho, 18, '0');
                        ELSE
                            trecho := substring(restante FROM '^[^0-9]+');
                            resultado := resultado || trecho;
                        END IF;

                        IF trecho IS NULL OR trecho = '' THEN
                            EXIT;
                        END IF;

                        restante := substring(restante FROM char_length(trecho) + 1);
                    END LOOP;
                    RETURN resultado;
                END
                $$ LANGUAGE plpgsql STABLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS opentube_natural_sort_key(text);");
        }
    }
}
