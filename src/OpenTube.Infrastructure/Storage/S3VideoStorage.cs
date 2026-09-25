using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Options;

namespace OpenTube.Infrastructure.Storage;

/// <summary>
/// Implementação sobre a API S3, usada com MinIO no ambiente local e com qualquer serviço
/// compatível em produção.
/// </summary>
public class S3VideoStorage : IVideoStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly IAmazonS3 _signingClient;
    private readonly StorageOptions _options;
    private readonly ILogger<S3VideoStorage> _logger;
    private readonly bool _ownsSigningClient;

    /// <summary>
    /// Protocolo das URLs assinadas. O SDK assume HTTPS por padrão, o que quebra um storage
    /// local servido em texto claro.
    /// </summary>
    private readonly Protocol _signingProtocol;

    public S3VideoStorage(IOptions<StorageOptions> options, ILogger<S3VideoStorage> logger)
    {
        _options = options.Value;
        _logger = logger;

        _client = BuildClient(_options.Endpoint, _options);
        _signingProtocol = _options.ResolvedPublicEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;

        // As URLs assinadas incluem o host na assinatura. Quando a aplicação fala com o storage
        // por rede interna e o navegador por um endereço público, é preciso assinar com o
        // endereço que o navegador vai usar, senão o storage rejeita a assinatura.
        if (string.Equals(_options.Endpoint, _options.ResolvedPublicEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            _signingClient = _client;
            _ownsSigningClient = false;
        }
        else
        {
            _signingClient = BuildClient(_options.ResolvedPublicEndpoint, _options);
            _ownsSigningClient = true;
        }
    }

    private static IAmazonS3 BuildClient(string endpoint, StorageOptions options) =>
        new AmazonS3Client(options.AccessKey, options.SecretKey, new AmazonS3Config
        {
            ServiceURL = endpoint,
            // MinIO e compatíveis não usam bucket como subdomínio.
            ForcePathStyle = true,
            AuthenticationRegion = options.Region,
            // Sem isto a URL assinada sai sempre com https, e um storage local em http
            // devolve erro de conexão ao navegador.
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        });

    private string BucketName(StorageBucket bucket) =>
        bucket == StorageBucket.Originals ? _options.OriginalsBucket : _options.VodBucket;

    public async Task EnsureBucketsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var bucket in Enum.GetValues<StorageBucket>())
        {
            var name = BucketName(bucket);
            var buckets = await _client.ListBucketsAsync(cancellationToken);

            if (buckets.Buckets?.Any(b => b.BucketName == name) == true)
                continue;

            _logger.LogInformation("Criando bucket {Bucket}", name);
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = name }, cancellationToken);
        }

        await AplicarLeituraDoVodAsync(cancellationToken);
    }

    /// <summary>
    /// Com autorização por segmento, quem busca o arquivo é o servidor da frente, sem
    /// assinatura. A leitura anônima só é aceitável porque o storage não é publicado:
    /// a única porta de entrada é o servidor, que pergunta à aplicação antes. O bucket
    /// de originais continua privado — o envio usa URL assinada.
    /// </summary>
    private async Task AplicarLeituraDoVodAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.VodBucket)
            || _options.VodBucket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'))
            throw new InvalidOperationException($"Nome de bucket inválido: {_options.VodBucket}");

        if (!_options.SegmentAuthorization)
        {
            try
            {
                await _client.DeleteBucketPolicyAsync(_options.VodBucket, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }

            return;
        }

        _logger.LogInformation(
            "Bucket {Bucket} com leitura anônima para o servidor da frente", _options.VodBucket);

        var policy = """
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Sid": "LeituraDoServidorDaFrente",
                  "Effect": "Allow",
                  "Principal": {"AWS": ["*"]},
                  "Action": ["s3:GetObject"],
                  "Resource": ["arn:aws:s3:::BUCKET/*"]
                }
              ]
            }
            """.Replace("BUCKET", _options.VodBucket, StringComparison.Ordinal);

        await _client.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = _options.VodBucket,
            Policy = policy
        }, cancellationToken);
    }

    public async Task<string> StartUploadAsync(string key, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var response = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _options.OriginalsBucket,
            Key = key,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
        }, cancellationToken);

        return response.UploadId;
    }

    public IReadOnlyList<UploadPartUrl> SignUploadParts(string key, string uploadId, int firstPart, int partCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstPart, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(partCount, 1);
        // O protocolo S3 limita um envio multipart a 10.000 pedaços.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstPart + partCount - 1, 10_000);

        var expiration = DateTime.UtcNow + _options.UploadUrlLifetime;

        return Enumerable.Range(firstPart, partCount)
            .Select(part => new UploadPartUrl(part, _signingClient.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _options.OriginalsBucket,
                Key = key,
                Verb = HttpVerb.PUT,
                UploadId = uploadId,
                PartNumber = part,
                Protocol = _signingProtocol,
                Expires = expiration
            })))
            .ToList();
    }

    public async Task<long> CompleteUploadAsync(string key, string uploadId, IEnumerable<CompletedPart> parts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var ordenados = parts
            .OrderBy(p => p.PartNumber)
            .Select(p => new PartETag(p.PartNumber, p.ETag))
            .ToList();

        if (ordenados.Count == 0)
            throw new InvalidOperationException("Um envio multipart precisa de ao menos um pedaço.");

        await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _options.OriginalsBucket,
            Key = key,
            UploadId = uploadId,
            PartETags = ordenados
        }, cancellationToken);

        var tamanho = await GetSizeAsync(StorageBucket.Originals, key, cancellationToken);

        return tamanho ?? 0;
    }

    public async Task AbortUploadAsync(string key, string uploadId, CancellationToken cancellationToken = default)
    {
        await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = _options.OriginalsBucket,
            Key = key,
            UploadId = uploadId
        }, cancellationToken);
    }

    public string SignDownloadUrl(StorageBucket bucket, string key, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _signingClient.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = BucketName(bucket),
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = _signingProtocol,
            Expires = DateTime.UtcNow + (lifetime > TimeSpan.Zero ? lifetime : _options.PlaybackUrlLifetime)
        });
    }

    public async Task PutFileAsync(StorageBucket bucket, string key, string filePath, string contentType, CancellationToken cancellationToken = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BucketName(bucket),
            Key = key,
            FilePath = filePath,
            ContentType = contentType
        }, cancellationToken);
    }

    public async Task PutTextAsync(StorageBucket bucket, string key, string content, string contentType, CancellationToken cancellationToken = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BucketName(bucket),
            Key = key,
            ContentBody = content,
            ContentType = contentType
        }, cancellationToken);
    }

    public async Task GetFileAsync(StorageBucket bucket, string key, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetObjectAsync(BucketName(bucket), key, cancellationToken);

        var pasta = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(pasta))
            Directory.CreateDirectory(pasta);

        await response.WriteResponseStreamToFileAsync(destinationPath, append: false, cancellationToken);
    }

    public async Task<string> GetTextAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetObjectAsync(BucketName(bucket), key, cancellationToken);
        using var reader = new StreamReader(response.ResponseStream);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
        await GetSizeAsync(bucket, key, cancellationToken) is not null;

    public async Task<long?> GetSizeAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await _client.GetObjectMetadataAsync(BucketName(bucket), key, cancellationToken);
            return metadata.ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default)
    {
        var chaves = new List<string>();
        var request = new ListObjectsV2Request { BucketName = BucketName(bucket), Prefix = prefix };

        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken);
            chaves.AddRange(response.S3Objects?.Select(o => o.Key) ?? []);
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return chaves;
    }

    public async Task<int> DeletePrefixAsync(StorageBucket bucket, string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return await DeleteKeysAsync(bucket, await ListAsync(bucket, prefix, cancellationToken), cancellationToken);
    }

    public async Task<int> DeleteKeysAsync(StorageBucket bucket, IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var chaves = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
        if (chaves.Count == 0)
            return 0;

        // A API apaga no máximo mil objetos por chamada.
        foreach (var lote in chaves.Chunk(1000))
        {
            await _client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = BucketName(bucket),
                Objects = [.. lote.Select(k => new KeyVersion { Key = k })]
            }, cancellationToken);
        }

        return chaves.Count;
    }

    public void Dispose()
    {
        _client.Dispose();
        if (_ownsSigningClient)
            _signingClient.Dispose();

        GC.SuppressFinalize(this);
    }
}
