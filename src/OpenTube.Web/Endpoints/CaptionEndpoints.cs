// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.AspNetCore.Mvc;
using OpenTube.Domain.Captions;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Playback;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Services;
using OpenTube.Infrastructure.Storage;
using OpenTube.Web.Auth;

namespace OpenTube.Web.Endpoints;

/// <summary>Legendas: envio pela administração e entrega a quem tem acesso ao vídeo.</summary>
public static class CaptionEndpoints
{
    public static IEndpointRouteBuilder MapCaptionEndpoints(this IEndpointRouteBuilder rotas)
    {
        var administracao = rotas.MapGroup("/admin/videos/{videoId:guid}/captions")
            .RequireAuthorization(Policies.Administrator);

        // Envio de arquivo WebVTT ou SRT. Um idioma que já existe tem o conteúdo substituído.
        administracao.MapPost("/", async (
            Guid videoId,
            IFormFile? arquivo,
            [FromForm] string idioma,
            [FromForm] string? rotulo,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (arquivo is null || arquivo.Length == 0)
                    throw new InvalidOperationException("Choose a caption file.");

                if (arquivo.Length > CaptionService.MaxSizeBytes)
                    throw new InvalidOperationException("The caption file is too large.");

                using var leitor = new StreamReader(arquivo.OpenReadStream());
                var conteudo = await leitor.ReadToEndAsync(cancellationToken);

                var legenda = await legendas.UploadAsync(videoId, idioma, rotulo, conteudo, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoAlterado, AuditEntities.Video, videoId,
                    LocalText.Format("Caption {0} uploaded", legenda.Language!), cancellationToken);

                return Results.Redirect(Aba(videoId, "legenda=enviada"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or CaptionFormatException)
            {
                return Results.Redirect(Aba(videoId, "erro=" + Uri.EscapeDataString(Mensagem(e))));
            }
        });

        administracao.MapPost("/transcribe", async (
            Guid videoId,
            [FromForm] string idioma,
            [FromForm] string? rotulo,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await legendas.RequestTranscriptionAsync(videoId, idioma, rotulo, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoReprocessado, AuditEntities.Video, videoId,
                    LocalText.Format("Automatic transcription requested ({0})", CaptionLanguage.Normalize(idioma)), cancellationToken);

                return Results.Redirect(Aba(videoId, "legenda=pedida"));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect(Aba(videoId, "erro=" + Uri.EscapeDataString(Mensagem(e))));
            }
        });

        // Legenda vazia, escrita no editor: o caminho sem transcrição automática.
        administracao.MapPost("/new", async (
            Guid videoId,
            [FromForm] string idioma,
            [FromForm] string? rotulo,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var legenda = await legendas.CreateEmptyAsync(videoId, idioma, rotulo, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoAlterado, AuditEntities.Video, videoId,
                    LocalText.Format("Caption {0} created", legenda.Language!), cancellationToken);

                return Results.Redirect($"/admin/videos/{videoId}/captions/{legenda.Id}/edit");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect(Aba(videoId, "erro=" + Uri.EscapeDataString(Mensagem(e))));
            }
        });

        administracao.MapPost("/{assetId:guid}/delete", async (
            Guid videoId,
            Guid assetId,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var legenda = await legendas.FindAsync(videoId, assetId, cancellationToken);
                await legendas.DeleteAsync(videoId, assetId, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoAlterado, AuditEntities.Video, videoId,
                    LocalText.Format("Caption {0} removed", legenda?.Language ?? "?"), cancellationToken);

                return Results.Redirect(Aba(videoId, "legenda=removida"));
            }
            catch (InvalidOperationException e)
            {
                return Results.Redirect(Aba(videoId, "erro=" + Uri.EscapeDataString(Mensagem(e))));
            }
        });

        // Gravação do editor. Responde em JSON para o editor mostrar o resultado sem sair da
        // página: um erro de validação não pode custar o que a pessoa acabou de corrigir.
        administracao.MapPost("/{assetId:guid}/content", async (
            Guid videoId,
            Guid assetId,
            [FromBody] ConteudoDaLegenda pedido,
            CaptionService legendas,
            HttpContext contexto,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var legenda = await legendas.SaveEditAsync(videoId, assetId, pedido.Conteudo ?? string.Empty, cancellationToken);

                await contexto.RegistrarAsync(
                    AuditActions.VideoAlterado, AuditEntities.Video, videoId,
                    LocalText.Format("Caption {0} edited", legenda.Language!), cancellationToken);

                return Results.Ok(new { salvo = true, trechos = CaptionDocument.Parse(pedido.Conteudo!).Cues.Count });
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or CaptionFormatException)
            {
                return Results.BadRequest(new { erro = Mensagem(e) });
            }
        });

        // Arquivo para corrigir fora do sistema, com nome que diz de qual vídeo e idioma é.
        administracao.MapGet("/{assetId:guid}/download", async (
            Guid videoId,
            Guid assetId,
            CaptionService legendas,
            OpenTube.Infrastructure.Services.AdminVideoService videos,
            CancellationToken cancellationToken) =>
        {
            var legenda = await legendas.ReadAsync(assetId, cancellationToken);

            if (legenda is null || legenda.Value.Asset.VideoId != videoId)
                return Results.NotFound();

            var video = await videos.FindAsync(videoId, cancellationToken);
            var nome = $"{video?.Slug ?? videoId.ToString("n")}.{legenda.Value.Asset.Language}.vtt";

            return Results.File(System.Text.Encoding.UTF8.GetBytes(legenda.Value.Content), MediaTypes.WebVtt, nome);
        });

        // Situação das legendas, consultada pela página enquanto alguma está processando.
        administracao.MapGet("/status", async (
            Guid videoId,
            CaptionService legendas,
            CancellationToken cancellationToken) =>
        {
            var lista = await legendas.StatusAsync(videoId, cancellationToken);

            return Results.Ok(lista.Select(l => new { id = l.Id, idioma = l.Language, status = l.Status.ToString().ToLowerInvariant() }));
        });

        // A legenda segue a mesma regra de acesso do vídeo: ela é parte do conteúdo, e o
        // texto falado costuma revelar tanto quanto a imagem.
        rotas.MapGet("/api/videos/{videoId:guid}/captions/{assetId:guid}.vtt", async (
            Guid videoId,
            Guid assetId,
            CaptionService legendas,
            PlaybackService playback,
            CurrentViewer espectadores,
            CancellationToken cancellationToken) =>
        {
            var espectador = await espectadores.GetAsync(cancellationToken);

            if (!await playback.CanReceiveMediaAsync(videoId, espectador, objectKey: null, cancellationToken))
                return Results.NotFound();

            var legenda = await legendas.ReadAsync(assetId, cancellationToken);

            return legenda is null || legenda.Value.Asset.VideoId != videoId
                ? Results.NotFound()
                : Results.Text(legenda.Value.Content, MediaTypes.WebVtt);
        });

        return rotas;
    }

    private static string Aba(Guid videoId, string parametros) => $"/admin/videos/{videoId}?tab=captions&{parametros}";

    /// <summary>Mensagem traduzida; a de formato leva a linha ou o trecho com problema.</summary>
    private static string Mensagem(Exception e) => e is CaptionFormatException formato
        ? LocalText.Format(formato.Key, [.. formato.Args])
        : LocalText.Get(e.Message);
}

/// <summary>Legenda editada, já em WebVTT.</summary>
/// <param name="Conteudo">Conteúdo completo.</param>
public sealed record ConteudoDaLegenda(string? Conteudo);
