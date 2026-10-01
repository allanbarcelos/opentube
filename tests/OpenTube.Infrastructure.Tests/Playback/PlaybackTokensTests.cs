// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Playback;

namespace OpenTube.Infrastructure.Tests.Playback;

public class PlaybackTokensTests
{
    private static readonly Guid Video = Guid.CreateVersion7();
    private static readonly Viewer Allan = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

    private readonly FakeTimeProvider _relogio = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private PlaybackTokens Criar(string segredo = "segredo-de-teste") =>
        new(Microsoft.Extensions.Options.Options.Create(new SecurityOptions { TokenPepper = segredo, IpHashPepper = "ip" }), _relogio);

    [Fact]
    public void Vale_para_o_video_e_a_pessoa_para_os_quais_foi_emitido()
    {
        var tokens = Criar();
        var token = tokens.Issue(Video, Allan);

        Assert.True(tokens.Validate(token, Video, Allan));
        Assert.False(tokens.Validate(token, Guid.CreateVersion7(), Allan));
        Assert.False(tokens.Validate(token, Video, Viewer.Anonymous));
    }

    [Fact]
    public void Vence_em_uma_hora()
    {
        var tokens = Criar();
        var token = tokens.Issue(Video, Allan);

        _relogio.Advance(PlaybackTokens.PageLifetime + TimeSpan.FromSeconds(1));

        Assert.False(tokens.Validate(token, Video, Allan));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("99999999999999999999.x")]
    [InlineData("-5.abc")]
    public void Token_malformado_nao_vale(string? token)
    {
        Assert.False(Criar().Validate(token, Video, Allan));
    }

    [Fact]
    public void Prazo_alterado_ou_outro_segredo_invalidam_a_assinatura()
    {
        var tokens = Criar();
        var token = tokens.Issue(Video, Allan);
        var partes = token.Split('.');

        var esticado = (long.Parse(partes[0]) + 3600) + "." + partes[1];

        Assert.False(tokens.Validate(esticado, Video, Allan));
        Assert.False(Criar("outro-segredo").Validate(token, Video, Allan));
    }

    [Fact]
    public void Quem_assiste_e_a_conta_o_link_ou_o_anonimo()
    {
        var link = Guid.CreateVersion7();

        Assert.StartsWith("u", PlaybackTokens.ViewerKey(Allan));
        Assert.Equal("l" + link.ToString("n"), PlaybackTokens.ViewerKey(Viewer.WithLink(link)));
        Assert.Equal("anon", PlaybackTokens.ViewerKey(Viewer.Anonymous));
    }
}

public class PlaybackSealsTests
{
    private static readonly Guid Video = Guid.CreateVersion7();
    private static readonly Viewer Allan = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

    private readonly FakeTimeProvider _relogio = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private PlaybackSeals Criar(string segredo = "segredo-de-teste") =>
        new(Microsoft.Extensions.Options.Options.Create(new SecurityOptions { TokenPepper = segredo, IpHashPepper = "ip" }), _relogio);

    [Fact]
    public void Abre_o_que_selou_para_o_mesmo_tipo_e_a_mesma_pessoa()
    {
        var selos = Criar();
        var selo = selos.Seal(PlaybackSealKind.Segment, Video, Allan, "360p/seg-00007.m4s");

        var aberto = selos.Open(selo, PlaybackSealKind.Segment, Allan);

        Assert.Equal(Video, aberto?.VideoId);
        Assert.Equal("360p/seg-00007.m4s", aberto?.Path);
        Assert.Null(selos.Open(selo, PlaybackSealKind.Rendition, Allan));
        Assert.Null(selos.Open(selo, PlaybackSealKind.Segment, Viewer.Anonymous));
    }

    [Fact]
    public void Nao_revela_o_video_nem_o_arquivo()
    {
        var selo = Criar().Seal(PlaybackSealKind.Segment, Video, Allan, "360p/seg-00007.m4s");

        Assert.DoesNotContain("seg", selo);
        Assert.DoesNotContain("360p", selo);
        Assert.DoesNotContain(Video.ToString("n"), selo);
        Assert.All(selo, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void O_mesmo_arquivo_gera_selos_diferentes()
    {
        var selos = Criar();

        Assert.NotEqual(
            selos.Seal(PlaybackSealKind.Segment, Video, Allan, "360p/seg-00001.m4s"),
            selos.Seal(PlaybackSealKind.Segment, Video, Allan, "360p/seg-00001.m4s"));
    }

    [Fact]
    public void A_playlist_principal_vence_em_uma_hora_e_o_resto_em_doze()
    {
        var selos = Criar();
        var principal = selos.Seal(PlaybackSealKind.Master, Video, Allan);
        var segmento = selos.Seal(PlaybackSealKind.Segment, Video, Allan, "360p/seg-00001.m4s");

        _relogio.Advance(PlaybackSeals.MasterLifetime + TimeSpan.FromSeconds(1));

        Assert.Null(selos.Open(principal, PlaybackSealKind.Master, Allan));
        Assert.NotNull(selos.Open(segmento, PlaybackSealKind.Segment, Allan));

        _relogio.Advance(PlaybackSeals.PlaybackLifetime);

        Assert.Null(selos.Open(segmento, PlaybackSealKind.Segment, Allan));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Selo_invalido_nao_abre(string? selo)
    {
        Assert.Null(Criar().Open(selo, PlaybackSealKind.Segment, Allan));
    }

    [Fact]
    public void Selo_alterado_ou_de_outro_segredo_nao_abre()
    {
        var selo = Criar().Seal(PlaybackSealKind.Segment, Video, Allan, "360p/seg-00001.m4s");
        var trocado = (selo[10] == 'A' ? 'B' : 'A') + "";
        var alterado = selo[..10] + trocado + selo[11..];

        Assert.Null(Criar().Open(alterado, PlaybackSealKind.Segment, Allan));
        Assert.Null(Criar("outro-segredo").Open(selo, PlaybackSealKind.Segment, Allan));
    }
}

public class SegmentRateLimiterTests
{
    private static readonly Guid Video = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private SegmentRateLimiter Criar(int folga = 5, double porSegundo = 1) =>
        new(Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            TokenPepper = "t", IpHashPepper = "i", SegmentBurst = folga, SegmentsPerSecond = porSegundo
        }), _relogio, NullLogger<SegmentRateLimiter>.Instance);

    [Fact]
    public void A_folga_passa_de_uma_vez_e_depois_a_velocidade_e_limitada()
    {
        var limite = Criar(folga: 5, porSegundo: 1);

        for (var i = 0; i < 5; i++)
            Assert.True(limite.TryAcquire("u1", Video));

        Assert.False(limite.TryAcquire("u1", Video));

        // Um segundo depois, cabe mais um segmento.
        _relogio.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limite.TryAcquire("u1", Video));
        Assert.False(limite.TryAcquire("u1", Video));
    }

    [Fact]
    public void Assistir_no_ritmo_do_video_nunca_esbarra_no_limite()
    {
        var limite = Criar(folga: 5, porSegundo: 1);

        // Um segmento de 4 s a cada 4 s, por duas horas.
        for (var i = 0; i < 1800; i++)
        {
            Assert.True(limite.TryAcquire("u1", Video));
            _relogio.Advance(TimeSpan.FromSeconds(4));
        }
    }

    [Fact]
    public void Cada_pessoa_e_cada_video_tem_o_proprio_limite()
    {
        var limite = Criar(folga: 1);

        Assert.True(limite.TryAcquire("u1", Video));
        Assert.False(limite.TryAcquire("u1", Video));
        Assert.True(limite.TryAcquire("u2", Video));
        Assert.True(limite.TryAcquire("u1", Guid.CreateVersion7()));
    }

    [Fact]
    public void A_folga_nao_acumula_alem_do_teto()
    {
        var limite = Criar(folga: 3, porSegundo: 1);

        _relogio.Advance(TimeSpan.FromHours(1));

        for (var i = 0; i < 3; i++)
            Assert.True(limite.TryAcquire("u1", Video));

        Assert.False(limite.TryAcquire("u1", Video));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 0)]
    public void Zero_desliga_o_limite(int folga, double porSegundo)
    {
        var limite = Criar(folga, porSegundo);

        for (var i = 0; i < 100; i++)
            Assert.True(limite.TryAcquire("u1", Video));
    }
}
