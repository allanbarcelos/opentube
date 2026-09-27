// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Security;

namespace OpenTube.Web.Auth;

/// <summary>
/// Atalho para registrar uma ação administrativa a partir de uma requisição, reunindo o autor
/// e a origem sem repetir a mesma montagem em cada endereço.
/// </summary>
public static class Auditoria
{
    public static Task RegistrarAsync(
        this HttpContext contexto,
        string acao,
        string tipoDeEntidade,
        Guid? entidadeId,
        string resumo,
        CancellationToken cancellationToken = default)
    {
        var servicos = contexto.RequestServices;
        var trilha = servicos.GetRequiredService<AuditTrail>();
        var privacidade = servicos.GetRequiredService<PrivacyHasher>();

        return trilha.RecordAsync(
            ViewerContext.From(contexto.User),
            acao,
            tipoDeEntidade,
            entidadeId,
            resumo,
            privacidade.HashIp(contexto.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
    }
}
