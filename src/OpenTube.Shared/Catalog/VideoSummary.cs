namespace OpenTube.Shared.Catalog;

/// <summary>Resumo de um vídeo para listagens e resultados de busca.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Slug">Endereço legível.</param>
/// <param name="Title">Título.</param>
/// <param name="Description">Descrição.</param>
/// <param name="DurationSeconds">Duração.</param>
/// <param name="Visibility">Visibilidade, exibida apenas na área administrativa.</param>
/// <param name="Status">Estágio no pipeline.</param>
/// <param name="PublishedAt">Quando ficou pronto pela primeira vez.</param>
/// <param name="CreatedAt">Quando foi enviado.</param>
/// <param name="Tags">Etiquetas.</param>
public sealed record VideoSummary(
    Guid Id,
    string Slug,
    string Title,
    string? Description,
    double DurationSeconds,
    int Visibility,
    int Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags)
{
    /// <summary>Duração no formato <c>h:mm:ss</c>, ou <c>m:ss</c> em vídeos curtos.</summary>
    public string DurationLabel
    {
        get
        {
            var tempo = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));

            return tempo.TotalHours >= 1
                ? $"{(int)tempo.TotalHours}:{tempo.Minutes:D2}:{tempo.Seconds:D2}"
                : $"{tempo.Minutes}:{tempo.Seconds:D2}";
        }
    }
}

/// <summary>Uma página de resultados.</summary>
/// <param name="Items">Itens da página.</param>
/// <param name="Total">Total de itens que atendem ao filtro.</param>
/// <param name="Page">Página atual, começando em 1.</param>
/// <param name="PageSize">Tamanho da página.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < PageCount;

    public static PagedResult<T> Empty(int pageSize) => new([], 0, 1, pageSize);
}
