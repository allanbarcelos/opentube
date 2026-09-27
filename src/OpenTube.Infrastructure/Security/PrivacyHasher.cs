// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Options;
using OpenTube.Infrastructure.Options;

namespace OpenTube.Infrastructure.Security;

/// <summary>
/// Resume dados pessoais que só precisam ser comparados, nunca lidos. O endereço de origem
/// serve para contar tentativas e distinguir dispositivos; guardá-lo por extenso seria coletar
/// dado pessoal sem necessidade.
/// </summary>
public class PrivacyHasher(IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _options = options.Value;

    public string? HashIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? null : TokenHasher.Hash(ip.Trim(), _options.IpHashPepper);
}
