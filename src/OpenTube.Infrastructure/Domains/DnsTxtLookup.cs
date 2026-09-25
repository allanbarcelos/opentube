using DnsClient;
using Microsoft.Extensions.Logging;

namespace OpenTube.Infrastructure.Domains;

/// <summary>Consulta de registros TXT, isolada para que a verificação possa ser testada.</summary>
public interface IDnsTxtLookup
{
    Task<IReadOnlyList<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default);
}

public class DnsTxtLookup(ILogger<DnsTxtLookup> logger) : IDnsTxtLookup
{
    private readonly LookupClient _cliente = new(new LookupClientOptions
    {
        // A consulta é feita na hora em que o administrador aperta "verificar"; guardar a
        // resposta em cache só atrasaria o reconhecimento de um registro recém-publicado.
        UseCache = false,
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 2
    });

    public async Task<IReadOnlyList<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var resposta = await _cliente.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);

            if (resposta.HasError)
            {
                logger.LogInformation("Consulta TXT a {Nome} respondeu: {Erro}", name, resposta.ErrorMessage);
                return [];
            }

            return [.. resposta.Answers.TxtRecords().SelectMany(r => r.Text)];
        }
        catch (DnsResponseException e)
        {
            // Domínio inexistente ou servidor sem resposta: para a verificação, é o mesmo que
            // não encontrar o registro.
            logger.LogInformation(e, "Não foi possível consultar o TXT de {Nome}", name);
            return [];
        }
    }
}
