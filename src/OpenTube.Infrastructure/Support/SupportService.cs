// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Access;
using OpenTube.Infrastructure.Email;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Support;

/// <summary>Uma conversa com o que a interface precisa mostrar em torno dela.</summary>
/// <param name="Thread">A conversa.</param>
/// <param name="VideoTitle">Título do vídeo.</param>
/// <param name="VideoSlug">Endereço do vídeo.</param>
/// <param name="UserEmail">Endereço de quem abriu.</param>
public sealed record SupportThreadView(SupportThread Thread, string VideoTitle, string VideoSlug, string UserEmail);

/// <summary>
/// Conversas de suporte por vídeo. Não são comentários públicos: só o autor e os
/// administradores enxergam, e a verificação disso acontece aqui, e não na tela.
/// </summary>
public class SupportService(
    OpenTubeDbContext db,
    AccessService acesso,
    IEmailSender email,
    IOptions<SecurityOptions> options,
    TimeProvider clock,
    ILogger<SupportService> logger)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>
    /// Abre uma conversa. Só quem consegue assistir ao vídeo pode falar sobre ele — do
    /// contrário, a conversa viraria um canal para sondar o acervo.
    /// </summary>
    public async Task<SupportThread> OpenAsync(
        Guid videoId,
        Viewer viewer,
        string message,
        double? timestampSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (viewer.UserId is not { } userId)
            throw new InvalidOperationException("Sign in to start a conversation.");

        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, cancellationToken)
            ?? throw new InvalidOperationException("Video not found");

        if (!(await acesso.EvaluateAsync(viewer, video, cancellationToken)).Allowed)
            throw new InvalidOperationException("Video not found");

        var conversa = SupportThread.Open(videoId, userId, message, clock.GetUtcNow(), timestampSeconds);

        db.SupportThreads.Add(conversa);
        await db.SaveChangesAsync(cancellationToken);

        await AvisarAdministradoresAsync(conversa, video.Title, viewer.Email!, message, cancellationToken);

        logger.LogInformation("Conversa {ConversaId} aberta no vídeo {VideoId}", conversa.Id, videoId);

        return conversa;
    }

    /// <summary>Acrescenta uma mensagem, avisando o outro lado por email.</summary>
    public async Task<SupportThread> ReplyAsync(
        Guid threadId,
        Viewer viewer,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var conversa = await CarregarAsync(threadId, viewer, cancellationToken);

        if (viewer.UserId is not { } authorId)
            throw new InvalidOperationException("Sign in to reply.");

        var mensagem = conversa.Reply(authorId, message, viewer.IsAdmin, clock.GetUtcNow());

        // A mensagem precisa ser registrada explicitamente: como a entidade já nasce com a
        // chave preenchida, acrescentá-la à coleção de uma conversa já carregada faria o EF
        // entendê-la como alteração de uma linha existente, e não como inserção.
        db.SupportMessages.Add(mensagem);

        await db.SaveChangesAsync(cancellationToken);

        var titulo = await TituloDoVideoAsync(conversa.VideoId, cancellationToken);

        if (viewer.IsAdmin)
            await AvisarAutorAsync(conversa, titulo, message, cancellationToken);
        else
            await AvisarAdministradoresAsync(conversa, titulo, viewer.Email!, message, cancellationToken);

        return conversa;
    }

    public async Task CloseAsync(Guid threadId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        var conversa = await CarregarAsync(threadId, viewer, cancellationToken);

        conversa.Close(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReopenAsync(Guid threadId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        var conversa = await CarregarAsync(threadId, viewer, cancellationToken);

        conversa.Reopen(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Marca a conversa como lida por quem está olhando.</summary>
    public async Task MarkReadAsync(Guid threadId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        var conversa = await CarregarAsync(threadId, viewer, cancellationToken);
        var agora = clock.GetUtcNow();

        if (viewer.IsAdmin)
            conversa.MarkReadByAdmin(agora);
        else
            conversa.MarkReadByUser(agora);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Uma conversa, se quem pediu puder vê-la.</summary>
    public async Task<SupportThread?> FindAsync(Guid threadId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var conversa = await db.SupportThreads
            .Include(t => t.Messages)
            .FirstOrDefaultAsync(t => t.Id == threadId, cancellationToken);

        return conversa?.IsVisibleTo(viewer.UserId, viewer.IsAdmin) == true ? conversa : null;
    }

    /// <summary>Conversas de uma pessoa sobre um vídeo.</summary>
    public async Task<IReadOnlyList<SupportThread>> ForVideoAsync(
        Guid videoId, Viewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var consulta = db.SupportThreads
            .Include(t => t.Messages)
            .Where(t => t.VideoId == videoId);

        // O filtro por autor vai na consulta: trazer tudo e esconder na tela deixaria o
        // conteúdo alheio passar por qualquer descuido de apresentação.
        if (!viewer.IsAdmin)
        {
            if (viewer.UserId is not { } userId)
                return [];

            consulta = consulta.Where(t => t.UserId == userId);
        }

        return await consulta.OrderByDescending(t => t.LastMessageAt).ToListAsync(cancellationToken);
    }

    /// <summary>Fila da administração, com o contexto de cada conversa.</summary>
    public async Task<IReadOnlyList<SupportThreadView>> QueueAsync(
        SupportStatus? status = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var consulta = db.SupportThreads.Include(t => t.Messages).AsQueryable();

        if (status is { } filtro)
            consulta = consulta.Where(t => t.Status == filtro);

        var conversas = await consulta
            .OrderByDescending(t => t.LastMessageAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (conversas.Count == 0)
            return [];

        var videoIds = conversas.Select(t => t.VideoId).Distinct().ToList();
        var userIds = conversas.Select(t => t.UserId).Distinct().ToList();

        var videos = await db.Videos
            .Where(v => videoIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Title, v.Slug })
            .ToDictionaryAsync(v => v.Id, cancellationToken);

        var usuarios = await db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        return [.. conversas.Select(t => new SupportThreadView(
            t,
            videos.TryGetValue(t.VideoId, out var video) ? video.Title : LocalText.Get("Removed video"),
            videos.TryGetValue(t.VideoId, out var endereco) ? endereco.Slug : string.Empty,
            usuarios.TryGetValue(t.UserId, out var usuario) ? usuario.Email : LocalText.Get("unknown")))];
    }

    /// <summary>Quantas conversas aguardam resposta.</summary>
    public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) =>
        db.SupportThreads.CountAsync(t => t.Status == SupportStatus.Open, cancellationToken);

    private async Task<SupportThread> CarregarAsync(Guid threadId, Viewer viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var conversa = await db.SupportThreads
            .Include(t => t.Messages)
            .FirstOrDefaultAsync(t => t.Id == threadId, cancellationToken);

        // Conversa inexistente e conversa alheia respondem igual: a diferença já diria que
        // ela existe.
        if (conversa is null || !conversa.IsVisibleTo(viewer.UserId, viewer.IsAdmin))
            throw new InvalidOperationException("Conversation not found.");

        return conversa;
    }

    private Task<string> TituloDoVideoAsync(Guid videoId, CancellationToken cancellationToken) =>
        db.Videos.Where(v => v.Id == videoId).Select(v => v.Title).FirstOrDefaultAsync(cancellationToken)!;

    private async Task AvisarAdministradoresAsync(
        SupportThread conversa, string? videoTitle, string autor, string mensagem, CancellationToken cancellationToken)
    {
        var administradores = await db.Users
            .Where(u => u.IsAdmin && u.DisabledAt == null)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken);

        var endereco = $"{_options.PublicUrl.TrimEnd('/')}/admin/support/{conversa.Id}";

        foreach (var destinatario in administradores)
        {
            await email.SendAsync(EmailTemplates.SupportForAdmin(
                destinatario, autor, videoTitle ?? LocalText.Get("A video"), mensagem, endereco), cancellationToken);
        }
    }

    private async Task AvisarAutorAsync(
        SupportThread conversa, string? videoTitle, string mensagem, CancellationToken cancellationToken)
    {
        var destinatario = await db.Users
            .Where(u => u.Id == conversa.UserId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

        if (destinatario is null)
            return;

        var video = await db.Videos
            .Where(v => v.Id == conversa.VideoId)
            .Select(v => v.Slug)
            .FirstOrDefaultAsync(cancellationToken);

        var endereco = $"{_options.PublicUrl.TrimEnd('/')}/watch/{video}";

        await email.SendAsync(EmailTemplates.SupportForUser(
            destinatario, videoTitle ?? LocalText.Get("A video"), mensagem, endereco), cancellationToken);
    }
}
