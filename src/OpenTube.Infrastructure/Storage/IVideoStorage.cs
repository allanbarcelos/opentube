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
/// Leitura do storage: baixar, ler, conferir, listar e assinar um endereço de leitura. Quem só
/// entrega ou confere arquivos depende só disto, e não pode gravar nem apagar nada.
/// </summary>
public interface IStorageReader
{
    /// <summary>URL assinada de leitura, válida pelo tempo informado.</summary>
    string SignDownloadUrl(StorageBucket bucket, string key, TimeSpan lifetime);

    /// <summary>Baixa um objeto para um arquivo local.</summary>
    Task GetFileAsync(StorageBucket bucket, string key, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>Lê um objeto de texto. Lança se a chave não existir.</summary>
    Task<string> GetTextAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Lê um objeto de texto, ou <c>null</c> se a chave não existir.</summary>
    Task<string?> TryGetTextAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Tamanho do objeto em bytes, ou <c>null</c> se não existir.</summary>
    Task<long?> GetSizeAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Lista as chaves sob um prefixo.</summary>
    Task<IReadOnlyList<string>> ListAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default);
}

/// <summary>Escrita no storage: gravar e apagar objetos.</summary>
public interface IStorageWriter
{
    /// <summary>Envia um arquivo local para o storage.</summary>
    Task PutFileAsync(StorageBucket bucket, string key, string filePath, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Envia conteúdo em memória para o storage.</summary>
    Task PutTextAsync(StorageBucket bucket, string key, string content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Envia bytes em memória para o storage. Usado pela miniatura da coleção.</summary>
    Task PutBytesAsync(StorageBucket bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Apaga tudo o que estiver sob um prefixo.</summary>
    Task<int> DeletePrefixAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Apaga as chaves informadas; as que não existirem são ignoradas.</summary>
    Task<int> DeleteKeysAsync(StorageBucket bucket, IEnumerable<string> keys, CancellationToken cancellationToken = default);
}

/// <summary>
/// Envio multipart direto do navegador para o storage, sem passar pela aplicação — é o que
/// evita ocupar memória e tempo de requisição com arquivos grandes.
/// </summary>
public interface IMultipartUpload
{
    /// <summary>Abre um envio multipart e devolve o identificador do storage.</summary>
    Task<string> StartUploadAsync(string key, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Assina as URLs dos pedaços, que o navegador usa para enviar cada um.</summary>
    IReadOnlyList<UploadPartUrl> SignUploadParts(string key, string uploadId, int firstPart, int partCount);

    /// <summary>Fecha o envio multipart e devolve o tamanho final do objeto.</summary>
    Task<long> CompleteUploadAsync(string key, string uploadId, IEnumerable<CompletedPart> parts, CancellationToken cancellationToken = default);

    /// <summary>Cancela um envio interrompido, liberando os pedaços já recebidos.</summary>
    Task AbortUploadAsync(string key, string uploadId, CancellationToken cancellationToken = default);
}

/// <summary>Preparação do storage, feita uma vez na partida da aplicação.</summary>
public interface IStorageSetup
{
    /// <summary>Cria os buckets se ainda não existirem.</summary>
    Task EnsureBucketsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// O storage de objetos inteiro. A aplicação conversa só por interfaces, o que permite trocar o
/// servidor compatível com S3 sem mexer no resto do sistema. Os serviços dependem só do papel
/// que usam (<see cref="IStorageReader"/>, <see cref="IStorageWriter"/>,
/// <see cref="IMultipartUpload"/>, <see cref="IStorageSetup"/>); o todo fica para ferramentas,
/// como o gerador de exemplos, e para os testes.
/// </summary>
public interface IVideoStorage : IStorageReader, IStorageWriter, IMultipartUpload, IStorageSetup;
