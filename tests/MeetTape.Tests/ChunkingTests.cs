using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace MeetTape.Tests;

public sealed class ChunkingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetTapeChunks-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ThreeHourPlanHasContinuousOwnershipContextAndOriginalOffsets()
    {
        var chunks = AudioChunker.Plan(48000, 10800L * 48000, 120, "processed/test");
        Assert.Equal(90, chunks.Count);
        Assert.Equal(0, chunks[0].StartFrame);
        Assert.Equal(10800L * 48000, chunks[^1].EndFrame);
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.Equal(i + 1, chunk.Index);
            Assert.Equal(48000 + chunk.StartFrame, chunk.SourceStartFrame);
            Assert.InRange(chunk.EndFrame - chunk.StartFrame, 120L * 48000, 124L * 48000);
            if (i > 0)
            {
                Assert.Equal(chunks[i - 1].OwnEndFrame, chunk.OwnStartFrame);
                Assert.Equal(4L * 48000, chunks[i - 1].EndFrame - chunk.StartFrame);
            }
        }
        var last = AudioChunker.Plan(0, 251L * 48000, 120, "processed/test")[^1];
        Assert.Equal(11L * 48000, last.OwnEndFrame - last.OwnStartFrame);
        Assert.Equal(13L * 48000, last.EndFrame - last.StartFrame);
    }

    [Theory]
    [InlineData(0, 0, 120)]
    [InlineData(-1, 48000, 120)]
    [InlineData(0, 48000, 0)]
    [InlineData(0, 48000, 10801)]
    public void RejectsInvalidPlans(long start, long duration, int chunkSeconds) =>
        Assert.Throws<InvalidOperationException>(() => AudioChunker.Plan(start, duration, chunkSeconds, "processed/test"));

    [Fact]
    public void LargestSafePlanIncludesBoundaryContextWithinEightMiBBudget()
    {
        var chunks = AudioChunker.Plan(0, 10800L * 48000, AudioChunker.MaxChunkSeconds, "processed/test");
        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            long frames = chunk.EndFrame - chunk.StartFrame;
            // Conservative CBR size estimate; actual encoded byte count is validated separately.
            Assert.True(frames * 192_000 / (48000L * 8) < ChatApiClient.MaxAudioBytes);
        }
    }

    [Fact]
    public void EncodedSizeGuardAcceptsExactLimitAndRejectsOneByteOver()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "boundary.mp3");
        using (var file = File.Create(path)) file.SetLength(ChatApiClient.MaxAudioBytes);
        AudioChunker.ValidateUploadSize(path);
        using (var file = File.OpenWrite(path)) file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        Assert.Throws<InvalidDataException>(() => AudioChunker.ValidateUploadSize(path));
    }

    [Fact]
    public async Task IndependentMonoSourcesBoundaryContextAndLastChunkPreserveSignals()
    {
        string directory = await SessionAsync();
        byte[] meHash = Hash(Path.Combine(directory, "me.mp3")), themHash = Hash(Path.Combine(directory, "them.mp3"));
        var data = SessionStore.Read(directory);
        var audio = await new AudioChunker("ffmpeg").PrepareAsync(directory, data, default);
        AudioChunker.ValidateManifest(data with { Audio = audio });
        Assert.Equal(6, audio.Chunks.Count);
        for (int i = 0; i < audio.Chunks.Count; i++)
        {
            var chunk = audio.Chunks[i];
            Assert.Equal(i % 2 == 0 ? "Me" : "Them", chunk.Source);
            Assert.Equal(audio.Chunks[(i / 2) * 2].SourceStartFrame, chunk.SourceStartFrame);
            float[] pcm = await DecodeMonoAsync(Path.Combine(directory, chunk.RelativePath));
            Assert.Equal(chunk.EndFrame - chunk.StartFrame, pcm.LongLength);
            Assert.True(Rms(pcm, 0.05, 0.15) < 0.001 || i > 1);
            // Me's tone straddles the 2 s boundary, Them's tone straddles the 4 s boundary.
            if (i <= 2 && chunk.Source == "Me")
                Assert.True(Rms(pcm, 1.9 - chunk.StartFrame / 48000d, 2.1 - chunk.StartFrame / 48000d) > 0.05);
            if (i >= 3 && chunk.Source == "Them")
                Assert.True(Rms(pcm, 3.9 - chunk.StartFrame / 48000d, 4.1 - chunk.StartFrame / 48000d) > 0.05);
        }
        Assert.NotEqual(Hash(Path.Combine(directory, audio.Chunks[0].RelativePath)),
            Hash(Path.Combine(directory, audio.Chunks[1].RelativePath)));
        Assert.Equal(meHash, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Equal(themHash, Hash(Path.Combine(directory, "them.mp3")));
        Assert.Null(SessionStore.Read(directory).Audio);
    }

    [Fact]
    public async Task TrimmedInputCarriesOriginalOffsetAndKeepsDerivativesUnchanged()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        var trim = await new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1, 4.5, 5, "manual", default);
        string selected = Path.Combine(directory, trim.RelativeDirectory!);
        byte[] meHash = Hash(Path.Combine(selected, "me.mp3"));
        SessionStore.Save(directory, data with { Trim = trim });
        var audio = await new AudioChunker("ffmpeg").PrepareAsync(directory, SessionStore.Read(directory), default);
        Assert.Equal(4, audio.Chunks.Count);
        Assert.Equal(48000, audio.Chunks[0].SourceStartFrame);
        Assert.Equal(48000, audio.Chunks[1].SourceStartFrame);
        Assert.Equal(120000, audio.Chunks[2].SourceStartFrame);
        Assert.Equal(3.5 * 48000, audio.Chunks[^1].EndFrame);
        Assert.Equal(meHash, Hash(Path.Combine(selected, "me.mp3")));
    }

    [Fact]
    public async Task AutomaticPipelineDoesNotSelectCanceledOrFailedChunkSets()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        data = data with { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Pending) } };
        SessionStore.Save(directory, data);
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var jobs = new ProcessingJobs(config, () => false, readKey: _ => throw new Exception("No API call expected"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.ProcessSessionAsync(directory, canceled.Token));
        Assert.Null(SessionStore.Read(directory).Audio);
        using var duringPreparation = new CancellationTokenSource();
        var preparing = jobs.ProcessSessionAsync(directory, duringPreparation.Token);
        duringPreparation.Cancel();
        await preparing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(SessionStore.Read(directory).Audio);
        Assert.Equal(StageState.Pending, SessionStore.Read(directory).Stages["prepare"].Status);
        config = config with { Settings = config.Settings with { Capture = config.Settings.Capture with { FfmpegPath = "missing-ffmpeg-" + Guid.NewGuid() } } };
        using var failed = new ProcessingJobs(config, () => false, readKey: _ => throw new Exception("No API call expected"));
        await failed.ProcessSessionAsync(directory, default);
        data = SessionStore.Read(directory);
        Assert.Null(data.Audio);
        Assert.Equal(StageState.Paused, data.Stages["prepare"].Status);
        Assert.Equal(JobState.Paused, data.JobState);
    }

    [Fact]
    public async Task TruncatedSourcesEscapingPathsAndPendingTrimNeverChangeOriginals()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        byte[] original = Hash(Path.Combine(directory, "me.mp3"));
        var chunker = new AudioChunker("ffmpeg");
        await Assert.ThrowsAsync<InvalidDataException>(() => chunker.PrepareAsync(directory,
            data with { Trim = new(0, 5, true) { RelativeDirectory = "../outside" } }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chunker.PrepareAsync(directory,
            data with { Stages = new(data.Stages) { ["trim"] = new(true, StageState.AwaitingUser) } }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => chunker.PrepareAsync(directory,
            data with { DurationSeconds = 6 }, default));
        Assert.Equal(original, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Null(SessionStore.Read(directory).Audio);
    }

    internal async Task<string> SessionAsync(bool overlap = false, bool silentThem = false)
    {
        Directory.CreateDirectory(root);
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        var data = new SessionData
        {
            Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 5, RecordingState = RecordingState.Recorded,
            Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
            Processing = new(2, config.Settings.Transcription, config.Settings.Summary, config.Directory),
            Trim = new(0, 5, false) { Decision = "skip" },
            Stages = new() { ["trim"] = new(true, StageState.Completed), ["prepare"] = new(true, StageState.Pending),
                ["transcribe"] = new(true, StageState.Pending), ["assemble"] = new(true, StageState.Pending),
                ["summary"] = new(false, StageState.Disabled) }
        };
        var created = SessionStore.Create(root, data);
        using var lease = created.Lease;
        foreach (string name in new[] { "me.mp3", "them.mp3" })
        {
            using var encoder = new FfmpegEncoder("ffmpeg", Path.Combine(created.Directory, name));
            byte[] block = new byte[4800 * sizeof(float)];
            for (int n = 0; n < 50; n++)
            {
                for (int i = 0; i < 4800; i++)
                {
                    double time = (n * 4800 + i) / 48000d;
                    bool active = name == "me.mp3" ? time >= 1.8 && time < 2.2 :
                        !silentThem && (time >= 3.8 && time < 4.2 || overlap && time >= 1.8 && time < 2.2);
                    float value = active ? (float)(0.25 * Math.Sin(time * 2 * Math.PI * (name == "me.mp3" ? 440 : 880))) : 0;
                    BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(i * 4), value);
                }
                await encoder.Input.WriteAsync(block);
            }
            await encoder.CompleteAsync();
        }
        SessionStore.ProtectOriginals(created.Directory);
        return created.Directory;
    }

    private static async Task<float[]> DecodeMonoAsync(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-f", "f32le", "-ar", "48000", "-ac", "1", "pipe:1" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes).WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await errors);
        byte[] data = bytes.ToArray();
        float[] result = new float[data.Length / 4];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        return result;
    }

    private static double Rms(float[] pcm, double start, double end)
    {
        int first = (int)Math.Round(start * 48000), limit = (int)Math.Round(end * 48000);
        double sum = 0;
        for (int i = first; i < limit; i++) sum += pcm[i] * pcm[i];
        return Math.Sqrt(sum / (limit - first));
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
