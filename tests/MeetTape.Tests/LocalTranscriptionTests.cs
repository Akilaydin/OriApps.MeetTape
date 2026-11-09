using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MeetTape.Tests;

public sealed class LocalTranscriptionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MeetTapeLocalAsr-" + Guid.NewGuid().ToString("N"));

    public LocalTranscriptionTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void JsonlSegmentsPreserveTimesButTrustTheRecordingSource()
    {
        const string json = """
            {"type":"batch_header","load_ms":300}
            {"file":"audio.wav","text":"Первая фраза. Вторая фраза.","segments":[{"t0_ms":1120,"t1_ms":3480,"speaker_id":2,"text":" Первая фраза."},{"t0_ms":5200,"t1_ms":6600,"text":" Вторая фраза."}]}
            """;
        Assert.Equal("[00:01.12] Them: Первая фраза.\n[00:05.20] Them: Вторая фраза.",
            LocalTranscriptionClient.FormatJsonl(json, "Them", 10, 2));
    }

    [Theory]
    [InlineData("{\"text\":\"  Привет,   коллеги. \"}", "[00:02] Me: Привет, коллеги.")]
    [InlineData("{\"text\":\"\",\"segments\":[]}", "[NO_SPEECH]")]
    [InlineData("{\"text\":\" \"}", "[NO_SPEECH]")]
    public void PlainTextAndSilenceUseOwnedChunkTime(string response, string expected)
        => Assert.Equal(expected, LocalTranscriptionClient.FormatJsonl(response, "Me", 32, 2));

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"text\":123}")]
    [InlineData("{\"text\":\"partial\",\"error\":\"OUTPUT_TRUNCATED\"}")]
    [InlineData("{\"text\":\"\",\"segments\":[{\"t0_ms\":-1,\"text\":\"speech\"}]}")]
    [InlineData("{\"text\":\"first\"}\n{\"text\":\"second\"}")]
    public void InvalidOrPartialLocalResultsAreRejected(string json)
        => Assert.Throws<InvalidDataException>(() => LocalTranscriptionClient.FormatJsonl(json, "Me", 10, 0));

    [Theory]
    [InlineData("{\"text\":\"Real speech.\",\"segments\":[{\"t0_ms\":12000,\"text\":\"Real speech.\"}]}")]
    [InlineData("{\"text\":\"Real speech.\",\"segments\":[{\"t0_ms\":2000,\"text\":\"First\"},{\"t0_ms\":1000,\"text\":\"second\"}]}")]
    public void InvalidSegmentTimingFallsBackToFullTextWithoutLosingSpeech(string json)
        => Assert.Equal("[00:02] Me: Real speech.", LocalTranscriptionClient.FormatJsonl(json, "Me", 10, 2));

    [Fact]
    public void SourceAndDurationMustBeKnownForNonemptySpeech()
    {
        Assert.Throws<InvalidDataException>(() => LocalTranscriptionClient.FormatJsonl(
            "{\"text\":\"Speech\"}", null, 10, 0));
        Assert.Throws<InvalidDataException>(() => LocalTranscriptionClient.FormatJsonl(
            "{\"text\":\"Speech\"}", "Me", 10, 12));
    }

    [Fact]
    public async Task ModelIdentitySeparatesTheContentsOfDifferentModelsAndLanguages()
    {
        string model = Path.Combine(directory, "model.gguf");
        File.WriteAllBytes(model, new byte[2048]);
        var settings = new LocalTranscriptionSettings { ModelPath = "model.gguf", Language = "ru" };
        string ru = await LocalTranscriptionClient.ModelIdentityAsync(settings, directory, default);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(new byte[2048])), ru);
        Assert.NotEqual(ru, await LocalTranscriptionClient.ModelIdentityAsync(settings with { Language = "en" }, directory, default));
        File.WriteAllBytes(model, Enumerable.Repeat((byte)1, 2048).ToArray());
        Assert.NotEqual(ru, await LocalTranscriptionClient.ModelIdentityAsync(settings, directory, default));
        Assert.Throws<InvalidDataException>(() => LocalTranscriptionClient.ModelPath(settings with { ModelPath = "missing.gguf" }, directory));
    }

    [Fact]
    public async Task OfflineChunkUsesFfmpegAndProducesExistingTimedTextWithoutCloudCalls()
    {
        string ffmpeg = ConfigurationLoader.ResolveFfmpegPath("ffmpeg", AppContext.BaseDirectory);
        string mp3 = Path.Combine(directory, "audio.mp3");
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList =
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i",
                "sine=frequency=330:sample_rate=16000:duration=2",
                "-c:a", "libmp3lame", "-b:a", "96k", mp3
            }, UseShellExecute = false, CreateNoWindow = true })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        File.WriteAllBytes(Path.Combine(directory, "model.gguf"), new byte[2048]);
        string runner = Path.Combine(directory, "fake.exe");
        File.WriteAllText(runner, "fake executable for injected process runner");
        int calls = 0;
        var client = new LocalTranscriptionClient((start, token) =>
        {
            calls++;
            Assert.True(File.Exists(start.ArgumentList[^2]));
            string wav = File.ReadAllText(start.ArgumentList[^2]).Trim();
            Assert.True(File.Exists(wav));
            Assert.Equal("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(wav), 0, 4));
            Assert.Contains("--batch-jsonl", start.ArgumentList);
            Assert.Contains("--backend", start.ArgumentList);
            return Task.FromResult("{\"type\":\"batch_header\"}\n{\"text\":\"Слова из локальной модели.\"}");
        });
        var reply = await client.TranscribeAsync(
            new LocalTranscriptionSettings { ModelPath = "model.gguf", RunnerPath = runner },
            directory, ffmpeg, mp3, "Me", 2, 0, default);
        Assert.Equal(1, calls);
        Assert.Equal("[00:00] Me: Слова из локальной модели.", reply.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mp3))), reply.AudioSha256);
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "MeetTape-ASR-*"),
            d => Directory.GetFiles(d, "audio.wav").Any(f => File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task LocalPipelineCompletesBothTracksAndReusesCheckpointsWithoutCloudKey()
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        var saved = SessionStore.Read(session);
        string audioPath = Path.Combine(directory, "both-tracks.mp3");
        string ffmpeg = ConfigurationLoader.ResolveFfmpegPath("ffmpeg", AppContext.BaseDirectory);
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi",
                "-i", "sine=frequency=440:sample_rate=16000:duration=3", "-c:a", "libmp3lame",
                "-b:a", "96k", audioPath },
            UseShellExecute = false, CreateNoWindow = true
        })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        string modelPath = Path.Combine(directory, "test.gguf");
        File.WriteAllBytes(modelPath, new byte[2048]);
        string runner = Path.Combine(directory, "runner.exe");
        File.WriteAllText(runner, "injected runner, not started");
        var local = new LocalTranscriptionSettings { ModelPath = modelPath, RunnerPath = runner, Language = "ru" };
        var chunks = AudioChunker.PlanMono(0, 3L * AudioChunker.SampleRate, 120, saved.Audio!.RelativeDirectory);
        foreach (var chunk in chunks)
            File.Copy(audioPath, Path.Combine(session, chunk.RelativePath), overwrite: true);
        saved = saved with
        {
            Audio = saved.Audio with { Chunks = chunks },
            Processing = saved.Processing with { TranscriptionMode = TranscriptionMode.Local, LocalTranscription = local }
        };
        SessionStore.Save(session, saved);
        var config = new LoadedConfiguration(new Configuration
        {
            MeetingsRoot = Path.GetDirectoryName(session)!,
            Transcription = saved.Processing.Transcription,
            Summary = saved.Processing.Summary,
            TranscriptionMode = TranscriptionMode.Local,
            LocalTranscription = local,
            Capture = new CaptureSettings { FfmpegPath = ffmpeg }
        }, saved.Processing.ConfigurationDirectory);
        int calls = 0;
        var localClient = new LocalTranscriptionClient((_, _) =>
        {
            calls++;
            return Task.FromResult("{\"type\":\"batch_header\"}\n{\"text\":\"Разговор для проверки.\"}");
        });
        using var jobs = new ProcessingJobs(config, () => false, localClient);
        await jobs.ProcessSessionAsync(session, default);

        var completed = SessionStore.Read(session);
        Assert.Equal(JobState.Completed, completed.JobState);
        Assert.Equal(2, calls);
        Assert.Equal(chunks.Count, completed.Stages["transcribe"].CompletedChunks);
        Assert.Contains("**Me:** Разговор для проверки.", File.ReadAllText(Path.Combine(session, "transcription.md")));
        Assert.Contains("**Them:** Разговор для проверки.", File.ReadAllText(Path.Combine(session, "transcription.md")));
        foreach (var chunk in chunks)
        {
            var checkpoint = await ChunkStore.ReadAsync(session, completed, chunk, default);
            Assert.StartsWith("local:transcribe.cpp:", checkpoint.Model);
            Assert.Equal("ru", checkpoint.Model.Split(':')[^1]);
        }
        await jobs.ProcessSessionAsync(session, default);
        Assert.Equal(2, calls);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
