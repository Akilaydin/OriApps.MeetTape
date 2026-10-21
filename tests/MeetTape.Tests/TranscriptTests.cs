using System.Security.Cryptography;

namespace MeetTape.Tests;

public sealed class TranscriptTests : IDisposable
{
    private readonly JobTests fixture = new();

    [Fact]
    public async Task GlobalOrderingTechnicalTextLabelsAndBoundaryReconciliationAreDeterministic()
    {
        string directory = Prepare([
            "[00:00.8] Me: MP3 track 42 не потерял начало?",
            "[00:00.05] Me: MP3 track 42 не потерял начало? Дополнительный аргумент: bitrate 96 килобит в секунду.",
            "[00:00.25] Them: USB-C 3.2 — кабель, не адаптер.\n[00:00] Invented Name: Версия FLAC 1.4."
        ]);
        var data = SessionStore.Read(directory);
        byte[] meHash = Hash(Path.Combine(directory, "me.mp3"));
        var builder = new TranscriptAssembler();
        string first = await builder.BuildAsync(directory, data, default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("> Full transcript. Timestamps are approximate and relative to the original recording.", output);
        Assert.Equal(1, output.Split("MP3 track 42").Length - 1);
        Assert.Contains("Дополнительный аргумент: bitrate 96 килобит в секунду.", output);
        Assert.Contains("USB-C 3.2 — кабель, не адаптер.", output);
        Assert.Contains("**Unknown:** Версия FLAC 1.4.", output);
        Assert.True(output.IndexOf("**Unknown:**", StringComparison.Ordinal) < output.IndexOf("**Them:**", StringComparison.Ordinal));
        Assert.Equal(first, await builder.BuildAsync(directory, data, default));
        Assert.Equal(meHash, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Equal(output, File.ReadAllText(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task DailyStylePersonalNameLabelsAreRejectedRatherThanSilentlyMappedToUnknown()
    {
        string named = string.Join('\n', Enumerable.Range(0, 53).Select(i => $"[00:00] Артём О: Реплика {i}"));
        string directory = Prepare([named, "[NO_SPEECH]"], seconds: 120, mono: true);
        var data = SessionStore.Read(directory);
        data = data with { Processing = data.Processing with { MeDisplayName = "Артём О" } };
        SessionStore.Save(directory, data);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new TranscriptAssembler().BuildAsync(directory, data, default));
        Assert.Contains("Unexpected source label", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, "transcription.md")));
        Assert.True(File.Exists(ChunkStore.PathFor(directory, 1))); // Existing response retained for diagnosis.
    }

    [Fact]
    public async Task SameSourceBoundaryDuplicateIsRemovedButOtherSpeakerAndLaterRepetitionRemain()
    {
        string directory = Prepare([
            "[00:00.8] Me: Это одинаковая длинная реплика.",
            "[00:00.05] Me: Это одинаковая длинная реплика.\n[00:00.1] Them: Это одинаковая длинная реплика.",
            "[00:00.25] Me: Это одинаковая длинная реплика."
        ]);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(3, output.Split("Это одинаковая длинная реплика.").Length - 1);
        Assert.Equal(1, output.Split("**Them:**").Length - 1);
    }

    [Fact]
    public async Task IdenticalUtterancesSeparatedByLongChunksArePreserved()
    {
        string directory = Prepare([
            "[00:00.8] Me: Это одинаковая длинная реплика.",
            "[00:00.05] Me: Это одинаковая длинная реплика."
        ], seconds: 120, totalSeconds: 240);
        var assembler = new TranscriptAssembler();
        var data = SessionStore.Read(directory);
        string hash = await assembler.BuildAsync(directory, data, default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("Это одинаковая длинная реплика.").Length - 1);
        Assert.Contains("[00:00] **Me:** Это одинаковая длинная реплика.", output);
        Assert.Contains("[01:58] **Me:** Это одинаковая длинная реплика.", output);
        Assert.Equal(hash, await assembler.BuildAsync(directory, data, default));
        Assert.Equal(output, File.ReadAllText(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task LongChunkBoundaryDuplicateIsRemovedWithoutLosingOtherSourceOrLaterRepetition()
    {
        string directory = Prepare([
            "[01:59.2] Me: MP3 track 42 не потерял начало?",
            "[00:01.2] Me: MP3 track 42 не потерял начало?\n[00:01.3] Them: MP3 track 42 не потерял начало?",
            "[00:01.2] Me: MP3 track 42 не потерял начало?"
        ], seconds: 120, totalSeconds: 360);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(3, output.Split("MP3 track 42 не потерял начало?").Length - 1);
        Assert.Contains("[01:59] **Me:** MP3 track 42 не потерял начало?", output);
        Assert.Contains("[01:59] **Them:** MP3 track 42 не потерял начало?", output);
        Assert.Contains("[03:59] **Me:** MP3 track 42 не потерял начало?", output);
    }

    [Fact]
    public async Task BoundarySuffixStartingBeforeOverlapIsRemovedButContinuationRemains()
    {
        string directory = Prepare([
            "[01:57.5] Me: Мы проверили прогноз погоды и расписание поездов.",
            "[00:00.1] Me: прогноз погоды и расписание поездов, теперь выбираем маршрут."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(1, output.Split("прогноз погоды и расписание поездов").Length - 1);
        Assert.Contains("[01:57] **Me:** Мы проверили прогноз погоды и расписание поездов.", output);
        Assert.Contains("[01:58] **Me:** теперь выбираем маршрут.", output);
    }

    [Fact]
    public async Task LongUtteranceStartingThreeSecondsBeforeOverlapReconcilesItsBoundarySuffix()
    {
        string directory = Prepare([
            "[01:55] Me: Сначала проверили билеты и расписание поездов, затем выбрали маршрут и собрали вещи.",
            "[00:00.1] Me: затем выбрали маршрут и собрали вещи."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(1, output.Split("затем выбрали маршрут и собрали вещи.").Length - 1);
        Assert.Contains("[01:55] **Me:** Сначала проверили билеты и расписание поездов, затем выбрали маршрут и собрали вещи.", output);
    }

    [Theory]
    [InlineData("01:56")]
    [InlineData("01:58.05")]
    public async Task InterveningSameSourceUtterancePreventsOldSuffixFromRemovingRealRepetition(string nextTime)
    {
        string directory = Prepare([
            $"[01:55] Me: Сначала проверили билеты и расписание поездов, затем выбрали маршрут и собрали вещи.\n[{nextTime}] Me: После этого обсудили отдельную проблему.",
            "[00:00.1] Me: затем выбрали маршрут и собрали вещи."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("затем выбрали маршрут и собрали вещи.").Length - 1);
        Assert.Contains("[01:58] **Me:** затем выбрали маршрут и собрали вещи.", output);
        Assert.Contains("**Me:** После этого обсудили отдельную проблему.", output);
    }

    [Fact]
    public async Task InterveningOtherSourceDoesNotEndOldSpeakerBoundarySuffix()
    {
        string directory = Prepare([
            "[01:55] Me: Сначала проверили билеты и расписание поездов, затем выбрали маршрут и собрали вещи.\n[01:56] Them: Нужно проверить расписание позже.",
            "[00:00.1] Me: затем выбрали маршрут и собрали вещи."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(1, output.Split("затем выбрали маршрут и собрали вещи.").Length - 1);
        Assert.Contains("**Them:** Нужно проверить расписание позже.", output);
    }

    [Fact]
    public async Task SameTimestampPriorUtterancesStillDeduplicateExactBoundaryCopy()
    {
        string directory = Prepare([
            "[01:59] Me: Выходим гулять после проверки погоды.\n[01:59] Me: Обсудим перенос прогулки на завтра.",
            "[00:01] Me: Выходим гулять после проверки погоды."
        ], seconds: 120, totalSeconds: 240);
        var data = SessionStore.Read(directory);
        var assembler = new TranscriptAssembler();
        string hash = await assembler.BuildAsync(directory, data, default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(1, output.Split("**Me:** Выходим гулять после проверки погоды.").Length - 1);
        Assert.Contains("**Me:** Обсудим перенос прогулки на завтра.", output);
        Assert.Equal(hash, await assembler.BuildAsync(directory, data, default));
    }

    [Fact]
    public async Task FullStatementRepeatedThreeSecondsAfterPriorStartIsPreserved()
    {
        string directory = Prepare([
            "[01:55] Me: Мы согласовали время начала прогулки.",
            "[00:00.1] Me: Мы согласовали время начала прогулки."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("Мы согласовали время начала прогулки.").Length - 1);
    }

    [Fact]
    public async Task DistantUtteranceWithMatchingSuffixIsNotReconciled()
    {
        string directory = Prepare([
            "[01:52] Me: Сначала проверили билеты и расписание поездов, затем выбрали маршрут и собрали вещи.",
            "[00:00.1] Me: затем выбрали маршрут и собрали вещи."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("затем выбрали маршрут и собрали вещи.").Length - 1);
    }

    [Fact]
    public async Task SameSourceRepetitionThreeSecondsApartInsideOverlapIsPreserved()
    {
        string directory = Prepare([
            "[01:58.1] Me: Подтверждаем готовность к прогулке.",
            "[00:03.5] Me: Подтверждаем готовность к прогулке."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("Подтверждаем готовность к прогулке.").Length - 1);
        Assert.Contains("[01:58] **Me:** Подтверждаем готовность к прогулке.", output);
        Assert.Contains("[02:01] **Me:** Подтверждаем готовность к прогулке.", output);
    }

    [Fact]
    public async Task OnePriorUtteranceCannotRemoveBothBoundaryCopyAndLaterRepetition()
    {
        string directory = Prepare([
            "[01:59.0] Me: Мы согласовали повторный просмотр фильма.\n[01:59.1] Them: Подтверждаем прогулку после проверки погоды.",
            "[00:01.0] Me: Мы согласовали повторный просмотр фильма.\n[00:01.1] Them: Подтверждаем прогулку после проверки погоды.\n[00:01.7] Me: Мы согласовали повторный просмотр фильма."
        ], seconds: 120, totalSeconds: 240);
        var data = SessionStore.Read(directory);
        var assembler = new TranscriptAssembler();
        string hash = await assembler.BuildAsync(directory, data, default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("**Me:** Мы согласовали повторный просмотр фильма.").Length - 1);
        Assert.Equal(1, output.Split("**Them:** Подтверждаем прогулку после проверки погоды.").Length - 1);
        Assert.Equal(hash, await assembler.BuildAsync(directory, data, default));
    }

    [Fact]
    public async Task NearestPriorBoundaryCopyDoesNotConsumeGenuineLaterRepetition()
    {
        string directory = Prepare([
            "[01:58.5] Me: Мы согласовали повторный просмотр фильма.\n[01:59.4] Me: Мы согласовали повторный просмотр фильма.",
            "[00:01.4] Me: Мы согласовали повторный просмотр фильма.\n[00:02.2] Me: Мы согласовали повторный просмотр фильма."
        ], seconds: 120, totalSeconds: 240);
        var data = SessionStore.Read(directory);
        var assembler = new TranscriptAssembler();
        string hash = await assembler.BuildAsync(directory, data, default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(3, output.Split("**Me:** Мы согласовали повторный просмотр фильма.").Length - 1);
        Assert.Contains("[02:00] **Me:** Мы согласовали повторный просмотр фильма.", output);
        Assert.Equal(hash, await assembler.BuildAsync(directory, data, default));
    }

    [Fact]
    public async Task PartialSuffixReconciliationCannotConsumeSamePriorUtteranceTwice()
    {
        string directory = Prepare([
            "[01:59.0] Me: Сначала проверили прогноз погоды и расписание поездов до озера.",
            "[00:01.0] Me: прогноз погоды и расписание поездов до озера, затем выбрали маршрут.\n[00:01.7] Me: прогноз погоды и расписание поездов до озера, потом проверили билеты."
        ], seconds: 120, totalSeconds: 240);
        await new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("прогноз погоды и расписание поездов до озера").Length - 1);
        Assert.Contains("**Me:** затем выбрали маршрут.", output);
        Assert.Contains("**Me:** прогноз погоды и расписание поездов до озера, потом проверили билеты.", output);
    }

    [Fact]
    public async Task TrimOffsetRetainsOriginalRecordingOriginAndHourFormat()
    {
        string directory = fixture.Session(120);
        var data = SessionStore.Read(directory);
        var chunks = AudioChunker.Plan(3600L * 48000, 3 * 48000, 120, data.Audio!.RelativeDirectory);
        data = data with { DurationSeconds = 3603, Trim = new(3600, 3603, true) { RelativeDirectory = "processed/trim_test" },
            Audio = data.Audio with { Chunks = chunks }, Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Completed, 1, 1) } };
        SessionStore.Save(directory, data);
        Save(directory, data, chunks[0], "[00:01] Me: Original origin.");
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        Assert.Contains("[01:00:01] **Me:**", File.ReadAllText(Path.Combine(directory, "transcription.md")));
    }

    [Theory]
    [InlineData("no timestamps")]
    [InlineData("[NO_SPEECH] trailing text")]
    [InlineData("[NO_SPEECH]\n[00:00] Me: Contradictory speech.")]
    [InlineData("```markdown\n``` ")]
    [InlineData("[00:60] Me: invalid seconds")]
    [InlineData("[20:00] Me: outside chunk")]
    public async Task MalformedTimedTextNeverPublishesAFullTranscript(string invalid)
    {
        string directory = Prepare([invalid], 120);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default));
        Assert.False(File.Exists(Path.Combine(directory, "transcription.md")));
        Assert.Empty(Directory.GetFiles(directory, ".transcription-*.tmp"));
    }

    [Fact]
    public async Task MonoUtterancesWithMissingRepeatedLabelsUseTheRecordedSource()
    {
        string directory = Prepare([
            "[00:00] Me: Я понял.",
            "[00:00] Them: Первое предложение.\n[00:01] Второе предложение без метки."
        ], seconds: 2, totalSeconds: 2, mono: true);
        var data = SessionStore.Read(directory);
        foreach (var chunk in data.Audio!.Chunks)
            TranscriptAssembler.ValidateChunk((await ChunkStore.ReadAsync(directory, data, chunk, default)).Text, chunk);
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        string transcript = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("[00:00] **Me:** Я понял.", transcript);
        Assert.Contains("[00:00] **Them:** Первое предложение.", transcript);
        Assert.Contains("[00:01] **Them:** Второе предложение без метки.", transcript);
        Assert.Equal(2, transcript.Split("**Them:**").Length - 1);
    }

    [Theory]
    [InlineData("[00:00] Them: А. [00:12] Them: Б.")]
    [InlineData("[00:00] А. [00:12] Б.")]
    [InlineData("[00:00] Them: А. [00:12] Б.")]
    [InlineData("[00:00] А. [00:12] Me: Б.")]
    [InlineData("[00:00] Them: А. [00:12] Б. [00:14]")]
    public async Task JoinedMonoTimestampsSplitIntoIndependentTrackAttributedUtterances(string reply)
    {
        string directory = Prepare(["[NO_SPEECH]", reply], seconds: 15, totalSeconds: 15, mono: true);
        var data = SessionStore.Read(directory);
        var them = data.Audio!.Chunks[1];
        TranscriptAssembler.ValidateChunk(reply, them);
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        string transcript = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, transcript.Split("**Them:**").Length - 1);
        Assert.Contains("[00:00] **Them:** А.", transcript);
        Assert.Contains("[00:12] **Them:** Б.", transcript);
        Assert.DoesNotContain("[00:12] Б.", transcript);
    }

    [Fact]
    public void JoinedExplicitRolesRemainUnambiguousWithoutTerminalPunctuation()
    {
        var them = AudioChunker.PlanMono(0, 15L * 48000, 15, "processed/test")[1];
        TranscriptAssembler.ValidateChunk("[00:00] Them: А [00:12] Them: Б", them);
    }

    [Fact]
    public void WholeSecondRoundingNearFractionalChunkEndRemainsValid()
    {
        // The final 2.9-second segment can legitimately be marked [00:03] by the model.
        var them = AudioChunker.PlanMono(0, 139200, 45, "processed/test")[1];
        TranscriptAssembler.ValidateChunk("[00:03] Them: Последняя реплика.", them);
        TranscriptAssembler.ValidateChunk("[00:02] Them: Полная фраза. [00:03]", them);
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk("[00:04] Them: Outside rounding tolerance.", them));
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk("[00:02] Them: Полная фраза. [00:04]", them));
    }

    // Synthetic analogues of the five parser rejects in the faithful 438-chunk research pass.
    // No actual meeting speech belongs in source-controlled regression fixtures.
    [Theory]
    [InlineData("[00:00] Me: Первое замечание без завершающей точки [00:02] Далее идёт достаточно длинное продолжение без новой метки.")]
    [InlineData("[00:00] Me: Нет [00:01] да [00:02] Сначала проверьте настройки, затем повторите действие.")]
    [InlineData("[00:00] Я считаю что это важно: не удаляйте резервную копию.")]
    [InlineData("[00:00] Me: Первая фраза без точки [00:03] Следующая длинная фраза тоже содержит слова: и отрицание.")]
    [InlineData("[00:03] Me: [NO_SPEECH]")]
    public void FaithfulFormattingFailuresCanBeRecoveredWithoutChangingSpeech(string reply)
    {
        var chunk = AudioChunker.PlanMono(0, 15L * 48000, 15, "processed/test")[0];
        TranscriptAssembler.ValidateChunk(reply, chunk);
        string normalized = TranscriptAssembler.NormalizeMonoTimestamps(reply);
        static string[] Tokens(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"[\p{L}\p{N}]+")
            .Select(m => m.Value).ToArray();
        Assert.Equal(Tokens(reply), Tokens(normalized));
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(reply, @"\[\d\d:\d\d\]").Select(m => m.Value),
            System.Text.RegularExpressions.Regex.Matches(normalized, @"\[\d\d:\d\d\]").Select(m => m.Value));
    }

    // Four historical joined responses plus two historical wrapped-silence responses.
    [Theory]
    [InlineData("[00:00] Me: Начало реплики без точки [00:01] Продолжение содержит прежнее отрицание и числовое значение 15.")]
    [InlineData("[00:00] Me: Начало с повтором [00:01] Продолжение тоже включает повтор и короткий ответ нет.")]
    [InlineData("[00:00] Me: Первое [00:01] второе [00:02] Мы оставляем текст без сокращений.")]
    [InlineData("[00:00] Me: Не изменяйте число 15 [00:01] Теперь важно оставить число 50.")]
    [InlineData("[00:00] [NO_SPEECH]")]
    [InlineData("[00:00] Me: [NO_SPEECH]")]
    public void HistoricalFormatShapesRecoverLocally(string reply)
    {
        var chunk = AudioChunker.PlanMono(0, 15L * 48000, 15, "processed/test")[0];
        TranscriptAssembler.ValidateChunk(reply, chunk);
    }

    [Fact]
    public void IncompleteHistoricalChunk318ShapeStillNeedsAudioRetry()
    {
        var chunk = AudioChunker.PlanMono(0, 15L * 48000, 15, "processed/test")[1];
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk(
            "[00:00] Them: Незаконченная фраза без точки [00:07]", chunk));
    }

    [Theory]
    [InlineData("[00:03] [NO_SPEECH]")]
    [InlineData("[00:03] Me: [NO_SPEECH]")]
    [InlineData("[00:03] Them: [NO_SPEECH]")]
    public void WrappedSilenceOnlyIsCanonicalForMonoSource(string reply)
    {
        var chunk = AudioChunker.PlanMono(0, 5L * 48000, 5, "processed/test")[0];
        TranscriptAssembler.ValidateChunk(reply, chunk);
    }

    [Theory]
    [InlineData("[[NO_SPEECH]")]
    [InlineData("[[NO_SPEECH]]")]
    [InlineData("[NO_SPEECH]]")]
    [InlineData("[NO_SPEECH")]
    [InlineData("NO_SPEECH]")]
    [InlineData("[no_speech]")]
    [InlineData("[NO SPEECH]")]
    [InlineData("[00:02] Me: [[NO_SPEECH]")]
    [InlineData("[00:02] Me: [NO_SPEECH]]")]
    [InlineData("- [[NO_SPEECH]")]
    [InlineData("```text\n[[NO_SPEECH]\n```")]
    public async Task SilenceMarkerTyposAreRecoveredWithoutInventingSpeech(string reply)
    {
        string directory = Prepare([reply, "[NO_SPEECH]"], seconds: 5, totalSeconds: 5, mono: true);
        var data = SessionStore.Read(directory);
        var chunk = data.Audio!.Chunks[0];
        TranscriptAssembler.ValidateChunk(reply, chunk);
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        Assert.DoesNotContain("**Me:**", File.ReadAllText(Path.Combine(directory, "transcription.md")));
        Assert.Equal(reply, (await ChunkStore.ReadAsync(directory, data, chunk, default)).Text);
    }

    [Theory]
    [InlineData("[[00:02] Me: Сохранить число 15.")]
    [InlineData("[[00:02]] Me: Сохранить число 15.")]
    [InlineData("[00:02]] Me: Сохранить число 15.")]
    [InlineData("[00:02 Me: Сохранить число 15.")]
    [InlineData("00:02 Me: Сохранить число 15.")]
    [InlineData("00:02] Me: Сохранить число 15.")]
    [InlineData("(00:02) Me: Сохранить число 15.")]
    [InlineData("- [[00:02] Me: Сохранить число 15.")]
    [InlineData("**[[00:02] Me: Сохранить число 15.**")]
    [InlineData("**[00:02]** Me: Сохранить число 15.")]
    [InlineData("[00:02] **Me:** Сохранить число 15.")]
    [InlineData("[00:02] **Me**: Сохранить число 15.")]
    [InlineData("[00:02] Me — Сохранить число 15.")]
    [InlineData("[00:02] Me： Сохранить число 15.")]
    [InlineData("[ 00:02 ] Me: Сохранить число 15.")]
    [InlineData("[[ 00:02 ]] Me: Сохранить число 15.")]
    [InlineData("```text\n[[00:02] Me: Сохранить число 15.\n```")]
    [InlineData("```markdown\n[00:02] Me: Сохранить число 15.\n```")]
    public async Task UnambiguousTimedFormattingIsRecoveredAndOriginalCheckpointPreserved(string reply)
    {
        string directory = Prepare([reply, "[NO_SPEECH]"], seconds: 5, totalSeconds: 5, mono: true);
        var data = SessionStore.Read(directory);
        var chunk = data.Audio!.Chunks[0];
        TranscriptAssembler.ValidateChunk(reply, chunk);
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        Assert.Contains("[00:02] **Me:** Сохранить число 15.",
            File.ReadAllText(Path.Combine(directory, "transcription.md")));
        Assert.Equal(reply, (await ChunkStore.ReadAsync(directory, data, chunk, default)).Text);
    }

    [Fact]
    public async Task JoinedExtraBracketTimestampsKeepAllSpeechAndLocalTimes()
    {
        const string reply = "[00:00] Me: Первая фраза. [[00:02] Me: Вторая фраза: 50.";
        string directory = Prepare([reply, "[NO_SPEECH]"], seconds: 5, totalSeconds: 5, mono: true);
        var data = SessionStore.Read(directory);
        TranscriptAssembler.ValidateChunk(reply, data.Audio!.Chunks[0]);
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        string text = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("[00:00] **Me:** Первая фраза.", text);
        Assert.Contains("[00:02] **Me:** Вторая фраза: 50.", text);
    }

    [Theory]
    [InlineData("[[NO_SPEECH]\n[00:02] Me: Реальная речь.")]
    [InlineData("[[00:60] Me: Неверные секунды.")]
    [InlineData("[[00:07] Me: Выход за пять секунд.")]
    [InlineData("[[00:03] Me: Позже.\n[[00:02] Me: Раньше.")]
    [InlineData("00:02 Speaker: Неизвестный источник.")]
    [InlineData("[00:02] Assistant: Выдуманная роль.")]
    [InlineData("Reasoning about audio\n[[00:02] Me: Реальная речь.")]
    [InlineData("[[00:02] Me: Реальная речь.\n[NO_SPEECH]")]
    [InlineData("```text\n[[00:02] Me: Реальная речь.")]
    [InlineData("```text\n[[00:02] Me: Реальная речь.\n```\nПояснение модели.")]
    public void StructuralRecoveryDoesNotIgnoreContradictionsOrInventRoles(string reply)
    {
        var chunk = AudioChunker.PlanMono(0, 5L * 48000, 5, "processed/test")[0];
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk(reply, chunk));
    }

    [Theory]
    [InlineData("[00:10] Me: [NO_SPEECH]")]
    [InlineData("[00:02] Me: Текст. [00:03] Me: [NO_SPEECH]")]
    [InlineData("[NO_SPEECH]\n[00:02] Me: Не молчание.")]
    [InlineData("[00:01] Assistant: Неверная метка.")]
    [InlineData("[00:01] Артём О: Модель придумала имя.")]
    [InlineData("[00:00] Me: модель произнесла [00:02] секунды")]
    [InlineData("[00:01] Me: Незаконченная фраза [00:03]")]
    [InlineData("[00:03] Me: Поздняя.\n[00:01] Me: Ранняя.")]
    public void AmbiguousMonoResponsesRemainRejected(string reply)
    {
        var chunk = AudioChunker.PlanMono(0, 5L * 48000, 5, "processed/test")[0];
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk(reply, chunk));
    }

    [Fact]
    public async Task JoinedMonoSpeechKeepsWordsTimecodesAndRawCheckpoint()
    {
        const string raw = "[00:00] Them: Я не согласен [00:02] Верно, да, снова повторяем: HTTP 503, а не 200.";
        string directory = Prepare(["[NO_SPEECH]", raw], seconds: 5, totalSeconds: 5, mono: true);
        var data = SessionStore.Read(directory);
        var them = data.Audio!.Chunks[1];
        TranscriptAssembler.ValidateChunk(raw, them);
        string beforeHash = Convert.ToHexString(Hash(ChunkStore.PathFor(directory, them.Index)));
        await new TranscriptAssembler().BuildAsync(directory, data, default);
        string result = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("[00:00] **Them:** Я не согласен", result);
        Assert.Contains("[00:02] **Them:** Верно, да, снова повторяем: HTTP 503, а не 200.", result);
        Assert.Equal(raw, (await ChunkStore.ReadAsync(directory, data, them, default)).Text);
        Assert.Equal(beforeHash, Convert.ToHexString(Hash(ChunkStore.PathFor(directory, them.Index))));
    }

    [Theory]
    [InlineData("[00:00] Them: А. [00:12] Them:")]
    [InlineData("[00:00] Them: модель говорит [00:12] секунд")]
    [InlineData("[00:00] Them: А. [99:99]")]
    [InlineData("[00:00] Them: А. [00:17]")]
    [InlineData("[00:00] Them: А [00:12]")]
    [InlineData("[00:00] Them: А. [00:12] Б [00:14]")]
    [InlineData("[00:00] Them: А. [00:17] Them: Б.")]
    [InlineData("[00:00] Them: А. [00:12] Assistant: Б.")]
    [InlineData("[00:00] Them: А. [00:12] Them: [NO_SPEECH]")]
    [InlineData("Analysis first.\n[00:00] Them: А. [00:12] Them: Б.")]
    [InlineData("[00:00] Them: А. [00:12] Them: Б. [00:14]\nUnrelated text")]
    public void JoinedMonoFragmentsWithLostSpeechOrAmbiguousStructureAreRejected(string reply)
    {
        var them = AudioChunker.PlanMono(0, 15L * 48000, 15, "processed/test")[1];
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk(reply, them));
    }

    [Theory]
    [InlineData("[00:01] Unknown Person: Invented voice.")]
    [InlineData("The user wants a transcript.\n[00:01] A real utterance.")]
    [InlineData("[00:01] A real utterance.\nLet's refine it.")]
    [InlineData("[00:01] Me:")]
    [InlineData("[00:60] Utterance with invalid seconds.")]
    [InlineData("[20:00] Utterance outside the chunk.")]
    public void UnsafeMonoFormattingIsStillRejected(string text)
    {
        var chunk = AudioChunker.PlanMono(0, 5L * 48000, 5, "processed/test")[0];
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk(text, chunk));
    }

    [Fact]
    public void StereoChunksStillRequireExplicitLabels()
    {
        var chunk = AudioChunker.Plan(0, 5L * 48000, 5, "processed/test")[0];
        Assert.Throws<InvalidDataException>(() => TranscriptAssembler.ValidateChunk("[00:01] Speech without speaker.", chunk));
    }

    [Fact]
    public async Task MissingChunkOrPartialStageNeverPublishesFullOutput()
    {
        string directory = fixture.Session();
        var data = SessionStore.Read(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TranscriptAssembler().BuildAsync(directory, data, default));
        data = data with { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Completed, 3, 3) } };
        SessionStore.Save(directory, data);
        Save(directory, data, data.Audio!.Chunks[0], "[00:00] Me: Only one chunk.");
        await Assert.ThrowsAsync<FileNotFoundException>(() => new TranscriptAssembler().BuildAsync(directory, data, default));
        Assert.False(File.Exists(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task ExistingEditedOutputIsNotOverwrittenAndCancellationLeavesNoPartialOutput()
    {
        string directory = Prepare(["[00:00] Me: test"], 120);
        string target = Path.Combine(directory, "transcription.md");
        File.WriteAllText(target, "user notes");
        await Assert.ThrowsAsync<InvalidDataException>(() => new TranscriptAssembler().BuildAsync(directory, SessionStore.Read(directory), default));
        Assert.Equal("user notes", File.ReadAllText(target));
        string second = Prepare(["[00:00] Me: test"], 120);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TranscriptAssembler().BuildAsync(second, SessionStore.Read(second), canceled.Token));
        Assert.False(File.Exists(Path.Combine(second, "transcription.md")));
    }

    internal string Prepare(string[] text, int seconds = 1, int totalSeconds = 3, bool mono = false)
    {
        string directory = fixture.Session(seconds);
        var data = SessionStore.Read(directory);
        if (totalSeconds != 3 || mono)
        {
            var chunks = mono
                ? AudioChunker.PlanMono(0, totalSeconds * 48000L, seconds, data.Audio!.RelativeDirectory)
                : AudioChunker.Plan(0, totalSeconds * 48000L, seconds, data.Audio!.RelativeDirectory);
            foreach (var chunk in chunks) File.WriteAllBytes(Path.Combine(directory, chunk.RelativePath), [1, 2, (byte)chunk.Index]);
            data = data with { DurationSeconds = totalSeconds, Audio = data.Audio with { Chunks = chunks } };
        }
        Assert.Equal(text.Length, data.Audio!.Chunks.Count);
        data = data with { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Completed, text.Length, text.Length) } };
        SessionStore.Save(directory, data);
        for (int i = 0; i < text.Length; i++) Save(directory, data, data.Audio!.Chunks[i], text[i]);
        return directory;
    }

    private static void Save(string directory, SessionData data, AudioChunk chunk, string text) => ChunkStore.Save(directory,
        new(data.Id, chunk, Convert.ToHexString(Hash(Path.Combine(directory, chunk.RelativePath))), "test-model", "test-prompt-hash", text));
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    public void Dispose() => fixture.Dispose();
}
