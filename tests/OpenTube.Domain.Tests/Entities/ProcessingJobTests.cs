// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Tests.Entities;

public class ProcessingJobTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Reserva = TimeSpan.FromMinutes(10);

    private static ProcessingJob Novo() =>
        ProcessingJob.Create(JobKind.Transcode, Agora, Guid.CreateVersion7());

    [Fact]
    public void Nasce_pendente_e_pronto_para_executar()
    {
        var job = Novo();

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(Agora, job.RunAfter);
        Assert.Equal(0, job.Attempts);
        Assert.Equal("{}", job.Payload);
    }

    [Fact]
    public void Aceita_agendamento_futuro()
    {
        var job = ProcessingJob.Create(JobKind.AnalyticsRollup, Agora, delay: TimeSpan.FromMinutes(5));

        Assert.Equal(Agora.AddMinutes(5), job.RunAfter);
    }

    [Fact]
    public void Payload_em_branco_vira_objeto_vazio()
    {
        var job = ProcessingJob.Create(JobKind.Transcode, Agora, payload: "   ");

        Assert.Equal("{}", job.Payload);
    }

    [Fact]
    public void Reserva_o_job_ao_iniciar()
    {
        var job = Novo();

        job.Start("worker-1", Agora, Reserva);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("worker-1", job.LockedBy);
        Assert.Equal(Agora + Reserva, job.LockedUntil);
        Assert.Equal(Agora, job.StartedAt);
    }

    [Fact]
    public void Exige_identificacao_do_worker()
    {
        var job = Novo();

        Assert.ThrowsAny<ArgumentException>(() => job.Start("  ", Agora, Reserva));
    }

    [Fact]
    public void Estende_a_reserva_durante_a_execucao()
    {
        var job = Novo();
        job.Start("worker-1", Agora, Reserva);

        job.Renew(Agora.AddMinutes(8), Reserva);

        Assert.Equal(Agora.AddMinutes(18), job.LockedUntil);
    }

    [Fact]
    public void Nao_estende_reserva_de_job_parado()
    {
        var job = Novo();

        Assert.Throws<InvalidOperationException>(() => job.Renew(Agora, Reserva));
    }

    [Fact]
    public void Conclui_liberando_a_reserva()
    {
        var job = Novo();
        job.Start("worker-1", Agora, Reserva);

        job.Succeed(Agora.AddMinutes(4));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(Agora.AddMinutes(4), job.CompletedAt);
        Assert.Null(job.LockedBy);
        Assert.Null(job.LockedUntil);
    }

    [Fact]
    public void Falha_recuperavel_devolve_o_job_para_a_fila_com_recuo()
    {
        var job = Novo();
        job.Start("worker-1", Agora, Reserva);

        job.Fail("ffmpeg saiu com código 1", Agora.AddMinutes(2));

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal("ffmpeg saiu com código 1", job.LastError);
        Assert.Equal(Agora.AddMinutes(2).AddSeconds(30), job.RunAfter);
        Assert.Null(job.LockedBy);
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public void Desiste_depois_do_limite_de_tentativas()
    {
        var job = Novo();

        for (var tentativa = 1; tentativa <= ProcessingJob.MaxAttempts; tentativa++)
        {
            job.Start("worker-1", Agora, Reserva);
            job.Fail("erro", Agora);
        }

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(ProcessingJob.MaxAttempts, job.Attempts);
        Assert.Equal(Agora, job.CompletedAt);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 480)]
    public void Recuo_cresce_exponencialmente(int tentativas, int segundosEsperados)
    {
        Assert.Equal(TimeSpan.FromSeconds(segundosEsperados), ProcessingJob.BackoffFor(tentativas));
    }

    [Fact]
    public void Recuo_nao_quebra_com_contagem_zerada()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ProcessingJob.BackoffFor(0));
    }

    [Fact]
    public void Trunca_mensagem_de_erro_muito_longa()
    {
        var job = Novo();
        job.Start("worker-1", Agora, Reserva);

        job.Fail(new string('x', 6000), Agora);

        Assert.Equal(4000, job.LastError!.Length);
    }

    [Fact]
    public void Cancela_job_pendente()
    {
        var job = Novo();

        job.Cancel(Agora);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(Agora, job.CompletedAt);
    }

    [Fact]
    public void Nao_cancela_job_ja_concluido()
    {
        var job = Novo();
        job.Start("worker-1", Agora, Reserva);
        job.Succeed(Agora);

        Assert.Throws<InvalidOperationException>(() => job.Cancel(Agora));
    }

    [Fact]
    public void Sucesso_limpa_o_erro_da_tentativa_anterior()
    {
        var job = Novo();
        job.Start("worker-1", Agora, Reserva);
        job.Fail("falha passageira", Agora);
        job.Start("worker-1", Agora.AddMinutes(1), Reserva);

        job.Succeed(Agora.AddMinutes(2));

        Assert.Null(job.LastError);
    }
}
