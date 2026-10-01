// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Options;

namespace OpenTube.Web.Seguranca;

/// <summary>
/// Política de conteúdo (CSP): o navegador só executa os scripts do próprio site. Não há XSS
/// conhecido, mas títulos, descrições, legendas e mensagens vêm de fora; se um escape falhar
/// um dia, o script injetado não roda. O único script no próprio HTML é o mapa de importação do
/// Blazor, liberado por um nonce sorteado a cada pedido.
/// </summary>
public static class PoliticaDeConteudo
{
    private const string ChaveDoNonce = "opentube.csp-nonce";

    /// <summary>Nonce do pedido, para o script que o próprio HTML precisa trazer.</summary>
    public static string? Nonce(HttpContext? contexto) => contexto?.Items[ChaveDoNonce] as string;

    public static IApplicationBuilder UsePoliticaDeConteudo(this IApplicationBuilder app)
    {
        var storage = app.ApplicationServices.GetRequiredService<IOptions<StorageOptions>>().Value;

        // Sem autorização por segmento, miniaturas, segmentos e o envio vão direto ao storage,
        // que pode estar em outra origem (o MinIO na porta 9000, no desenvolvimento).
        var origemDoStorage = OrigemDe(storage.ResolvedPublicEndpoint);

        return app.Use(async (contexto, proximo) =>
        {
            // Hexadecimal, e não base64: o "+" do base64 sai no HTML como "&#x2B;". O navegador
            // decodifica e aceita, mas o atributo deixa de ser idêntico ao do cabeçalho.
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            contexto.Items[ChaveDoNonce] = nonce;

            contexto.Response.OnStarting(() =>
            {
                contexto.Response.Headers.ContentSecurityPolicy = Montar(nonce, origemDoStorage);
                return Task.CompletedTask;
            });

            await proximo(contexto);
        });
    }

    public static string Montar(string nonce, string? origemDoStorage)
    {
        var storage = string.IsNullOrEmpty(origemDoStorage) ? string.Empty : " " + origemDoStorage;

        return string.Join("; ",
            "default-src 'self'",
            $"script-src 'self' 'nonce-{nonce}'",
            // Atributos style são usados nas páginas e não executam código.
            "style-src 'self' 'unsafe-inline'",
            // data: é a marca d'água em SVG; blob: é o vídeo montado pelo hls.js.
            $"img-src 'self' data: blob:{storage}",
            $"media-src 'self' blob:{storage}",
            $"connect-src 'self'{storage}",
            "worker-src 'self' blob:",
            "font-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'self'");
    }

    /// <summary>Esquema, host e porta do endereço; nulo quando não é um endereço absoluto.</summary>
    public static string? OrigemDe(string? endereco) =>
        Uri.TryCreate(endereco, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
