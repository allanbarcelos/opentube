// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Caching.Memory;

namespace OpenTube.Web.Auth;

/// <summary>
/// Guarda por poucos minutos o endereço de um link recém-criado, para exibi-lo uma única vez
/// depois do redirecionamento. O segredo não vai na URL de propósito: ali ele acabaria no
/// histórico do navegador e nos registros do servidor.
/// </summary>
public class ShareLinkFlash(IMemoryCache cache)
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(5);

    public string Store(string url)
    {
        var chave = Guid.CreateVersion7().ToString("n");

        cache.Set(Chave(chave), url, Validade);

        return chave;
    }

    /// <summary>Lê e descarta o endereço guardado.</summary>
    public string? Take(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !cache.TryGetValue(Chave(id), out string? url))
            return null;

        cache.Remove(Chave(id));

        return url;
    }

    private static string Chave(string id) => "link-de-compartilhamento:" + id;
}
