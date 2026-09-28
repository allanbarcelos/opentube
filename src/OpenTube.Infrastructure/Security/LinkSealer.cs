// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Infrastructure.Security;

/// <summary>
/// Cifra o token de um link secreto para a administração poder mostrá-lo de novo. A chave sai
/// do mesmo segredo do servidor que resume os tokens: quem lê só o banco não recupera os
/// links, e trocar o segredo invalida os links e a leitura deles ao mesmo tempo.
/// </summary>
public static class LinkSealer
{
    private const int TamanhoDoNonce = 12;
    private const int TamanhoDaEtiqueta = 16;
    private static readonly byte[] Finalidade = Encoding.UTF8.GetBytes("opentube:share-link:v1");

    public static string Seal(string token, string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var aberto = Encoding.UTF8.GetBytes(token);
        var saida = new byte[TamanhoDoNonce + TamanhoDaEtiqueta + aberto.Length];
        var nonce = saida.AsSpan(0, TamanhoDoNonce);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(Chave(pepper), TamanhoDaEtiqueta);
        aes.Encrypt(nonce, aberto, saida.AsSpan(TamanhoDoNonce + TamanhoDaEtiqueta), saida.AsSpan(TamanhoDoNonce, TamanhoDaEtiqueta));

        return Convert.ToBase64String(saida);
    }

    /// <summary>Devolve o token, ou nulo se o valor não abrir com o segredo atual.</summary>
    public static string? Open(string? sealedToken, string pepper)
    {
        if (string.IsNullOrWhiteSpace(sealedToken))
            return null;

        try
        {
            var dados = Convert.FromBase64String(sealedToken);
            if (dados.Length <= TamanhoDoNonce + TamanhoDaEtiqueta)
                return null;

            var aberto = new byte[dados.Length - TamanhoDoNonce - TamanhoDaEtiqueta];
            using var aes = new AesGcm(Chave(pepper), TamanhoDaEtiqueta);
            aes.Decrypt(dados.AsSpan(0, TamanhoDoNonce), dados.AsSpan(TamanhoDoNonce + TamanhoDaEtiqueta),
                dados.AsSpan(TamanhoDoNonce, TamanhoDaEtiqueta), aberto);

            return Encoding.UTF8.GetString(aberto);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Chave(string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(pepper), 32, info: Finalidade);
    }
}
