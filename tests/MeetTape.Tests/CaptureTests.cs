using System.Runtime.InteropServices;
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
