// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Access;

namespace OpenTube.Infrastructure.Tests.Access;

/// <summary>A descrição de uma concessão, a mesma em todas as telas, na auditoria e no convite.</summary>
public class GrantLabelsTests
{
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly DateTimeOffset Agora = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void O_sujeito_diz_de_quem_e_o_acesso()
    {
        Assert.Equal("ana@empresa.com", GrantLabels.Subject(AccessGrant.ForUser(EmailAddress.Parse("ana@empresa.com"), GrantTargetType.All, null, Admin, Agora)));
        Assert.Equal("secret link", GrantLabels.Subject(AccessGrant.ForLink("resumo", GrantTargetType.All, null, Admin, Agora)));

        // O domínio leva o "@": sem ele, "empresa.com" se confunde com um endereço.
        Assert.Equal("@empresa.com", GrantLabels.Subject(AccessGrant.ForDomain("empresa.com", GrantTargetType.All, null, Admin, Agora)));
    }

    [Fact]
    public void O_alvo_na_frase_traz_o_nome_ou_diz_que_foi_apagado()
    {
        Assert.Equal("video “Plano”", GrantLabels.TargetPhrase(GrantTargetType.Video, "Plano"));
        Assert.Equal("collection “Treinamentos”", GrantLabels.TargetPhrase(GrantTargetType.Collection, "Treinamentos"));
        Assert.Equal("a removed video", GrantLabels.TargetPhrase(GrantTargetType.Video, null));
        Assert.Equal("the whole library", GrantLabels.TargetPhrase(GrantTargetType.All, null));
    }

    [Fact]
    public void Todo_tipo_de_sujeito_e_de_alvo_tem_descricao()
    {
        // Um tipo novo de concessão que ninguém descreveu cairia no texto de outro tipo. Este
        // teste obriga a decidir o texto dele aqui, no único lugar que descreve concessões.
        foreach (var alvo in Enum.GetValues<GrantTargetType>())
        {
            Assert.False(string.IsNullOrWhiteSpace(GrantLabels.Target(alvo)));
            Assert.False(string.IsNullOrWhiteSpace(GrantLabels.TargetPhrase(alvo, "Nome")));
        }

        Assert.Equal(
            Enum.GetValues<GrantTargetType>().Length,
            Enum.GetValues<GrantTargetType>().Select(GrantLabels.Target).Distinct().Count());
    }
}
