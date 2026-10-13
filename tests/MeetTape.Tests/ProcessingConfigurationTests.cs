using System.Net;
using System.Security.Cryptography;

namespace MeetTape.Tests;

public sealed class ProcessingConfigurationTests
{
    [Fact]
    public async Task EditingPromptKeepsRunningJobInstructionsAndNextJobReadsSavedText()
    {
        using var fixture = new JobTests();
        string first = fixture.Session(), next = fixture.Session();
        var data = SessionStore.Read(first);
        string prompt = Path.Combine(Path.GetDirectoryName(first)!, "fixture-prompt.md");
        File.WriteAllText(prompt, "Fixture old instructions");
        var api = data.Processing.Transcription with { PromptFile = prompt };
        foreach (string directory in new[] { first, next })
        {
            var saved = SessionStore.Read(directory);
            SessionStore.Save(directory, saved with { Processing = saved.Processing with { Transcription = api } });
        }
        var config = new LoadedConfiguration(new Configuration
        {
            MeetingsRoot = Path.GetDirectoryName(first)!, Transcription = api, Summary = data.Processing.Summary
        }, data.Processing.ConfigurationDirectory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var handler = new PromptHandler(async (request, token) =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync(token));
            if (requests.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:00] Me: Fixture words.\"}}]}")
            };
        });
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        Task running = jobs.ProcessSessionAsync(first, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var editor = new PromptDocument(prompt, "transcription") { Text = "Fixture new instructions" };
        editor.VerifyUnchanged(); editor.Save();
        Assert.False(running.IsCompleted);
        release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobState.Completed, SessionStore.Read(first).JobState);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, body => Assert.Contains("Fixture old instructions", body));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(first, 1));
        await jobs.ProcessSessionAsync(next, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(next).JobState);
        Assert.Equal(6, requests.Count);
        Assert.All(requests.Skip(3), body => Assert.Contains("Fixture new instructions", body));
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(first, 1)));
    }

    [Fact]
    public async Task SettingsChangeRetainsRunningContractAndResumeKeepsSuccessfulChunks()
    {
        using var fixture = new JobTests();
        string directory = fixture.Session();
        var original = SessionStore.Read(directory);
        var config = new LoadedConfiguration(new Configuration
        {
            MeetingsRoot = Path.GetDirectoryName(directory)!,
            Transcription = original.Processing.Transcription, Summary = original.Processing.Summary
        }, original.Processing.ConfigurationDirectory);
        string prompt = Path.Combine(directory, "new-prompt.md");
        File.WriteAllText(prompt, "New fixture instructions");
        var updated = config with { Settings = config.Settings with
        {
            ChunkSeconds = 90, Stages = new() { Trim = false, Transcribe = false },
            Transcription = config.Settings.Transcription with
            {
                Model = "new-model", Endpoint = "https://new.example.test/chat",
                PromptFile = prompt, ApiKeyEnvironmentVariable = "NEW_FIXTURE_KEY"
            }
        } };
        using var handler = new PendingHandler();
        using var http = new HttpClient(handler);
        var keySettings = new List<ApiSettings>();
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: settings =>
        { keySettings.Add(settings); return "fixture-key"; });
        Task running = jobs.ProcessSessionAsync(directory, default);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        jobs.ApplyConfiguration(updated);
        Assert.False(running.IsCompleted);
        Assert.Equal(original.Processing, SessionStore.Read(directory).Processing);
        handler.Release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobState.Paused, SessionStore.Read(directory).JobState);
        Assert.Equal(2, handler.Calls);
        Assert.All(keySettings, settings => Assert.Equal(config.Settings.Transcription, settings));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        byte[] audio = File.ReadAllBytes(Path.Combine(directory, "me.mp3"));

        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);
        var completed = SessionStore.Read(directory);
        Assert.Equal(JobState.Completed, completed.JobState);
        Assert.Equal(4, handler.Calls);
        Assert.All(keySettings.Skip(2), settings => Assert.Equal(updated.Settings.Transcription, settings));
        Assert.Equal(original.Processing.ChunkSeconds, completed.Processing.ChunkSeconds);
        Assert.True(completed.Stages["transcribe"].Enabled);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(audio, File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptFailurePausesWithoutRequestsAndResumeRetainsSuccessfulChunks(bool empty)
    {
        using var fixture = new JobTests();
        string directory = fixture.Session();
        var data = SessionStore.Read(directory);
        string prompt = Path.Combine(directory, "fixture-prompt.md");
        if (empty) File.WriteAllText(prompt, " ");
        var transcription = data.Processing.Transcription with { PromptFile = prompt };
        var config = new LoadedConfiguration(new Configuration
        {
            MeetingsRoot = Path.GetDirectoryName(directory)!,
            Transcription = transcription, Summary = data.Processing.Summary
        }, data.Processing.ConfigurationDirectory);
        data = data with { Processing = data.Processing with { Transcription = transcription } };
        SessionStore.Save(directory, data);
        var first = data.Audio!.Chunks[0];
        ChunkStore.Save(directory, new(data.Id, first,
            await TranscriptAssembler.HashAsync(Path.Combine(directory, first.RelativePath), default),
            "fixture-model", "fixture-prompt-hash", "[00:00] Me: Saved words."));
        byte[] checkpoint = File.ReadAllBytes(ChunkStore.PathFor(directory, 1));
        byte[] original = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");

        await jobs.ProcessSessionAsync(directory, default);
        var paused = SessionStore.Read(directory);
        Assert.Equal(JobState.Paused, paused.JobState);
        Assert.Equal(StageState.Paused, paused.Stages["transcribe"].Status);
        Assert.Contains(empty ? "empty" : "not found", paused.Error!);
        Assert.Contains("transcription prompt", paused.Error!);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        File.WriteAllText(prompt, "Fixture instructions");
        jobs.Resume(directory);
        await jobs.ProcessSessionAsync(directory, default);
        Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(checkpoint, File.ReadAllBytes(ChunkStore.PathFor(directory, 1)));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3"))));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:00] Me: New words.\"}}]}")
            });
        }
    }

    private sealed class PromptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            int call = ++Calls;
            if (call == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(token);
            }
            return new(call == 2 ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:00] Me: New words.\"}}]}")
            };
        }
    }
}
