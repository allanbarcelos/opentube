// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Web.Seguranca;

/// <summary>
/// Distingue o player do site de quem abre o endereço do vídeo direto. O navegador diz, nos
/// cabeçalhos <c>Sec-Fetch-*</c>, se o pedido é uma navegação (o endereço colado na barra,
/// aberto numa aba ou num iframe) e de que site ele parte; a página não consegue mudar isso.
/// </summary>
/// <remarks>
/// Pedido sem esses cabeçalhos passa: é o player nativo do Safari e do iPhone, que busca a
/// playlist por fora do navegador. Programas de linha de comando também não os mandam; para
/// eles valem o token de reprodução e o limite de velocidade.
/// </remarks>
public static class PedidoDoPlayer
{
    private static readonly string[] DestinosDeNavegacao = ["document", "iframe", "frame", "embed", "object"];

    public static bool EhAberturaDireta(HttpRequest pedido)
    {
        ArgumentNullException.ThrowIfNull(pedido);

        var cabecalhos = pedido.Headers;
        var modo = cabecalhos["Sec-Fetch-Mode"].ToString();
        var destino = cabecalhos["Sec-Fetch-Dest"].ToString();
        var site = cabecalhos["Sec-Fetch-Site"].ToString();

        return string.Equals(modo, "navigate", StringComparison.OrdinalIgnoreCase)
            || DestinosDeNavegacao.Contains(destino, StringComparer.OrdinalIgnoreCase)
            || string.Equals(site, "cross-site", StringComparison.OrdinalIgnoreCase);
    }
}
