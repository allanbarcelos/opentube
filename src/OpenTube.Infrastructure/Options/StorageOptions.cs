// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.ComponentModel.DataAnnotations;

namespace OpenTube.Infrastructure.Options;

/// <summary>Configuração do storage compatível com S3 onde ficam originais e saídas HLS.</summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Endereço do serviço, por exemplo <c>http://localhost:9000</c>.</summary>
    [Required]
    public string Endpoint { get; set; } = "http://localhost:9000";

    [Required]
    public string AccessKey { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    /// <summary>Bucket dos arquivos enviados, preservados para reprocessamento.</summary>
    public string OriginalsBucket { get; set; } = "originals";

    /// <summary>Bucket das saídas prontas para reprodução.</summary>
    public string VodBucket { get; set; } = "vod";

    /// <summary>
    /// Endereço usado nas URLs assinadas entregues ao navegador. Diferente de <see cref="Endpoint"/>
    /// quando a aplicação fala com o storage por rede interna e o navegador pelo endereço público.
    /// </summary>
    public string? PublicEndpoint { get; set; }

    /// <summary>Validade das URLs assinadas de envio.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Validade das URLs assinadas de reprodução.</summary>
    public TimeSpan PlaybackUrlLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Entrega os segmentos por um caminho da própria aplicação, autorizado a cada pedido,
    /// em vez de por endereço assinado. Exige um servidor na frente capaz de consultar a
    /// autorização e entregar o arquivo — a aplicação decide, não transporta.
    /// </summary>
    public bool SegmentAuthorization { get; set; }

    /// <summary>Caminho por onde os segmentos são servidos quando a autorização é por pedido.</summary>
    public string SegmentPath { get; set; } = "/vod";

    /// <summary>Maior arquivo aceito num envio. Padrão de 20 GiB.</summary>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    public string ResolvedPublicEndpoint => string.IsNullOrWhiteSpace(PublicEndpoint) ? Endpoint : PublicEndpoint;
}
