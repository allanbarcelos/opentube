// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Storage;
using OpenTube.Worker.Media;

namespace OpenTube.Mock;

/// <summary>
/// Grava o catálogo de exemplo no banco e no storage de desenvolvimento. O clipe é gerado
/// uma vez e publicado em cada vídeo; a capa muda. Uma nova execução substitui só os slugs
/// que começam com <c>mock-</c>.
/// </summary>
internal sealed class Gerador(
    OpenTubeDbContext db,
    IVideoStorage storage,
    AdminSeeder admins,
    TranscodePipeline pipeline,
    IProcessRunner processos,
    IOptions<MediaToolOptions> media,
    IOptions<SecurityOptions> seguranca,
    TimeProvider clock,
    ILogger<Gerador> logger)
{
    private const int LarguraDoClipe = 640;
    private const int AlturaDoClipe = 360;
    private const int DuracaoSegundos = 6;

    private Plano _plano = null!;

    public async Task ExecutarAsync(Plano plano, CancellationToken cancellationToken = default)
    {
        _plano = plano;
        Console.WriteLine(plano.Pedido.Resumo());

        await MigrarAsync(cancellationToken);
        await storage.EnsureBucketsAsync(cancellationToken);

        var admin = await GarantirAdministradorAsync(cancellationToken);

        if (plano.Videos.Count == 0)
        {
            await ApagarAnteriorAsync(cancellationToken);
            Imprimir(admin.Email);
            return;
        }

        var agora = clock.GetUtcNow();

        Console.WriteLine("Gerando um clipe de teste e transcodificando (só a versão 360p)...");

        var trabalho = Directory.CreateTempSubdirectory("opentube-mock-");

        try
        {
            var midia = await PrepararMidiaAsync(trabalho.FullName, cancellationToken);
            await SubstituirAsync(admin, midia, agora, cancellationToken);
        }
        finally
        {
            try
            {
                trabalho.Delete(recursive: true);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Não foi possível apagar a pasta temporária {Pasta}", trabalho.FullName);
            }
        }

        Imprimir(admin.Email);
    }

    private async Task MigrarAsync(CancellationToken cancellationToken)
    {
        const int tentativas = 30;

        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                await db.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (Exception e) when (tentativa < tentativas && BancoIndisponivel(e))
            {
                logger.LogWarning("Banco ainda indisponível ({Tentativa}/{Tentativas})", tentativa, tentativas);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static bool BancoIndisponivel(Exception erro)
    {
        for (var atual = erro; atual is not null; atual = atual.InnerException)
        {
            if (atual is PostgresException)
                return false;

            if (atual is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
                return true;
        }

        return false;
    }

    private async Task<User> GarantirAdministradorAsync(CancellationToken cancellationToken)
    {
        await admins.EnsureAdminsAsync(cancellationToken);

        string? email = null;

        foreach (var bruto in seguranca.Value.AdminEmails)
        {
            if (EmailAddress.TryParse(bruto, out var endereco))
            {
                email = endereco.Value;
                break;
            }
        }

        if (email is null)
        {
            throw new InvalidOperationException(
                "Nenhum administrador configurado. Defina OPENTUBE_ADMIN_EMAIL no .env: o make watch e o make mock usam esse endereço.");
        }

        return await db.Users.FirstAsync(u => u.Email == email, cancellationToken);
    }

    private async Task<MidiaPronta> PrepararMidiaAsync(string pasta, CancellationToken cancellationToken)
    {
        var ffmpeg = media.Value.FfmpegPath;
        var amostra = Path.Combine(pasta, "amostra.mp4");

        await ExecutarAsync(ffmpeg, [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"color=c=0x1d4ed8:s={LarguraDoClipe}x{AlturaDoClipe}:r=25:d={DuracaoSegundos}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={DuracaoSegundos}",
            "-shortest",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast",
            "-c:a", "aac", "-b:a", "96k",
            "-movflags", "+faststart",
            amostra
        ], "clipe de teste", cancellationToken);

        var saida = await pipeline.RunAsync(amostra, pasta, cancellationToken);
        var capas = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var video in _plano.Videos)
        {
            var caminho = Path.Combine(pasta, "capas", video.Slug + ".jpg");
            await GerarImagemAsync(ffmpeg, caminho, video.Cor, LarguraDoClipe, AlturaDoClipe, cancellationToken);
            capas[video.Slug] = caminho;
        }

        var capasDeColecao = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var colecao in _plano.Colecoes)
        {
            if (colecao.CorDaCapa is null)
                continue;

            var caminho = Path.Combine(pasta, "colecoes", colecao.Slug + ".jpg");
            await GerarImagemAsync(ffmpeg, caminho, colecao.CorDaCapa, CollectionThumbnailProcessor.MaxWidth, CollectionThumbnailProcessor.MaxHeight, cancellationToken);
            capasDeColecao[colecao.Slug] = caminho;
        }

        return new MidiaPronta(amostra, new FileInfo(amostra).Length, saida, capas, capasDeColecao);
    }

    private async Task GerarImagemAsync(
        string ffmpeg, string caminho, string cor, int largura, int altura, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);

        await ExecutarAsync(ffmpeg, [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"color=c={cor}:s={largura}x{altura}:d=1",
            "-frames:v", "1",
            "-c:v", "mjpeg",
            caminho
        ], "imagem de teste", cancellationToken);
    }

    private async Task ExecutarAsync(
        string programa, IReadOnlyList<string> argumentos, string etapa, CancellationToken cancellationToken)
    {
        ProcessResult resultado;

        try
        {
            resultado = await processos.RunAsync(programa, argumentos, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"{programa} não está no PATH. Instale o FFmpeg antes de make mock.", e);
        }

        if (!resultado.Succeeded)
            throw new InvalidOperationException($"Falha ao gerar {etapa}: {resultado.ShortError()}");
    }

    private async Task ApagarAnteriorAsync(CancellationToken cancellationToken)
    {
        var videosAntigos = await db.Videos.AsNoTracking()
            .Where(v => v.Slug.StartsWith(Catalogo.Prefixo))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var colecoesAntigas = await db.Collections.AsNoTracking()
            .Where(c => c.Slug.StartsWith(Catalogo.Prefixo))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (videosAntigos.Count == 0 && colecoesAntigas.Count == 0)
            return;

        Console.WriteLine($"Substituindo a geração anterior ({videosAntigos.Count} vídeos, {colecoesAntigas.Count} coleções).");
        await ApagarStorageAsync(videosAntigos, colecoesAntigas, cancellationToken);
        await ApagarRegistrosAsync(videosAntigos, colecoesAntigas, cancellationToken);
    }

    private async Task SubstituirAsync(User admin, MidiaPronta midia, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await ApagarAnteriorAsync(cancellationToken);

        Console.WriteLine($"Gravando {_plano.Videos.Count} vídeos e {_plano.Colecoes.Count} coleções...");

        var criados = new List<Video>();
        var colecoesCriadas = new List<Collection>();

        try
        {
            foreach (var video in _plano.Videos)
                criados.Add(await GravarVideoAsync(video, admin, midia, agora, cancellationToken));

            foreach (var colecao in _plano.Colecoes)
                colecoesCriadas.Add(await GravarColecaoAsync(colecao, admin, criados, midia, agora, cancellationToken));

            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            foreach (var video in criados)
            {
                await ApagarPrefixoAsync(StorageBucket.Originals, StorageKeys.VodPrefix(video.Id), CancellationToken.None);
                await ApagarPrefixoAsync(StorageBucket.Vod, StorageKeys.VodPrefix(video.Id), CancellationToken.None);
            }

            foreach (var colecao in colecoesCriadas)
                await ApagarPrefixoAsync(StorageBucket.Vod, $"collections/{colecao.Id:n}/", CancellationToken.None);

            throw;
        }

        foreach (var video in criados)
        {
            var playlist = StorageKeys.MasterUnder(video.HlsPrefix!);

            if (!await storage.ExistsAsync(StorageBucket.Vod, playlist, cancellationToken))
                throw new InvalidOperationException($"A playlist de {video.Slug} não chegou ao storage.");
        }

        foreach (var colecao in colecoesCriadas)
        {
            if (colecao.ThumbnailKey is not { } chave)
                continue;

            if (!await storage.ExistsAsync(StorageBucket.Vod, chave, cancellationToken))
                throw new InvalidOperationException($"A capa de {colecao.Slug} não chegou ao storage.");
        }
    }

    private async Task ApagarStorageAsync(
        IReadOnlyList<Guid> videos, IReadOnlyList<Guid> colecoes, CancellationToken cancellationToken)
    {
        foreach (var id in videos)
        {
            var prefixo = StorageKeys.VodPrefix(id);
            await ApagarPrefixoAsync(StorageBucket.Originals, prefixo, cancellationToken);
            await ApagarPrefixoAsync(StorageBucket.Vod, prefixo, cancellationToken);
        }

        foreach (var id in colecoes)
            await ApagarPrefixoAsync(StorageBucket.Vod, $"collections/{id:n}/", cancellationToken);
    }

    private async Task ApagarPrefixoAsync(StorageBucket bucket, string prefixo, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeletePrefixAsync(bucket, prefixo, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Não foi possível apagar {Prefixo} no storage", prefixo);
        }
    }

    private async Task ApagarRegistrosAsync(
        IReadOnlyList<Guid> videos, IReadOnlyList<Guid> colecoes, CancellationToken cancellationToken)
    {
        var alvos = videos.Concat(colecoes).ToArray();

        if (alvos.Length > 0)
        {
            await db.AccessGrants.Where(g => g.TargetId != null && alvos.Contains(g.TargetId.Value)).ExecuteDeleteAsync(cancellationToken);
            await db.Invitations.Where(i => i.TargetId != null && alvos.Contains(i.TargetId.Value)).ExecuteDeleteAsync(cancellationToken);
        }

        if (videos.Count == 0)
        {
            if (colecoes.Count > 0)
                await db.Collections.Where(c => colecoes.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);

            return;
        }

        await db.PlaybackEvents.Where(e => videos.Contains(e.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.PlaybackSessions.Where(s => videos.Contains(s.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.VideoDailyStats.Where(s => videos.Contains(s.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.VideoRetentionBuckets.Where(b => videos.Contains(b.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.SupportThreads.Where(t => videos.Contains(t.VideoId)).ExecuteDeleteAsync(cancellationToken);
        await db.ProcessingJobs.Where(j => j.TargetId != null && videos.Contains(j.TargetId.Value)).ExecuteDeleteAsync(cancellationToken);
        await db.CollectionVideos.Where(v => videos.Contains(v.VideoId)).ExecuteDeleteAsync(cancellationToken);

        if (colecoes.Count > 0)
            await db.Collections.Where(c => colecoes.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);

        await db.Videos.Where(v => videos.Contains(v.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<Video> GravarVideoAsync(
        VideoDeTeste plano, User admin, MidiaPronta midia, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var chaveOriginal = StorageKeys.Original(id, "amostra.mp4");
        var prefixo = StorageKeys.OutputPrefix(id, Guid.CreateVersion7());

        await storage.PutFileAsync(StorageBucket.Originals, chaveOriginal, midia.Amostra, MediaTypes.Mp4, cancellationToken);
        await EnviarSaidasAsync(prefixo, midia.Saida, midia.Capas[plano.Slug], cancellationToken);

        var video = Video.CreateDraft(plano.Titulo, plano.Slug, chaveOriginal, admin.Id, agora, plano.Descricao, id);
        video.ReplaceTags(plano.Tags);
        video.MarkUploaded(midia.Tamanho);
        video.StartProcessing();
        video.MarkReady(
            prefixo,
            midia.Saida.Info.DurationSeconds,
            midia.Saida.Info.Width,
            midia.Saida.Info.Height,
            StorageKeys.ThumbnailUnder(prefixo),
            StorageKeys.SpriteUnder(prefixo),
            agora);
        video.ChangeVisibility(plano.Visibilidade);

        db.Videos.Add(video);

        var posicao = 0;

        foreach (var capitulo in plano.Capitulos)
        {
            if (capitulo.StartSeconds >= midia.Saida.Info.DurationSeconds)
                continue;

            db.VideoChapters.Add(VideoChapter.Create(video.Id, posicao++, capitulo));
        }

        return video;
    }

    private async Task EnviarSaidasAsync(
        string prefixo, TranscodeOutput saida, string capa, CancellationToken cancellationToken)
    {
        foreach (var arquivo in Directory.EnumerateFiles(saida.OutputDirectory, "*", SearchOption.AllDirectories))
        {
            var relativo = Path.GetRelativePath(saida.OutputDirectory, arquivo).Replace(Path.DirectorySeparatorChar, '/');

            await storage.PutFileAsync(
                StorageBucket.Vod,
                prefixo + relativo,
                arquivo,
                MediaTypes.ForOutput(arquivo),
                cancellationToken);
        }

        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.ThumbnailUnder(prefixo), capa, MediaTypes.Jpeg, cancellationToken);
        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.SpriteUnder(prefixo), saida.SpritePath, MediaTypes.Jpeg, cancellationToken);
        await storage.PutFileAsync(StorageBucket.Vod, StorageKeys.SpriteMetadataUnder(prefixo), saida.SpriteVttPath, MediaTypes.WebVtt, cancellationToken);
    }

    private async Task<Collection> GravarColecaoAsync(
        ColecaoDeTeste plano,
        User admin,
        IReadOnlyList<Video> videos,
        MidiaPronta midia,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var colecao = Collection.Create(plano.Nome, plano.Slug, admin.Id, agora, plano.Descricao);

        foreach (var slug in plano.Videos)
            colecao.Add(videos.First(v => v.Slug == slug).Id, agora);

        if (midia.CapasDeColecao.TryGetValue(plano.Slug, out var caminho))
        {
            var jpeg = CollectionThumbnailProcessor.Normalize(await File.ReadAllBytesAsync(caminho, cancellationToken));
            var versao = clock.GetUtcNow().ToUnixTimeMilliseconds();

            if (versao <= 0)
                versao = 1;

            var chave = StorageKeys.CollectionThumbnail(colecao.Id, versao);
            await storage.PutBytesAsync(StorageBucket.Vod, chave, jpeg, MediaTypes.Jpeg, cancellationToken);
            colecao.SetThumbnail(chave, versao);
        }

        if (plano.RestritaAoConvidado)
        {
            db.AccessGrants.Add(AccessGrant.ForUser(
                EmailAddress.Parse(Catalogo.Convidado),
                GrantTargetType.Collection,
                colecao.Id,
                admin.Id,
                agora,
                note: Catalogo.Nota));
        }

        db.Collections.Add(colecao);
        return colecao;
    }

    private void Imprimir(string administrador)
    {
        var raiz = seguranca.Value.PublicUrl.TrimEnd('/');
        var emColecao = _plano.Colecoes.SelectMany(c => c.Videos).ToHashSet(StringComparer.Ordinal);
        var restrita = _plano.Colecoes.Any(c => c.RestritaAoConvidado);

        Console.WriteLine();
        Console.WriteLine("Catálogo de teste gravado. Rodar de novo substitui só os slugs que começam com mock-.");

        if (_plano.Videos.Count > 0)
            Console.WriteLine("O clipe é o mesmo em todos os vídeos; a capa de cada um muda.");

        Console.WriteLine();
        Console.WriteLine($"Administrador: {administrador}");

        if (restrita)
        {
            Console.WriteLine($"Convidado da coleção restrita: {Catalogo.Convidado}");
            Console.WriteLine($"  Código de acesso: {raiz}/sign-in");
            Console.WriteLine("  O código chega no Mailpit: http://localhost:8025");
        }

        Console.WriteLine();

        foreach (var colecao in _plano.Colecoes)
        {
            var capa = colecao.CorDaCapa is null ? "capa com o nome" : "capa própria";
            var acesso = colecao.RestritaAoConvidado ? "restrita" : "pública";
            Console.WriteLine($"  {raiz}/collections/{colecao.Slug}");
            Console.WriteLine($"    {colecao.Nome} — {colecao.Videos.Count} vídeos, {acesso}, {capa}");
        }

        foreach (var video in _plano.Videos.Where(v => !emColecao.Contains(v.Slug)))
        {
            var acesso = video.Visibilidade switch
            {
                VideoVisibility.Private => "privado",
                VideoVisibility.Restricted => "restrito",
                _ => "público"
            };

            Console.WriteLine($"  {raiz}/watch/{video.Slug}");
            Console.WriteLine($"    {video.Titulo} — {acesso}");
        }

        Console.WriteLine();
    }

    private sealed record MidiaPronta(
        string Amostra,
        long Tamanho,
        TranscodeOutput Saida,
        IReadOnlyDictionary<string, string> Capas,
        IReadOnlyDictionary<string, string> CapasDeColecao);
}
