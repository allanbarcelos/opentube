// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;

namespace OpenTube.Domain.Tests.Entities;

public class CollectionTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static Collection Nova() => Collection.Create("Treinamentos 2026", "treinamentos-2026", Admin, Agora);

    [Fact]
    public void Nasce_vazia_e_ativa()
    {
        var colecao = Nova();

        Assert.Empty(colecao.Videos);
        Assert.False(colecao.IsDeleted);
        Assert.Equal("Treinamentos 2026", colecao.Name);
    }

    [Theory]
    [InlineData(null, "slug")]
    [InlineData("", "slug")]
    [InlineData("Nome", "")]
    public void Exige_nome_e_endereco(string? nome, string slug)
    {
        Assert.ThrowsAny<ArgumentException>(() => Collection.Create(nome!, slug, Admin, Agora));
    }

    [Fact]
    public void Acrescenta_videos_na_ordem_de_entrada()
    {
        var colecao = Nova();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        colecao.Add(a);
        colecao.Add(b);

        Assert.Equal([a, b], colecao.Videos.OrderBy(v => v.Position).Select(v => v.VideoId));
        Assert.Equal([0, 1], colecao.Videos.OrderBy(v => v.Position).Select(v => v.Position));
    }

    [Fact]
    public void Acrescentar_o_mesmo_video_duas_vezes_nao_duplica()
    {
        var colecao = Nova();
        var video = Guid.CreateVersion7();

        colecao.Add(video);
        colecao.Add(video);

        Assert.Single(colecao.Videos);
    }

    [Fact]
    public void Remove_um_video_da_colecao()
    {
        var colecao = Nova();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        colecao.Add(a);
        colecao.Add(b);

        colecao.Remove(a);

        Assert.Equal([b], colecao.Videos.Select(v => v.VideoId));
    }

    [Fact]
    public void Remover_video_ausente_nao_e_erro()
    {
        var colecao = Nova();

        colecao.Remove(Guid.CreateVersion7());

        Assert.Empty(colecao.Videos);
    }

    [Fact]
    public void Redefinir_o_conteudo_reordena_e_descarta_repeticoes()
    {
        var colecao = Nova();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        colecao.Add(a);

        colecao.Replace([c, b, c]);

        Assert.Equal([c, b], colecao.Videos.OrderBy(v => v.Position).Select(v => v.VideoId));
    }

    [Fact]
    public void A_posicao_continua_depois_de_uma_remocao()
    {
        var colecao = Nova();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        colecao.Add(a);
        colecao.Add(b);
        colecao.Remove(a);

        colecao.Add(c);

        // O novo vídeo entra no fim, sem colidir com a posição de quem ficou.
        Assert.Equal(2, colecao.Videos.Single(v => v.VideoId == c).Position);
    }

    [Fact]
    public void Renomeia_removendo_espacos()
    {
        var colecao = Nova();

        colecao.Rename("  Treinamentos  ", "  obrigatórios  ");

        Assert.Equal("Treinamentos", colecao.Name);
        Assert.Equal("obrigatórios", colecao.Description);
    }

    [Fact]
    public void Descricao_em_branco_vira_nula()
    {
        var colecao = Nova();

        colecao.Rename("Treinamentos", "   ");

        Assert.Null(colecao.Description);
    }

    [Fact]
    public void Renomear_exige_nome()
    {
        var colecao = Nova();

        Assert.ThrowsAny<ArgumentException>(() => colecao.Rename("  ", null));
    }

    [Fact]
    public void Exclusao_logica_e_restauracao()
    {
        var colecao = Nova();

        colecao.SoftDelete(Agora);
        Assert.True(colecao.IsDeleted);

        colecao.SoftDelete(Agora.AddDays(1));
        Assert.Equal(Agora, colecao.DeletedAt);

        colecao.Restore();
        Assert.False(colecao.IsDeleted);
    }

    [Fact]
    public void Exige_a_lista_ao_redefinir()
    {
        Assert.Throws<ArgumentNullException>(() => Nova().Replace(null!));
    }
}
