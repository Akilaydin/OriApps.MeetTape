using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;

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
    public async Task StereoChannelsBoundaryContextAndLastChunkPreserveSourceSignals()
    {
        string directory = await SessionAsync();
        byte[] meHash = Hash(Path.Combine(directory, "me.mp3")), themHash = Hash(Path.Combine(directory, "them.mp3"));
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var saved = SessionStore.Read(directory);
        Assert.Equal(StageState.Completed, saved.Stages["prepare"].Status);
        Assert.Equal(3, saved.Stages["prepare"].TotalChunks);
        var audio = Assert.IsType<PreparedAudio>(saved.Audio);
        Assert.Equal(3, audio.Chunks.Count);
        for (int i = 0; i < audio.Chunks.Count; i++)
        {
            var chunk = audio.Chunks[i];
            float[] pcm = await DecodeStereoAsync(Path.Combine(directory, chunk.RelativePath));
            Assert.Equal((chunk.EndFrame - chunk.StartFrame) * 2, pcm.LongLength);
            Assert.True(Rms(pcm, 0, 0.05, 0.15) < 0.001 || i > 0);
            // Me's tone straddles the 2 s boundary, Them's tone straddles the 4 s boundary.
            if (i <= 1) Assert.True(Rms(pcm, 0, 1.9 - chunk.StartFrame / 48000d, 2.1 - chunk.StartFrame / 48000d) > 0.05);
            if (i >= 1) Assert.True(Rms(pcm, 1, 3.9 - chunk.StartFrame / 48000d, 4.1 - chunk.StartFrame / 48000d) > 0.05);
        }
        Assert.Equal(meHash, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Equal(themHash, Hash(Path.Combine(directory, "them.mp3")));
        byte[] metadata = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        Assert.Throws<InvalidOperationException>(() => workflow.PrepareChunks(directory));
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(directory, "session.json")));
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
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var audio = Assert.IsType<PreparedAudio>(SessionStore.Read(directory).Audio);
        Assert.Equal(2, audio.Chunks.Count);
        Assert.Equal(48000, audio.Chunks[0].SourceStartFrame);
        Assert.Equal(120000, audio.Chunks[1].SourceStartFrame);
        Assert.Equal(3.5 * 48000, audio.Chunks[^1].EndFrame);
        Assert.Equal(meHash, Hash(Path.Combine(selected, "me.mp3")));
    }

    [Fact]
    public async Task CancellationAndEncoderFailureNeverSelectPartialChunkSets()
    {
        string directory = await SessionAsync();
        using (var workflow = new PostRecordingWorkflow("ffmpeg"))
        {
            workflow.PrepareChunks(directory);
            workflow.CancelAndWait();
            Assert.Null(SessionStore.Read(directory).Audio);
            Assert.NotEqual(StageState.Completed, SessionStore.Read(directory).Stages["prepare"].Status);
        }
        using var failed = new PostRecordingWorkflow("missing-ffmpeg-" + Guid.NewGuid());
        failed.PrepareChunks(directory);
        await failed.Completion;
        var data = SessionStore.Read(directory);
        Assert.Null(data.Audio);
        Assert.Equal(StageState.Paused, data.Stages["prepare"].Status);
        Assert.Equal(JobState.Paused, data.JobState);
    }

    [Fact]
    public async Task TruncatedSourceAndEscapingTrimPathDoNotCompletePreparation()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AudioChunker("ffmpeg").PrepareAsync(directory,
            data with { Trim = new(0, 5, true) { RelativeDirectory = "../outside" } }, default));
        // Metadata requires 6 seconds while both real sources have only 5: no padding hides the loss.
        SessionStore.Save(directory, data with { DurationSeconds = 6 });
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(SessionStore.Read(directory).Audio);
        Assert.Equal(StageState.Paused, SessionStore.Read(directory).Stages["prepare"].Status);
    }

    [Fact]
    public async Task PendingTrimAndMetadataWriteFailureRetainSourcesAndPriorSelection()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        SessionStore.Save(directory, data with { Stages = new(data.Stages) { ["trim"] = new(true, StageState.AwaitingUser) } });
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        Assert.Throws<InvalidOperationException>(() => workflow.PrepareChunks(directory));
        SessionStore.Save(directory, data);
        string metadata = Path.Combine(directory, "session.json");
        byte[] before = File.ReadAllBytes(metadata), hash = Hash(Path.Combine(directory, "me.mp3"));
        File.SetAttributes(metadata, FileAttributes.ReadOnly);
        workflow.PrepareChunks(directory);
        await workflow.Completion;
        Assert.Equal(before, File.ReadAllBytes(metadata));
        Assert.Equal(hash, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Null(SessionStore.Read(directory).Audio);
        Assert.False(Directory.Exists(Path.Combine(directory, "processed")));
    }

    [Fact]
    public async Task RechunkLegacyOversizedSelectionLeavesOriginalAndOldChunkUntouched()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        string originalDirectory = "processed/audio_legacy";
        Directory.CreateDirectory(Path.Combine(directory, originalDirectory));
        var oldChunks = AudioChunker.Plan(0, 5L * 48000, 360, originalDirectory);
        string oldPath = Path.Combine(directory, oldChunks[0].RelativePath);
        using (var file = File.Create(oldPath)) file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        string foreignFile = Path.Combine(directory, "processed", "keep.txt");
        File.WriteAllText(foreignFile, "unrelated content");
        SessionStore.Save(directory, data with
        {
            Processing = data.Processing with { ChunkSeconds = 360 }, Audio = new(originalDirectory, oldChunks),
            Stages = new(data.Stages) { ["prepare"] = new(true, StageState.Completed),
                ["transcribe"] = new(true, StageState.Paused) }, JobState = JobState.Paused
        });
        byte[] original = Hash(Path.Combine(directory, "me.mp3"));
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.Rechunk(directory, 2);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var repaired = SessionStore.Read(directory);
        Assert.Equal(JobState.NotStarted, repaired.JobState);
        Assert.Equal(2, repaired.Processing.ChunkSeconds);
        Assert.Equal(3, repaired.Audio!.Chunks.Count);
        Assert.Null(repaired.Audio.Recovery);
        Assert.All(repaired.Audio.Chunks, chunk => Assert.True(new FileInfo(Path.Combine(directory, chunk.RelativePath)).Length <= ChatApiClient.MaxAudioBytes));
        Assert.Equal(ChatApiClient.MaxAudioBytes + 1L, new FileInfo(oldPath).Length);
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
        Assert.Equal(original, Hash(Path.Combine(directory, "me.mp3")));
    }

    [Fact]
    public async Task LegacyDurationWithoutSelectedAudioIsRejectedBeforeEncoding()
    {
        string directory = await SessionAsync();
        var data = SessionStore.Read(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AudioChunker("ffmpeg").PrepareAsync(directory,
            data with { Processing = data.Processing with { ChunkSeconds = 360 } }, default));
        Assert.False(Directory.Exists(Path.Combine(directory, "processed")));
    }

    [Fact]
    public async Task RechunkPreservesPaidPrefixAndOnlyUploadsNewChunks()
    {
        string directory = await SessionAsync();
        byte[] me = Hash(Path.Combine(directory, "me.mp3"));
        byte[] them = Hash(Path.Combine(directory, "them.mp3"));
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var data = SessionStore.Read(directory);
        var oldChunk = data.Audio!.Chunks[0];
        string oversizedPath = Path.Combine(directory, data.Audio.Chunks[1].RelativePath);
        using (var file = File.OpenWrite(oversizedPath)) file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        byte[] prepared = Hash(Path.Combine(directory, oldChunk.RelativePath));
        string sha = Convert.ToHexString(prepared);
        ChunkStore.Save(directory, new(data.Id, oldChunk, sha, "already-paid", "prompt-hash", "[00:01] Me: earlier words"));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        SessionStore.Save(directory, data with
        {
            Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Paused, 1, 3) },
            JobState = JobState.Paused
        });
        workflow.Rechunk(directory, 1);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var repaired = SessionStore.Read(directory);
        Assert.Equal(4, repaired.Audio!.Chunks.Count);
        Assert.Equal(oldChunk, repaired.Audio.Chunks[0]);
        Assert.Equal(1, repaired.Audio.Recovery!.PreservedChunks);
        Assert.Equal(2, repaired.Audio.Recovery.OriginalChunkSeconds);
        Assert.Equal(1, repaired.Processing.ChunkSeconds);
        var firstFresh = repaired.Audio.Chunks[1];
        long leftContext = AudioChunker.SampleRate / 4; // New one-second chunks use 0.25s context.
        Assert.Equal(oldChunk.OwnEndFrame, firstFresh.OwnStartFrame);
        Assert.Equal(oldChunk.OwnEndFrame - leftContext, firstFresh.StartFrame);
        Assert.Equal(firstFresh.StartFrame, firstFresh.SourceStartFrame);
        Assert.Equal(3L * AudioChunker.SampleRate / 4, oldChunk.EndFrame - firstFresh.StartFrame);
        float[] originalPcm = await DecodeStereoAsync(Path.Combine(directory, oldChunk.RelativePath));
        float[] recoveredPcm = await DecodeStereoAsync(Path.Combine(directory, firstFresh.RelativePath));
        Assert.Equal((firstFresh.EndFrame - firstFresh.StartFrame) * 2, recoveredPcm.LongLength);
        Assert.True(Rms(originalPcm, 0, 1.85, 2.15) > 0.05);
        double freshStartSeconds = firstFresh.StartFrame / (double)AudioChunker.SampleRate;
        Assert.True(Rms(recoveredPcm, 0, 1.85 - freshStartSeconds, 1.95 - freshStartSeconds) > 0.05);
        Assert.True(Rms(recoveredPcm, 0, 2.05 - freshStartSeconds, 2.15 - freshStartSeconds) > 0.05);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(prepared, Hash(Path.Combine(directory, oldChunk.RelativePath)));
        Assert.Equal(me, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Equal(them, Hash(Path.Combine(directory, "them.mp3")));
        Assert.Equal(ChatApiClient.MaxAudioBytes + 1L, new FileInfo(oversizedPath).Length);

        using var handler = new CountingResponseHandler();
        using var http = new HttpClient(handler);
        var example = ConfigurationLoader.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json")));
        using var jobs = new ProcessingJobs(example, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
    }

    [Fact]
    public async Task RechunkAfterTrimKeepsOriginalSourceOffsetForRecoveredLeftContext()
    {
        string directory = await SessionAsync();
        var initial = SessionStore.Read(directory);
        var trim = await new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1, 4.5, 5, "manual", default);
        SessionStore.Save(directory, initial with { Trim = trim });
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var old = SessionStore.Read(directory);
        var preserved = old.Audio!.Chunks[0];
        ChunkStore.Save(directory, new(old.Id, preserved,
            Convert.ToHexString(Hash(Path.Combine(directory, preserved.RelativePath))), "already-paid", "prompt-hash",
            "[00:01] Me: paid words"));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        byte[] originalMe = Hash(Path.Combine(directory, "me.mp3"));
        byte[] trimmedMe = Hash(Path.Combine(directory, trim.RelativeDirectory!, "me.mp3"));
        using (var file = File.OpenWrite(Path.Combine(directory, old.Audio.Chunks[1].RelativePath)))
            file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        SessionStore.Save(directory, old with
        { Stages = new(old.Stages) { ["transcribe"] = new(true, StageState.Paused) }, JobState = JobState.Paused });

        workflow.Rechunk(directory, 1);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var repaired = SessionStore.Read(directory);
        Assert.Equal(StageState.Completed, repaired.Stages["prepare"].Status);
        var firstFresh = repaired.Audio!.Chunks[1];
        Assert.Equal(preserved.OwnEndFrame, firstFresh.OwnStartFrame);
        Assert.Equal(7L * AudioChunker.SampleRate / 4, firstFresh.StartFrame);
        Assert.Equal(firstFresh.StartFrame + AudioChunker.SampleRate, firstFresh.SourceStartFrame);
        Assert.Equal(3L * AudioChunker.SampleRate / 4, preserved.EndFrame - firstFresh.StartFrame);
        float[] pcm = await DecodeStereoAsync(Path.Combine(directory, firstFresh.RelativePath));
        double signalStart = 3.9 - firstFresh.SourceStartFrame / (double)AudioChunker.SampleRate;
        Assert.True(Rms(pcm, 1, signalStart, signalStart + 0.2) > 0.05);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(originalMe, Hash(Path.Combine(directory, "me.mp3")));
        Assert.Equal(trimmedMe, Hash(Path.Combine(directory, trim.RelativeDirectory!, "me.mp3")));
    }

    [Fact]
    public async Task RechunkRejectsNonPrefixPaidResultsWithoutReplacingFiles()
    {
        string directory = await SessionAsync();
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var data = SessionStore.Read(directory);
        string oversizedPath = Path.Combine(directory, data.Audio!.Chunks[0].RelativePath);
        using (var file = File.OpenWrite(oversizedPath)) file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        var second = data.Audio!.Chunks[1];
        ChunkStore.Save(directory, new(data.Id, second, Convert.ToHexString(Hash(Path.Combine(directory, second.RelativePath))),
            "already-paid", "prompt-hash", "[00:01] Me: second"));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 2));
        SessionStore.Save(directory, data with
        { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Paused) }, JobState = JobState.Paused });
        int recovered = 0;
        workflow.Rechunk(directory, 1, _ => recovered++);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var saved = SessionStore.Read(directory);
        Assert.Equal(0, recovered);
        Assert.Equal(JobState.Paused, saved.JobState);
        Assert.Equal(data.Audio.RelativeDirectory, saved.Audio!.RelativeDirectory);
        Assert.Equal(data.Audio.Chunks, saved.Audio.Chunks);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 2)));
        Assert.Equal(2, saved.Processing.ChunkSeconds);
    }

    [Fact]
    public async Task RechunkDoesNotReprepareAlreadyUploadableChunks()
    {
        string directory = await SessionAsync();
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        byte[] metadata = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        Assert.Throws<InvalidOperationException>(() => workflow.Rechunk(directory, 1));
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(directory, "session.json")));
    }

    [Fact]
    public async Task CanceledRechunkDoesNotSignalUnblockOrSelectReplacement()
    {
        string directory = await SessionAsync();
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var old = SessionStore.Read(directory);
        using (var file = File.OpenWrite(Path.Combine(directory, old.Audio!.Chunks[0].RelativePath)))
            file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        SessionStore.Save(directory, old with
        { Stages = new(old.Stages) { ["transcribe"] = new(true, StageState.Paused) }, JobState = JobState.Paused });

        int unblocks = 0;
        workflow.Rechunk(directory, 1, _ => unblocks++);
        workflow.CancelAndWait();
        var saved = SessionStore.Read(directory);
        Assert.Equal(0, unblocks);
        Assert.Equal(JobState.Paused, saved.JobState);
        Assert.Equal(old.Audio.RelativeDirectory, saved.Audio!.RelativeDirectory);
        Assert.Equal(old.Processing.ChunkSeconds, saved.Processing.ChunkSeconds);
    }

    [Fact]
    public async Task Oversize413BlockIsClearedOnlyAfterSuccessfulRechunkInSameProcess()
    {
        string directory = await SessionAsync();
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.PrepareChunks(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var data = SessionStore.Read(directory);
        string oversized = Path.Combine(directory, data.Audio!.Chunks[0].RelativePath);
        using (var file = File.OpenWrite(oversized)) file.SetLength(ChatApiClient.MaxAudioBytes + 1L);
        SessionStore.Save(directory, data with
        { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Pending) } });

        var example = ConfigurationLoader.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json")));
        var config = example with { Settings = example.Settings with { MeetingsRoot = root } };
        using var handler = new CountingResponseHandler();
        using var http = new HttpClient(handler);
        var scans = Channel.CreateUnbounded<bool>();
        using var nextScan = new SemaphoreSlim(0);
        using var jobs = new ProcessingJobs(config, () => false, http,
            delay: async (_, token) =>
            {
                scans.Writer.TryWrite(true); // The scanner has finished this pass.
                await nextScan.WaitAsync(token);
            }, readKey: _ => "test-key");
        jobs.Start();
        await WaitForScanAsync(scans.Reader, directory, jobs);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal(StageState.Paused, SessionStore.Read(directory).Stages["transcribe"].Status);
        Assert.Equal(0, handler.Calls); // Local 413 was raised before any HTTP call.

        jobs.PauseAndWait();
        workflow.Rechunk(directory, 1, jobs.UnblockRecovered);
        jobs.Start(); // CLI resumes its worker before asynchronous rechunk has finished.
        await WaitForScanAsync(scans.Reader, directory, jobs);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var rechunked = SessionStore.Read(directory);
        Assert.True(rechunked.Stages["prepare"].Status == StageState.Completed,
            $"Rechunk did not complete: {rechunked.Error}; state={rechunked.JobState}.");
        nextScan.Release(); // Force a scanner pass after the rechunk callback unblocks the session.
        await WaitForScanAsync(scans.Reader, directory, jobs);
        var completed = SessionStore.Read(directory);
        Assert.True(completed.JobState == JobState.Completed,
            $"Processing ended in {completed.JobState}, transcribe={completed.Stages["transcribe"].Status}, " +
            $"error={completed.Error}, HTTP calls={handler.Calls}.");
        Assert.Equal(5, handler.Calls);
        Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
        Assert.Equal(StageState.Completed, completed.Stages["transcribe"].Status);
        Assert.Equal(ChatApiClient.MaxAudioBytes + 1L, new FileInfo(oversized).Length);
        jobs.PauseAndWait();
    }

    private static async Task WaitForScanAsync(ChannelReader<bool> scans, string directory, ProcessingJobs jobs)
    {
        try { await scans.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            var session = SessionStore.Read(directory);
            Assert.Fail($"Background scanner did not finish a pass: state={session.JobState}, " +
                $"prepare={session.Stages["prepare"].Status}, transcribe={session.Stages["transcribe"].Status}, " +
                $"error={session.Error}, worker={jobs.Completion.Status}.");
        }
    }

    private sealed class CountingResponseHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:00] Me: new words\"}}]}") });
        }
    }

    internal async Task<string> SessionAsync()
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
            Stages = new() { ["trim"] = new(true, StageState.Completed), ["prepare"] = new(true, StageState.Pending) }
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
                    bool active = name == "me.mp3" ? time >= 1.8 && time < 2.2 : time >= 3.8 && time < 4.2;
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

    private static async Task<float[]> DecodeStereoAsync(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-f", "f32le", "-ar", "48000", "-ac", "2", "pipe:1" })
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

    private static double Rms(float[] pcm, int channel, double start, double end)
    {
        int first = (int)Math.Round(start * 48000), limit = (int)Math.Round(end * 48000);
        double sum = 0;
        for (int i = first; i < limit; i++) sum += pcm[i * 2 + channel] * pcm[i * 2 + channel];
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
