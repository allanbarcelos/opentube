// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Localization;

namespace OpenTube.Infrastructure.Access;

/// <summary>
/// Como uma concessão é descrita para as pessoas: de quem ela é e sobre o quê. É o único lugar
/// que sabe escrever isso, para o painel, a página da pessoa, a auditoria e o convite dizerem
/// o mesmo — e para um tipo novo de concessão mudar o texto em um lugar só. As regras de quem
/// pode assistir continuam no domínio (AccessPolicy).
/// </summary>
public static class GrantLabels
{
    /// <summary>De quem é a concessão: o endereço, o domínio com "@", o link secreto ou qualquer visitante.</summary>
    public static string Subject(AccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return grant.SubjectType switch
        {
            GrantSubjectType.Domain => "@" + grant.SubjectValue,
            GrantSubjectType.Link => LocalText.Get("secret link"),
            GrantSubjectType.Public => LocalText.Get("any visitor"),
            _ => grant.SubjectValue
        };
    }

    /// <summary>
    /// O alvo, sem nome: "O acervo inteiro", "Uma coleção", "Um vídeo". Serve para listas e para
    /// quando o nome não existe mais.
    /// </summary>
    public static string Target(GrantTargetType type) => type switch
    {
        GrantTargetType.All => LocalText.Get("The whole library"),
        GrantTargetType.Collection => LocalText.Get("A collection"),
        _ => LocalText.Get("A video")
    };

    /// <summary>
    /// O alvo dentro de uma frase, pelo nome: vídeo “Título”, coleção “Nome” ou o acervo inteiro.
    /// Sem o nome — o vídeo ou a coleção foi apagado —, diz isso.
    /// </summary>
    public static string TargetPhrase(GrantTargetType type, string? name) => type switch
    {
        GrantTargetType.Video => name is null ? LocalText.Get("a removed video") : LocalText.Format("video “{0}”", name),
        GrantTargetType.Collection => name is null ? LocalText.Get("a removed collection") : LocalText.Format("collection “{0}”", name),
        _ => LocalText.Get("the whole library")
    };
}
