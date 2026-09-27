// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.HttpOverrides;

namespace OpenTube.Web.Auth;

/// <summary>
/// Leitura do endereço real de quem acessa quando a aplicação está atrás do servidor da frente.
/// Sem isto, toda requisição parece vir do Caddy: o limite de pedidos de código por origem vira
/// um limite do site inteiro, o limite de reproduções simultâneas não distingue ninguém e os
/// cookies saem sem a marca de conexão segura, porque a aplicação só enxerga HTTP.
/// </summary>
public static class ReverseProxy
{
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// Redes das quais se aceita o cabeçalho de endereço original. Por padrão, só redes
    /// privadas e a própria máquina: é onde o servidor da frente vive, e um cliente vindo da
    /// internet não consegue se passar por elas para forjar o próprio endereço.
    /// </summary>
    private static readonly string[] RedesPadrao =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        var redes = configuration.GetSection($"{SectionName}:TrustedNetworks").Get<string[]>() is { Length: > 0 } configuradas
            ? configuradas
            : RedesPadrao;

        services.Configure<ForwardedHeadersOptions>(opcoes =>
        {
            opcoes.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            opcoes.KnownIPNetworks.Clear();
            opcoes.KnownProxies.Clear();

            foreach (var rede in redes)
                opcoes.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(rede));
        });

        return services;
    }
}
