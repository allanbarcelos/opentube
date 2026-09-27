// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Queue;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Services;

/// <summary>
/// A fila vista pela administração: o que está esperando, rodando ou falhou, e o cancelamento
/// do que ainda não começou, deixando vídeo e legenda coerentes.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ProcessingQueueServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ProcessingQueueService Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db) =>
        new(db, _relogio, NullLogger<ProcessingQueueService>.Instance);

    /// <summary>Vídeo recém-enviado, esperando a primeira transcodificação.</summary>
    private async Task<(Video Video, Guid JobId)> VideoNaFilaAsync(string titulo = "Reunião geral")
    {
        await using var db = postgres.CreateContext();
        var id = Guid.CreateVersion7();
        var video = Video.CreateDraft(titulo, $"v-{id:n}"[..20], StorageKeys.Original(id, "a.mp4"), Admin, Agora, id: id);
        video.MarkUploaded(1024);
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        var jobId = await new PostgresJobQueue(db, _relogio).EnqueueAsync(
            JobKind.Transcode, video.Id, new TranscodePayload(video.Id, video.OriginalKey));

        return (video, jobId);
    }

    private async Task<ProcessingJob> TrabalhoAsync(Guid id)
    {
        await using var db = postgres.CreateContext();
        return await db.ProcessingJobs.SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Lista_o_que_esta_na_fila_com_o_video_e_o_idioma()
    {
        var (video, transcodificacao) = await VideoNaFilaAsync();

        await using var db = postgres.CreateContext();
        var legenda = VideoAsset.CaptionTranscriptionRequest(video.Id, "pt-br", null, StorageKeys.Caption(video.Id, "pt-br"), Agora);
        db.VideoAssets.Add(legenda);
        await db.SaveChangesAsync();
        var fila = new PostgresJobQueue(db, _relogio);
        await fila.EnqueueAsync(JobKind.Transcript, video.Id, new TranscriptionRequest(video.Id, video.OriginalKey, "pt-br", legenda.Id));
        await fila.EnqueueAsync(JobKind.AnalyticsRollup, delay: TimeSpan.FromMinutes(5));

        var itens = await Criar(db).ListAsync(JobStatus.Pending);

        Assert.Equal(3, itens.Count);

        var primeiro = itens[0];
        Assert.Equal(transcodificacao, primeiro.Id);
        Assert.Equal("Reunião geral", primeiro.VideoTitle);
        Assert.True(primeiro.CanCancel);

        var daLegenda = Assert.Single(itens, i => i.Kind == JobKind.Transcript);
        Assert.Equal("pt-br", daLegenda.Language);
        Assert.True(daLegenda.CanCancel);

        // A agregação da audiência se reagenda sozinha: aparece, agendada, mas não se cancela.
        var agregacao = itens[^1];
        Assert.Equal(JobKind.AnalyticsRollup, agregacao.Kind);
        Assert.Equal(Agora.AddMinutes(5), agregacao.RunAfter);
        Assert.Null(agregacao.VideoId);
        Assert.False(agregacao.CanCancel);
    }

    [Fact]
    public async Task Cancelar_a_transcodificacao_marca_o_video_como_falho()
    {
        var (video, jobId) = await VideoNaFilaAsync();

        await using (var db = postgres.CreateContext())
            Assert.Equal(video.Id, await Criar(db).CancelAsync(jobId));

        Assert.Equal(JobStatus.Cancelled, (await TrabalhoAsync(jobId)).Status);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(VideoStatus.Failed, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Status);

        // E o worker não pega mais esse trabalho.
        Assert.Null(await new PostgresJobQueue(leitura, _relogio).DequeueAsync("worker", [JobKind.Transcode], TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Cancelar_o_reprocessamento_de_um_video_pronto_o_mantem_pronto()
    {
        var (video, jobId) = await VideoNaFilaAsync();
        await using (var db = postgres.CreateContext())
        {
            var pronto = await db.Videos.SingleAsync(v => v.Id == video.Id);
            pronto.StartProcessing();
            pronto.MarkReady(StorageKeys.VodPrefix(video.Id), 60, 1280, 720, null, null, Agora);
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
            await Criar(db).CancelAsync(jobId);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(VideoStatus.Ready, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Status);
    }

    [Fact]
    public async Task Cancelar_a_legenda_registra_a_falha_com_o_motivo()
    {
        var (video, _) = await VideoNaFilaAsync();
        Guid legendaId, jobId;

        await using (var db = postgres.CreateContext())
        {
            var legenda = VideoAsset.CaptionTranscriptionRequest(video.Id, "auto", null, StorageKeys.Caption(video.Id, "auto"), Agora);
            db.VideoAssets.Add(legenda);
            await db.SaveChangesAsync();
            legendaId = legenda.Id;
            jobId = await new PostgresJobQueue(db, _relogio).EnqueueAsync(
                JobKind.Transcript, video.Id, new TranscriptionRequest(video.Id, video.OriginalKey, "auto", legenda.Id));
        }

        await using (var db = postgres.CreateContext())
            await Criar(db).CancelAsync(jobId);

        await using var leitura = postgres.CreateContext();
        var cancelada = await leitura.VideoAssets.SingleAsync(a => a.Id == legendaId);
        Assert.Equal(CaptionStatus.Failed, cancelada.Status);
        Assert.Equal("Cancelled by an administrator.", cancelada.Error);

        // O vídeo não tem nada a ver com a legenda cancelada.
        Assert.Equal(VideoStatus.Uploaded, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Status);
    }

    [Fact]
    public async Task Trabalho_que_ja_comecou_nao_e_cancelado()
    {
        var (video, jobId) = await VideoNaFilaAsync();

        await using (var db = postgres.CreateContext())
            await new PostgresJobQueue(db, _relogio).DequeueAsync("worker-1", [JobKind.Transcode], TimeSpan.FromMinutes(5));

        await using (var db = postgres.CreateContext())
        {
            var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).CancelAsync(jobId));
            Assert.Equal("This job has already started or finished and can no longer be cancelled.", erro.Message);
        }

        Assert.Equal(JobStatus.Running, (await TrabalhoAsync(jobId)).Status);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(VideoStatus.Uploaded, (await leitura.Videos.SingleAsync(v => v.Id == video.Id)).Status);

        var rodando = Assert.Single(await Criar(leitura).ListAsync(JobStatus.Running));
        Assert.Equal("worker-1", rodando.LockedBy);
        Assert.False(rodando.CanCancel);
    }

    [Fact]
    public async Task Manutencao_automatica_nao_se_cancela()
    {
        Guid jobId;
        await using (var db = postgres.CreateContext())
            jobId = await new PostgresJobQueue(db, _relogio).EnqueueAsync(JobKind.AnalyticsRollup);

        await using (var db = postgres.CreateContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).CancelAsync(jobId));

        Assert.Equal(JobStatus.Pending, (await TrabalhoAsync(jobId)).Status);
    }

    [Fact]
    public async Task Trabalho_que_falhou_pode_sair_da_lista_de_falhas()
    {
        var (_, jobId) = await VideoNaFilaAsync();

        await using (var db = postgres.CreateContext())
        {
            var fila = new PostgresJobQueue(db, _relogio);
            for (var i = 0; i < ProcessingJob.MaxAttempts; i++)
            {
                _relogio.Advance(TimeSpan.FromHours(1));
                await fila.DequeueAsync("worker", [JobKind.Transcode], TimeSpan.FromMinutes(5));
                await fila.FailAsync(jobId, "ffprobe não reconheceu o arquivo");
            }
        }

        await using (var db = postgres.CreateContext())
        {
            var falha = Assert.Single(await Criar(db).ListAsync(JobStatus.Failed));
            Assert.Equal("ffprobe não reconheceu o arquivo", falha.LastError);
            Assert.False(falha.CanCancel);

            // Pendente não sai por aqui: isso é cancelar.
            var (_, outro) = await VideoNaFilaAsync("Outro");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).DismissAsync(outro));

            await Criar(db).DismissAsync(jobId);
        }

        await using var leitura = postgres.CreateContext();
        Assert.Empty(await Criar(leitura).ListAsync(JobStatus.Failed));
        Assert.Equal(JobStatus.Cancelled, (await TrabalhoAsync(jobId)).Status);
    }
}
