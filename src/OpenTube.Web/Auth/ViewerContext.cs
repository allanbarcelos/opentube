// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Security.Claims;
using OpenTube.Domain.Access;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Web.Auth;

/// <summary>Nomes das informações guardadas no cookie de sessão.</summary>
public static class OpenTubeClaims
{
    public const string SessionId = "otb:sid";
    public const string IsAdmin = "otb:admin";
}

/// <summary>Traduz a identidade da requisição no espectador usado pelas regras de acesso.</summary>
public static class ViewerContext
{
    public static Viewer From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated is not true)
            return Viewer.Anonymous;

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = principal.FindFirstValue(ClaimTypes.Email);

        if (!Guid.TryParse(id, out var userId) || !EmailAddress.TryParse(email, out var endereco))
            return Viewer.Anonymous;

        var admin = string.Equals(principal.FindFirstValue(OpenTubeClaims.IsAdmin), "1", StringComparison.Ordinal);

        return Viewer.Authenticated(userId, endereco, admin);
    }

    public static Guid? SessionId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(OpenTubeClaims.SessionId), out var id) ? id : null;
}
