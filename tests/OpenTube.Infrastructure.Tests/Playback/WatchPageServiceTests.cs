// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Domain.Access;
using OpenTube.Domain.Media;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Playback;

namespace OpenTube.Infrastructure.Tests.Playback;

/// <summary>As partes da página do vídeo que não dependem do banco.</summary>
public class WatchPageServiceTests
{
    private static readonly Viewer Allan = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("allan@barcelos.dev"));

    [Fact]
    public void Sem_capitulos_ou_sem_duracao_a_barra_nao_tem_trechos()
    {
        Assert.Empty(WatchPageService.Segments([], TimeSpan.FromMinutes(10)));
        Assert.Empty(WatchPageService.Segments([new Chapter(0, "Abertura")], TimeSpan.Zero));
    }

    [Fact]
    public void Cada_capitulo_vira_um_trecho_ate_o_seguinte_e_o_ultimo_ate_o_fim()
    {
        Chapter[] sumario = [new(0, "Abertura"), new(60, "Números"), new(300, "Perguntas")];

        var trechos = WatchPageService.Segments(sumario, TimeSpan.FromMinutes(10));

        Assert.Equal(
            [new ProgressSegment(0, 60, "Abertura"), new ProgressSegment(60, 300, "Números"), new ProgressSegment(300, 600, "Perguntas")],
            trechos);
    }

    [Fact]
    public void Antes_do_primeiro_capitulo_fica_um_trecho_sem_titulo()
    {
        var trechos = WatchPageService.Segments([new Chapter(30, "Começo de fato")], TimeSpan.FromMinutes(2));

        Assert.Equal([new ProgressSegment(0, 30, ""), new ProgressSegment(30, 120, "Começo de fato")], trechos);
    }

    [Fact]
    public void A_marca_dagua_identifica_quem_assiste()
    {
        var link = Guid.CreateVersion7();

        Assert.Equal("allan@barcelos.dev", WatchPageService.WatermarkText(Allan, enabled: true));
        Assert.Equal($"link {link.ToString("n")[..8]}", WatchPageService.WatermarkText(Viewer.WithLink(link), enabled: true));

        // Anônimo num vídeo público: não há de quem seria o vazamento.
        Assert.Null(WatchPageService.WatermarkText(Viewer.Anonymous, enabled: true));
    }

    [Fact]
    public void Com_a_marca_desligada_ninguem_e_identificado()
    {
        Assert.Null(WatchPageService.WatermarkText(Allan, enabled: false));
        Assert.Null(WatchPageService.WatermarkText(Viewer.WithLink(Guid.CreateVersion7()), enabled: false));
    }
}
