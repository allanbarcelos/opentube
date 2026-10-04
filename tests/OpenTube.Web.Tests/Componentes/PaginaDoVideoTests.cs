// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Reflection;
using Microsoft.AspNetCore.Components;
using OpenTube.Infrastructure.Playback;
using OpenTube.Web.Components.Pages;

namespace OpenTube.Web.Tests.Componentes;

/// <summary>
/// A página do vídeo só exibe: quem reúne o que ela mostra é o WatchPageService. Se ela voltar
/// a injetar serviço por serviço, volta também a coordenar consultas que não são dela.
/// </summary>
public class PaginaDoVideoTests
{
    [Fact]
    public void A_pagina_do_video_recebe_a_pagina_montada_e_nao_os_servicos()
    {
        var injetados = typeof(Watch)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(p => p.PropertyType)
            .ToList();

        Assert.Contains(typeof(WatchPageService), injetados);
        Assert.DoesNotContain(typeof(VideoCatalog), injetados);
        Assert.DoesNotContain(typeof(PlaybackTokens), injetados);

        // O serviço da página, quem assiste, a navegação e o texto da interface.
        Assert.True(injetados.Count <= 4, $"A página injeta {injetados.Count} serviços: {string.Join(", ", injetados.Select(t => t.Name))}");
    }
}
