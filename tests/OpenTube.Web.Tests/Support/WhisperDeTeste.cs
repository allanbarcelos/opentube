// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Transcription;
using OpenTube.TestSupport;

namespace OpenTube.Web.Tests.Support;

/// <summary>Registra um worker de transcrição, como o do container do Whisper faria.</summary>
public static class WhisperDeTeste
{
    public static async Task InformarAsync(PostgresFixture postgres, bool disponivel = true)
    {
        await using var db = postgres.CreateContext();
        await new TranscriptionAvailability(db, TimeProvider.System)
            .ReportAsync("worker-de-teste", disponivel, "whisper.cpp · CPU · small-q5_1 · 4 threads");
    }
}
