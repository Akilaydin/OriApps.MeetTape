using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace MeetTape.Tests;

public sealed class TrimmingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetTapeTrim-" + Guid.NewGuid().ToString("N"));
    private readonly LoadedConfiguration config;

    public TrimmingTests()
    {
        Directory.CreateDirectory(root);
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        config = ConfigurationLoader.Load(example);
    }

    [Fact]
    public async Task SuggestionUsesBothTracksAndPreservesInteriorSilenceAndOriginals()
    {
        string directory = await SessionAsync([(1, 1.5)], [(2.5, 3)]);
        var data = SessionStore.Read(directory);
        byte[] meHash = Hash(directory, "me.mp3"), themHash = Hash(directory, "them.mp3");
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.Begin(directory, "Выходные: План / Маршрут");
        directory = workflow.SelectedDirectory!;
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var named = SessionStore.Read(directory);
        Assert.Equal(data.Id, named.Id);
        Assert.Equal("Выходные: План / Маршрут", named.Title);
        Assert.Contains("Выходные_ План _ Маршрут", Path.GetFileName(directory));
        var suggestion = await new AudioTrimmer("ffmpeg").SuggestAsync(directory, 4, default);
        Assert.True(suggestion.HasActivity);
        Assert.InRange(suggestion.StartSeconds, 0.88, 0.91);
        Assert.InRange(suggestion.EndSeconds, 3.09, 3.12);
        workflow.Choose("accept");
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var completed = SessionStore.Read(directory);
        Assert.Equal(StageState.Completed, completed.Stages["trim"].Status);
        Assert.Equal("accept", completed.Trim.Decision);
        Assert.True(completed.Trim.Applied);
        Assert.StartsWith("processed/trim_", completed.Trim.RelativeDirectory!);
        float[] me = await EncodingTests.DecodeAsync(Path.Combine(directory, completed.Trim.RelativeDirectory!, "me.mp3"));
        float[] them = await EncodingTests.DecodeAsync(Path.Combine(directory, completed.Trim.RelativeDirectory!, "them.mp3"));
        Assert.Equal(me.Length, them.Length);
        Assert.Equal((int)Math.Round((completed.Trim.EndSeconds!.Value - completed.Trim.StartSeconds) * 48000), me.Length);
        Assert.True(Rms(me, 0.2, 0.4) > 0.05);
        Assert.True(Rms(them, 1.7, 1.9) > 0.05);
        Assert.True(Rms(me, 0.8, 1.2) < 0.001 && Rms(them, 0.8, 1.2) < 0.001);
        Assert.Equal(meHash, Hash(directory, "me.mp3"));
        Assert.Equal(themHash, Hash(directory, "them.mp3"));
        Assert.True(File.GetAttributes(Path.Combine(directory, "me.mp3")).HasFlag(FileAttributes.ReadOnly));
        Assert.Throws<InvalidOperationException>(() => workflow.Begin(directory, "unexpected rename"));
        Assert.Equal(named.Title, SessionStore.Read(directory).Title);
    }

    [Fact]
    public async Task WhollySilentTrackDoesNotHideOtherSourcesEdges()
    {
        string directory = await SessionAsync([], [(1, 3)]);
        var found = await new AudioTrimmer("ffmpeg").SuggestAsync(directory, 4, default);
        Assert.True(found.HasActivity);
        Assert.InRange(found.StartSeconds, 0.88, 0.91);
        Assert.InRange(found.EndSeconds, 3.09, 3.12);
    }

    [Fact]
    public async Task NoActivityAcceptsFullOriginalsWithoutEmptyDerivatives()
    {
        string directory = await SessionAsync([], []);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<OperationNotice>();
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.Notice += notices.Enqueue;
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(notices, notice => notice.Kind == NoticeKind.Warning && notice.Message.Contains("No activity detected"));
        workflow.Choose("accept");
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = SessionStore.Read(directory);
        Assert.Equal(new TrimParameters(0, 4, false) { Decision = "accept" }, saved.Trim);
        Assert.Equal(StageState.Completed, saved.Stages["trim"].Status);
        Assert.False(Directory.Exists(Path.Combine(directory, "processed")));
    }

    [Fact]
    public async Task ManualBoundsAndSkipPersistChoicesAndRejectInvalidBounds()
    {
        string directory = await SessionAsync([(1, 3)], []);
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] before = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        Assert.Throws<InvalidOperationException>(() => workflow.Choose("manual", 3, 2));
        Assert.Throws<InvalidOperationException>(() => workflow.Choose("manual", -1, 2));
        Assert.Throws<InvalidOperationException>(() => workflow.Choose("manual", 0, 5));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "session.json")));
        workflow.Choose("manual", 0.5, 3.5);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("manual", SessionStore.Read(directory).Trim.Decision);
        string second = await SessionAsync([], []);
        workflow.Begin(second);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        workflow.Choose("skip");
        Assert.Equal(new TrimParameters(0, 4, false) { Decision = "skip" }, SessionStore.Read(second).Trim);
        Assert.False(Directory.Exists(Path.Combine(second, "processed")));
    }

    [Fact]
    public async Task CancellationBeforeNewCaptureLeavesChoicePendingAndCanResume()
    {
        string directory = await SessionAsync([], []);
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.Begin(directory);
        workflow.CancelAndWait();
        Assert.True(workflow.Completion.IsCompleted);
        Assert.NotEqual(StageState.Completed, SessionStore.Read(directory).Stages["trim"].Status);
        // The coordinator starts only after all preparation work has joined.
        using var capture = new CaptureCoordinator(() =>
        {
            Assert.True(workflow.Completion.IsCompleted);
            return new(new Source(), new Source());
        });
        capture.Start(); capture.Stop();
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        workflow.Choose("skip");
        Assert.Equal(StageState.Completed, SessionStore.Read(directory).Stages["trim"].Status);
    }

    [Fact]
    public async Task EncoderFailurePausesWithoutSelectingPartialDerivative()
    {
        string directory = await SessionAsync([(1, 3)], []);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<OperationNotice>();
        using var workflow = new PostRecordingWorkflow("missing-ffmpeg-" + Guid.NewGuid());
        workflow.Notice += notices.Enqueue;
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StageState.Paused, SessionStore.Read(directory).Stages["trim"].Status);
        workflow.Choose("manual", 1, 3);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var failed = SessionStore.Read(directory);
        Assert.Equal(JobState.Paused, failed.JobState);
        Assert.Equal(StageState.Paused, failed.Stages["trim"].Status);
        Assert.Null(failed.Trim.RelativeDirectory);
        Assert.False(failed.Trim.Applied);
        Assert.Contains(notices, notice => notice.Kind == NoticeKind.Warning && notice.Message.Contains("Cannot start FFmpeg")
            && notice.Message.Contains("missing"));
        Assert.True(File.Exists(Path.Combine(directory, "me.mp3")));
    }

    [Fact]
    public async Task FilesystemFailureRetainsPreviousMetadataAndSourceBytes()
    {
        string directory = await SessionAsync([], []);
        byte[] original = Hash(directory, "me.mp3");
        var notices = new System.Collections.Concurrent.ConcurrentQueue<OperationNotice>();
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        workflow.Notice += notices.Enqueue;
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        string metadata = Path.Combine(directory, "session.json");
        byte[] before = File.ReadAllBytes(metadata);
        File.SetAttributes(metadata, FileAttributes.ReadOnly);
        workflow.Choose("manual", 1, 3);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(before, File.ReadAllBytes(metadata));
        Assert.Equal(original, Hash(directory, "me.mp3"));
        Assert.Contains(notices, notice => notice.Kind == NoticeKind.Warning && notice.Message.Contains("Could not update session.json"));
        Assert.False(Directory.Exists(Path.Combine(directory, "processed")));
    }

    [Fact]
    public async Task UnicodeTitleAndCollisionNeverOverwriteAnotherSession()
    {
        string directory = await SessionAsync([], []);
        var data = SessionStore.Read(directory);
        string title = string.Concat(Enumerable.Repeat("😀", 80));
        directory = PostRecordingWorkflow.SetTitle(directory, title);
        Assert.Equal(title, SessionStore.Read(directory).Title);
        Assert.DoesNotContain('\uFFFD', Path.GetFileName(directory));
        string collision = Path.Combine(root, $"collision_{data.StartedAtUtc.ToLocalTime():dd.MM.yyyy HH-mm} 00h00m");
        Directory.CreateDirectory(collision);
        File.WriteAllText(Path.Combine(collision, "keep.txt"), "untouched");
        byte[] before = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        Assert.Throws<RecordingException>(() => PostRecordingWorkflow.SetTitle(directory, "collision"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "session.json")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(collision, "keep.txt")));
        Assert.Throws<InvalidOperationException>(() => PostRecordingWorkflow.SetTitle(directory, "invalid\ntitle"));
    }

    [Fact]
    public async Task ControllerNamesStoppedRecordingAndRejectsPreparationDuringCapture()
    {
        string configPath = Path.Combine(root, "config.json");
        ConfigurationBootstrap.Initialize(configPath, root);
        var json = JsonNode.Parse(File.ReadAllText(configPath))!;
        json["stages"]!["trim"] = false;
        json["stages"]!["transcribe"] = false;
        File.WriteAllText(configPath, json.ToJsonString());
        var loaded = ConfigurationLoader.Load(configPath);
        var capture = new Mp3Recorder(loaded, () => new(new Source(), new Source())).Capture;
        var controller = new DesktopController(loaded, capture);
        try
        {
            await controller.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ChooseTrimAsync(TrimDecision.Manual, 0, 1));
            string directory = (await controller.StopAsync())!;
            var stopped = SessionStore.Read(directory);
            Assert.StartsWith("Unnamed meeting_", Path.GetFileName(directory));
            Assert.Equal(directory, (await controller.ReadAsync()).AwaitingTitleDirectory);
            Assert.Equal(directory, capture.Status.RecordingDirectory);
            await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
            await controller.PrepareAsync(directory, "Weekend");
            var saved = Assert.Single(SessionStore.Discover(root));
            Assert.Equal("Weekend", saved.Data!.Title);
            Assert.Contains("Weekend", saved.Directory);
            Assert.DoesNotContain(stopped.Id.ToString("N"), Path.GetFileName(saved.Directory));
            Assert.Equal(stopped.Id, saved.Data.Id);
            Assert.Equal(saved.Directory, capture.Status.RecordingDirectory);
            Assert.Null((await controller.ReadAsync()).AwaitingTitleDirectory);
        }
        finally { await controller.ShutdownAsync(); }
    }
    [Fact]
    public async Task ExitFinalizesANewUnnamedFolderWithoutStartingPreparationOrTranscription()
    {
        var controller = new DesktopController(RecordingConfiguration(), openSources: _ => new(new Source(), new Source()));
        try
        {
            await controller.StartAsync();
            var initial = (await controller.ReadAsync()).Sessions.Single().Data!;
            await controller.ShutdownAsync();
            var saved = Assert.Single(SessionStore.Discover(root));
            Assert.StartsWith("Unnamed meeting_", Path.GetFileName(saved.Directory));
            Assert.DoesNotContain(initial.Id.ToString("N"), Path.GetFileName(saved.Directory));
            Assert.Equal(initial.Id, saved.Data!.Id);
            Assert.True(saved.MePresent && saved.ThemPresent);
            Assert.False((await controller.ReadAsync()).Capture.IsActive);
            Assert.False(Directory.Exists(Path.Combine(saved.Directory, "processed")));
            Assert.False(File.Exists(Path.Combine(saved.Directory, "transcription.md")));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Fact]
    public async Task DefaultNameCollisionRetainsAudioAndNamingGateAndCanRecoverWithChosenTitle()
    {
        var controller = new DesktopController(RecordingConfiguration(), openSources: _ => new(new Source(), new Source()));
        try
        {
            await controller.StartAsync();
            var initial = (await controller.ReadAsync()).Sessions.Single();
            string collision = Path.Combine(root, $"Unnamed meeting_{initial.Data!.StartedAtUtc.ToLocalTime():dd.MM.yyyy HH-mm} 00h00m");
            Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "keep.txt"), "Untouched");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(controller.StopAsync);
            Assert.Contains("already exists", error.Message);
            Assert.True(Directory.Exists(initial.Directory));
            Assert.Equal("Untouched", File.ReadAllText(Path.Combine(collision, "keep.txt")));
            var stopped = await controller.ReadAsync();
            Assert.False(stopped.Capture.IsActive); Assert.Null(stopped.Capture.Error);
            Assert.Equal(initial.Directory, stopped.AwaitingTitleDirectory);
            Assert.True(File.Exists(Path.Combine(initial.Directory, "me.mp3")));
            Assert.True(File.Exists(Path.Combine(initial.Directory, "them.mp3")));
            await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
            await controller.PrepareAsync(initial.Directory, "Different title");
            var named = Assert.Single(SessionStore.Discover(root));
            Assert.StartsWith("Different title_", Path.GetFileName(named.Directory));
            Assert.Equal(initial.Data.Id, named.Data!.Id);
            Assert.Equal(named.Directory, (await controller.ReadAsync()).Capture.RecordingDirectory);
            Assert.Null((await controller.ReadAsync()).AwaitingTitleDirectory);
        }
        finally { await controller.ShutdownAsync(); }
    }

    private LoadedConfiguration RecordingConfiguration() => config with
    {
        Settings = config.Settings with { MeetingsRoot = root, Stages = new() { Trim = false, Transcribe = false, Summary = false } }
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitAfterDefaultNameCollisionStillCompletesAndRetainsFinalizedSource(bool stopFirst)
    {
        var controller = new DesktopController(RecordingConfiguration(), openSources: _ => new(new Source(), new Source()));
        var notices = new List<OperationNotice>();
        controller.Notice += notices.Add;
        await controller.StartAsync();
        var initial = (await controller.ReadAsync()).Sessions.Single();
        string collision = Path.Combine(root, $"Unnamed meeting_{initial.Data!.StartedAtUtc.ToLocalTime():dd.MM.yyyy HH-mm} 00h00m");
        Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "keep.txt"), "Untouched");
        if (stopFirst) await Assert.ThrowsAsync<InvalidOperationException>(controller.StopAsync);
        byte[]? audio = stopFirst ? File.ReadAllBytes(Path.Combine(initial.Directory, "me.mp3")) : null;
        await controller.ShutdownAsync();
        await controller.ShutdownAsync();
        var saved = Assert.Single(SessionStore.Discover(root));
        Assert.Equal(initial.Directory, saved.Directory);
        Assert.Equal(initial.Data.Id, saved.Data!.Id);
        Assert.Equal(RecordingState.Recorded, saved.Data.RecordingState);
        if (audio is not null) Assert.Equal(audio, File.ReadAllBytes(Path.Combine(saved.Directory, "me.mp3")));
        Assert.True(saved.MePresent && saved.ThemPresent);
        Assert.Equal("Untouched", File.ReadAllText(Path.Combine(collision, "keep.txt")));
        Assert.Contains(notices, n => n.Kind == NoticeKind.Error);
    }

    [Fact]
    public async Task CancelBeforeNamingPersistsDecisionAndAllowsNextCaptureWithoutApi()
    {
        string configPath = Path.Combine(root, "config.json");
        ConfigurationBootstrap.Initialize(configPath, root);
        var loaded = ConfigurationLoader.Load(configPath);
        var capture = new Mp3Recorder(loaded, () => new(new Source(), new Source())).Capture;
        var controller = new DesktopController(loaded, capture, readKey: _ => throw new Exception("Cancellation must not send requests"));
        try
        {
            await controller.StartAsync();
            await Task.Delay(30);
            string directory = (await controller.StopAsync())!;
            byte[] original = File.ReadAllBytes(Path.Combine(directory, "me.mp3"));
            await controller.CancelProcessingAsync(directory);
            var canceled = SessionStore.Read(directory);
            Assert.Equal(JobState.Canceled, canceled.JobState);
            Assert.Null(canceled.Title);
            Assert.Null((await controller.ReadAsync()).AwaitingTitleDirectory);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
            await controller.StartAsync();
            Assert.True((await controller.ReadAsync()).Capture.IsActive);
            Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
            string? next;
            try { next = await controller.StopAsync(); }
            catch (InvalidOperationException error) when (error.Message == "Named session directory already exists; refusing to overwrite.")
            { next = (await controller.ReadAsync()).AwaitingTitleDirectory; }
            Assert.NotNull(next);
            await controller.PrepareAsync(next, "Next meeting");
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Fact]
    public async Task CancelTrimPreparationRetainsOriginalsAndExplicitNameTrimClearsCancellation()
    {
        string directory = await SessionAsync([], []);
        byte[] original = Hash(directory, "me.mp3");
        var loaded = config with { Settings = config.Settings with { MeetingsRoot = root } };
        using var capture = new CaptureCoordinator(() => new(new Source(), new Source()));
        var controller = new DesktopController(loaded, capture);
        try
        {
            await controller.PrepareAsync(directory, null);
            await controller.CancelProcessingAsync(directory);
            Assert.Equal(JobState.Canceled, SessionStore.Read(directory).JobState);
            Assert.False((await controller.ReadAsync()).Preparing);
            Assert.Equal(original, Hash(directory, "me.mp3"));
            await controller.PrepareAsync(directory, null);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while ((await controller.ReadAsync()).Preparing)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline);
                await Task.Delay(25);
            }
            Assert.NotEqual(JobState.Canceled, SessionStore.Read(directory).JobState);
            Assert.Equal(StageState.AwaitingUser, SessionStore.Read(directory).Stages["trim"].Status);
            Assert.Equal(original, Hash(directory, "me.mp3"));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("01:23.5", 83.5)]
    [InlineData("02:01:03", 7263)]
    public void ParsesManualTimes(string text, double expected)
    {
        Assert.True(AudioTrimmer.TryParseTime(text, out double seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("1:60")]
    [InlineData("1:2:3:4")]
    [InlineData("1,5")]
    [InlineData("Infinity")]
    public void RejectsInvalidTimes(string text) => Assert.False(AudioTrimmer.TryParseTime(text, out _));

    private async Task<string> SessionAsync((double Start, double End)[] me, (double Start, double End)[] them)
    {
        var data = new SessionData
        {
            Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 4,
            RecordingState = RecordingState.Recorded,
            Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
            Processing = new(120, config.Settings.Transcription, config.Settings.Summary, config.Directory),
            Trim = new(0, 4), Stages = new() { ["trim"] = new(true, StageState.AwaitingUser) }
        };
        var created = SessionStore.Create(root, data);
        using var lease = created.Lease;
        await EncodeAsync(Path.Combine(created.Directory, "me.mp3"), me);
        await EncodeAsync(Path.Combine(created.Directory, "them.mp3"), them);
        SessionStore.ProtectOriginals(created.Directory);
        return created.Directory;
    }

    private static async Task EncodeAsync(string path, (double Start, double End)[] active)
    {
        using var encoder = new FfmpegEncoder("ffmpeg", path);
        byte[] block = new byte[4800 * sizeof(float)];
        for (int n = 0; n < 40; n++)
        {
            for (int i = 0; i < 4800; i++)
            {
                double time = (n * 4800 + i) / 48000d;
                float value = active.Any(a => time >= a.Start && time < a.End) ? (float)(0.25 * Math.Sin(time * 2 * Math.PI * 440)) : 0;
                BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(i * sizeof(float)), value);
            }
            await encoder.Input.WriteAsync(block);
        }
        await encoder.CompleteAsync();
    }

    private static byte[] Hash(string directory, string name) => SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)));

    private static double Rms(float[] samples, double start, double end)
    {
        int a = (int)(start * 48000), b = (int)(end * 48000);
        return Math.Sqrt(samples[a..b].Average(x => x * x));
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private sealed class Source : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
    }
}
