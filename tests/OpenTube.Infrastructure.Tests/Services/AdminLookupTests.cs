// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

/// <summary>
/// Opções dos seletores da administração: buscadas no servidor, sem acento nem maiúscula, e
/// entregues vinte por vez — nunca a lista inteira, e nunca cortada num limite.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AdminLookupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<Video>> CriarVideosAsync(params string[] titulos)
    {
        await using var db = postgres.CreateContext();

        var videos = titulos.Select(t =>
        {
            var id = Guid.CreateVersion7();
            return Video.CreateDraft(t, $"v-{id:n}"[..20], "originals/a.mp4", Admin, Agora, id: id);
        }).ToList();

        db.Videos.AddRange(videos);
        await db.SaveChangesAsync();

        return videos;
    }

    private async Task<Collection> CriarColecaoAsync(string nome, params Guid[] videos)
    {
        await using var db = postgres.CreateContext();
        var colecao = Collection.Create(nome, "c-" + Guid.CreateVersion7().ToString("n")[..12], Admin, Agora);

        foreach (var video in videos)
            colecao.Add(video, Agora);

        db.Collections.Add(colecao);
        await db.SaveChangesAsync();

        return colecao;
    }

    private AdminLookup Criar(out IAsyncDisposable db)
    {
        var contexto = postgres.CreateContext();
        db = contexto;
        return new AdminLookup(contexto);
    }

    [Fact]
    public async Task Entrega_vinte_por_vez_e_avisa_quando_ha_mais()
    {
        await CriarVideosAsync([.. Enumerable.Range(1, 25).Select(i => $"Aula {i}")]);
        var colecao = await CriarColecaoAsync("Vazia");
        var opcoes = Criar(out var db);
        await using var _ = db;

        var primeira = await opcoes.VideosForCollectionAsync(colecao.Id, null, page: 1);
        var segunda = await opcoes.VideosForCollectionAsync(colecao.Id, null, page: 2);

        Assert.Equal(AdminLookup.PageSize, primeira.Items.Count);
        Assert.True(primeira.HasMore);
        Assert.Equal(5, segunda.Items.Count);
        Assert.False(segunda.HasMore);

        // Ordem pelo nome, com o número valendo pelo valor, e nada repetido entre as páginas.
        Assert.Equal("Aula 1", primeira.Items[0].Label);
        Assert.Equal("Aula 2", primeira.Items[1].Label);
        Assert.Equal("Aula 25", segunda.Items[^1].Label);
        Assert.Empty(primeira.Items.Select(i => i.Id).Intersect(segunda.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task Acha_qualquer_video_do_acervo_mesmo_alem_dos_cem_mais_recentes()
    {
        // O <select> antigo trazia só os 100 mais recentes: o primeiro vídeo enviado sumia dele.
        await CriarVideosAsync("Boas-vindas ao time");
        await CriarVideosAsync([.. Enumerable.Range(1, 120).Select(i => $"Reunião semanal {i}")]);
        var colecao = await CriarColecaoAsync("Integração");
        var opcoes = Criar(out var db);
        await using var _ = db;

        var achados = await opcoes.VideosForCollectionAsync(colecao.Id, "boas-vindas");

        Assert.Equal("Boas-vindas ao time", Assert.Single(achados.Items).Label);
    }

    [Theory]
    [InlineData("reuniao")]
    [InlineData("REUNIÃO")]
    [InlineData("trimes")]
    public async Task A_busca_ignora_acento_e_maiuscula_e_acha_no_meio_do_nome(string termo)
    {
        await CriarVideosAsync("Reunião Trimestral", "Planejamento");
        var colecao = await CriarColecaoAsync("Diretoria");
        var opcoes = Criar(out var db);
        await using var _ = db;

        var achados = await opcoes.VideosForCollectionAsync(colecao.Id, termo);

        Assert.Equal("Reunião Trimestral", Assert.Single(achados.Items).Label);
    }

    [Fact]
    public async Task Coringas_do_LIKE_sao_procurados_como_texto()
    {
        await CriarVideosAsync("Meta de 100% batida", "Planejamento", "nome_com_sublinhado");
        var colecao = await CriarColecaoAsync("Metas");
        var opcoes = Criar(out var db);
        await using var _ = db;

        Assert.Equal("Meta de 100% batida", Assert.Single((await opcoes.VideosForCollectionAsync(colecao.Id, "%")).Items).Label);
        Assert.Equal("nome_com_sublinhado", Assert.Single((await opcoes.VideosForCollectionAsync(colecao.Id, "_")).Items).Label);
    }

    [Fact]
    public async Task Nao_oferece_o_que_ja_esta_na_colecao_nem_o_excluido_e_avisa_onde_o_video_esta()
    {
        var videos = await CriarVideosAsync("Já na coleção", "Em outra coleção", "Excluído", "Solto");
        var colecao = await CriarColecaoAsync("Esta", videos[0].Id);
        await CriarColecaoAsync("Outra", videos[1].Id);

        await using (var escrita = postgres.CreateContext())
        {
            var excluido = await escrita.Videos.FindAsync(videos[2].Id);
            excluido!.SoftDelete(Agora);
            await escrita.SaveChangesAsync();
        }

        var opcoes = Criar(out var db);
        await using var _ = db;

        var itens = (await opcoes.VideosForCollectionAsync(colecao.Id, null)).Items;

        Assert.Equal(["Em outra coleção", "Solto"], itens.Select(i => i.Label));
        Assert.Equal("In collection “Outra”", itens[0].Hint);
        Assert.Null(itens[1].Hint);
    }

    [Fact]
    public async Task Colecoes_de_destino_deixam_de_fora_a_propria_e_dizem_quantos_videos_tem()
    {
        // Um vídeo fica em uma coleção só: cada coleção recebe os seus.
        var videos = await CriarVideosAsync("Um", "Dois", "Três");
        var esta = await CriarColecaoAsync("Esta");
        await CriarColecaoAsync("Treinamentos", videos[0].Id, videos[1].Id);
        await CriarColecaoAsync("Onboarding", videos[2].Id);
        var opcoes = Criar(out var db);
        await using var _ = db;

        var todas = (await opcoes.CollectionsAsync(null, except: esta.Id)).Items;
        var buscadas = (await opcoes.CollectionsAsync("trein", except: esta.Id)).Items;

        Assert.Equal(["Onboarding", "Treinamentos"], todas.Select(c => c.Label));
        Assert.Equal("1 video", todas[0].Hint);
        Assert.Equal("Treinamentos", Assert.Single(buscadas).Label);
        Assert.Equal("2 videos", buscadas[0].Hint);
    }
}
