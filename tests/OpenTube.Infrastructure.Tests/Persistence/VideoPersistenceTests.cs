using Dapper;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Tests.Support;

namespace OpenTube.Infrastructure.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public class VideoPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Video NovoVideo(string titulo = "Reunião Trimestral", string slug = "reuniao-trimestral") =>
        Video.CreateDraft(titulo, slug, "originals/a.mp4", Guid.CreateVersion7(), Agora, "Resultados do trimestre");

    [Fact]
    public async Task Grava_e_recupera_um_video_com_todos_os_campos()
    {
        var video = NovoVideo();
        video.ReplaceTags(["treinamento", "financeiro"]);
        video.MarkUploaded(4096);

        await using (var db = fixture.CreateContext())
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using var leitura = fixture.CreateContext();
        var lido = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Equal("Reunião Trimestral", lido.Title);
        Assert.Equal(VideoStatus.Uploaded, lido.Status);
        Assert.Equal(VideoVisibility.Private, lido.Visibility);
        Assert.Equal(4096, lido.SizeBytes);
        Assert.Equal(["treinamento", "financeiro"], lido.Tags);
    }

    [Fact]
    public async Task Recusa_dois_videos_com_o_mesmo_endereco()
    {
        await using var db = fixture.CreateContext();
        db.Videos.Add(NovoVideo());
        await db.SaveChangesAsync();

        db.Videos.Add(NovoVideo("Outro título", "reuniao-trimestral"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Remove_os_arquivos_derivados_junto_com_o_video()
    {
        var video = NovoVideo();
        video.AddAsset(VideoAsset.Create(video.Id, VideoAssetKind.Caption, "vod/a/pt.vtt", Agora, "pt-BR"));

        await using (var db = fixture.CreateContext())
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var alvo = await db.Videos.SingleAsync(v => v.Id == video.Id);
            db.Videos.Remove(alvo);
            await db.SaveChangesAsync();
        }

        await using var leitura = fixture.CreateContext();
        Assert.Empty(await leitura.VideoAssets.ToListAsync());
    }

    [Fact]
    public async Task Preenche_o_vetor_de_busca_ao_gravar()
    {
        var video = NovoVideo("Segurança da Informação", "seguranca-da-informacao");
        video.ReplaceTags(["compliance"]);

        await using (var db = fixture.CreateContext())
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using var db2 = fixture.CreateContext();
        var conexao = db2.Database.GetDbConnection();

        var encontrouPorTitulo = await conexao.ExecuteScalarAsync<bool>(
            "SELECT search_vector @@ to_tsquery('portuguese_unaccent', 'seguranca') FROM videos WHERE id = @Id",
            new { Id = video.Id });

        var encontrouPorEtiqueta = await conexao.ExecuteScalarAsync<bool>(
            "SELECT search_vector @@ to_tsquery('portuguese_unaccent', 'compliance') FROM videos WHERE id = @Id",
            new { Id = video.Id });

        Assert.True(encontrouPorTitulo, "o título deveria alimentar o vetor de busca");
        Assert.True(encontrouPorEtiqueta, "as etiquetas deveriam alimentar o vetor de busca");
    }

    [Fact]
    public async Task Atualiza_o_vetor_de_busca_quando_a_transcricao_chega()
    {
        var video = NovoVideo();

        await using (var db = fixture.CreateContext())
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var alvo = await db.Videos.SingleAsync(v => v.Id == video.Id);
            alvo.SetTranscript("bom dia, vamos falar sobre o orçamento do próximo ano");
            await db.SaveChangesAsync();
        }

        await using var leitura = fixture.CreateContext();
        var achou = await leitura.Database.GetDbConnection().ExecuteScalarAsync<bool>(
            "SELECT search_vector @@ to_tsquery('portuguese_unaccent', 'orcamento') FROM videos WHERE id = @Id",
            new { Id = video.Id });

        Assert.True(achou, "a transcrição deveria entrar no vetor de busca");
    }

    [Fact]
    public async Task Prioriza_o_titulo_sobre_a_descricao_no_ranqueamento()
    {
        var noTitulo = NovoVideo("Orçamento anual", "orcamento-anual");
        var naDescricao = Video.CreateDraft("Reunião de equipe", "reuniao-de-equipe", "originals/b.mp4", Guid.CreateVersion7(), Agora, "falamos de orçamento");

        await using (var db = fixture.CreateContext())
        {
            db.Videos.AddRange(noTitulo, naDescricao);
            await db.SaveChangesAsync();
        }

        await using var leitura = fixture.CreateContext();
        var primeiro = await leitura.Database.GetDbConnection().ExecuteScalarAsync<Guid>(
            """
            SELECT id FROM videos
            WHERE search_vector @@ to_tsquery('portuguese_unaccent', 'orcamento')
            ORDER BY ts_rank(search_vector, to_tsquery('portuguese_unaccent', 'orcamento')) DESC
            LIMIT 1
            """);

        Assert.Equal(noTitulo.Id, primeiro);
    }

    [Fact]
    public async Task Grava_usuario_com_email_unico()
    {
        await using var db = fixture.CreateContext();
        db.Users.Add(User.Create(EmailAddress.Parse("allan@barcelos.dev"), Agora, isAdmin: true));
        await db.SaveChangesAsync();

        db.Users.Add(User.Create(EmailAddress.Parse("allan@barcelos.dev"), Agora));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Video_sem_etiquetas_grava_arranjo_vazio_em_vez_de_nulo()
    {
        var video = NovoVideo();

        await using (var db = fixture.CreateContext())
        {
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }

        await using var leitura = fixture.CreateContext();
        var lido = await leitura.Videos.SingleAsync(v => v.Id == video.Id);

        Assert.Empty(lido.Tags);
    }
}
