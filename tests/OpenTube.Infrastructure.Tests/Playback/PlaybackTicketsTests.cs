// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Playback;

namespace OpenTube.Infrastructure.Tests.Playback;

public class PlaybackTicketsTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Video = Guid.CreateVersion7();
    private static readonly Guid Concessao = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    private PlaybackTickets Criar(string segredo = "segredo") =>
        new(Microsoft.Extensions.Options.Options.Create(new SecurityOptions { TokenPepper = segredo, IpHashPepper = "ip" }), _relogio);

    [Fact]
    public void Confere_o_bilhete_emitido()
    {
        var bilhetes = Criar();
        var bilhete = bilhetes.Issue(Video, Concessao, durationSeconds: 600);

        Assert.Equal(new ViewInProgress(Video, Concessao), bilhetes.Validate(bilhete.CookieName, bilhete.Value));
    }

    [Fact]
    public void Vale_pela_duracao_com_folga_e_tem_teto()
    {
        var bilhetes = Criar();

        Assert.Equal(Agora.AddMinutes(10).AddHours(2), bilhetes.Issue(Video, Concessao, 600).ExpiresAt);
        Assert.Equal(Agora.AddHours(12), bilhetes.Issue(Video, Concessao, 100 * 3600).ExpiresAt);
    }

    [Fact]
    public void Recusa_bilhete_vencido()
    {
        var bilhetes = Criar();
        var bilhete = bilhetes.Issue(Video, Concessao, 600);

        _relogio.Advance(TimeSpan.FromHours(3));

        Assert.Null(bilhetes.Validate(bilhete.CookieName, bilhete.Value));
    }

    [Fact]
    public void Recusa_bilhete_de_outro_video()
    {
        var bilhetes = Criar();
        var bilhete = bilhetes.Issue(Video, Concessao, 600);
        var outroVideo = PlaybackTickets.CookiePrefix + Guid.CreateVersion7().ToString("n");

        Assert.Null(bilhetes.Validate(outroVideo, bilhete.Value));
    }

    [Fact]
    public void Recusa_bilhete_adulterado()
    {
        var bilhetes = Criar();
        var bilhete = bilhetes.Issue(Video, Concessao, 600);
        var partes = bilhete.Value.Split('.');

        var outraConcessao = string.Join('.', Guid.CreateVersion7().ToString("n"), partes[1], partes[2]);
        var prazoEsticado = string.Join('.', partes[0], (long.Parse(partes[1]) + 86400).ToString(), partes[2]);

        Assert.Null(bilhetes.Validate(bilhete.CookieName, outraConcessao));
        Assert.Null(bilhetes.Validate(bilhete.CookieName, prazoEsticado));
        Assert.Null(Criar("outro-segredo").Validate(bilhete.CookieName, bilhete.Value));
    }

    [Fact]
    public void Ignora_prazo_fora_do_intervalo()
    {
        var nome = PlaybackTickets.CookiePrefix + Video.ToString("n");
        var valor = string.Join('.', Concessao.ToString("n"), long.MaxValue.ToString(), "assinatura");

        Assert.Null(Criar().Validate(nome, valor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("a.b.c")]
    [InlineData("..")]
    public void Ignora_conteudo_fora_do_formato(string valor)
    {
        Assert.Null(Criar().Validate(PlaybackTickets.CookiePrefix + Video.ToString("n"), valor));
    }

    [Fact]
    public void Aplica_ao_espectador_so_os_bilhetes_validos()
    {
        var bilhetes = Criar();
        var bilhete = bilhetes.Issue(Video, Concessao, 600);

        var espectador = bilhetes.Apply(Viewer.Anonymous,
        [
            new(bilhete.CookieName, bilhete.Value),
            new(PlaybackTickets.CookiePrefix + Guid.CreateVersion7().ToString("n"), bilhete.Value),
            new("opentube.sessao", "qualquer")
        ]);

        Assert.True(espectador.IsContinuing(Video, Concessao));
        Assert.Single(espectador.ViewsInProgress);
    }
}
