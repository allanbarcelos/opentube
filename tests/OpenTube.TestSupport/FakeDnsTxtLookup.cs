// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Domains;

namespace OpenTube.TestSupport;

/// <summary>Consulta de DNS controlada pelo teste, sem depender de rede nem de propagação.</summary>
public class FakeDnsTxtLookup : IDnsTxtLookup
{
    private readonly Dictionary<string, IReadOnlyList<string>> _registros = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nomes consultados, na ordem, para conferir o que foi perguntado.</summary>
    public List<string> Consultas { get; } = [];

    public void Publicar(string nome, params string[] valores) => _registros[nome] = valores;

    public void Remover(string nome) => _registros.Remove(nome);

    public Task<IReadOnlyList<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default)
    {
        Consultas.Add(name);

        return Task.FromResult(_registros.TryGetValue(name, out var valores) ? valores : []);
    }
}
