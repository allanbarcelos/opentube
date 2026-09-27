namespace OpenTube.Domain.Media;

/// <summary>
/// Nomes que o envio sugere a partir do que chegou do disco: o título do vídeo vem do nome do
/// arquivo, e o nome da coleção vem da pasta enviada. Quem envia pode trocar os dois.
/// </summary>
public static class UploadNames
{
    /// <summary>Maior título de vídeo aceito.</summary>
    public const int MaxTitleLength = 300;

    /// <summary>Maior nome de coleção aceito.</summary>
    public const int MaxCollectionNameLength = 200;

    /// <summary>
    /// Título a partir do nome do arquivo, sem a extensão: <c>Reunião de março.mp4</c> vira
    /// <c>Reunião de março</c>. Caminhos (de uma pasta enviada) ficam de fora; só vale o nome.
    /// </summary>
    public static string TitleFromFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var nome = UltimoSegmento(fileName);
        var semExtensao = Path.GetFileNameWithoutExtension(nome).Trim();

        // Arquivo sem nome além da extensão (".mp4") fica com o nome inteiro, e não vazio.
        var titulo = semExtensao.Length > 0 ? semExtensao : nome.Trim();

        return Cortar(titulo, MaxTitleLength);
    }

    /// <summary>Nome da coleção a partir da pasta enviada: o primeiro nível do caminho.</summary>
    public static string CollectionFromPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var pasta = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return Cortar(pasta, MaxCollectionNameLength);
    }

    private static string UltimoSegmento(string caminho) =>
        caminho.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } partes
            ? partes[^1]
            : caminho;

    private static string Cortar(string texto, int limite) => texto.Length <= limite ? texto : texto[..limite].TrimEnd();
}
