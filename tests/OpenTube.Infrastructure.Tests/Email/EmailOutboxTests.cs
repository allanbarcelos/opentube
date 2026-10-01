// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTube.Infrastructure.Email;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Email;

public class EmailOutboxTests
{
    private static EmailMessage Mensagem(string para) => new(para, "Assunto", "<p>corpo</p>", "corpo");

    /// <summary>Remetente que falha na primeira mensagem e entrega as seguintes.</summary>
    private sealed class FalhaNaPrimeira(FakeEmailSender destino) : IEmailSender
    {
        private int _chamadas;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _chamadas) == 1
                ? Task.FromException(new InvalidOperationException("SMTP fora do ar"))
                : destino.SendAsync(message, cancellationToken);
    }

    private static (EmailOutbox Fila, EmailOutboxDispatcher Despachante, ServiceProvider Provider) Montar(Func<IServiceProvider, IEmailSender> remetente)
    {
        var servicos = new ServiceCollection();
        servicos.AddScoped(remetente);
        var provider = servicos.BuildServiceProvider();

        var fila = new EmailOutbox(NullLogger<EmailOutbox>.Instance);
        var despachante = new EmailOutboxDispatcher(
            fila, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EmailOutboxDispatcher>.Instance);

        return (fila, despachante, provider);
    }

    private static async Task EsperarAsync(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);

        while (!condicao() && DateTime.UtcNow < limite)
            await Task.Delay(20);
    }

    [Fact]
    public async Task Enfileirar_nao_espera_o_envio_e_o_despachante_entrega()
    {
        var emails = new FakeEmailSender();
        var (fila, despachante, provider) = Montar(_ => emails);
        await using var _ = provider;

        await fila.EnqueueAsync(Mensagem("a@barcelos.dev"));
        Assert.Empty(emails.Sent);

        await despachante.StartAsync(CancellationToken.None);
        await EsperarAsync(() => emails.Sent.Count == 1);
        await despachante.StopAsync(CancellationToken.None);

        Assert.Equal("a@barcelos.dev", Assert.Single(emails.Sent).To);
    }

    [Fact]
    public async Task Falha_de_envio_nao_para_a_fila()
    {
        var emails = new FakeEmailSender();
        var remetente = new FalhaNaPrimeira(emails);
        var (fila, despachante, provider) = Montar(_ => remetente);
        await using var _ = provider;

        await despachante.StartAsync(CancellationToken.None);
        await fila.EnqueueAsync(Mensagem("perdida@barcelos.dev"));
        await fila.EnqueueAsync(Mensagem("entregue@barcelos.dev"));
        await EsperarAsync(() => emails.Sent.Count == 1);
        await despachante.StopAsync(CancellationToken.None);

        Assert.Equal("entregue@barcelos.dev", Assert.Single(emails.Sent).To);
    }
}
