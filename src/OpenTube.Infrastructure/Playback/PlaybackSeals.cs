// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Access;
using OpenTube.Infrastructure.Options;

namespace OpenTube.Infrastructure.Playback;

/// <summary>O que um selo de reprodução abre.</summary>
public enum PlaybackSealKind : byte
{
    /// <summary>A playlist principal.</summary>
    Master = 1,

    /// <summary>A playlist de uma versão; o caminho é o nome da versão.</summary>
    Rendition = 2,

    /// <summary>Um arquivo de uma versão; o caminho é <c>versão/arquivo</c>.</summary>
    Segment = 3
}

/// <summary>Conteúdo de um selo aberto e conferido.</summary>
/// <param name="Kind">O que ele abre.</param>
/// <param name="VideoId">Vídeo.</param>
/// <param name="Path">Versão, ou versão e arquivo; vazio na playlist principal.</param>
public readonly record struct PlaybackSeal(PlaybackSealKind Kind, Guid VideoId, string Path);

/// <summary>
/// Endereços opacos da reprodução. Cada playlist e cada pedaço do vídeo é pedido por um selo
/// cifrado, e não por um caminho legível: o DevTools não mostra vídeo, versão, ordem nem
/// extensão, e trocar um número no endereço não leva ao pedaço seguinte. O selo prende o pedido
/// ao vídeo, a quem assiste e a um prazo; a política de acesso continua sendo conferida a cada
/// pedido.
/// </summary>
public class PlaybackSeals(IOptions<SecurityOptions> options, TimeProvider clock)
{
    /// <summary>Tempo entre receber o endereço da playlist principal e o player pedi-la.</summary>
    public static readonly TimeSpan MasterLifetime = TimeSpan.FromHours(1);

    /// <summary>Duração de uma reprodução, pausas incluídas.</summary>
    public static readonly TimeSpan PlaybackLifetime = TimeSpan.FromHours(12);

    private const int TamanhoDoNonce = 12;
    private const int TamanhoDaEtiqueta = 16;
    private static readonly byte[] Finalidade = Encoding.UTF8.GetBytes("opentube:playback-seal:v1");

    private readonly byte[] _chave = HKDF.DeriveKey(
        HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(options.Value.TokenPepper), 32, info: Finalidade);

    public string Seal(PlaybackSealKind kind, Guid videoId, Viewer viewer, string path = "")
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var validade = kind is PlaybackSealKind.Master ? MasterLifetime : PlaybackLifetime;
        var expira = (clock.GetUtcNow() + validade).ToUnixTimeSeconds();
        var espectador = Encoding.UTF8.GetBytes(PlaybackTokens.ViewerKey(viewer));
        var caminho = Encoding.UTF8.GetBytes(path);

        // tipo | vídeo | prazo | tamanho e chave de quem assiste | caminho
        var aberto = new byte[1 + 16 + 8 + 1 + espectador.Length + caminho.Length];
        aberto[0] = (byte)kind;
        videoId.TryWriteBytes(aberto.AsSpan(1, 16));
        BinaryPrimitives.WriteInt64BigEndian(aberto.AsSpan(17, 8), expira);
        aberto[25] = (byte)espectador.Length;
        espectador.CopyTo(aberto, 26);
        caminho.CopyTo(aberto, 26 + espectador.Length);

        var selado = new byte[TamanhoDoNonce + TamanhoDaEtiqueta + aberto.Length];
        var nonce = selado.AsSpan(0, TamanhoDoNonce);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_chave, TamanhoDaEtiqueta);
        aes.Encrypt(nonce, aberto, selado.AsSpan(TamanhoDoNonce + TamanhoDaEtiqueta), selado.AsSpan(TamanhoDoNonce, TamanhoDaEtiqueta));

        return Convert.ToBase64String(selado).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Abre o selo. Devolve nulo quando ele não abre com o segredo atual, é de outro tipo, foi
    /// emitido para outra pessoa ou venceu.
    /// </summary>
    public PlaybackSeal? Open(string? seal, PlaybackSealKind kind, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (string.IsNullOrWhiteSpace(seal) || seal.Length > 1024)
            return null;

        byte[] selado;
        try
        {
            var padrao = seal.Replace('-', '+').Replace('_', '/');
            selado = Convert.FromBase64String(padrao.PadRight(padrao.Length + (4 - padrao.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            return null;
        }

        if (selado.Length < TamanhoDoNonce + TamanhoDaEtiqueta + 26)
            return null;

        var aberto = new byte[selado.Length - TamanhoDoNonce - TamanhoDaEtiqueta];
        try
        {
            using var aes = new AesGcm(_chave, TamanhoDaEtiqueta);
            aes.Decrypt(selado.AsSpan(0, TamanhoDoNonce), selado.AsSpan(TamanhoDoNonce + TamanhoDaEtiqueta),
                selado.AsSpan(TamanhoDoNonce, TamanhoDaEtiqueta), aberto);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var tamanhoDoEspectador = aberto[25];
        if (aberto[0] != (byte)kind || aberto.Length < 26 + tamanhoDoEspectador)
            return null;

        var expira = BinaryPrimitives.ReadInt64BigEndian(aberto.AsSpan(17, 8));
        if (expira <= clock.GetUtcNow().ToUnixTimeSeconds())
            return null;

        var espectador = Encoding.UTF8.GetString(aberto, 26, tamanhoDoEspectador);
        if (!string.Equals(espectador, PlaybackTokens.ViewerKey(viewer), StringComparison.Ordinal))
            return null;

        return new PlaybackSeal(
            (PlaybackSealKind)aberto[0],
            new Guid(aberto.AsSpan(1, 16)),
            Encoding.UTF8.GetString(aberto, 26 + tamanhoDoEspectador, aberto.Length - 26 - tamanhoDoEspectador));
    }
}
