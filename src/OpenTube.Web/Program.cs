// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using OpenTube.Infrastructure;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;
using OpenTube.Web.Components;
using OpenTube.Web.Endpoints;
using OpenTube.Web.Seguranca;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("OPENTUBE_");
// No Swarm os segredos chegam como arquivos em /run/secrets, nunca como variável de
// ambiente. Fora de produção o diretório não existe e esta fonte fica inativa.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// Sem isto o codificador padrão transforma todo acento em entidade numérica, o que incha
// cada página de um site em português e atrapalha qualquer inspeção do HTML.
builder.Services.Configure<WebEncoderOptions>(opcoes =>
    opcoes.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddLocalization();
builder.Services.AddSingleton<UiText>();

var idiomas = new[] { "en", "pt", "fr" };
builder.Services.Configure<RequestLocalizationOptions>(opcoes =>
{
    opcoes.SetDefaultCulture("en")
        .AddSupportedCultures(idiomas)
        .AddSupportedUICultures(idiomas);

    // O cookie da escolha explícita vem primeiro. Sem ele, vale o idioma do navegador.
    // Se nenhum dos dois servir, a base é o inglês.
    opcoes.RequestCultureProviders =
    [
        new CookieRequestCultureProvider { CookieName = Idioma.Cookie },
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
});

builder.Services.AddOpenTubeInfrastructure(builder.Configuration);
builder.Services.AddSessionAuthentication();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentViewer>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ShareLinkFlash>();
builder.Services.AddHealthChecks();
builder.Services.AddReverseProxySupport(builder.Configuration);
builder.Services.AddRateLimiter(opcoes =>
{
    opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    AnalyticsEndpoints.AddRateLimit(opcoes);
});

var app = builder.Build();

// Primeiro de tudo: o resto do pipeline — limites por origem, cookies seguros, HSTS — precisa
// enxergar o endereço e o protocolo de quem acessa, e não os do servidor da frente.
app.UseForwardedHeaders();
app.UsePoliticaDeConteudo();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();

// A proteção contra falsificação precisa vir depois da autenticação: o token é vinculado à
// identidade de quem carregou a página, e validá-lo antes faria todo formulário de pessoa
// autenticada ser recusado.
app.UseAntiforgery();
app.UseRateLimiter();

// Arquivos do wwwroot pelo manifesto da compilação: o @Assets das páginas ganha a impressão do
// conteúdo no endereço (guardado para sempre pelo navegador), e o endereço sem ela é revalidado
// a cada pedido. Com UseStaticFiles o navegador guardava uma versão antiga do JavaScript por
// conta própria depois de uma atualização.
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Todo endereço que altera alguma coisa exige o token antifalsificação, inclusive os que não
// leem campos de formulário. Ficam de fora só a coleta do player (enviada por sendBeacon, que
// não leva cabeçalho próprio) e a autorização de segmento, que é GET.
var rotas = app.MapGroup(string.Empty).ExigirAntifalsificacao();

rotas.MapPost("/language", (HttpContext contexto, [FromForm] string? idioma, [FromForm] string? voltar) =>
{
    var escolhido = idioma is "en" or "pt" or "fr" ? idioma : "en";

    contexto.Response.Cookies.Append(Idioma.Cookie, CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(escolhido)), new CookieOptions
    {
        MaxAge = TimeSpan.FromDays(365),
        IsEssential = true,
        HttpOnly = true,
        Secure = contexto.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/"
    });

    return Results.Redirect(Retorno.EhLocal(voltar) && !voltar!.Contains('\\') ? voltar : "/");
});

rotas.MapAuthEndpoints();
rotas.MapPlaybackEndpoints();
rotas.MapAdminEndpoints();
rotas.MapShareEndpoints();
rotas.MapCollectionEndpoints();
rotas.MapCollectionFavoriteEndpoints();
rotas.MapListingPreferenceEndpoints();
rotas.MapVideoFavoriteEndpoints();
rotas.MapAccessEndpoints();
rotas.MapExportEndpoints();
rotas.MapSupportEndpoints();
rotas.MapCaptionEndpoints();
rotas.MapRatingEndpoints();
rotas.MapWatermarkEndpoints();
app.MapAnalyticsEndpoints();
app.MapSegmentAuthorization();
app.MapHealthChecks("/health");

await PrepararAsync(app);

await app.RunAsync();

/// <summary>
/// Deixa o ambiente pronto antes de atender: esquema aplicado, buckets criados e
/// administradores promovidos. Sem isso, a primeira requisição falharia numa instalação nova.
/// </summary>
static async Task PrepararAsync(WebApplication app)
{
    using var escopo = app.Services.CreateScope();
    var servicos = escopo.ServiceProvider;

    var db = servicos.GetRequiredService<OpenTubeDbContext>();
    await db.Database.MigrateAsync();

    await servicos.GetRequiredService<IVideoStorage>().EnsureBucketsAsync();
    await servicos.GetRequiredService<AdminSeeder>().EnsureAdminsAsync();
}

/// <summary>Cookie da escolha de idioma. O valor segue o formato do provedor de cultura.</summary>
static class Idioma
{
    public const string Cookie = "opentube.idioma";
}

/// <summary>Exposto para que a suíte de testes possa subir a aplicação em memória.</summary>
public partial class Program;
