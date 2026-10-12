using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio.Wave;

namespace MeetTape.Tests;

public sealed class CaptureTests
{
    [Fact]
    public void BothSourcesStartAndStopAndSecondCaptureIsRejected()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        capture.Start();
        Assert.True(me.Running && them.Running);
        Assert.True(capture.Status.IsActive);
        Assert.Throws<InvalidOperationException>(() => capture.Start());
        capture.Stop();
        Assert.False(capture.Status.IsActive);
        Assert.True(me.Disposed && them.Disposed);
        Assert.Throws<InvalidOperationException>(() => capture.Stop());
    }

    [Fact]
    public void QuietLoopbackDoesNotStopMicrophoneOrRequireBuffering()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        int delivered = 0;
        using var capture = new CaptureCoordinator(() => new(me, them), (channel, args, _, _) =>
        {
            Assert.Equal(CaptureChannel.Me, channel);
            Assert.Same(me.Buffer, args.Buffer);
            delivered++;
        });
        capture.Start();
        for (int i = 0; i < 20000; i++)
            me.Emit();
        Assert.Equal(20000, delivered);
        Assert.Equal(20000L * me.Buffer.Length, capture.Status.MeBytes);
        Assert.Equal(0, capture.Status.ThemBytes);
        Assert.True(capture.Status.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeviceFailureStopsBothSourcesOutsideCallbackAndKeepsSafeDiagnostic(bool microphone)
    {
        var me = new FakeSource();
        var them = new FakeSource();
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new CaptureCoordinator(() => new(me, them));
        capture.Failed += message => failed.TrySetResult(message);
        capture.Start();
        (microphone ? me : them).Fail(new COMException("private meeting content", unchecked((int)0x88890004)));
        string diagnostic = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(microphone ? "Me" : "Them", diagnostic);
        Assert.Contains("88890004", diagnostic);
        Assert.DoesNotContain("private meeting content", diagnostic);
        Assert.False(capture.Status.IsActive);
        Assert.True(me.Disposed && them.Disposed);
        Assert.False(me.DisposedInsideCallback || them.DisposedInsideCallback);
    }

    [Fact]
    public void FailureStartingSecondSourceDisposesBothAndAllowsNewSession()
    {
        var firstMe = new FakeSource();
        var firstThem = new FakeSource { FailStart = true };
        int opened = 0;
        using var capture = new CaptureCoordinator(() => ++opened == 1
            ? new(firstMe, firstThem) : new(new FakeSource(), new FakeSource()));
        var error = Assert.Throws<InvalidOperationException>(() => capture.Start());
        Assert.DoesNotContain("private", error.Message);
        Assert.True(firstMe.Disposed && firstThem.Disposed);
        Assert.False(capture.Status.IsActive);
        capture.Start();
        Assert.True(capture.Status.IsActive);
    }

    [Fact]
    public async Task ConsumerFailureStopsCaptureRatherThanSilentlyLosingData()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new CaptureCoordinator(() => new(me, them), (_, _, _, _) => throw new IOException("private path"));
        capture.Failed += _ => failed.TrySetResult();
        capture.Start();
        me.Emit();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(capture.Status.IsActive);
        Assert.DoesNotContain("private path", capture.Status.Error!);
    }

    [Fact]
    public void StopAndDisposeFailuresStillReleaseOtherSourceAndReturnDiagnostic()
    {
        var me = new FakeSource { FailStop = true, FailDispose = true };
        var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        capture.Start();
        capture.Stop();
        Assert.False(capture.Status.IsActive);
        Assert.True(them.Disposed);
        Assert.NotNull(capture.Status.Error);
    }

    [Fact]
    public void CliHandlesDuplicateStartStopAndStopsOnExit()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        int result = Cli.Run(["--config", example], new StringReader("start\nstart\nstatus\nexit\n"), output, error,
            _ => new CaptureCoordinator(() => new(me, them)));
        Assert.Equal(0, result);
        Assert.Contains("already active", output.ToString());
        Assert.Contains("Capturing", output.ToString());
        Assert.True(me.Disposed && them.Disposed);
    }

    [Fact]
    public void RepeatedTimingProblemsNotifyOnceUntilTheirImpactEscalates()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        var warnings = new List<string>();
        capture.Warning += warnings.Add;
        capture.Start();
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 1, 480, "one frame"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 2, 480, "two frames"));
        Assert.Single(warnings);
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 960, 480, "larger than packet"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 970, 480, "still larger"));
        Assert.Equal(2, warnings.Count);
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.InvalidTimestamp, 0, 480, "QPC invalid"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Discontinuity, 0, 480, "WASAPI gap"));
        Assert.Equal(4, warnings.Count);
        Assert.All(warnings, warning => Assert.Contains("Recording continues", warning));
        Assert.True(capture.Status.IsActive);
        Assert.Equal(5, capture.Status.ClockAdjustments);
        Assert.Equal(1, capture.Status.Discontinuities);
        Assert.Equal(1, capture.Status.ThemTiming.InvalidTimestamps);
        Assert.Equal(970, capture.Status.ThemTiming.LargestShiftFrames);
        capture.Stop();
        Assert.Null(capture.Status.Error);
    }

    [Fact]
    public async Task CliShowsWarningDuringRecordingAndTimingCountersOnStatusAndStop()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTapeCliTiming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = new QueuedReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var me = new FakeSource();
        var them = new FakeSource();
        CaptureCoordinator? capture = null;
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        string projectRoot = Path.GetDirectoryName(example)!;
        string configPath = Path.Combine(root, "config.json");
        var config = JsonNode.Parse(File.ReadAllText(example))!;
        config["meetingsRoot"] = Path.Combine(root, "meetings");
        config["transcription"]!["promptFile"] = Path.Combine(projectRoot, "prompts", "transcription.md");
        config["summary"]!["promptFile"] = Path.Combine(projectRoot, "prompts", "summary.md");
        File.WriteAllText(configPath, config.ToJsonString());
        Task<int>? cli = null;
        try
        {
            cli = Task.Run(() => Cli.Run(["--config", configPath], input, output, error,
                _ => capture = new CaptureCoordinator(() => new(me, them), (channel, _, _, _) =>
                    capture!.ReportTiming(channel, new(AudioTimingIssueKind.Overlap, 307, 480, "synthetic QPC regression"))),
                (_, _) => 0, () => [], _ => { }));
            input.Post("start");
            Assert.True(SpinWait.SpinUntil(() => capture?.Status.IsActive == true, TimeSpan.FromSeconds(5)));
            them.Emit();
            Assert.Equal(1, capture!.Status.ClockAdjustments);
            Assert.True(capture.Status.IsActive);
            input.Post("status");
            input.Post("stop");
            input.Post("exit");
            Assert.Equal(0, await cli.WaitAsync(TimeSpan.FromSeconds(10)));
            string result = output.ToString();
            Assert.Contains("Them: audio timing moved by 6.396 ms. Recording continues", result);
            Assert.Contains("Capturing.", result);
            Assert.Contains("WASAPI timing corrections: Me 0", result);
            Assert.Contains("Them 1", result);
            Assert.Contains("Recording retained", result);
        }
        finally
        {
            input.Post("exit");
            if (cli is { IsCompleted: false }) await cli.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AsynchronousCaptureFailureRestartsDeferredJobsWithoutRequiringStop()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTapeAutoResume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = new QueuedReader();
        var output = new StringWriter();
        var error = new StringWriter();
        var me = new FakeSource();
        var them = new FakeSource();
        CaptureCoordinator? capture = null;
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        string projectRoot = Path.GetDirectoryName(example)!;
        string configPath = Path.Combine(root, "config.json");
        var config = JsonNode.Parse(File.ReadAllText(example))!;
        config["meetingsRoot"] = Path.Combine(root, "meetings");
        config["transcription"]!["promptFile"] = Path.Combine(projectRoot, "prompts", "transcription.md");
        config["summary"]!["promptFile"] = Path.Combine(projectRoot, "prompts", "summary.md");
        File.WriteAllText(configPath, config.ToJsonString());
        Task<int>? cli = null;
        try
        {
            cli = Task.Run(() => Cli.Run(["--config", configPath], input, output, error,
                _ => capture = new CaptureCoordinator(() => new(me, them)),
                (_, _) => 0, () => [], _ => { }));
            input.Post("start");
            Assert.True(SpinWait.SpinUntil(() => capture?.Status.IsActive == true, TimeSpan.FromSeconds(5)));

            var loaded = ConfigurationLoader.Load(configPath);
            var session = new SessionData
            {
                Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 1,
                RecordingState = RecordingState.Recorded,
                Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
                Processing = new(120, loaded.Settings.Transcription, loaded.Settings.Summary, loaded.Directory),
                Stages = new()
                {
                    ["trim"] = new(true, StageState.Completed),
                    ["prepare"] = new(true, StageState.Pending),
                    ["transcribe"] = new(true, StageState.Pending),
                    ["assemble"] = new(true, StageState.Pending),
                    ["summary"] = new(false, StageState.Disabled)
                }
            };
            var created = SessionStore.Create(loaded.MeetingsRoot, session);
            created.Lease.Dispose();
            // Missing originals force the resumed job into a recoverable pause; no provider call is possible.
            me.Fail(new IOException("Simulated device failure"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { return SessionStore.Read(created.Directory).JobState == JobState.Paused; }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(5)), "The deferred runner did not restart after capture failed.");
            Assert.False(capture!.Status.IsActive);
        }
        finally
        {
            input.Post("exit");
            if (cli is not null) Assert.Equal(0, await cli.WaitAsync(TimeSpan.FromSeconds(10)));
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class QueuedReader : TextReader
    {
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        public void Post(string text) => messages.Writer.TryWrite(text);
        public override string? ReadLine() => messages.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class FakeSource : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public byte[] Buffer { get; } = new byte[320];
        public bool Running { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedInsideCallback { get; private set; }
        public bool FailStart { get; init; }
        public bool FailStop { get; init; }
        public bool FailDispose { get; init; }
        private bool inCallback;
        private int callbackThread;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording()
        {
            if (FailStart) throw new COMException("private startup text");
            Running = true;
        }
        public void StopRecording()
        {
            Running = false;
            if (FailStop) throw new COMException("private stop text");
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }
        public void Emit() => DataAvailable?.Invoke(this, new WaveInEventArgs(Buffer, Buffer.Length));
        public void Fail(Exception error)
        {
            inCallback = true;
            callbackThread = Environment.CurrentManagedThreadId;
            try { RecordingStopped?.Invoke(this, new StoppedEventArgs(error)); }
            finally { inCallback = false; }
        }
        public void Dispose()
        {
            DisposedInsideCallback = inCallback && callbackThread == Environment.CurrentManagedThreadId;
            Running = false;
            Disposed = true;
            if (FailDispose) throw new COMException("private dispose text");
        }
    }
}
