// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenTube.Infrastructure.Email;

/// <summary>
/// Mensagens despachadas fora do pedido. Serve onde o tempo da resposta não pode depender do
/// envio: o código de acesso só sai para quem tem convite, e esperar o SMTP só nesse caso
/// entregaria a lista de convidados a quem medisse o tempo.
/// </summary>
public interface IEmailOutbox
{
    /// <summary>Guarda a mensagem para envio em segundo plano. Não espera o servidor de email.</summary>
    ValueTask EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fila em memória. Uma mensagem perdida num reinício custa só pedir o código de novo; uma fila
/// durável não compensaria para mensagens que valem quinze minutos.
/// </summary>
public sealed class EmailOutbox(ILogger<EmailOutbox> logger) : IEmailOutbox
{
    /// <summary>
    /// Teto da fila. Os limites de pedido por email e por origem já seguram o volume; o teto só
    /// impede que um servidor de email parado acumule memória sem fim.
    /// </summary>
    public const int Capacidade = 1000;

    private readonly Channel<EmailMessage> _fila = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(Capacidade) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    internal ChannelReader<EmailMessage> Leitor => _fila.Reader;

    public ValueTask EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_fila.Writer.TryWrite(message))
            logger.LogError("Fila de emails cheia; mensagem descartada");

        return ValueTask.CompletedTask;
    }
}

/// <summary>Envia o que entra na <see cref="EmailOutbox"/>, uma mensagem por vez.</summary>
public sealed class EmailOutboxDispatcher(
    EmailOutbox outbox,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mensagem in outbox.Leitor.ReadAllAsync(stoppingToken))
                await EnviarAsync(mensagem, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligamento: o que ficou na fila se perde, e quem esperava pede um código novo.
        }
    }

    private async Task EnviarAsync(EmailMessage mensagem, CancellationToken cancellationToken)
    {
        try
        {
            using var escopo = scopeFactory.CreateScope();
            await escopo.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(mensagem, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Uma falha de envio não pode parar a fila: as mensagens seguintes ainda podem sair.
            logger.LogError(e, "Falha ao enviar email da fila");
        }
    }
}
