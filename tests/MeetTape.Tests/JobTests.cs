using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MeetTape.Tests;

public sealed class JobTests : IDisposable
{
    [Fact]
    public async Task RenamedPausedMeetingCanResumeAndPublishTranscriptFromItsExistingRelativeChunks()
    {
        string directory = Session(120);
        var data = SessionStore.Read(directory);
        byte[] original = File.ReadAllBytes(Path.Combine(directory, "me.mp3"));
        using (var failed = new Handler((_, _) => Task.FromResult(Response(400))))
        using (var http = new HttpClient(failed))
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key"))
            await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        string named = PostRecordingWorkflow.SetTitle(directory, "Development review");
        using var success = new Handler((_, _) => Task.FromResult(Response()));
        using var client = new HttpClient(success);
        using var resumed = new ProcessingJobs(config, () => false, client, readKey: _ => "fixture-key");
        resumed.Resume(named);
        await resumed.ProcessSessionAsync(named, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(named).JobState);
        Assert.Equal(data.Id, SessionStore.Read(named).Id);
        Assert.Equal(data.Audio!.Chunks, SessionStore.Read(named).Audio!.Chunks);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(named, "me.mp3")));
        Assert.True(File.Exists(Path.Combine(named, "transcription.md")));
        await resumed.ProcessSessionAsync(named, default);
        Assert.Equal(1, success.Calls); Assert.False(Directory.Exists(directory));
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetTapeJobs-" + Guid.NewGuid().ToString("N"));
    private readonly LoadedConfiguration config;
    private const string Valid = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:01] Me: test\"}}]}";

    public JobTests()
    {
        Directory.CreateDirectory(root);
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        config = loaded with { Settings = loaded.Settings with { MeetingsRoot = root } };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitNoSpeechChunksCompleteAndRecoverWithoutInventingUtterances(bool allSilent, bool restart)
    {
        string directory = Session();
        using var canceled = new CancellationTokenSource();
        using var first = new Handler((call, _) =>
        {
            if (restart && call == 2) { canceled.Cancel(); throw new OperationCanceledException(canceled.Token); }
            string text = allSilent || call == 1 ? "[NO_SPEECH]" : "[00:00] Me: Test phrase.";
            string raw = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = text } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(raw, Encoding.UTF8, "application/json") });
        });
        using (var http = new HttpClient(first))
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key"))
            await jobs.ProcessSessionAsync(directory, canceled.Token);
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        if (restart)
        {
            using var remaining = new Handler((_, _) => Task.FromResult(allSilent
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[NO_SPEECH]\"}}]}", Encoding.UTF8, "application/json") }
                : Response()));
            using var http = new HttpClient(remaining);
            using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(2, remaining.Calls);
            Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        }
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        string transcript = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.DoesNotContain("[NO_SPEECH]", transcript);
        Assert.Equal(!allSilent, transcript.Contains("**Me:**"));
        Assert.False(File.Exists(Path.Combine(directory, "summary.md")));
    }

    [Fact]
    public async Task RestartSkipsDurableSuccessEvenWhenMetadataProgressWasLost()
    {
        string directory = Session();
        using var canceled = new CancellationTokenSource();
        using var first = new Handler((call, _) =>
        {
            if (call == 2) { canceled.Cancel(); throw new OperationCanceledException(canceled.Token); }
            return Task.FromResult(Response());
        });
        using (var http = new HttpClient(first))
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key"))
            await jobs.ProcessSessionAsync(directory, canceled.Token);
        Assert.True(File.Exists(ChunkStore.PathFor(directory, 1)));
        var saved = SessionStore.Read(directory);
        Assert.NotEqual(JobState.Completed, saved.JobState);
        SessionStore.Save(directory, saved with { Stages = new(saved.Stages) { ["transcribe"] = new(true, StageState.Pending, 0, 3) } });
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        using var remaining = new Handler((_, _) => Task.FromResult(Response()));
        using var nextHttp = new HttpClient(remaining);
        using var restarted = new ProcessingJobs(config, () => false, nextHttp, readKey: _ => "test-key");
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(2, remaining.Calls);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(3, SessionStore.Read(directory).Stages["transcribe"].CompletedChunks);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task ResultIsCommittedBeforeMetadataAndRecoveredAfterMetadataWriteFailure()
    {
        string directory = Session(120);
        string metadata = Path.Combine(directory, "session.json");
        using var first = new Handler((_, _) => { File.SetAttributes(metadata, FileAttributes.ReadOnly); return Task.FromResult(Response()); });
        using (var http = new HttpClient(first))
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key"))
            await jobs.ProcessSessionAsync(directory, default);
        Assert.True(File.Exists(ChunkStore.PathFor(directory, 1)));
        Assert.NotEqual(JobState.Completed, SessionStore.Read(directory).JobState);
        File.SetAttributes(metadata, FileAttributes.Normal);
        using var none = new Handler((_, _) => throw new Exception("Successful chunk must not be sent again"));
        using var nextHttp = new HttpClient(none);
        using var recovered = new ProcessingJobs(config, () => false, nextHttp, readKey: _ => throw new Exception("No key needed for cached success"));
        await recovered.ProcessSessionAsync(directory, default);
        Assert.Equal(0, none.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task ManyTransientFailuresRetryWithoutLimitAndPersistCappedBackoff()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        using var handler = new Handler((call, _) => Task.FromResult(call <= 12 ? Response(503) : Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now);
        double[] backoffs = [1, 2, 4, 8, 16, 32, 64, 128, 256, 300, 300, 300];
        for (int attempt = 1; attempt <= backoffs.Length; attempt++)
        {
            await jobs.ProcessSessionAsync(directory, default);
            var retry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
            Assert.Equal(attempt, retry.Attempts);
            Assert.Equal(backoffs[attempt - 1], (retry.NextAttemptUtc - now).TotalSeconds);
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(attempt, handler.Calls); // No retry before the stored deadline.
            now = retry.NextAttemptUtc;
        }
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(13, handler.Calls);
        Assert.Null(SessionStore.Read(directory).Retry);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(300, ProcessingJobs.Backoff(100, TimeSpan.FromHours(1)).TotalSeconds);
        Assert.Equal(1, ProcessingJobs.Backoff(1, TimeSpan.FromSeconds(-5)).TotalSeconds);
    }

    [Fact]
    public async Task RestartRetainsPendingBackoffAndRetryAfterIsCapped()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        var data = SessionStore.Read(directory);
        SessionStore.Save(directory, data with { Retry = new(1, 8, now + TimeSpan.FromSeconds(2)) });
        using var handler = new Handler((call, _) =>
        {
            var response = call == 1 ? Response(429) : Response();
            if (call == 1) response.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
        {
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(0, handler.Calls);
            now += TimeSpan.FromSeconds(2);
            await jobs.ProcessSessionAsync(directory, default);
        }
        var deferred = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
        Assert.Equal(9, deferred.Attempts);
        Assert.Equal(300, (deferred.NextAttemptUtc - now).TotalSeconds);
        using var restarted = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now);
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        now = deferred.NextAttemptUtc;
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task BackwardsSystemClockCannotDeferRetryLongerThanFiveMonotonicMinutes()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        long monotonic = 0;
        var data = SessionStore.Read(directory);
        SessionStore.Save(directory, data with { Retry = new(1, 12, now + TimeSpan.FromMinutes(3)) });
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key",
            utcNow: () => now, timestamp: () => monotonic);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(0, handler.Calls);
        now -= TimeSpan.FromHours(1); // The previously persisted UTC deadline is now more than one hour away.
        monotonic = Stopwatch.Frequency * 299L;
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(0, handler.Calls);
        monotonic += Stopwatch.Frequency;
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task CompletedCheckpointBypassesStaleRetryDeadlineWithoutAnotherPaidRequest()
    {
        string directory = Session(120);
        var data = SessionStore.Read(directory);
        var chunk = Assert.Single(data.Audio!.Chunks);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, chunk.RelativePath))));
        ChunkStore.Save(directory, new(data.Id, chunk, hash, "test-model", "prompt-hash", "[00:01] Me: test"));
        SessionStore.Save(directory, data with { Retry = new(chunk.Index, 4, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)) });
        using var handler = new Handler((_, _) => throw new Exception("Saved result must not be resent"));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => throw new Exception("No key needed"));
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Null(SessionStore.Read(directory).Retry);
    }

    [Fact]
    public async Task FailureOnLaterChunkRetainsAttemptCountWhileEarlierCheckpointsAreReplayed()
    {
        string directory = Session(1); // Three one-second chunks.
        var now = DateTimeOffset.UtcNow;
        using var handler = new Handler((call, _) => Task.FromResult(call is 2 or 3 ? Response(503) : Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now);

        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(2, handler.Calls);
        byte[] completedFirst = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        var firstRetry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
        Assert.Equal(2, firstRetry.ChunkIndex);
        Assert.Equal(1, firstRetry.Attempts);
        now = firstRetry.NextAttemptUtc;

        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(3, handler.Calls);
        var secondRetry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
        Assert.Equal(2, secondRetry.ChunkIndex);
        Assert.Equal(2, secondRetry.Attempts);
        Assert.Equal(2, (secondRetry.NextAttemptUtc - now).TotalSeconds);
        Assert.Equal(completedFirst, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        now = secondRetry.NextAttemptUtc;

        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(5, handler.Calls); // Chunk 1 was not resent; chunk 2 succeeded, followed by chunk 3.
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Null(SessionStore.Read(directory).Retry);
    }

    [Fact]
    public async Task SchedulerProcessesOtherMeetingsWhileFirstMeetingWaitsForRetry()
    {
        string failing = Session(120);
        string ready = Session(120);
        var readyChunk = SessionStore.Read(ready).Audio!.Chunks[0];
        byte[] readyAudio = [17, 18, 19];
        File.WriteAllBytes(Path.Combine(ready, readyChunk.RelativePath), readyAudio);
        string marker = Convert.ToBase64String(readyAudio);
        using var handler = new RoutingHandler(marker);
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        jobs.Start();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            while (SessionStore.Read(ready).JobState != JobState.Completed || SessionStore.Read(failing).Retry is null)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The ready meeting remained blocked by another meeting's retry.");
                await Task.Delay(25);
            }
            Assert.Equal(1, handler.FailingCalls);
            Assert.Equal(1, handler.ReadyCalls);
            var deferred = Assert.IsType<RetryProgress>(SessionStore.Read(failing).Retry);
            Assert.True(deferred.NextAttemptUtc > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30));
            Assert.False(File.Exists(ChunkStore.PathFor(failing, 1)));
            Assert.True(File.Exists(ChunkStore.PathFor(ready, 1)));
        }
        finally { jobs.PauseAndWait(); }
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(413)]
    public async Task PermanentHttpFailurePausesUntilExplicitResume(int status)
    {
        string directory = Session(120);
        using var handler = new Handler((call, _) => Task.FromResult(call == 1 ? Response(status) : Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal(StageState.Paused, SessionStore.Read(directory).Stages["transcribe"].Status);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task InvalidCheckpointOrModifiedAudioIsPausedWithoutResendingSavedSuccess()
    {
        string directory = Session(120);
        var data = SessionStore.Read(directory);
        var chunk = data.Audio!.Chunks[0];
        ChunkStore.Save(directory, new(data.Id, chunk, "wrong-hash", "test-model", "test-prompt-hash", "saved text"));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        using var handler = new Handler((_, _) => throw new Exception("Invalid cache must pause, not resend"));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Throws<IOException>(() => ChunkStore.Save(directory, new(data.Id, chunk, "", "", "", "overwrite")));
    }

    [Fact]
    public async Task TruncatedResponseAndCheckpointFilesystemFaultNeverCompleteJobs()
    {
        string directory = Session(120);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}") }));
        using var http = new HttpClient(handler);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key"))
            await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.False(File.Exists(ChunkStore.PathFor(directory, 1)));
        string second = Session(120);
        File.WriteAllText(Path.Combine(second, "chunks"), "filesystem obstruction");
        using var success = new Handler((_, _) => Task.FromResult(Response()));
        using var successHttp = new HttpClient(success);
        using var failedStore = new ProcessingJobs(config, () => false, successHttp, readKey: _ => "test-key");
        await failedStore.ProcessSessionAsync(second, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(second).JobState);
        Assert.NotEqual(StageState.Completed, SessionStore.Read(second).Stages["transcribe"].Status);
        Assert.False(File.Exists(Path.Combine(second, "summary.md")));
    }

    [Fact]
    public async Task NewCaptureCancelsAndJoinsNetworkWorkAndExitRetainsProgress()
    {
        string directory = Session(120);
        bool active = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool requestCanceled = false;
        using var handler = new Handler(async (_, token) =>
        {
            Assert.False(active);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { requestCanceled = true; throw; }
            return Response();
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => active, http, readKey: _ => "test-key");
        jobs.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        jobs.PauseAndWait();
        Assert.True(requestCanceled);
        Assert.True(jobs.Completion.IsCompleted);
        active = true;
        jobs.Start();
        Assert.Equal(1, handler.Calls);
        Assert.NotEqual(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(StageState.Pending, SessionStore.Read(directory).Stages["transcribe"].Status);
    }

    [Fact]
    public async Task ReadyTrimAutomaticallyPreparesRealStereoAndProcessesAllChunks()
    {
        using var fixture = new ChunkingTests();
        string directory = await fixture.SessionAsync();
        var data = SessionStore.Read(directory);
        SessionStore.Save(directory, data with { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Pending) } });
        byte[] original = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, default);
        var saved = SessionStore.Read(directory);
        Assert.Equal(JobState.Completed, saved.JobState);
        Assert.Equal(3, saved.Audio!.Chunks.Count);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3"))));
    }

    internal string Session(int seconds = 1)
    {
        var data = new SessionData
        {
            Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 3, RecordingState = RecordingState.Recorded,
            Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
            Processing = new(seconds, config.Settings.Transcription, config.Settings.Summary, config.Directory),
            Trim = new(0, 3, false) { Decision = "skip" },
            Stages = new() { ["trim"] = new(true, StageState.Completed), ["prepare"] = new(true, StageState.Completed),
                ["transcribe"] = new(true, StageState.Pending), ["assemble"] = new(true, StageState.Pending), ["summary"] = new(false, StageState.Disabled) }
        };
        var created = SessionStore.Create(root, data);
        using var lease = created.Lease;
        File.WriteAllBytes(Path.Combine(created.Directory, "me.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(created.Directory, "them.mp3"), [4, 5, 6]);
        string relative = "processed/audio_test";
        Directory.CreateDirectory(Path.Combine(created.Directory, relative));
        var chunks = AudioChunker.Plan(0, 3 * 48000, seconds, relative);
        foreach (var chunk in chunks) File.WriteAllBytes(Path.Combine(created.Directory, chunk.RelativePath), [1, 2, (byte)chunk.Index]);
        SessionStore.Save(created.Directory, data with { Audio = new(relative, chunks) });
        return created.Directory;
    }

    private static HttpResponseMessage Response(int status = 200) => new((HttpStatusCode)status)
        { Content = new StringContent(status == 200 ? Valid : "private provider error", Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(++Calls, token);
    }
    private sealed class RoutingHandler(string readyAudioMarker) : HttpMessageHandler
    {
        public int FailingCalls { get; private set; }
        public int ReadyCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = await request.Content!.ReadAsStringAsync(token);
            if (body.Contains(readyAudioMarker, StringComparison.Ordinal))
            {
                ReadyCalls++;
                return Response();
            }
            FailingCalls++;
            var transient = Response(429);
            transient.Headers.RetryAfter = new(TimeSpan.FromMinutes(1));
            return transient;
        }
    }
    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
