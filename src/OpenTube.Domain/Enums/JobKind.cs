namespace OpenTube.Domain.Enums;

/// <summary>Tipos de trabalho que o worker sabe executar.</summary>
public enum JobKind
{
    /// <summary>Transcodificação do original para HLS, com thumbnail e sprites.</summary>
    Transcode = 0,

    /// <summary>Transcrição do áudio para legenda e busca.</summary>
    Transcript = 1,

    /// <summary>Agregação periódica dos eventos de reprodução.</summary>
    AnalyticsRollup = 2,

    /// <summary>Remoção dos arquivos de um vídeo excluído.</summary>
    StorageCleanup = 3,

    /// <summary>Remoção das gerações de saída que deixaram de ser a publicada.</summary>
    RetireOutputs = 4
}
