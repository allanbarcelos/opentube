using System.Security.Cryptography;

namespace OpenTube.Infrastructure.Security;

/// <summary>Geração dos segredos usados no acesso sem senha.</summary>
public static class OneTimeCode
{
    /// <summary>Quantidade de dígitos do código enviado por email.</summary>
    public const int Digits = 6;

    /// <summary>
    /// Código numérico de seis dígitos, com zeros à esquerda preservados. Sorteado com gerador
    /// criptográfico e sem resto de divisão enviesado.
    /// </summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Token do link de acesso direto, em base64 seguro para URL.</summary>
    public static string GenerateToken(int bytes = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 16);

        return Base64Url(RandomNumberGenerator.GetBytes(bytes));
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
