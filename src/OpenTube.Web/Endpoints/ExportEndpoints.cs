using Microsoft.EntityFrameworkCore;
using OpenTube.Infrastructure.Analytics;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Shared.Analytics;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Exportação dos relatórios. O destino real desses arquivos é uma planilha, e é por isso que
/// eles saem como CSV em vez de ficarem presos à tela.
/// </summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder rotas)
    {
        var grupo = rotas.MapGroup("/admin/exportar").RequireAuthorization(Policies.Administrator);

        grupo.MapGet("/videos/{videoId:guid}/espectadores.csv", async (
            Guid videoId,
            AnalyticsQueries consultas,
            OpenTubeDbContext db,
            CancellationToken cancellationToken) =>
        {
            var titulo = await db.Videos
                .Where(v => v.Id == videoId)
                .Select(v => v.Title)
                .FirstOrDefaultAsync(cancellationToken);

            if (titulo is null)
                return Results.NotFound();

            var espectadores = await consultas.ViewersAsync(videoId, 5000, cancellationToken);

            var csv = CsvWriter.Build(
                ["Pessoa", "Sessões", "Segundos assistidos", "Chegou ao fim", "Primeira vez", "Última vez", "Aparelho"],
                espectadores.Select(e => new object?[]
                {
                    e.DisplayName, e.Sessions, e.WatchSeconds, e.Completed, e.FirstAt, e.LastAt, e.Device
                }));

            return Arquivo(csv, $"espectadores-{Nome(titulo)}.csv");
        });

        grupo.MapGet("/pessoas/{userId:guid}/atividade.csv", async (
            Guid userId,
            AnalyticsQueries consultas,
            OpenTubeDbContext db,
            CancellationToken cancellationToken) =>
        {
            var email = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(cancellationToken);

            if (email is null)
                return Results.NotFound();

            var atividade = await consultas.ViewerTimelineAsync(userId, 5000, cancellationToken);

            var csv = CsvWriter.Build(
                ["Vídeo", "Quando", "Segundos assistidos", "Duração", "Fração assistida", "Chegou ao fim", "Aparelho"],
                atividade.Select(a => new object?[]
                {
                    a.Title, a.At, a.WatchSeconds, a.DurationSeconds, Math.Round(a.Coverage * 100, 1), a.Completed, a.Device
                }));

            return Arquivo(csv, $"atividade-{Nome(email)}.csv");
        });

        return rotas;
    }

    private static IResult Arquivo(string csv, string nome) =>
        Results.File(CsvWriter.ToBytes(csv), CsvWriter.ContentType, nome);

    /// <summary>
    /// Nome de arquivo previsível a partir do título. O título vem de quem envia o vídeo e
    /// não pode influenciar o cabeçalho da resposta.
    /// </summary>
    private static string Nome(string valor)
    {
        var limpo = OpenTube.Domain.ValueObjects.Slug.From(valor);

        return limpo.Length == 0 ? "relatorio" : limpo;
    }
}
