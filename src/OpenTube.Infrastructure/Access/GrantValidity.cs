// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Localization;

namespace OpenTube.Infrastructure.Access;

/// <summary>Como a validade de uma concessão foi definida pelo administrador.</summary>
/// <param name="ExpiresAt">Data fixa de término, quando houver.</param>
/// <param name="DurationAfterFirstUse">Prazo contado a partir do primeiro acesso.</param>
public readonly record struct GrantValidity(DateTimeOffset? ExpiresAt, TimeSpan? DurationAfterFirstUse)
{
    /// <summary>Acesso sem prazo.</summary>
    public static GrantValidity Forever => new(null, null);

    public static GrantValidity Until(DateTimeOffset when) => new(when, null);

    public static GrantValidity For(TimeSpan afterFirstUse) => new(null, afterFirstUse);

    /// <summary>Descrição usada no email de convite, no idioma do pedido.</summary>
    public string Describe() => this switch
    {
        { DurationAfterFirstUse: { } prazo } => LocalText.Format("{0} days from the first visit", (int)Math.Round(prazo.TotalDays)),
        { ExpiresAt: { } fim } => LocalText.Format("until {0}", fim.ToLocalTime().ToString("d")),
        _ => LocalText.Get("no end date")
    };
}
