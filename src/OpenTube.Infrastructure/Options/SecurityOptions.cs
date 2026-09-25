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

    /// <summary>Quantos códigos um mesmo endereço de origem pode pedir por hora.</summary>
    public int CodesPerHourPerIp { get; set; } = 5;

    /// <summary>Quantos códigos um mesmo email pode receber por dia.</summary>
    public int CodesPerDayPerEmail { get; set; } = 10;

    /// <summary>Quantos códigos um mesmo domínio pode receber por dia.</summary>
    public int CodesPerDayPerDomain { get; set; } = 100;

    /// <summary>
    /// Quantas origens distintas podem reproduzir ao mesmo tempo com a mesma conta. Zero
    /// desliga a verificação. Detecta credencial repassada; não a impede em rede compartilhada,
    /// onde várias pessoas saem pelo mesmo endereço.
    /// </summary>
    public int MaxConcurrentPlaybacks { get; set; } = 3;

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
