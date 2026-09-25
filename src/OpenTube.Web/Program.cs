using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using OpenTube.Infrastructure;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;
using OpenTube.Web.Components;
using OpenTube.Web.Endpoints;

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/erro", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// A proteção contra falsificação precisa vir depois da autenticação: o token é vinculado à
// identidade de quem carregou a página, e validá-lo antes faria todo formulário de pessoa
// autenticada ser recusado.
app.UseAntiforgery();
app.UseRateLimiter();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAuthEndpoints();
app.MapPlaybackEndpoints();
app.MapAdminEndpoints();
app.MapShareEndpoints();
app.MapCollectionEndpoints();
app.MapAccessEndpoints();
app.MapDomainEndpoints();
app.MapAnalyticsEndpoints();
app.MapExportEndpoints();
app.MapSupportEndpoints();
app.MapCaptionEndpoints();
app.MapSegmentAuthorization();
app.MapHealthChecks("/saude");

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

/// <summary>Exposto para que a suíte de testes possa subir a aplicação em memória.</summary>
public partial class Program;
