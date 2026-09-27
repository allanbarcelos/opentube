// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Media;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

[Collection(IntegrationCollection.Name)]
public class VideoChapterServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> VideoAsync(double duracao = 600)
    {
        await using var db = postgres.CreateContext();
        var id = Guid.CreateVersion7();
        var video = Video.CreateDraft("Reunião", $"v-{id:n}"[..20], StorageKeys.Original(id, "a.mp4"), Guid.CreateVersion7(), Agora, id: id);
        video.MarkUploaded(1024);
        video.StartProcessing();
        video.MarkReady(StorageKeys.VodPrefix(id), duracao, 1280, 720, null, null, Agora);
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        return id;
    }

    private VideoChapterService Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db) =>
        new(db, NullLogger<VideoChapterService>.Instance);

    [Fact]
    public async Task Salva_em_ordem_e_troca_o_sumario_inteiro()
    {
        var videoId = await VideoAsync();

        await using (var db = postgres.CreateContext())
            await Criar(db).ReplaceAsync(videoId, [("5:00", "Perguntas"), ("0:00", "Abertura"), ("1:30", "Resultados")]);

        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(
                [new Chapter(0, "Abertura"), new Chapter(90, "Resultados"), new Chapter(300, "Perguntas")],
                await Criar(db).ListAsync(videoId));

            // Salvar de novo substitui, não acumula.
            await Criar(db).ReplaceAsync(videoId, [("0:00", "Tudo")]);
        }

        await using var leitura = postgres.CreateContext();
        Assert.Equal([new Chapter(0, "Tudo")], await Criar(leitura).ListAsync(videoId));
    }

    [Fact]
    public async Task Linha_invalida_nao_grava_nada()
    {
        var videoId = await VideoAsync(duracao: 120);

        await using (var db = postgres.CreateContext())
            await Criar(db).ReplaceAsync(videoId, [("0:00", "Abertura")]);

        await using (var db = postgres.CreateContext())
        {
            var erro = await Assert.ThrowsAsync<ChapterException>(() =>
                Criar(db).ReplaceAsync(videoId, [("0:00", "Abertura"), ("3:00", "Depois do fim")]));
            Assert.Equal("Chapter {0}: {1} is past the end of the video ({2}).", erro.Key);
        }

        await using var leitura = postgres.CreateContext();
        Assert.Equal([new Chapter(0, "Abertura")], await Criar(leitura).ListAsync(videoId));
    }

    [Fact]
    public async Task Sem_linhas_o_video_fica_sem_sumario()
    {
        var videoId = await VideoAsync();

        await using (var db = postgres.CreateContext())
        {
            await Criar(db).ReplaceAsync(videoId, [("0:00", "Abertura")]);
            await Criar(db).ReplaceAsync(videoId, [("", "")]);
        }

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await Criar(leitura).ListAsync(videoId));
    }

    [Fact]
    public async Task O_sumario_de_um_video_nao_aparece_em_outro_e_sai_junto_com_ele()
    {
        var primeiro = await VideoAsync();
        var segundo = await VideoAsync();

        await using (var db = postgres.CreateContext())
        {
            await Criar(db).ReplaceAsync(primeiro, [("0:00", "Do primeiro")]);
            Assert.Empty(await Criar(db).ListAsync(segundo));

            await db.Videos.Where(v => v.Id == primeiro).ExecuteDeleteAsync();
        }

        await using var leitura = postgres.CreateContext();
        Assert.Equal(0, await leitura.VideoChapters.CountAsync());
    }
}
