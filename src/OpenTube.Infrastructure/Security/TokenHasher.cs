using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Infrastructure.Security;

/// <summary>
/// Resume códigos, tokens e endereços de origem antes de gravá-los. Usa HMAC com um segredo do
/// servidor: um resumo simples de um código de seis dígitos seria quebrado por tabela em
/// milissegundos, já que só existem um milhão de valores possíveis.
/// </summary>
public static class TokenHasher
{
    public static string Hash(string value, string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);

        var chave = Encoding.UTF8.GetBytes(pepper);
        var resumo = HMACSHA256.HashData(chave, Encoding.UTF8.GetBytes(value));

        return Convert.ToBase64String(resumo);
    }

    /// <summary>
    /// Compara em tempo constante. A comparação comum de texto sai mais cedo no primeiro
    /// caractere diferente, e essa diferença de tempo vaza informação sobre o valor correto.
    /// </summary>
    public static bool Verify(string value, string expectedHash, string pepper)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        var calculado = Encoding.UTF8.GetBytes(Hash(value, pepper));
        var esperado = Encoding.UTF8.GetBytes(expectedHash);

        return CryptographicOperations.FixedTimeEquals(calculado, esperado);
    }
}
