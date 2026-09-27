// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Analytics;

/// <summary>Aparelho e programa deduzidos da identificação do navegador.</summary>
/// <param name="Device">Categoria do aparelho.</param>
/// <param name="OperatingSystem">Sistema operacional.</param>
/// <param name="Browser">Navegador.</param>
public readonly record struct ClientProfile(DeviceType Device, string OperatingSystem, string Browser);

/// <summary>
/// Identifica aparelho, sistema e navegador a partir do cabeçalho enviado pelo cliente. É uma
/// leitura aproximada de propósito: serve para agrupar o relatório, não para decidir acesso,
/// e por isso não vale carregar uma base de assinaturas atrás de exatidão.
/// </summary>
public static class UserAgentParser
{
    public const string Desconhecido = "Desconhecido";

    public static ClientProfile Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new ClientProfile(DeviceType.Unknown, Desconhecido, Desconhecido);

        var texto = userAgent.ToLowerInvariant();

        return new ClientProfile(Aparelho(texto), Sistema(texto), Navegador(texto));
    }

    private static DeviceType Aparelho(string ua)
    {
        if (ua.Contains("smart-tv") || ua.Contains("smarttv") || ua.Contains("appletv") ||
            ua.Contains("googletv") || ua.Contains("hbbtv") || ua.Contains("crkey"))
            return DeviceType.Tv;

        if (ua.Contains("ipad") || (ua.Contains("android") && !ua.Contains("mobile")) || ua.Contains("tablet"))
            return DeviceType.Tablet;

        if (ua.Contains("mobi") || ua.Contains("iphone") || ua.Contains("ipod") ||
            ua.Contains("android") || ua.Contains("windows phone"))
            return DeviceType.Mobile;

        if (ua.Contains("windows") || ua.Contains("macintosh") || ua.Contains("mac os") ||
            ua.Contains("linux") || ua.Contains("cros"))
            return DeviceType.Desktop;

        return DeviceType.Unknown;
    }

    private static string Sistema(string ua)
    {
        if (ua.Contains("windows nt")) return "Windows";
        if (ua.Contains("iphone") || ua.Contains("ipad") || ua.Contains("ipod")) return "iOS";
        if (ua.Contains("mac os x") || ua.Contains("macintosh")) return "macOS";
        if (ua.Contains("android")) return "Android";
        if (ua.Contains("cros")) return "ChromeOS";
        if (ua.Contains("linux")) return "Linux";

        return Desconhecido;
    }

    private static string Navegador(string ua)
    {
        // A ordem importa: quase todo navegador se declara Safari e Chrome no caminho, então
        // os mais específicos precisam ser testados antes.
        if (ua.Contains("edg/") || ua.Contains("edga/") || ua.Contains("edgios/")) return "Edge";
        if (ua.Contains("opr/") || ua.Contains("opera")) return "Opera";
        if (ua.Contains("firefox/") || ua.Contains("fxios/")) return "Firefox";
        if (ua.Contains("crios/")) return "Chrome";
        if (ua.Contains("chrome/") || ua.Contains("chromium/")) return "Chrome";
        if (ua.Contains("safari/")) return "Safari";

        return Desconhecido;
    }
}
