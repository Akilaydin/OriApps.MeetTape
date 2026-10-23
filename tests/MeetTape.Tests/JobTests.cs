using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeetTape.Tests;

public sealed class JobTests : IDisposable
{
    [Fact]
    public async Task RecycledStorageFailureDoesNotLeaveReplacementBlockedInTheScheduler()
    {
        string logDirectory = Path.Combine(root, "logs");
        await using var log = new FileDiagnostics(logDirectory);
        using var logScope = AppDiagnostics.Use(log);
        string directory = Session();
        bool fail = true;
        using var handler = new Handler((_, _) =>
        {
            if (fail)
            {
                File.SetAttributes(Path.Combine(directory, "session.json"), FileAttributes.ReadOnly);
                return Task.FromResult(Response(400));
            }
            return Task.FromResult(Response());
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        await jobs.ProcessSessionAsync(directory, default);
        File.SetAttributes(Path.Combine(directory, "session.json"), FileAttributes.Normal);
        jobs.Cancel(directory);
        string bin = Path.Combine(root, "Fixture bin"); Directory.CreateDirectory(bin);
        jobs.RemoveInactiveSession(directory, () => Directory.Move(directory, Path.Combine(bin, "removed")));
        string replacement = Session(); Directory.Move(replacement, directory);
        fail = false; jobs.Start();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (SessionStore.Read(directory).JobState != JobState.Completed)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                await log.DisposeAsync();
                Assert.Fail($"Replacement session did not complete; calls={handler.Calls}; state={SessionStore.Read(directory).JobState}; scheduler={jobs.Completion.Status}. " +
                    File.ReadAllText(Path.Combine(logDirectory, "meettape.log")));
            }
            await Task.Delay(10);
        }
        jobs.PauseAndWait();
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task RemovalExcludesActiveJobsAndStaleSchedulerSnapshotsWithoutBlockingOtherMeetings()
    {
        string directory = Session(), other = Session();
        Guid removedId = SessionStore.Read(directory).Id;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = true;
        using var handler = new Handler(async (_, token) =>
        {
            if (hold) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Response();
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        Task running = jobs.ProcessSessionAsync(directory, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => jobs.RemoveInactiveSession(directory, () => throw new Exception("Must not remove active job")));
        jobs.Cancel(directory); await running;
        Assert.Throws<IOException>(() => jobs.RemoveInactiveSession(directory, () => throw new IOException("Fixture bin refused")));
        string bin = Path.Combine(root, "Fixture bin"); Directory.CreateDirectory(bin);
        jobs.RemoveInactiveSession(directory, () => Directory.Move(directory, Path.Combine(bin, "removed")));
        await jobs.ProcessSessionAsync(directory, default, removedId);
        string replacement = Session(); Directory.Move(replacement, directory);
        await jobs.ProcessSessionAsync(directory, default, removedId);
        Assert.Equal(JobState.NotStarted, SessionStore.Read(directory).JobState);
        Assert.Equal(1, handler.Calls);
        hold = false;
        jobs.Start();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (SessionStore.Read(directory).JobState != JobState.Completed || SessionStore.Read(other).JobState != JobState.Completed)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "New session and unrelated meeting must remain schedulable after path reuse.");
            await Task.Delay(10);
        }
        jobs.PauseAndWait();
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(JobState.Completed, SessionStore.Read(other).JobState);
        bool secondRemoval = false;
        jobs.RemoveInactiveSession(directory, () => secondRemoval = true);
        Assert.True(secondRemoval);
    }

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

    [Theory]
    [InlineData(1, "Text without timestamps")]
    [InlineData(2, "Text without timestamps")]
    [InlineData(1, "[00:10] Me: Outside the chunk")]
    [InlineData(1, "```markdown\n```")]
    public async Task InvalidTimedReplyRetriesOnceThenPausesWithoutReuploadingEarlierChunks(int invalidIndex, string invalidText)
    {
        string directory = Session();
        var now = DateTimeOffset.UtcNow;
        using var first = new Handler((call, _) => Task.FromResult(call == invalidIndex || call == invalidIndex + 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                { choices = new[] { new { finish_reason = "stop", message = new { content = invalidText } } } }),
                Encoding.UTF8, "application/json") }
            : Response()));
        using var http = new HttpClient(first);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
            await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(invalidIndex, first.Calls);
        var deferred = SessionStore.Read(directory);
        Assert.NotEqual(JobState.Paused, deferred.JobState);
        var retry = Assert.IsType<RetryProgress>(deferred.Retry);
        Assert.Equal(1, retry.InvalidTimedTextFailures);
        Assert.Equal(1, (retry.NextAttemptUtc - now).TotalSeconds);
        byte[]? retained = invalidIndex > 1 ? File.ReadAllBytes(ChunkStore.PathFor(directory, 1)) : null;
        using (var restarted = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
        {
            await restarted.ProcessSessionAsync(directory, default);
            Assert.Equal(invalidIndex, first.Calls); // The budget and deadline survived restart.
            now = retry.NextAttemptUtc;
            await restarted.ProcessSessionAsync(directory, default);
            await restarted.ProcessSessionAsync(directory, default);
        }
        Assert.Equal(invalidIndex + 1, first.Calls);
        var paused = SessionStore.Read(directory);
        Assert.Equal(JobState.Paused, paused.JobState);
        Assert.Equal(StageState.Paused, paused.Stages["transcribe"].Status);
        Assert.Contains("Resume", paused.Error);
        Assert.Contains("retry exhausted", paused.Error);
        Assert.Equal(invalidIndex - 1, paused.Stages["transcribe"].CompletedChunks);
        Assert.False(File.Exists(ChunkStore.PathFor(directory, invalidIndex)));
        Assert.False(File.Exists(Path.Combine(directory, "transcription.md")));
        string[] rejected = Directory.GetFiles(Path.Combine(directory, "chunks"), "*.rejected-*.json");
        Assert.Equal(2, rejected.Length);
        foreach (string path in rejected)
            Assert.Contains(invalidText, (await ChunkStore.ReadFileAsync<ChunkResult>(path, default))!.Text);

        using var next = new Handler((_, _) => Task.FromResult(Response()));
        using var client = new HttpClient(next);
        using var resumed = new ProcessingJobs(config, () => false, client, readKey: _ => "test-key");
        resumed.Resume(directory);
        await resumed.ProcessSessionAsync(directory, default);
        Assert.Equal(4 - invalidIndex, next.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        if (retained is not null) Assert.Equal(retained, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.All(rejected, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task InvalidTimedCheckpointIsRetainedForDiagnosisAndCanBeRetriedThroughResume()
    {
        string directory = Session();
        var data = SessionStore.Read(directory);
        var chunk = data.Audio!.Chunks[0];
        string hash = await TranscriptAssembler.HashAsync(Path.Combine(directory, chunk.RelativePath), default);
        ChunkStore.Save(directory, new(data.Id, chunk, hash, "fixture-model", "fixture-prompt", "Untimed old response"));
        byte[] rejected = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.False(File.Exists(ChunkStore.PathFor(directory, 1)));
        string diagnosis = Assert.Single(Directory.GetFiles(Path.Combine(directory, "chunks"), "*.rejected-*.json"));
        Assert.Equal(rejected, File.ReadAllBytes(diagnosis));
        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task AutomaticFormatRetrySucceedsAfterRestartWithoutResendingACompletedChunk()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        using var handler = new Handler((call, _) => Task.FromResult(TextResponse(call == 1
            ? "Unformatted answer from the model." : "[00:00] Me: Recovered.")));
        using var http = new HttpClient(handler);
        using (var first = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
            await first.ProcessSessionAsync(directory, default);
        var retry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
        Assert.Equal(1, retry.InvalidTimedTextFailures);
        string rejected = Assert.Single(Directory.GetFiles(Path.Combine(directory, "chunks"), "*.rejected-*.json"));
        byte[] original = File.ReadAllBytes(rejected);
        now = retry.NextAttemptUtc;
        using (var next = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
        {
            await next.ProcessSessionAsync(directory, default);
            await next.ProcessSessionAsync(directory, default);
        }
        Assert.Equal(2, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Null(SessionStore.Read(directory).Retry);
        Assert.Equal(original, File.ReadAllBytes(rejected));
        Assert.Contains("Recovered.", File.ReadAllText(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task TransientFailureBetweenMalformedRepliesDoesNotResetFormatRetryBudget()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        using var handler = new Handler((call, _) => Task.FromResult(call == 2
            ? Response(503) : TextResponse("No valid timestamps here.")));
        using var http = new HttpClient(handler);
        for (int call = 1; call <= 3; call++)
        {
            using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now);
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(call, handler.Calls);
            if (call < 3)
            {
                var retry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
                Assert.Equal(1, retry.InvalidTimedTextFailures);
                now = retry.NextAttemptUtc;
            }
        }
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(directory, "chunks"), "*.rejected-*.json").Length);
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
    public async Task ApiDiagnosticsCorrelateSessionChunkAndPersistedAttemptWithRetryDecision()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        await using (var log = new FileDiagnostics(Path.Combine(root, "logs")))
        using (AppDiagnostics.Use(log))
        {
            using var handler = new Handler((call, _) => Task.FromResult(call == 1 ? Response(429) : Response()));
            using var http = new HttpClient(handler);
            using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key", utcNow: () => now);
            await jobs.ProcessSessionAsync(directory, default);
            now = SessionStore.Read(directory).Retry!.NextAttemptUtc;
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(2, handler.Calls);
            Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        }
        string logged = File.ReadAllText(Path.Combine(root, "logs", "meettape.log"));
        Assert.Contains("session=" + SessionStore.Read(directory).Id, logged);
        Assert.Contains("chunk=1; attempt=1", logged);
        Assert.Contains("chunk=1; attempt=2", logged);
        Assert.Contains("decision=retry", logged);
        Assert.Contains("decision=accepted", logged);
        Assert.DoesNotContain("fixture-key", logged);
        Assert.DoesNotContain("[00:01] Me: test", logged);
    }

    [Fact]
    public async Task EmptyChoicesRetriesOnlyFailedChunkAfterRestartWithoutManualResume()
    {
        string directory = Session();
        var now = DateTimeOffset.UtcNow;
        byte[] original = File.ReadAllBytes(Path.Combine(directory, "me.mp3"));
        using var handler = new Handler((call, _) => Task.FromResult(call == 2
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[]}") }
            : Response()));
        using var http = new HttpClient(handler);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
            await jobs.ProcessSessionAsync(directory, default);
        var deferred = SessionStore.Read(directory);
        Assert.NotEqual(JobState.Paused, deferred.JobState);
        var retry = Assert.IsType<RetryProgress>(deferred.Retry);
        Assert.Equal(2, retry.ChunkIndex);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal(1, (retry.NextAttemptUtc - now).TotalSeconds);
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        using var restarted = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now);
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(2, handler.Calls);
        now = retry.NextAttemptUtc;
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Null(SessionStore.Read(directory).Retry);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
        Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task PersistentEmptyChoicesStopsAfterThreeRequestsAcrossRestartsThenAllowsExplicitResume()
    {
        string directory = Session(120);
        var now = DateTimeOffset.UtcNow;
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[]}") }));
        using var http = new HttpClient(handler);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now);
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(attempt, handler.Calls);
            var saved = SessionStore.Read(directory);
            if (attempt < 3)
            {
                Assert.NotEqual(JobState.Paused, saved.JobState);
                var retry = Assert.IsType<RetryProgress>(saved.Retry);
                Assert.Equal(attempt, retry.Attempts);
                Assert.Equal(attempt, (retry.NextAttemptUtc - now).TotalSeconds);
                now = retry.NextAttemptUtc;
            }
            else
            {
                Assert.Equal(JobState.Paused, saved.JobState);
                Assert.Contains("3", saved.Error);
                Assert.Contains("empty choices", saved.Error);
                Assert.Contains("Resume", saved.Error);
            }
        }
        using (var restarted = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key", utcNow: () => now))
            await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(3, handler.Calls);
        Assert.False(File.Exists(ChunkStore.PathFor(directory, 1)));
        using var success = new Handler((_, _) => Task.FromResult(Response()));
        using var resumedHttp = new HttpClient(success);
        using var resumed = new ProcessingJobs(config, () => false, resumedHttp, readKey: _ => "test-key", utcNow: () => now);
        resumed.Resume(directory);
        await resumed.ProcessSessionAsync(directory, default);
        Assert.Equal(1, success.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Theory]
    [InlineData("{\"choices\":[],\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    [InlineData("{\"choices\":[],\"error\":{\"code\":\"blocked\"}}")]
    [InlineData("{\"choices\":null}")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task BlockedOrMalformedRepliesPauseWithoutEmptyChoicesRetry(string raw)
    {
        string directory = Session(120);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(raw) }));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, default);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Null(SessionStore.Read(directory).Retry);
    }

    [Fact]
    public async Task CancelDuringEmptyChoicesBackoffMakesNoFurtherRequestsAfterRestart()
    {
        string directory = Session(120);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[]}") }));
        using var http = new HttpClient(handler);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key"))
        {
            await jobs.ProcessSessionAsync(directory, default);
            Assert.NotNull(SessionStore.Read(directory).Retry);
            jobs.Cancel(directory);
        }
        using var restarted = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
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
    public async Task ReadyTrimAutomaticallyPreparesRealMonoTracksAndProcessesAllChunks()
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
        Assert.Equal(6, saved.Audio!.Chunks.Count);
        Assert.Equal(6, handler.Calls);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3"))));
    }

    [Fact]
    public async Task IndependentMonoRequestsForceTrackLabelsAndMergeSimultaneousSpeech()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync(overlap: true);
        var initial = SessionStore.Read(directory);
        SessionStore.Save(directory, initial with { Processing = initial.Processing with { MeDisplayName = "Артём О" } });
        string[] replies = [
            "[00:01.90] Them: Микрофонная реплика.", // Deliberately wrong model source: the Me track wins.
            "[00:01.90] Me: Одновременный ответ системы.",
            "[NO_SPEECH]", "[00:01.90] Them: Поздняя реплика системы.",
            "[NO_SPEECH]", "[NO_SPEECH]"
        ];
        using var handler = new InspectingHandler(replies);
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        await jobs.ProcessSessionAsync(directory, default);
        var saved = SessionStore.Read(directory);
        Assert.Equal(JobState.Completed, saved.JobState);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(6, saved.Audio!.Chunks.Count);
        for (int i = 0; i < saved.Audio.Chunks.Count; i++)
        {
            var chunk = saved.Audio.Chunks[i];
            Assert.Equal(i % 2 == 0 ? "Me" : "Them", chunk.Source);
            Assert.Contains($"MONO audio from the {chunk.Source} recording source", handler.Requests[i].Instruction);
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory, chunk.RelativePath)), handler.Requests[i].Audio);
            Assert.True(File.Exists(ChunkStore.PathFor(directory, chunk.Index)));
        }
        string transcript = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("[00:01] **Артём О:** Микрофонная реплика.", transcript);
        Assert.Contains("[00:01] **Them:** Одновременный ответ системы.", transcript);
        Assert.True(transcript.IndexOf("Микрофонная реплика.", StringComparison.Ordinal)
            < transcript.IndexOf("Одновременный ответ системы.", StringComparison.Ordinal));
        Assert.Contains("**Them:** Поздняя реплика системы.", transcript);
        Assert.DoesNotContain("**Unknown:**", transcript);
    }

    [Fact]
    public async Task RejectedMonoTrackCanResumeWithoutResendingPreviousSourceCheckpoint()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync();
        var now = DateTimeOffset.UtcNow;
        using var handler = new Handler((call, _) => Task.FromResult(TextResponse(
            call == 2 ? "[00:01] Артём О: Incorrect API label" : "[00:01] Me: Accepted track utterance")));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key", utcNow: () => now);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(2, handler.Calls);
        Assert.NotEqual(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.False(File.Exists(Path.Combine(directory, "transcription.md")));
        byte[] first = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        Assert.False(File.Exists(ChunkStore.PathFor(directory, 2)));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "chunks"), "0002.rejected-*.json"));

        now = SessionStore.Read(directory).Retry!.NextAttemptUtc;
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(7, handler.Calls); // One rejected Them request + remaining four source chunks.
        Assert.Equal(first, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task MonoResponsesWithMissingTrackLabelsCompleteWithoutAnotherApiRequest()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync();
        using var handler = new Handler((call, _) => Task.FromResult(TextResponse(call % 2 == 0
            ? "[00:00] Them: Первое предложение.\n[00:01] Следующая реплика без метки."
            : "[00:00] Me: Принято.")));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(6, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "chunks"), "*.rejected-*.json"));
        Assert.Contains("**Them:** Следующая реплика без метки.",
            File.ReadAllText(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task MonoFormattingIsRecoveredBeforeCheckpointWithoutExtraAudioRequests()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync();
        const string meReply = "[00:00] Me: Я не меняю число 15 [00:01] Теперь нужно сохранить число 50 и два повтора.";
        const string themReply = "[00:00] Это обычная часть ответа: не придумывайте имя говорящего.";
        using var handler = new Handler((call, _) => Task.FromResult(TextResponse(
            call % 2 == 0 ? themReply : meReply)));
        using var http = new HttpClient(handler);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key"))
            await jobs.ProcessSessionAsync(directory, default);

        Assert.Equal(6, handler.Calls);
        var data = SessionStore.Read(directory);
        Assert.Equal(JobState.Completed, data.JobState);
        Assert.Null(data.Retry);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "chunks"), "*.rejected-*.json"));
        Assert.Equal(meReply, (await ChunkStore.ReadAsync(directory, data, data.Audio!.Chunks[0], default)).Text);
        Assert.Equal(themReply, (await ChunkStore.ReadAsync(directory, data, data.Audio.Chunks[1], default)).Text);
        string transcript = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("не меняю число 15", transcript);
        Assert.Contains("сохранить число 50 и два повтора.", transcript);
        Assert.Contains("**Them:** Это обычная часть ответа: не придумывайте имя говорящего.", transcript);

        using var restarted = new ProcessingJobs(config, () => false,
            new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No already saved chunk may be resent."))),
            readKey: _ => "fixture-key");
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("wrong-model", false)]
    [InlineData("wrong-prompt", false)]
    [InlineData("wrong-audio", false)]
    [InlineData("reasoning", false)]
    [InlineData("extra-timestamp-bracket", true)]
    [InlineData("extra-silence-bracket", true)]
    public async Task ResumeReusesOnlyCompatibleLocallyRepairableRejectedMonoReplies(string variant, bool expectedReuse)
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync();
        var data = SessionStore.Read(directory);
        var prepared = await new AudioChunker("ffmpeg").PrepareAsync(directory, data, default);
        data = data with
        {
            Audio = prepared, JobState = JobState.Paused,
            Stages = new(data.Stages)
            {
                ["prepare"] = new(true, StageState.Completed, prepared.Chunks.Count, prepared.Chunks.Count),
                ["transcribe"] = new(true, StageState.Paused)
            }
        };
        SessionStore.Save(directory, data);
        var first = prepared.Chunks[0];
        var rejectedChunk = prepared.Chunks[1];
        string prompt = (config with { Directory = data.Processing.ConfigurationDirectory })
            .ReadPrompt(data.Processing.Transcription, "transcription");
        string firstHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            prompt + ChatApiClient.InstructionFor(first.Source))));
        string secondHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            prompt + ChatApiClient.InstructionFor(rejectedChunk.Source))));
        static string AudioHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        ChunkStore.Save(directory, new(data.Id, first, AudioHash(Path.Combine(directory, first.RelativePath)),
            data.Processing.Transcription.Model, firstHash, "[00:00] Me: Already paid."));
        byte[] previous = File.ReadAllBytes(ChunkStore.PathFor(directory, first.Index));
        var rejected = new ChunkResult(data.Id, rejectedChunk,
            variant == "wrong-audio" ? "NOT_THE_SELECTED_AUDIO" : AudioHash(Path.Combine(directory, rejectedChunk.RelativePath)),
            variant == "wrong-model" ? "different-model" : data.Processing.Transcription.Model,
            variant == "wrong-prompt" ? "different-prompt" : secondHash,
            variant switch
            {
                "reasoning" => "Let me analyze the audio.\n[00:00] Them: Some speech.",
                "extra-timestamp-bracket" => "[[00:00] Them: Сохранить исходный текст.",
                "extra-silence-bracket" => "[[NO_SPEECH]",
                _ => "[00:00] Them: Начало не завершено [00:01] Продолжение содержит несколько слов без метки."
            });
        string rejectedPath = Path.Combine(directory, "chunks", $"0002.rejected-{Guid.NewGuid():N}.json");
        ChunkStore.SaveFile(rejectedPath, rejected);
        byte[] rejectedBytes = File.ReadAllBytes(rejectedPath);

        using var handler = new Handler((_, _) => Task.FromResult(TextResponse("[00:00] Me: Fresh response.")));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);

        Assert.Equal(expectedReuse ? 4 : 5, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(previous, File.ReadAllBytes(ChunkStore.PathFor(directory, first.Index)));
        Assert.Equal(rejectedBytes, File.ReadAllBytes(rejectedPath));
        var recovered = await ChunkStore.ReadAsync(directory, SessionStore.Read(directory), rejectedChunk, default);
        Assert.Equal(expectedReuse ? rejected.Text : "[00:00] Me: Fresh response.", recovered.Text);
        Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task PreUpgradeStereoCheckpointWithModelSpeakerNameIsReusedWithoutAnotherUpload()
    {
        string directory = Session(120);
        var data = SessionStore.Read(directory);
        var chunk = Assert.Single(data.Audio!.Chunks);
        Assert.Null(chunk.Source);
        ChunkStore.Save(directory, new(data.Id, chunk,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, chunk.RelativePath)))),
            "previous-model", "saved-prompt-hash", "[00:01] Alice: Previously transcribed sentence."));

        using var handler = new Handler((_, _) => throw new InvalidOperationException("No paid retranscription expected"));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("**Unknown:** Previously transcribed sentence.",
            File.ReadAllText(Path.Combine(directory, "transcription.md")));
        Assert.True(File.Exists(ChunkStore.PathFor(directory, chunk.Index)));
    }

    [Fact]
    public async Task SilentThemTrackCompletesWithNoSpeechCheckpointsAndMeSpeech()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync(silentThem: true);
        string[] replies = [
            "[00:01.9] Me: First microphone utterance.", "[NO_SPEECH]",
            "[00:00.4] Me: Second microphone utterance.", "[NO_SPEECH]",
            "[NO_SPEECH]", "[NO_SPEECH]"
        ];
        using var handler = new InspectingHandler(replies);
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(6, handler.Requests.Count);
        foreach (var chunk in SessionStore.Read(directory).Audio!.Chunks.Where(chunk => chunk.Source == "Them"))
            Assert.Equal("[NO_SPEECH]", (await ChunkStore.ReadAsync(directory, SessionStore.Read(directory), chunk, default)).Text);
        string transcript = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Contains("First microphone utterance.", transcript);
        Assert.Contains("Second microphone utterance.", transcript);
        Assert.DoesNotContain("**Them:**", transcript);
    }

    [Fact]
    public async Task RepeatedMonoBoundaryIsReconciledPerSourceButIdenticalCrossTrackSpeechIsKept()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync(overlap: true);
        string[] replies = [
            "[00:01.90] Me: Shared phrase from microphone.",
            "[00:01.90] Them: Shared phrase from microphone.",
            "[00:00.40] Me: Shared phrase from microphone.",
            "[00:00.40] Them: Shared phrase from microphone.",
            "[NO_SPEECH]", "[NO_SPEECH]"
        ];
        using var handler = new InspectingHandler(replies);
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        string output = File.ReadAllText(Path.Combine(directory, "transcription.md"));
        Assert.Equal(2, output.Split("Shared phrase from microphone.").Length - 1);
        Assert.Contains("**Me:** Shared phrase from microphone.", output);
        Assert.Contains("**Them:** Shared phrase from microphone.", output);
    }

    [Fact]
    public async Task CancelDuringSecondMonoUploadResumesFromSavedMeCheckpointOnly()
    {
        using var tones = new ChunkingTests();
        string directory = await tones.SessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new Handler(async (call, token) =>
        {
            if (call == 2)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return TextResponse("[NO_SPEECH]");
        });
        using var http = new HttpClient(first);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key"))
        {
            Task running = jobs.ProcessSessionAsync(directory, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => jobs.Cancel(directory));
            await running;
        }
        Assert.Equal(2, first.Calls);
        Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
        byte[] savedMe = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        Assert.False(File.Exists(ChunkStore.PathFor(directory, 2)));

        using var remaining = new Handler((_, _) => Task.FromResult(TextResponse("[NO_SPEECH]")));
        using var nextHttp = new HttpClient(remaining);
        using var resumed = new ProcessingJobs(config, () => false, nextHttp, readKey: _ => "fixture-key");
        await resumed.ProcessSessionAsync(directory, default);
        Assert.Equal(0, remaining.Calls);
        resumed.Resume(directory);
        await resumed.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(5, remaining.Calls);
        Assert.Equal(savedMe, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
    }

    [Fact]
    public async Task CancelInFlightRetainsCheckpointsAndRestartRequiresExplicitResume()
    {
        string directory = Session();
        byte[] original = File.ReadAllBytes(Path.Combine(directory, "me.mp3"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool canceled = false;
        using var handler = new Handler(async (call, token) =>
        {
            if (call == 2)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled = true; throw; }
            }
            return Response();
        });
        using var http = new HttpClient(handler);
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key"))
        {
            Task operation = jobs.ProcessSessionAsync(directory, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => jobs.Cancel(directory));
            await operation;
            Assert.True(canceled);
            Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
            Assert.Null(SessionStore.Read(directory).Error);
        }
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        using var remaining = new Handler((_, _) => Task.FromResult(Response()));
        using var nextHttp = new HttpClient(remaining);
        using var restarted = new ProcessingJobs(config, () => false, nextHttp, readKey: _ => "test-key");
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(0, remaining.Calls);
        Assert.True(ProcessingJobs.CanResume(SessionStore.Read(directory)));
        restarted.Resume(directory);
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(2, remaining.Calls);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.False(ProcessingJobs.CanCancel(SessionStore.Read(directory)));
    }

    [Fact]
    public async Task CancelDuringRetryNeverRestartsButOtherMeetingCompletes()
    {
        string canceled = Session(120);
        string other = Session(120);
        using var handler = new Handler((call, _) => Task.FromResult(call == 1 ? Response(503) : Response()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(canceled, default);
        Assert.NotNull(SessionStore.Read(canceled).Retry);
        jobs.Cancel(canceled);
        await jobs.ProcessSessionAsync(canceled, default);
        Assert.Equal(1, handler.Calls);
        Assert.Null(SessionStore.Read(canceled).Retry);
        Assert.Equal(JobState.Canceled, SessionStore.Read(canceled).JobState);
        await jobs.ProcessSessionAsync(other, default);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(JobState.Completed, SessionStore.Read(other).JobState);
    }

    [Fact]
    public async Task CancellationAfterAValidatedReplyKeepsPaidCheckpointForResume()
    {
        string directory = Session(120);
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new CancelOnDisposeContent(cancellation.Cancel) }));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(directory, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        jobs.Cancel(directory);
        Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task CancelSelectedSessionDoesNotInterruptAnotherInFlightRequest()
    {
        string canceled = Session(120);
        string other = Session(120);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response();
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        Task operation = jobs.ProcessSessionAsync(other, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            jobs.Cancel(canceled);
            Assert.False(operation.IsCompleted);
            Assert.Equal(JobState.Canceled, SessionStore.Read(canceled).JobState);
        }
        finally { release.TrySetResult(); await operation; }
        Assert.Equal(JobState.Completed, SessionStore.Read(other).JobState);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FailedCancellationSaveBlocksNewRequestsAndRetainsOriginalMetadata()
    {
        string directory = Session(120);
        string metadata = Path.Combine(directory, "session.json");
        byte[] original = File.ReadAllBytes(metadata);
        using var handler = new Handler((_, _) => throw new Exception("Canceled session must stay blocked after a save failure"));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        File.SetAttributes(metadata, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => jobs.Cancel(directory));
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(original, File.ReadAllBytes(metadata));
        }
        finally { File.SetAttributes(metadata, FileAttributes.Normal); }
        jobs.Cancel(directory);
        Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
    }

    [Fact]
    public async Task CancelRacingSessionStartupDoesNotFaultProcessing()
    {
        bool holdRequests = true;
        using var handler = new Handler(async (_, token) =>
        {
            if (holdRequests) await Task.Delay(Timeout.Infinite, token);
            return Response();
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string directory = Session(120);
            Task processing = Task.Run(() => jobs.ProcessSessionAsync(directory, default));
            await Task.Yield();
            await Task.Run(() => jobs.Cancel(directory));
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
        }
        string other = Session(120);
        holdRequests = false;
        await jobs.ProcessSessionAsync(other, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(other).JobState);
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
    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new
        { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } }), Encoding.UTF8, "application/json") };

    private sealed class InspectingHandler(string[] replies) : HttpMessageHandler
    {
        public List<(string Instruction, byte[] Audio)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var parts = json.RootElement.GetProperty("messages")[0].GetProperty("content");
            string instruction = parts[0].GetProperty("text").GetString()!;
            byte[] audio = Convert.FromBase64String(parts[1].GetProperty("input_audio").GetProperty("data").GetString()!);
            int index = Requests.Count;
            Requests.Add((instruction, audio));
            return TextResponse(replies[index]);
        }
    }
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(++Calls, token);
    }
    private sealed class CancelOnDisposeContent(Action cancel) : StringContent(Valid, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) cancel();
            base.Dispose(disposing);
        }
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
