// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Infrastructure.Storage;

/// <summary>Um pedaço do envio multipart, com a URL que o navegador usa para enviá-lo.</summary>
/// <param name="PartNumber">Número do pedaço, começando em 1.</param>
/// <param name="Url">URL assinada de envio.</param>
public readonly record struct UploadPartUrl(int PartNumber, string Url);

/// <summary>Pedaço já enviado, identificado pela etiqueta devolvida pelo storage.</summary>
/// <param name="PartNumber">Número do pedaço.</param>
/// <param name="ETag">Etiqueta devolvida no envio.</param>
public readonly record struct CompletedPart(int PartNumber, string ETag);

/// <summary>Buckets usados pela aplicação.</summary>
public enum StorageBucket
{
    /// <summary>Arquivos enviados, preservados para reprocessamento.</summary>
    Originals = 0,

    /// <summary>Saídas prontas para reprodução.</summary>
    Vod = 1
}

/// <summary>
/// Acesso ao storage de objetos. A aplicação conversa apenas por esta interface, o que permite
/// trocar o servidor compatível com S3 sem mexer no resto do sistema.
/// </summary>
public interface IVideoStorage
{
    /// <summary>Cria os buckets se ainda não existirem.</summary>
    Task EnsureBucketsAsync(CancellationToken cancellationToken = default);

    /// <summary>Abre um envio multipart e devolve o identificador do storage.</summary>
    Task<string> StartUploadAsync(string key, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assina as URLs dos pedaços. O navegador envia direto ao storage, sem passar pela
    /// aplicação — é o que evita ocupar memória e tempo de requisição com arquivos grandes.
    /// </summary>
    IReadOnlyList<UploadPartUrl> SignUploadParts(string key, string uploadId, int firstPart, int partCount);

    /// <summary>Fecha o envio multipart e devolve o tamanho final do objeto.</summary>
    Task<long> CompleteUploadAsync(string key, string uploadId, IEnumerable<CompletedPart> parts, CancellationToken cancellationToken = default);

    /// <summary>Cancela um envio interrompido, liberando os pedaços já recebidos.</summary>
    Task AbortUploadAsync(string key, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>URL assinada de leitura, válida pelo tempo informado.</summary>
    string SignDownloadUrl(StorageBucket bucket, string key, TimeSpan lifetime);

    /// <summary>Envia um arquivo local para o storage.</summary>
    Task PutFileAsync(StorageBucket bucket, string key, string filePath, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Envia conteúdo em memória para o storage.</summary>
    Task PutTextAsync(StorageBucket bucket, string key, string content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Baixa um objeto para um arquivo local.</summary>
    Task GetFileAsync(StorageBucket bucket, string key, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>Lê um objeto de texto.</summary>
    Task<string> GetTextAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Tamanho do objeto em bytes, ou <c>null</c> se não existir.</summary>
    Task<long?> GetSizeAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Lista as chaves sob um prefixo.</summary>
    Task<IReadOnlyList<string>> ListAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Apaga tudo o que estiver sob um prefixo.</summary>
    Task<int> DeletePrefixAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Apaga as chaves informadas; as que não existirem são ignoradas.</summary>
    Task<int> DeleteKeysAsync(StorageBucket bucket, IEnumerable<string> keys, CancellationToken cancellationToken = default);
}
