// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Web.Endpoints;

/// <summary>
/// Endereço de volta vindo de um formulário. Só vale caminho do próprio site: "//outro.site" e
/// "/\outro.site" (que os navegadores tratam como "//") levariam quem clicou para fora dele.
/// </summary>
public static class Retorno
{
    public static string Para(string? destino, string parametro)
    {
        var caminho = EhLocal(destino) ? destino! : "/";

        // O parâmetro entra antes da âncora, para a página ler e ainda rolar até o lugar certo.
        var ancora = string.Empty;
        var cerquilha = caminho.IndexOf('#', StringComparison.Ordinal);
        if (cerquilha >= 0)
        {
            ancora = caminho[cerquilha..];
            caminho = caminho[..cerquilha];
        }

        return caminho + (caminho.Contains('?') ? "&" : "?") + parametro + ancora;
    }

    public static bool EhLocal(string? destino) =>
        !string.IsNullOrWhiteSpace(destino)
        && destino[0] == '/'
        && (destino.Length == 1 || (destino[1] != '/' && destino[1] != '\\'));
}
