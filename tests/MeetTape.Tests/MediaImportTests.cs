namespace MeetTape.Tests;

public sealed class MediaImportTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("meettape-import-test-").FullName;

    private LoadedConfiguration Config(bool trim = false) => new(new Configuration
    {
        MeetingsRoot = "meetings", Stages = new() { Trim = trim },
        Transcription = new() { Endpoint = "https://example.invalid", Model = "test", PromptFile = "test-prompt.md" },
        Summary = new() { Endpoint = "https://example.invalid", Model = "test", PromptFile = "test-prompt.md" }
    }, root);

    [Theory]
    [InlineData(".wav")]
    [InlineData(".mp4")]
    public async Task ImportSavesOneTrackAndReusesBothTranscriptionPreparers(string extension)
    {
        var config = Config();
        string input = Path.Combine(root, "Моя встреча" + extension);
        string[] media = extension == ".wav"
            ? ["-f", "lavfi", "-i", "sine=frequency=440:duration=1"]
            : ["-f", "lavfi", "-i", "color=c=black:s=64x64:r=10:d=1",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "mpeg4", "-c:a", "aac", "-shortest"];
        await AudioTrimmer.RunAsync("ffmpeg", ["-nostdin", "-v", "error", .. media, input], null, default);
        var original = await File.ReadAllBytesAsync(input);

        var controller = new DesktopController(config);
        try { await controller.ImportMediaAsync(input); }
        finally { await controller.ShutdownAsync(); }
        var saved = Assert.Single(SessionStore.Discover(config.MeetingsRoot));
        var data = saved.Data!;
        Assert.Null(saved.Diagnostic);
        Assert.True(data.ImportedMedia);
        Assert.Equal("Моя встреча", data.Title);
        Assert.Equal(RecordingState.Recorded, data.RecordingState);
        Assert.False(ProcessingJobs.Eligible(data));
        Assert.True(ProcessingJobs.CanResume(data));
        Assert.Equal("Ready to transcribe", SessionRow.From(saved).DisplayStatus);
        Assert.True(data.DurationSeconds is > 0.99 and < 1.06);
        Assert.False(saved.MePresent);
        Assert.True(saved.ThemPresent);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));

        var cloud = await new AudioChunker("ffmpeg").PrepareAsync(saved.Directory, data, default);
        var local = await new LocalAudioChunker("ffmpeg").PrepareAsync(saved.Directory, data, default);
        Assert.Equal("Them", Assert.Single(cloud.Chunks).Source);
        Assert.Equal("Them", Assert.Single(local.Chunks).Source);
        AudioChunker.ValidateManifest(data with { Audio = cloud });
        LocalAudioChunker.ValidateManifest(data with { LocalAudio = local });
    }

    [Theory]
    [InlineData(false, TrimDecision.Skip)]
    [InlineData(true, TrimDecision.Skip)]
    [InlineData(true, TrimDecision.Manual)]
    public async Task NamingAndOptionalTrimStartImportedProcessingWithoutResume(bool trim, TrimDecision decision)
    {
        var config = Config(trim);
        File.WriteAllText(Path.Combine(root, "test-prompt.md"), "Fixture prompt");
        string input = Path.Combine(root, "original.wav");
        await AudioTrimmer.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=2", input], null, default);
        using var handler = new ImportProvider();
        using var http = new HttpClient(handler);
        var controller = new DesktopController(config, http: http, readKey: _ => "fixture");
        try
        {
            await controller.InitializeAsync();
            await controller.ImportMediaAsync(input);
            var snapshot = await controller.ReadAsync();
            var imported = Assert.Single(snapshot.Sessions);
            Assert.Equal(imported.Directory, snapshot.AwaitingTitleDirectory);
            Assert.False(ProcessingJobs.Eligible(imported.Data!));
            Assert.Equal(0, handler.Calls);

            await controller.PrepareAsync(imported.Directory, "Named interview");
            var named = Assert.Single((await controller.ReadAsync()).Sessions);
            Assert.Equal(imported.Data!.Id, named.Data!.Id);
            Assert.Equal("Named interview", named.Data.Title);
            if (trim)
            {
                await WaitUntilAsync(async () => await controller.ReadAsync() is { Preparing: false, Suggestion: not null });
                Assert.False(ProcessingJobs.Eligible(SessionStore.Read(named.Directory)));
                Assert.Equal(0, handler.Calls);
                if (decision == TrimDecision.Manual) await controller.ChooseTrimAsync(decision, 0.25, 1.25);
                else await controller.ChooseTrimAsync(decision);
            }

            await WaitUntilAsync(() => Task.FromResult(SessionStore.Read(named.Directory).JobState == JobState.Completed));
            Assert.Equal(1, handler.Calls);
            Assert.Contains("Imported transcript.", File.ReadAllText(Path.Combine(named.Directory, "transcription.md")));
        }
        finally { await controller.ShutdownAsync(); }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "Expected the shared workflow to advance.");
            await Task.Delay(25);
        }
    }

    private sealed class ImportProvider : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"[00:00] Them: Imported transcript."}}]}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task ImportedAudioCanBeTrimmedWithoutAMicrophoneTrack()
    {
        var config = Config(trim: true);
        string input = Path.Combine(root, "audio.wav");
        await AudioTrimmer.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=2", input], null, default);
        string directory = await MediaImport.ImportAsync(config, input, default);
        using var prep = new PostRecordingWorkflow("ffmpeg");
        prep.Begin(directory);
        await prep.Completion;
        Assert.NotNull(prep.Suggestion);
        prep.Choose("manual", 0.25, 1.25);
        await prep.Completion;
        var data = SessionStore.Read(directory);
        Assert.True(data.Trim.Applied);
        Assert.Equal(StageState.Completed, data.Stages["trim"].Status);
        var trimmed = Path.Combine(directory, data.Trim.RelativeDirectory!);
        Assert.False(File.Exists(Path.Combine(trimmed, "me.mp3")));
        Assert.True(File.Exists(Path.Combine(trimmed, "them.mp3")));
    }

    [Fact]
    public async Task VideoWithoutAudioDoesNotPublishOrModifyAnything()
    {
        var config = Config();
        string input = Path.Combine(root, "silent.mp4");
        await AudioTrimmer.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i",
            "color=c=black:s=64x64:r=10:d=1", "-c:v", "mpeg4", "-an", input], null, default);
        var original = await File.ReadAllBytesAsync(input);
        await Assert.ThrowsAsync<RecordingException>(() => MediaImport.ImportAsync(config, input, default));
        Assert.Empty(SessionStore.Discover(config.MeetingsRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(config.MeetingsRoot, ".imports")));
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }
}
