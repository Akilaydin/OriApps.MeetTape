using NAudio.Wave;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;

namespace MeetTape.Tests;

public sealed class OptimizedLocalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MeetTapeOptimized-" + Guid.NewGuid().ToString("N"));

    public OptimizedLocalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void NativePreparationPlansLongerWavWindowsWithoutChangingCloudMp3Plan()
    {
        long frames = 440L * AudioChunker.SampleRate;
        var local = LocalAudioChunker.Plan(0, frames, "processed/local_test");
        var cloud = AudioChunker.PlanMono(0, frames, 45, "processed/cloud_test");
        Assert.Equal(6, local.Count);
        Assert.Equal(20, cloud.Count);
        Assert.All(local, chunk =>
        {
            Assert.EndsWith(".wav", chunk.RelativePath);
            Assert.True(chunk.EndFrame - chunk.StartFrame <= 184L * AudioChunker.SampleRate);
        });
        Assert.Equal(["Me", "Them", "Me", "Them", "Me", "Them"], local.Select(c => c.Source));
        Assert.Equal("processed/local_test/0001-me.wav", local[0].RelativePath.Replace('\\', '/'));
        Assert.EndsWith(".mp3", cloud[0].RelativePath);
    }

    [Fact]
    public async Task MissingLocalModelStopsBeforePreparingLargeWavFiles()
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        var before = SessionStore.Read(session);
        var missing = Settings() with { ModelPath = Path.Combine(directory, "missing.gguf") };
        using var jobs = new ProcessingJobs(Config(session, before, missing, "ffmpeg", TranscriptionMode.LocalBatch),
            () => false);
        await jobs.ProcessSessionAsync(session, default);
        var after = SessionStore.Read(session);
        Assert.Equal(JobState.Paused, after.JobState);
        Assert.Null(after.LocalAudio);
        Assert.Equal(before.Audio!.RelativeDirectory, after.Audio!.RelativeDirectory);
        Assert.Empty(Directory.GetDirectories(Path.Combine(session, "processed"), "local_*"));
    }

    [Fact]
    public async Task NativeBatchPublishesBothTracksWithIndependentCheckpointsAndOneModelLoad()
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        string ffmpeg = await WriteTestAudioAsync(session);
        var before = SessionStore.Read(session);
        var localSettings = Settings();
        var configuration = Config(session, before, localSettings, ffmpeg, TranscriptionMode.LocalBatch);
        int runs = 0;
        var local = new LocalTranscriptionClient(runBatch: async (start, emit, token) =>
        {
            runs++;
            var files = InputFiles(start);
            Assert.Equal(2, files.Count);
            Assert.Contains("--batch-jsonl", start.ArgumentList);
            Assert.Equal("ru", start.ArgumentList[start.ArgumentList.IndexOf("-l") + 1]);
            await emit("{\"type\":\"batch_header\",\"load_ms\":20}");
            foreach (string file in files)
            {
                using var wav = new WaveFileReader(file);
                Assert.Equal(16000, wav.WaveFormat.SampleRate);
                Assert.Equal(1, wav.WaveFormat.Channels);
                Assert.Equal(16, wav.WaveFormat.BitsPerSample);
                await emit(JsonSerializer.Serialize(new
                {
                    file,
                    text = "Два потока распознавания.",
                    segments = new[] { new { t0_ms = 750, t1_ms = 2200, text = "Два потока распознавания." } }
                }));
            }
        });
        using var jobs = new ProcessingJobs(configuration, () => false, local);
        await jobs.ProcessSessionAsync(session, default);

        var after = SessionStore.Read(session);
        Assert.Equal(JobState.Completed, after.JobState);
        Assert.Equal(1, runs);
        Assert.NotNull(after.LocalAudio);
        Assert.Equal(before.Audio!.RelativeDirectory, after.Audio!.RelativeDirectory);
        Assert.Equal(before.Audio.Chunks, after.Audio.Chunks);
        Assert.Equal(2, after.Stages["transcribe"].CompletedChunks);
        Assert.True(Directory.Exists(Path.Combine(session, "chunks", "local")));
        Assert.False(File.Exists(ChunkStore.PathFor(session, 1)));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(session, "chunks", "local"), "*.json").Length);
        var markdown = File.ReadAllText(Path.Combine(session, "transcription.md"));
        Assert.Contains("**Me:** Два потока распознавания.", markdown);
        Assert.Contains("**Them:** Два потока распознавания.", markdown);
        foreach (var chunk in after.LocalAudio!.Chunks)
            Assert.StartsWith("local:transcribe.cpp:", (await ChunkStore.ReadAsync(session, after, chunk, default, true)).Model);
        await jobs.ProcessSessionAsync(session, default);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task FailedBatchResumesRemainingSegmentWithoutRepeatingSavedOutput()
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        string ffmpeg = await WriteTestAudioAsync(session);
        var settings = Settings();
        var config = Config(session, SessionStore.Read(session), settings, ffmpeg, TranscriptionMode.LocalBatch);
        int runs = 0;
        var inputsPerRun = new List<int>();
        var local = new LocalTranscriptionClient(runBatch: async (start, emit, token) =>
        {
            runs++;
            var files = InputFiles(start);
            inputsPerRun.Add(files.Count);
            await emit("{\"type\":\"batch_header\"}");
            await emit(JsonSerializer.Serialize(new { file = files[0], text = "Успешная часть." }));
            if (runs == 1) throw new InvalidDataException("Simulated native interruption");
        });
        using var jobs = new ProcessingJobs(config, () => false, local);
        await jobs.ProcessSessionAsync(session, default);
        var paused = SessionStore.Read(session);
        Assert.Equal(JobState.Paused, paused.JobState);
        Assert.Equal(1, paused.Stages["transcribe"].CompletedChunks);
        Assert.NotNull(paused.LocalAudio);
        Assert.True(File.Exists(ChunkStore.PathFor(session, 1, true)));
        Assert.False(File.Exists(Path.Combine(session, "transcription.md")));

        jobs.Resume(session);
        await jobs.ProcessSessionAsync(session, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(session).JobState);
        Assert.Equal([2, 1], inputsPerRun);
        Assert.Equal(2, runs);
        Assert.True(File.Exists(Path.Combine(session, "transcription.md")));
        Assert.True(File.Exists(ChunkStore.PathFor(session, 2, true)));
    }

    [Fact]
    public async Task CancelActiveBatchRetainsFlushedCheckpointAndResumeProcessesOnlyRemainingAudio()
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        string ffmpeg = await WriteTestAudioAsync(session);
        var settings = Settings();
        var config = Config(session, SessionStore.Read(session), settings, ffmpeg, TranscriptionMode.LocalBatch);
        var firstSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        var local = new LocalTranscriptionClient(runBatch: async (start, emit, token) =>
        {
            runs++;
            var files = InputFiles(start);
            await emit("{\"type\":\"batch_header\"}");
            await emit(JsonSerializer.Serialize(new { file = files[0], text = "Успешная речь." }));
            if (runs == 1)
            {
                firstSaved.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        });
        using var jobs = new ProcessingJobs(config, () => false, local);
        Task processing = jobs.ProcessSessionAsync(session, default);
        await firstSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(() => jobs.Cancel(session)).WaitAsync(TimeSpan.FromSeconds(5));
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        var canceled = SessionStore.Read(session);
        Assert.Equal(JobState.Canceled, canceled.JobState);
        Assert.True(File.Exists(ChunkStore.PathFor(session, 1, true)));
        Assert.False(File.Exists(ChunkStore.PathFor(session, 2, true)));
        Assert.False(File.Exists(Path.Combine(session, "transcription.md")));

        jobs.Resume(session);
        await jobs.ProcessSessionAsync(session, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(session).JobState);
        Assert.Equal(2, runs);
        Assert.True(File.Exists(Path.Combine(session, "transcription.md")));
    }

    [Fact]
    public async Task SwitchingFromIncompleteNativeBatchToCloudReusesExistingCloudAudioAndDoesNotTouchLocalCheckpoint()
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        string ffmpeg = await WriteTestAudioAsync(session);
        var before = SessionStore.Read(session);
        var localSettings = Settings();
        var fastConfig = Config(session, before, localSettings, ffmpeg, TranscriptionMode.LocalBatch);
        var local = new LocalTranscriptionClient(runBatch: async (start, emit, token) =>
        {
            var files = InputFiles(start);
            await emit("{\"type\":\"batch_header\"}");
            await emit(JsonSerializer.Serialize(new { file = files[0], text = "Сохранённый локальный результат." }));
            throw new InvalidDataException("Pause native to test Cloud resume");
        });
        int cloudRequests = 0;
        using var http = new HttpClient(new FakeHandler((request, token) =>
        {
            cloudRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"finish_reason":"stop","message":{"content":"[00:00] Me: Сохранённый облачный результат."}}]}""",
                    Encoding.UTF8, "application/json")
            });
        }));
        using var jobs = new ProcessingJobs(fastConfig, () => false, local, http, _ => "test-key");
        await jobs.ProcessSessionAsync(session, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(session).JobState);
        string localCheckpoint = ChunkStore.PathFor(session, 1, localBatch: true);
        byte[] localBytes = File.ReadAllBytes(localCheckpoint);
        byte[] existingCloudMp3 = File.ReadAllBytes(Path.Combine(session, before.Audio!.Chunks[0].RelativePath));

        jobs.ApplyConfiguration(Config(session, before, localSettings, ffmpeg, TranscriptionMode.CloudApi));
        jobs.Resume(session);
        await jobs.ProcessSessionAsync(session, default);

        var completed = SessionStore.Read(session);
        Assert.Equal(JobState.Completed, completed.JobState);
        Assert.Equal(1, cloudRequests);
        Assert.NotNull(completed.LocalAudio);
        Assert.Equal(before.Audio!.RelativeDirectory, completed.Audio!.RelativeDirectory);
        Assert.Equal(before.Audio.Chunks, completed.Audio.Chunks);
        Assert.Equal(localBytes, File.ReadAllBytes(localCheckpoint));
        Assert.Equal(existingCloudMp3, File.ReadAllBytes(Path.Combine(session, before.Audio.Chunks[0].RelativePath)));
        Assert.True(File.Exists(ChunkStore.PathFor(session, 1)));
        Assert.Contains("Сохранённый облачный результат.", File.ReadAllText(Path.Combine(session, "transcription.md")));
    }

    [Fact]
    public async Task NativeBatchUsesAsciiAliasesForUnicodeWindowsPathsWithoutChangingSource()
    {
        string folder = Path.Combine(directory, "Русский созвон", "Аудиодорожки");
        Directory.CreateDirectory(folder);
        string wav = Path.Combine(folder, "голос-me.wav");
        byte[] bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(wav, bytes);
        string modelFolder = Path.Combine(directory, "Русская модель");
        Directory.CreateDirectory(modelFolder);
        string model = Path.Combine(modelFolder, "голос.gguf");
        File.WriteAllBytes(model, new byte[2048]);
        string runner = Path.Combine(directory, "fake.exe");
        File.WriteAllText(runner, "test executable");
        var settings = new LocalTranscriptionSettings { ModelPath = model, RunnerPath = runner };
        var chunk = new AudioChunk(1, "processed/local_abc/0001-me.wav", 0, AudioChunker.SampleRate,
            0, AudioChunker.SampleRate, 0, "Me");
        int results = 0;
        var client = new LocalTranscriptionClient(runBatch: async (start, emit, token) =>
        {
            var input = Assert.Single(InputFiles(start));
            Assert.All(input, c => Assert.True(c <= 127));
            Assert.NotEqual(wav, input);
            Assert.Equal(bytes, File.ReadAllBytes(input));
            string modelAlias = start.ArgumentList[start.ArgumentList.IndexOf("-m") + 1];
            Assert.All(modelAlias, c => Assert.True(c <= 127));
            Assert.Equal(File.ReadAllBytes(model), File.ReadAllBytes(modelAlias));
            await emit("{\"type\":\"batch_header\"}");
            await emit(JsonSerializer.Serialize(new { file = input, text = "Говорю по-русски." }));
        });
        await client.TranscribeBatchAsync(settings, directory,
            [new LocalTranscriptionClient.BatchInput(chunk, wav)], (recorded, response) =>
            {
                Assert.Equal(chunk, recorded);
                Assert.Equal("[00:00] Me: Говорю по-русски.", response.Text);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), response.AudioSha256);
                results++;
                return Task.CompletedTask;
            }, default);
        Assert.Equal(1, results);
        Assert.Equal(bytes, File.ReadAllBytes(wav));
        Assert.Equal(new byte[2048], File.ReadAllBytes(model));
    }

    [Fact]
    public void CyrillicWindowsTempUsesPrivateAsciiFallbackWithoutDisclosingAudio()
    {
        string fakeUnicodeTemp = Path.Combine(directory, "Русский пользователь");
        string sharedRoot = Path.Combine(directory, "common-data");
        Directory.CreateDirectory(fakeUnicodeTemp);
        Directory.CreateDirectory(sharedRoot);
        string fallback = LocalTranscriptionClient.CreateBatchDirectory(fakeUnicodeTemp, sharedRoot,
            shortPath: _ => null);
        try
        {
            Assert.StartsWith(sharedRoot, fallback, StringComparison.OrdinalIgnoreCase);
            Assert.All(fallback, c => Assert.True(c <= 127));
            var acl = new DirectoryInfo(fallback).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Contains(acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<FileSystemAccessRule>(), rule =>
                    rule.IdentityReference.Equals(System.Security.Principal.WindowsIdentity.GetCurrent().User));
        }
        finally { Directory.Delete(fallback, recursive: true); }
    }

    [Fact]
    public void UnicodeTempPrefersAsciiShortPathOverSharedFallback()
    {
        string shortTemp = Path.Combine(directory, "shorttemp");
        Directory.CreateDirectory(shortTemp);
        string folder = LocalTranscriptionClient.CreateBatchDirectory(
            Path.Combine(directory, "Русский пользователь"), Path.Combine(directory, "other"),
            shortPath: _ => shortTemp);
        try { Assert.StartsWith(shortTemp, folder, StringComparison.OrdinalIgnoreCase); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private LocalTranscriptionSettings Settings()
    {
        string model = Path.Combine(directory, "model.gguf");
        string runner = Path.Combine(directory, "runner.exe");
        File.WriteAllBytes(model, new byte[2048]);
        File.WriteAllText(runner, "fake runner");
        return new LocalTranscriptionSettings { ModelPath = model, RunnerPath = runner, Language = "ru" };
    }

    private static LoadedConfiguration Config(string session, SessionData data, LocalTranscriptionSettings local,
        string ffmpeg, TranscriptionMode mode) => new(new Configuration
        {
            MeetingsRoot = Path.GetDirectoryName(session)!,
            Transcription = mode == TranscriptionMode.LocalBatch
                ? data.Processing.Transcription with { PromptFile = "missing-unused-gemini-prompt.md" }
                : data.Processing.Transcription,
            Summary = data.Processing.Summary,
            TranscriptionMode = mode,
            LocalTranscription = local,
            Capture = new CaptureSettings { FfmpegPath = ffmpeg }
        }, data.Processing.ConfigurationDirectory);

    private static IReadOnlyList<string> InputFiles(ProcessStartInfo start)
    {
        int index = start.ArgumentList.IndexOf("--batch");
        Assert.True(index >= 0);
        return File.ReadAllLines(start.ArgumentList[index + 1]);
    }

    private static async Task<string> WriteTestAudioAsync(string session)
    {
        string ffmpeg = ConfigurationLoader.ResolveFfmpegPath("ffmpeg", AppContext.BaseDirectory);
        string path = Path.Combine(session, "me.mp3");
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
                "-c:a", "libmp3lame", "-b:a", "96k", path },
            CreateNoWindow = true, UseShellExecute = false
        })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        File.Copy(path, Path.Combine(session, "them.mp3"), overwrite: true);
        return ffmpeg;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => send(request, token);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
