// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Security;
using OpenTube.TestSupport;
using OpenTube.Web.Tests.Support;

namespace OpenTube.Web.Tests.Paginas;

/// <summary>
/// Registro das ações administrativas: é o que sustenta a resposta a "quem liberou esse
/// vídeo?" meses depois.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuditoriaTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string Admin = "allan@barcelos.dev";

    private OpenTubeWebFactory _app = default!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _app = new OpenTubeWebFactory(postgres, minio, Admin);

        using var cliente = _app.CreateBrowser();
        await cliente.GetAsync("/health");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task EntrarComoAdminAsync(HttpClient cliente)
    {
        _app.Emails.Clear();

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, "/sign-in", "/sign-in/code", new Dictionary<string, string> { ["email"] = Admin });

        await FormularioHelpers.EnviarFormularioAsync(
            cliente,
            $"/sign-in?email={Uri.EscapeDataString(Admin)}&enviado=1",
            "/sign-in/verify",
            new Dictionary<string, string> { ["email"] = Admin, ["codigo"] = _app.Emails.LastCode() });
    }

    [Fact]
    public async Task Liberar_um_video_fica_registrado_com_quem_fez()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/save",
            new Dictionary<string, string>
            {
                ["titulo"] = "Plano Confidencial",
                ["descricao"] = "",
                ["etiquetas"] = "",
                ["visibilidade"] = ((int)VideoVisibility.Public).ToString()
            });

        await using var db = postgres.CreateContext();
        var registro = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.VideoAlterado);

        Assert.Equal(Admin, registro.ActorEmail);
        Assert.Equal(video.Id, registro.EntityId);
        Assert.Contains("Public", registro.Summary);
    }

    [Fact]
    public async Task O_convite_fica_registrado_com_quem_recebeu_e_a_validade()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = "convidado@empresa.com",
                ["validade"] = "dias",
                ["valorDaValidade"] = "30"
            });

        await using var db = postgres.CreateContext();
        var registro = await db.AuditEntries.SingleAsync(e => e.Action == AuditActions.AcessoConcedido);

        Assert.Contains("convidado@empresa.com", registro.Summary);
        Assert.Contains("30 days from the first visit", registro.Summary);
    }

    [Fact]
    public async Task A_revogacao_fica_registrada()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Restricted);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", "/admin/access/invite",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString(),
                ["emails"] = "convidado@empresa.com",
                ["validade"] = "sempre",
                ["valorDaValidade"] = ""
            });

        Guid concessaoId;
        await using (var db = postgres.CreateContext())
            concessaoId = (await db.AccessGrants.SingleAsync()).Id;

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/access/{concessaoId}/revoke",
            new Dictionary<string, string>
            {
                ["alvoTipo"] = ((int)GrantTargetType.Video).ToString(),
                ["alvoId"] = video.Id.ToString()
            });

        await using var leitura = postgres.CreateContext();
        var registro = await leitura.AuditEntries.SingleAsync(e => e.Action == AuditActions.AcessoRevogado);

        // O registro diz de quem era o acesso e sobre qual vídeo, não só "revogado".
        Assert.Contains("convidado@empresa.com", registro.Summary);
        Assert.Contains("Plano Confidencial", registro.Summary);

        // E a página não mostra mais o código cru da ação.
        var html = await cliente.GetStringAsync("/admin/audit");
        Assert.DoesNotContain(AuditActions.AcessoRevogado, html);
    }

    [Fact]
    public async Task A_pagina_de_auditoria_lista_e_filtra()
    {
        using var storage = minio.CreateStorage();
        var video = await AcervoDeTeste.PublicarAsync(postgres, storage, "Plano Confidencial", VideoVisibility.Private);

        using var cliente = _app.CreateBrowser();
        await EntrarComoAdminAsync(cliente);

        await FormularioHelpers.EnviarFormularioAsync(
            cliente, $"/admin/videos/{video.Id}", $"/admin/videos/{video.Id}/delete", new Dictionary<string, string>());

        Assert.Contains("Video 'Plano Confidencial' deleted.", WebUtility.HtmlDecode(await cliente.GetStringAsync("/admin/audit")));
        Assert.Contains("Video 'Plano Confidencial' deleted.", WebUtility.HtmlDecode(await cliente.GetStringAsync("/admin/audit?type=video")));
        Assert.Contains("No actions recorded", await cliente.GetStringAsync("/admin/audit?type=domain"));
        Assert.Contains("No actions recorded", await cliente.GetStringAsync("/admin/audit?who=outra@pessoa.com"));
    }

    [Fact]
    public async Task Convidado_nao_alcanca_a_auditoria()
    {
        using var cliente = _app.CreateBrowser();

        var resposta = await cliente.GetAsync("/admin/audit");

        Assert.Equal(HttpStatusCode.Found, resposta.StatusCode);
        Assert.Contains("/sign-in", resposta.Headers.Location!.ToString());
    }
}
