// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Access;

/// <summary>
/// Provedores de email abertos ao público. Uma concessão de domínio diz "todo email deste
/// domínio assiste"; para um desses, seria todo mundo que abre uma conta gratuita. Quem usa um
/// deles é convidado pelo próprio endereço.
/// </summary>
public static class PublicEmailProviders
{
    /// <summary>Domínios exatos dos provedores mais usados, inclusive os regionais comuns.</summary>
    private static readonly HashSet<string> Dominios = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com",
        "outlook.com", "hotmail.com", "live.com", "msn.com", "passport.com",
        "yahoo.com", "ymail.com", "rocketmail.com",
        "icloud.com", "me.com", "mac.com",
        "aol.com", "aim.com",
        "proton.me", "protonmail.com", "protonmail.ch", "pm.me",
        "tutanota.com", "tutanota.de", "tuta.io", "tuta.com",
        "gmx.com", "gmx.net", "gmx.de", "web.de", "t-online.de",
        "mail.com", "email.com", "zoho.com", "zohomail.com", "fastmail.com", "hey.com",
        "yandex.com", "yandex.ru", "ya.ru", "mail.ru", "rambler.ru",
        "qq.com", "163.com", "126.com", "sina.com", "naver.com", "daum.net",
        "uol.com.br", "bol.com.br", "terra.com.br", "ig.com.br", "globo.com", "globomail.com",
        "sapo.pt",
        "orange.fr", "wanadoo.fr", "free.fr", "sfr.fr", "laposte.net", "neuf.fr",
        "libero.it", "virgilio.it", "tiscali.it"
    };

    /// <summary>
    /// Marcas que têm um domínio por país (hotmail.com.br, yahoo.co.jp, outlook.fr). Contam só
    /// quando o resto do domínio é um sufixo curto de país: live.empresa.com não é o Live.
    /// </summary>
    private static readonly HashSet<string> Marcas = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "googlemail", "hotmail", "outlook", "live", "msn", "windowslive",
        "yahoo", "ymail", "aol", "gmx", "yandex", "icloud", "protonmail", "zoho"
    };

    /// <summary>Se o domínio (já normalizado, sem arroba) é de um provedor aberto ao público.</summary>
    public static bool IsPublic(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return false;

        var normalizado = domain.Trim().TrimEnd('.').ToLowerInvariant();

        if (Dominios.Contains(normalizado))
            return true;

        var partes = normalizado.Split('.');

        return partes.Length is 2 or 3
            && Marcas.Contains(partes[0])
            && partes.Skip(1).All(p => p.Length is >= 2 and <= 3);
    }
}
