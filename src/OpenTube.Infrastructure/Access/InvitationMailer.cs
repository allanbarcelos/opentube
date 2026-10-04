// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Email;
using OpenTube.Infrastructure.Localization;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Access;

/// <summary>
/// Email de convite: diz o que foi liberado e até quando e leva à entrada com o endereço já
/// preenchido e, depois do código, ao que foi liberado. Conceder fica no
/// <see cref="GrantService"/>; aqui só se avisa a pessoa.
/// </summary>
public class InvitationMailer(
    OpenTubeDbContext db,
    GrantQueries queries,
    IEmailSender email,
    ISiteIdentity site,
    IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>Envia o convite a cada endereço, um email por pessoa.</summary>
    public async Task SendAsync(
        IEnumerable<EmailAddress> to,
        GrantTargetType targetType,
        Guid? targetId,
        GrantValidity validity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(to);

        var rotulo = await DescreverAlvoAsync(targetType, targetId, cancellationToken);
        var destino = await DestinoDoAlvoAsync(targetType, targetId, cancellationToken);
        var nome = (await site.GetAsync(cancellationToken)).Name;

        foreach (var endereco in to)
        {
            await email.SendAsync(EmailTemplates.Invite(
                endereco.Value, rotulo, validity.Describe(), EnderecoDeEntrada(endereco, destino), nome),
                cancellationToken);
        }
    }

    /// <summary>Nome do que foi liberado, para aparecer no convite.</summary>
    private async Task<string> DescreverAlvoAsync(GrantTargetType tipo, Guid? alvoId, CancellationToken cancellationToken) =>
        await queries.TargetNameAsync(tipo, alvoId, cancellationToken) ?? GrantLabels.Target(tipo);

    /// <summary>
    /// Onde a pessoa convidada cai depois de entrar: o vídeo liberado ou, para uma coleção ou o
    /// acervo, a página inicial, que lista o que ela pode ver.
    /// </summary>
    private async Task<string> DestinoDoAlvoAsync(GrantTargetType tipo, Guid? alvoId, CancellationToken cancellationToken) =>
        tipo is GrantTargetType.Video
        && await db.Videos.Where(v => v.Id == alvoId).Select(v => v.Slug).FirstOrDefaultAsync(cancellationToken) is { } slug
            ? $"/watch/{slug}"
            : "/";

    /// <summary>Página de entrada com o endereço já preenchido e o destino depois do código.</summary>
    private string EnderecoDeEntrada(EmailAddress endereco, string destino) =>
        $"{_options.PublicUrl.TrimEnd('/')}/sign-in?email={Uri.EscapeDataString(endereco.Value)}&voltar={Uri.EscapeDataString(destino)}";
}
