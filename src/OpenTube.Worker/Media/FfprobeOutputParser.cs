// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Globalization;
using System.Text.Json;

namespace OpenTube.Worker.Media;

/// <summary>
/// Lê a saída em JSON do <c>ffprobe</c>. Fica separada do processo para que a interpretação
/// de arquivos estranhos possa ser testada sem executar nada.
/// </summary>
public static class FfprobeOutputParser
{
    public static MediaInfo Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;

        if (!raiz.TryGetProperty("streams", out var streams) || streams.ValueKind is not JsonValueKind.Array)
            throw new InvalidOperationException("A saída do ffprobe não traz a lista de fluxos.");

        JsonElement? video = null;
        JsonElement? audio = null;

        foreach (var stream in streams.EnumerateArray())
        {
            var tipo = Texto(stream, "codec_type");

            if (video is null && tipo == "video" && !EhCapa(stream))
                video = stream;
            else if (audio is null && tipo == "audio")
                audio = stream;
        }

        if (video is null)
            throw new InvalidOperationException("O arquivo enviado não tem fluxo de vídeo.");

        var largura = Inteiro(video.Value, "width");
        var altura = Inteiro(video.Value, "height");

        if (largura <= 0 || altura <= 0)
            throw new InvalidOperationException("Não foi possível determinar as dimensões do vídeo.");

        return new MediaInfo(
            DurationSeconds: Duracao(raiz, video.Value),
            Width: largura,
            Height: altura,
            FrameRate: Fracao(Texto(video.Value, "avg_frame_rate") ?? Texto(video.Value, "r_frame_rate")),
            HasAudio: audio is not null,
            VideoCodec: Texto(video.Value, "codec_name"),
            AudioCodec: audio is null ? null : Texto(audio.Value, "codec_name"));
    }

    /// <summary>
    /// Capa embutida (por exemplo, a arte de um arquivo de áudio) aparece como fluxo de vídeo
    /// de um quadro só. Tratá-la como vídeo faria o worker transcodificar uma imagem parada.
    /// </summary>
    private static bool EhCapa(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposicao) &&
        disposicao.TryGetProperty("attached_pic", out var capa) &&
        capa.GetInt32() == 1;

    private static double Duracao(JsonElement raiz, JsonElement video)
    {
        if (raiz.TryGetProperty("format", out var formato) &&
            double.TryParse(Texto(formato, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var doFormato) &&
            doFormato > 0)
            return doFormato;

        return double.TryParse(Texto(video, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var doFluxo)
            ? doFluxo
            : 0;
    }

    /// <summary>Converte a taxa de quadros, que o ffprobe informa como fração ("30000/1001").</summary>
    public static double Fracao(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return 0;

        var partes = valor.Split('/');

        if (partes.Length == 2 &&
            double.TryParse(partes[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerador) &&
            double.TryParse(partes[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominador) &&
            denominador != 0)
            return numerador / denominador;

        return double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var simples) ? simples : 0;
    }

    private static string? Texto(JsonElement elemento, string propriedade) =>
        elemento.TryGetProperty(propriedade, out var valor) && valor.ValueKind is JsonValueKind.String
            ? valor.GetString()
            : null;

    private static int Inteiro(JsonElement elemento, string propriedade) =>
        elemento.TryGetProperty(propriedade, out var valor) && valor.TryGetInt32(out var numero) ? numero : 0;
}
