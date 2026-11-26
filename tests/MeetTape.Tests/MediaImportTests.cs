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
