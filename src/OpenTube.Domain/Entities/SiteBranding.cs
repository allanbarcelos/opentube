// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

namespace OpenTube.Domain.Entities;

/// <summary>
/// Personalização do site, definida pela administração: o nome que aparece na barra, nos
/// títulos e nos emails, o logotipo da barra e se o rodapé mostra o crédito e o link do
/// repositório. Existe no máximo uma; sem ela, o site usa o nome e o rodapé de fábrica.
/// </summary>
public class SiteBranding
{
    /// <summary>Identificador fixo: a personalização é uma configuração única do site.</summary>
    public const int SingletonId = 1;

    /// <summary>Nome de fábrica, enquanto a administração não definir outro.</summary>
    public const string DefaultName = "OpenTube";

    public const int MaxNameLength = 60;

    /// <summary>
    /// Tamanho padrão do logotipo: cabe em 480 × 128, mantendo a proporção. Na barra ele tem 32 px
    /// de altura; 128 mantém a nitidez em telas de alta densidade, e a largura acomoda logotipos
    /// horizontais.
    /// </summary>
    public const int LogoStandardWidth = 480;

    public const int LogoStandardHeight = 128;

    /// <summary>Menor lado maior aceito: abaixo disso o logotipo fica borrado na barra.</summary>
    public const int LogoMinLongestSide = 32;

    /// <summary>Tamanho máximo do logotipo guardado, já no tamanho padrão.</summary>
    public const int LogoMaxBytes = 512 * 1024;

    public const string LogoContentType = "image/png";

    private SiteBranding() { }

    public int Id { get; private set; }

    public string Name { get; private set; } = DefaultName;

    public byte[]? Logo { get; private set; }
    public int? LogoWidth { get; private set; }
    public int? LogoHeight { get; private set; }

    /// <summary>Mostra no rodapé o crédito "Desenvolvido por".</summary>
    public bool ShowPoweredBy { get; private set; } = true;

    /// <summary>Mostra no rodapé o link para o repositório do OpenTube.</summary>
    public bool ShowRepositoryLink { get; private set; } = true;

    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Muda a cada alteração. Vai no endereço do logotipo, que pode ficar em cache.</summary>
    public long Version => UpdatedAt.ToUnixTimeMilliseconds();

    public static SiteBranding Define(
        string name, bool showPoweredBy, bool showRepositoryLink, Guid adminId, DateTimeOffset now) =>
        new()
        {
            Id = SingletonId,
            Name = ValidarNome(name),
            ShowPoweredBy = showPoweredBy,
            ShowRepositoryLink = showRepositoryLink,
            UpdatedBy = adminId,
            UpdatedAt = now
        };

    public void Change(string name, bool showPoweredBy, bool showRepositoryLink, Guid adminId, DateTimeOffset now)
    {
        Name = ValidarNome(name);
        ShowPoweredBy = showPoweredBy;
        ShowRepositoryLink = showRepositoryLink;
        UpdatedBy = adminId;
        UpdatedAt = now;
    }

    /// <summary>Troca o logotipo. A imagem já chega no tamanho padrão; aqui só se garante isso.</summary>
    public void SetLogo(byte[] image, Guid adminId, DateTimeOffset now)
    {
        var (largura, altura) = PlayerWatermark.ReadPngSize(image);

        if (image.Length > LogoMaxBytes)
            throw new ArgumentException("The logo is larger than 512 KB.");

        if (largura > LogoStandardWidth || altura > LogoStandardHeight)
            throw new ArgumentException("The logo must be resized to the standard logo size first.");

        if (Math.Max(largura, altura) < LogoMinLongestSide)
            throw new ArgumentException("The logo must be at least 32 pixels on its longest side.");

        Logo = image;
        LogoWidth = largura;
        LogoHeight = altura;
        UpdatedBy = adminId;
        UpdatedAt = now;
    }

    public void RemoveLogo(Guid adminId, DateTimeOffset now)
    {
        Logo = null;
        LogoWidth = null;
        LogoHeight = null;
        UpdatedBy = adminId;
        UpdatedAt = now;
    }

    /// <summary>Sem espaços nas pontas, sem quebras nem caracteres de controle, e não vazio.</summary>
    private static string ValidarNome(string name)
    {
        var nome = (name ?? string.Empty).Trim();

        if (nome.Length == 0)
            throw new ArgumentException("Enter the site name.");

        if (nome.Length > MaxNameLength)
            throw new ArgumentException("The site name must be at most 60 characters.");

        if (nome.Any(char.IsControl))
            throw new ArgumentException("The site name cannot contain line breaks.");

        return nome;
    }
}
