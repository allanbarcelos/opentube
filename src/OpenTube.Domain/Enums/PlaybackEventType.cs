namespace OpenTube.Domain.Enums;

/// <summary>O que o player relatou.</summary>
public enum PlaybackEventType
{
    /// <summary>Início da reprodução.</summary>
    Start = 0,

    /// <summary>Batida periódica com o trecho assistido desde a anterior.</summary>
    Progress = 1,

    Pause = 2,

    /// <summary>Salto na linha do tempo.</summary>
    Seek = 3,

    /// <summary>Chegou ao fim do vídeo.</summary>
    Ended = 4,

    /// <summary>Troca de qualidade pelo player adaptativo.</summary>
    Quality = 5,

    /// <summary>Erro de reprodução relatado pelo player.</summary>
    Error = 6
}

/// <summary>Categoria do aparelho usado, deduzida da identificação do navegador.</summary>
public enum DeviceType
{
    Unknown = 0,
    Desktop = 1,
    Mobile = 2,
    Tablet = 3,
    Tv = 4
}
