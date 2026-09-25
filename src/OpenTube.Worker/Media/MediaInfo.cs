namespace OpenTube.Worker.Media;

/// <summary>O que o <c>ffprobe</c> descobriu sobre o arquivo enviado.</summary>
/// <param name="DurationSeconds">Duração total.</param>
/// <param name="Width">Largura do fluxo de vídeo.</param>
/// <param name="Height">Altura do fluxo de vídeo.</param>
/// <param name="FrameRate">Quadros por segundo.</param>
/// <param name="HasAudio">Se existe fluxo de áudio.</param>
/// <param name="VideoCodec">Codec do vídeo.</param>
/// <param name="AudioCodec">Codec do áudio, quando houver.</param>
public sealed record MediaInfo(
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec)
{
    public bool IsPortrait => Height > Width;
}
