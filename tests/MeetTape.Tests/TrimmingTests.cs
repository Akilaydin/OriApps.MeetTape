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
        using var workflow = new PostRecordingWorkflow("ffmpeg", TextWriter.Null);
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
        var output = new StringWriter();
        using var workflow = new PostRecordingWorkflow("ffmpeg", output);
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("No activity detected", output.ToString());
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
        using var workflow = new PostRecordingWorkflow("ffmpeg", TextWriter.Null);
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
        using var workflow = new PostRecordingWorkflow("ffmpeg", TextWriter.Null);
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
        var output = new StringWriter();
        using var workflow = new PostRecordingWorkflow("missing-ffmpeg-" + Guid.NewGuid(), output);
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
        Assert.Contains("check originals, FFmpeg", output.ToString());
        Assert.True(File.Exists(Path.Combine(directory, "me.mp3")));
    }

    [Fact]
    public async Task FilesystemFailureRetainsPreviousMetadataAndSourceBytes()
    {
        string directory = await SessionAsync([], []);
        byte[] original = Hash(directory, "me.mp3");
        var output = new StringWriter();
        using var workflow = new PostRecordingWorkflow("ffmpeg", output);
        workflow.Begin(directory);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        string metadata = Path.Combine(directory, "session.json");
        byte[] before = File.ReadAllBytes(metadata);
        File.SetAttributes(metadata, FileAttributes.ReadOnly);
        workflow.Choose("manual", 1, 3);
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(before, File.ReadAllBytes(metadata));
        Assert.Equal(original, Hash(directory, "me.mp3"));
        Assert.Contains("Could not update session.json", output.ToString());
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
        string collision = Path.Combine(root, $"{data.StartedAtUtc.ToLocalTime():yyyy-MM-dd_HHmmss}_collision_{data.Id:N}");
        Directory.CreateDirectory(collision);
        File.WriteAllText(Path.Combine(collision, "keep.txt"), "untouched");
        byte[] before = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        Assert.Throws<RecordingException>(() => PostRecordingWorkflow.SetTitle(directory, "collision"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "session.json")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(collision, "keep.txt")));
        Assert.Throws<InvalidOperationException>(() => PostRecordingWorkflow.SetTitle(directory, "invalid\ntitle"));
    }

    [Fact]
    public void CliPromptsTitleAndPreparationCommandsAreRejectedDuringCapture()
    {
        string configPath = Path.Combine(root, "config.json");
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var json = JsonNode.Parse(File.ReadAllText(example))!;
        json["meetingsRoot"] = root;
        json["stages"]!["trim"] = false;
        json["stages"]!["transcribe"] = false;
        File.WriteAllText(configPath, json.ToJsonString());
        var loaded = ConfigurationLoader.Load(configPath);
        var output = new StringWriter();
        int result = Cli.Run(["--config", configPath], new StringReader("start\ntrim 0 1\nstop\nWeekend\nsessions\nexit\n"),
            output, TextWriter.Null, _ => new Mp3Recorder(loaded, () => new(new Source(), new Source())).Capture);
        Assert.Equal(0, result);
        Assert.Contains("Meeting title", output.ToString());
        Assert.Contains("paused during capture", output.ToString());
        var saved = Assert.Single(SessionStore.Discover(root));
        Assert.Equal("Weekend", saved.Data!.Title);
        Assert.Contains("Weekend", saved.Directory);
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
