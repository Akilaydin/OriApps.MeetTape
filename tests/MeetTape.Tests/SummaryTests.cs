using System.Net;
using System.Text.Json;

namespace MeetTape.Tests;

public sealed class SummaryTests : IDisposable
{
    [Fact]
    public async Task RenamedMeetingCanRequestSummaryWithoutChangingTranscriptOrIdentity()
    {
        var (directory, config) = await SessionAsync();
        var data = SessionStore.Read(directory);
        byte[] transcript = File.ReadAllBytes(Path.Combine(directory, "transcription.md"));
        string named = PostRecordingWorkflow.SetTitle(directory, data.Title ?? "");
        config = config with { Settings = config.Settings with { Summary = config.Settings.Summary with
            { PromptFile = Path.Combine(named, "summary-prompt.md") } } };
        using var handler = new Handler((_, _) => Task.FromResult(Reply()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        jobs.RequestSummary(named);
        await jobs.ProcessSessionAsync(named, default);
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(Path.Combine(named, "summary.md")));
        Assert.Equal(transcript, File.ReadAllBytes(Path.Combine(named, "transcription.md")));
        Assert.Equal(data.Id, SessionStore.Read(named).Id);
        Assert.False(Directory.Exists(directory));
    }

    private readonly TranscriptTests fixture = new();
    private static HttpResponseMessage Reply(int status = 200) => new((HttpStatusCode)status)
        { Content = new StringContent(status == 200 ? "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"# Decisions\\n- Retry after 300 seconds.\"}}]}" : "private error") };

    [Fact]
    public async Task OnlyExplicitRequestUsesSeparateModelPromptAndPreservesTranscriptAndRepeatedResult()
    {
        var (directory, config) = await SessionAsync();
        byte[] original = File.ReadAllBytes(Path.Combine(directory, "transcription.md"));
        using var handler = new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal("summary-model", body.RootElement.GetProperty("model").GetString());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("summary-prompt", messages[0].GetProperty("content").GetString());
            Assert.Contains("Full transcript", messages[1].GetProperty("content").GetString());
            return Reply();
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "summary-key");
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(0, handler.Calls);
        jobs.RequestSummary(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("Retry after 300 seconds.", File.ReadAllText(Path.Combine(directory, "summary.md")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "transcription.md")));
        Assert.Equal(StageState.Completed, SessionStore.Read(directory).Stages["summary"].Status);
        Assert.Throws<InvalidOperationException>(() => jobs.RequestSummary(directory));
        Assert.Equal(1, handler.Calls);
        File.Delete(Path.Combine(directory, "summary.md"));
        jobs.RequestSummary(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(Path.Combine(directory, "summary.md")));
    }

    [Fact]
    public async Task DisabledOrIncompleteTranscriptCannotRequestSummary()
    {
        var (directory, config) = await SessionAsync();
        using var handler = new Handler((_, _) => Task.FromResult(Reply()));
        using var http = new HttpClient(handler);
        using (var disabled = new ProcessingJobs(config with { Settings = config.Settings with { Stages = new() { Summary = false } } },
            () => false, http, readKey: _ => "key"))
            Assert.Throws<InvalidOperationException>(() => disabled.RequestSummary(directory));
        var data = SessionStore.Read(directory);
        SessionStore.Save(directory, data with { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Pending) } });
        using var incomplete = new ProcessingJobs(config, () => false, http, readKey: _ => "key");
        Assert.Throws<InvalidOperationException>(() => incomplete.RequestSummary(directory));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TooLongInputAndCaptureAreRejectedBeforeRequest()
    {
        var (_, config) = await SessionAsync();
        using var handler = new Handler((_, _) => Task.FromResult(Reply()));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "key")
            .SummarizeAsync(config.Settings.Summary, "prompt", new string('x', 2 * 1024 * 1024 + 1), default));
        Assert.Equal(ApiFailureKind.Permanent, failure.Kind);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ChatApiClient(http, () => true, _ => "key")
            .SummarizeAsync(config.Settings.Summary, "prompt", "transcript", default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TransientSummaryRetriesAndPermanentFailurePausesWithoutChangingTranscript()
    {
        var (directory, config) = await SessionAsync();
        var now = DateTimeOffset.UtcNow;
        int calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(Reply(++calls <= 2 ? 503 : 403)));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "key", utcNow: () => now);
        string hash = SessionStore.Read(directory).TranscriptSha256!;
        jobs.RequestSummary(directory);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await jobs.ProcessSessionAsync(directory, default);
            var retry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
            Assert.Equal(0, retry.ChunkIndex);
            Assert.Equal(attempt, retry.Attempts);
            Assert.Equal(Math.Pow(2, attempt - 1), (retry.NextAttemptUtc - now).TotalSeconds);
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(attempt, handler.Calls);
            now = retry.NextAttemptUtc;
        }
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal(StageState.Paused, SessionStore.Read(directory).Stages["summary"].Status);
        Assert.False(File.Exists(Path.Combine(directory, "summary.md")));
        Assert.Equal(hash, SessionStore.Read(directory).TranscriptSha256);
    }

    [Fact]
    public async Task PublicationFaultKeepsPaidResultAndResumeUsesItWithoutAnotherRequest()
    {
        var (directory, config) = await SessionAsync();
        string target = Path.Combine(directory, "summary.md");
        File.WriteAllText(target, "user notes");
        using var handler = new Handler((_, _) => Task.FromResult(Reply()));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "key");
        jobs.RequestSummary(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal("user notes", File.ReadAllText(target));
        Assert.True(File.Exists(Path.Combine(directory, "summary.json")));
        File.Move(target, target + ".user-backup");
        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(StageState.Completed, SessionStore.Read(directory).Stages["summary"].Status);
    }

    [Fact]
    public async Task DeferredSummaryBackoffSurvivesRestartAndTruncatedSummaryNeverCompletes()
    {
        var (directory, config) = await SessionAsync();
        var now = DateTimeOffset.UtcNow;
        using var transient = new Handler((_, _) => Task.FromResult(Reply(503)));
        using (var http = new HttpClient(transient))
        using (var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "key", utcNow: () => now))
        { jobs.RequestSummary(directory); await jobs.ProcessSessionAsync(directory, default); }
        var retry = Assert.IsType<RetryProgress>(SessionStore.Read(directory).Retry);
        Assert.Equal(0, retry.ChunkIndex);
        Assert.Equal(StageState.Running, SessionStore.Read(directory).Stages["summary"].Status);
        using var incomplete = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}") }));
        using var nextHttp = new HttpClient(incomplete);
        using var restarted = new ProcessingJobs(config, () => false, nextHttp, readKey: _ => "key", utcNow: () => now);
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(0, incomplete.Calls); // Persisted retry deadline still applies after restart.
        now = retry.NextAttemptUtc;
        await restarted.ProcessSessionAsync(directory, default);
        Assert.Equal(1, incomplete.Calls);
        Assert.Equal(StageState.Paused, SessionStore.Read(directory).Stages["summary"].Status);
        Assert.False(File.Exists(Path.Combine(directory, "summary.md")));
    }

    private async Task<(string Directory, LoadedConfiguration Config)> SessionAsync()
    {
        string directory = fixture.Prepare(["[00:00] Me: technical details"], 120);
        var data = SessionStore.Read(directory);
        string hash = await new TranscriptAssembler().BuildAsync(directory, data, default);
        SessionStore.Save(directory, data with { TranscriptSha256 = hash, JobState = JobState.Completed,
            Stages = new(data.Stages) { ["assemble"] = new(true, StageState.Completed), ["summary"] = new(true, StageState.AwaitingRequest) } });
        string prompt = Path.Combine(directory, "summary-prompt.md");
        File.WriteAllText(prompt, "summary-prompt");
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        config = config with { Settings = config.Settings with { Stages = new() { Summary = true },
            Summary = config.Settings.Summary with { Model = "summary-model", PromptFile = prompt } } };
        return (directory, config);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); }
    }
    public void Dispose() => fixture.Dispose();
}
