using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetTape.Tests;

public sealed class CaptureTests
{
    [Fact]
    public void PcmActivityIsIndependentIgnoresSilentBuffersDecaysAndResetsForNextCapture()
    {
        long now = Stopwatch.Frequency * 10;
        int opened = 0;
        using var capture = new CaptureCoordinator(() =>
        {
            opened++;
            return new(new FakeSource(), new FakeSource());
        }, levelTimestamp: () => now);
        static AudioPacket Packet(float amplitude, AudioClientBufferFlags flags = 0)
        {
            byte[] bytes = new byte[320];
            for (int i = 0; i < bytes.Length; i += sizeof(float)) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), amplitude);
            return AudioPacket.Copy(new WasapiPacketEventArgs(bytes, bytes.Length, 0, 0, flags), WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
        }
        using var me = Packet(0.5f);
        using var them = Packet(-0.25f);
        using var silent = Packet(0.75f, AudioClientBufferFlags.Silent);
        capture.Start();
        capture.ReportLevel(CaptureChannel.Me, me);
        Assert.Equal(0.5, capture.Status.MeLevel);
        Assert.Equal(0d, capture.Status.ThemLevel);
        capture.ReportLevel(CaptureChannel.Them, them);
        Assert.Equal(0.25, capture.Status.ThemLevel);
        now += Stopwatch.Frequency / 10;
        Assert.Equal(1d / 3, capture.Status.MeLevel, 5);
        Assert.Equal(1d / 6, capture.Status.ThemLevel, 5);
        capture.ReportLevel(CaptureChannel.Me, silent);
        Assert.Equal(0d, capture.Status.MeLevel);
        Assert.True(capture.Status.ThemLevel > 0);
        now += Stopwatch.Frequency;
        Assert.Equal(0d, capture.Status.ThemLevel);
        capture.ReportLevel(CaptureChannel.Me, me);
        capture.Stop();
        Assert.Equal(0d, capture.Status.MeLevel);
        Assert.Equal(0d, capture.Status.ThemLevel);
        capture.Start();
        Assert.Equal(0d, capture.Status.MeLevel);
        Assert.Equal(0d, capture.Status.ThemLevel);
        Assert.Equal(2, opened);
        Assert.Null(capture.Status.Error);
    }

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
    public async Task ControllerRejectsDuplicateStartStopAndFinalizesOnExit()
    {
        var me = new FakeSource(); var them = new FakeSource();
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        using var capture = new CaptureCoordinator(() => new(me, them));
        var config = ConfigurationLoader.Load(example);
        var controller = new DesktopController(config with { Settings = config.Settings with { MeetingsRoot = "dist/controller-empty" } }, capture);
        try
        {
            await controller.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
            Assert.True(capture.Status.IsActive);
            await controller.StopAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.StopAsync());
            await controller.StartAsync();
        }
        finally { await controller.ShutdownAsync(); }
        Assert.False(capture.Status.IsActive);
        Assert.True(me.Disposed && them.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
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
    public async Task ControllerPublishesLiveWarningsAndRetainsTimingCounters()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        var me = new FakeSource(); var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        var controller = new DesktopController(config with { Settings = config.Settings with { MeetingsRoot = "dist/controller-empty" } }, capture);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<OperationNotice>();
        controller.Notice += notices.Enqueue;
        try
        {
            await controller.StartAsync();
            capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 307, 480, "synthetic QPC regression"));
            Assert.Contains(notices, notice => notice.Kind == NoticeKind.Warning && notice.Message.Contains("Recording continues"));
            var active = await controller.ReadAsync();
            Assert.True(active.Capture.IsActive);
            Assert.Equal(1, active.Capture.ClockAdjustments);
            await controller.StopAsync();
            Assert.Equal(307, (await controller.ReadAsync()).Capture.ThemTiming.LargestShiftFrames);
        }
        finally { await controller.ShutdownAsync(); }
    }
    [Fact]
    public async Task AsynchronousCaptureFailureRestartsDeferredJobsWithoutRequiringStop()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTapeAutoResume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var me = new FakeSource(); var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        string path = Path.Combine(root, "config.json");
        ConfigurationBootstrap.Initialize(path, Path.Combine(root, "meetings"));
        var config = ConfigurationLoader.Load(path);
        var controller = new DesktopController(config, capture);
        try
        {
            await controller.InitializeAsync();
            await controller.StartAsync();
            var session = new SessionData
            {
                Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 1,
                RecordingState = RecordingState.Recorded,
                Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
                Processing = new(120, config.Settings.Transcription, config.Settings.Summary, config.Directory),
                Stages = new()
                {
                    ["trim"] = new(true, StageState.Completed), ["prepare"] = new(true, StageState.Pending),
                    ["transcribe"] = new(true, StageState.Pending), ["assemble"] = new(true, StageState.Pending),
                    ["summary"] = new(false, StageState.Disabled)
                }
            };
            var created = SessionStore.Create(config.MeetingsRoot, session);
            created.Lease.Dispose();
            // Missing originals force a pause before any provider request.
            me.Fail(new IOException("Simulated device failure"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { return SessionStore.Read(created.Directory).JobState == JobState.Paused; }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(5)), "Deferred runner did not restart after capture failed.");
            Assert.False(capture.Status.IsActive);
        }
        finally { await controller.ShutdownAsync(); Directory.Delete(root, recursive: true); }
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
