// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Dapper;
using Microsoft.EntityFrameworkCore;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Services;

/// <summary>Uma opção de um seletor: o que vai no formulário, o que aparece e um detalhe.</summary>
/// <param name="Id">Valor enviado no formulário.</param>
/// <param name="Label">Texto da opção.</param>
/// <param name="Hint">Detalhe mostrado abaixo do texto, quando houver.</param>
public sealed record LookupItem(Guid Id, string Label, string? Hint);

/// <summary>Uma página de opções.</summary>
/// <param name="Items">As opções desta página.</param>
/// <param name="Page">Número da página, a partir de 1.</param>
/// <param name="HasMore">Se há mais páginas depois desta.</param>
public sealed record LookupPage(IReadOnlyList<LookupItem> Items, int Page, bool HasMore);

/// <summary>
/// Opções dos seletores da administração que vêm do banco, buscadas e paginadas no servidor. Um
/// &lt;select&gt; com o acervo inteiro pesaria na página e, cortado num limite, esconderia o que
/// passasse dele; aqui a pessoa digita e as opções chegam aos poucos.
/// </summary>
/// <remarks>
/// A busca ignora maiúsculas e acentos e acha o termo em qualquer parte do nome. A ordem é a
/// mesma da página inicial: pelo nome, com cada número valendo pelo valor.
/// </remarks>
public class AdminLookup(OpenTubeDbContext db)
{
    public const int PageSize = 20;

    /// <summary>
    /// Vídeos que podem entrar numa coleção: os não excluídos que ainda não estão nela. O detalhe
    /// diz em que outra coleção o vídeo está, porque adicioná-lo aqui o tira de lá.
    /// </summary>
    public async Task<LookupPage> VideosForCollectionAsync(
        Guid collectionId, string? query, int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);

        var linhas = await db.Database.GetDbConnection().QueryAsync<Linha>(new CommandDefinition("""
            SELECT v.id AS Id,
                   v.title AS Label,
                   (SELECT c.name
                      FROM collection_videos cv
                      JOIN collections c ON c.id = cv.collection_id AND c.deleted_at IS NULL
                     WHERE cv.video_id = v.id
                     ORDER BY c.name
                     LIMIT 1) AS Hint
              FROM videos v
             WHERE v.deleted_at IS NULL
               AND NOT EXISTS (
                   SELECT 1 FROM collection_videos cv
                    WHERE cv.collection_id = @Colecao AND cv.video_id = v.id)
               AND (@Termo IS NULL OR unaccent(lower(v.title)) LIKE '%' || unaccent(lower(@Termo)) || '%' ESCAPE '\')
             ORDER BY opentube_natural_sort_key(v.title), v.id
             LIMIT @Limite OFFSET @Salto
            """,
            new { Colecao = collectionId, Termo = Termo(query), Limite = PageSize + 1, Salto = (page - 1) * PageSize },
            cancellationToken: cancellationToken));

        var itens = linhas
            .Select(l => l with { Hint = l.Hint is null ? null : Localization.LocalText.Format("In collection “{0}”", l.Hint) })
            .ToList();

        return Pagina(itens, page);
    }

    /// <summary>Coleções não excluídas, menos a informada. O detalhe é quantos vídeos ela tem.</summary>
    public async Task<LookupPage> CollectionsAsync(
        string? query, Guid? except = null, int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);

        var linhas = await db.Database.GetDbConnection().QueryAsync<LinhaDeColecao>(new CommandDefinition("""
            SELECT c.id AS Id,
                   c.name AS Label,
                   (SELECT COUNT(*) FROM collection_videos cv WHERE cv.collection_id = c.id)::int AS Videos
              FROM collections c
             WHERE c.deleted_at IS NULL
               AND (@Excecao IS NULL OR c.id <> @Excecao)
               AND (@Termo IS NULL OR unaccent(lower(c.name)) LIKE '%' || unaccent(lower(@Termo)) || '%' ESCAPE '\')
             ORDER BY opentube_natural_sort_key(c.name), c.id
             LIMIT @Limite OFFSET @Salto
            """,
            new { Excecao = except, Termo = Termo(query), Limite = PageSize + 1, Salto = (page - 1) * PageSize },
            cancellationToken: cancellationToken));

        var itens = linhas
            .Select(l => new Linha(l.Id, l.Label, Localization.LocalText.Format(l.Videos == 1 ? "{0} video" : "{0} videos", l.Videos)))
            .ToList();

        return Pagina(itens, page);
    }

    /// <summary>
    /// Termo da busca para o LIKE: sem espaços nas pontas e com %, _ e \ escapados, para que
    /// sejam procurados como texto e não como coringa. Vazio vira nulo, que traz tudo.
    /// </summary>
    private static string? Termo(string? query)
    {
        var termo = (query ?? string.Empty).Trim();

        if (termo.Length == 0)
            return null;

        if (termo.Length > 100)
            termo = termo[..100];

        return termo.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
    }

    /// <summary>Uma linha a mais que a página diz se há a seguinte, sem contar o total.</summary>
    private static LookupPage Pagina(List<Linha> linhas, int pagina) =>
        new([.. linhas.Take(PageSize).Select(l => new LookupItem(l.Id, l.Label, l.Hint))], pagina, linhas.Count > PageSize);

    private sealed record Linha(Guid Id, string Label, string? Hint);

    private sealed record LinhaDeColecao(Guid Id, string Label, int Videos);
}
