// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.ComponentModel.DataAnnotations;

namespace OpenTube.Infrastructure.Options;

/// <summary>Parâmetros de autenticação, limitação de taxa e privacidade.</summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Segredo usado para resumir códigos e tokens. Sem ele, quem lê o banco consegue
    /// reconstruir um código de seis dígitos por tentativa e erro em segundos.
    /// </summary>
    [Required]
    public string TokenPepper { get; set; } = string.Empty;

    /// <summary>Segredo usado para resumir endereços de origem, preservando a privacidade.</summary>
    [Required]
    public string IpHashPepper { get; set; } = string.Empty;

    /// <summary>Validade do código de seis dígitos.</summary>
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Validade do link de convite enviado pelo administrador.</summary>
    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Duração da sessão, renovada a cada acesso.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Quantos códigos o mesmo email pode pedir em dez minutos.
    /// Zero ou negativo cai no padrão de cinco: um valor vazio não pode barrar o primeiro pedido.
    /// </summary>
    public int CodesPerWindow { get; set; } = 5;

    /// <summary>
    /// Quantos códigos a mesma origem pode pedir em dez minutos, somando todos os emails. Folgado
    /// de propósito: uma empresa inteira sai pelo mesmo endereço. Zero ou negativo cai no padrão.
    /// </summary>
    public int CodesPerIpWindow { get; set; } = 50;

    /// <summary>
    /// Quantos códigos errados o mesmo email pode digitar em 24 horas, somando todos os códigos
    /// pedidos. Sem este teto, pedir um código novo a cada cinco erros dá cerca de 3.600 palpites
    /// por dia. Atingido o teto, só o link do email entra. Zero ou negativo cai no padrão.
    /// </summary>
    public int CodeAttemptsPerDay { get; set; } = 20;

    /// <summary>
    /// Quantas origens distintas podem reproduzir ao mesmo tempo com a mesma conta. Zero
    /// desliga a verificação. Detecta credencial repassada; não a impede em rede compartilhada,
    /// onde várias pessoas saem pelo mesmo endereço.
    /// </summary>
    public int MaxConcurrentPlaybacks { get; set; } = 3;

    /// <summary>
    /// Quantos segmentos a mesma pessoa pode buscar de uma vez num vídeo antes de o limite de
    /// velocidade valer: o carregamento inicial e os saltos para outro ponto. Com segmentos de
    /// 4 s, 45 dão três minutos. Zero desliga o limite.
    /// </summary>
    public int SegmentBurst { get; set; } = 45;

    /// <summary>
    /// Segmentos por segundo repostos depois da folga inicial. Com segmentos de 4 s, 1 por
    /// segundo é quatro vezes a velocidade do vídeo: o player nunca chega nele, e baixar o
    /// vídeo leva pelo menos um quarto da duração. Zero desliga o limite.
    /// </summary>
    public double SegmentsPerSecond { get; set; } = 1;

    /// <summary>
    /// Exibe o endereço de quem assiste sobre o vídeo. Não impede a gravação de tela, mas
    /// identifica a origem de um vazamento e inibe o repasse casual.
    /// </summary>
    public bool WatermarkEnabled { get; set; } = true;

    /// <summary>Emails que recebem o papel de administrador ao subir a aplicação.</summary>
    public string[] AdminEmails { get; set; } = [];

    /// <summary>Endereço público da aplicação, usado para montar os links dos emails.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";
}
