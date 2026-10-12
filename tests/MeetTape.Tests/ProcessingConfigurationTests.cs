using System.Net;
using System.Security.Cryptography;

namespace MeetTape.Tests;

public sealed class ProcessingConfigurationTests
{
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
}
