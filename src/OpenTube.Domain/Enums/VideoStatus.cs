namespace OpenTube.Domain.Enums;

/// <summary>Estágio do vídeo no pipeline de ingestão.</summary>
public enum VideoStatus
{
    /// <summary>Registro criado, arquivo ainda não enviado por completo.</summary>
    Draft = 0,

    /// <summary>Arquivo original recebido, aguardando transcodificação.</summary>
    Uploaded = 1,

    /// <summary>Transcodificação em andamento.</summary>
    Processing = 2,

    /// <summary>Pronto para reprodução.</summary>
    Ready = 3,

    /// <summary>Falhou no processamento; ver o job correspondente.</summary>
    Failed = 4
}
