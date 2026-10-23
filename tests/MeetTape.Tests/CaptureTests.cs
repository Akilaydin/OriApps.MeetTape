using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OriApps.MeetTape.Settings;

namespace MeetTape.Tests;

public sealed class CaptureTests
{
    [Fact]
    public void WindowsCommunicationsIsRequestedOnlyForMicrophoneAndNeverForLoopback()
    {
        var categories = new List<AudioStreamCategory>();
        void Record(AudioStreamCategory category) => categories.Add(category);

        WasapiPacketCapture.ApplyMicrophoneMode(false, MicrophoneCaptureMode.WindowsDefault, Record);
        WasapiPacketCapture.ApplyMicrophoneMode(true, MicrophoneCaptureMode.WindowsDefault, Record);
        Assert.Empty(categories); // The legacy mode does not request any Windows stream processing.

        WasapiPacketCapture.ApplyMicrophoneMode(false, MicrophoneCaptureMode.WindowsCommunications, Record);
        Assert.Equal([AudioStreamCategory.Communications], categories);
        Assert.Throws<InvalidDataException>(() => WasapiPacketCapture.ApplyMicrophoneMode(
            true, MicrophoneCaptureMode.WindowsCommunications, Record));
        Assert.Single(categories); // Never apply a microphone processing category to Them.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsupportedWindowsCommunicationsFailsWithoutSilentlyUsingDefault(bool comError)
    {
        var failure = Assert.Throws<RecordingException>(() => WasapiPacketCapture.ApplyMicrophoneMode(
            false, MicrophoneCaptureMode.WindowsCommunications, _ =>
            {
                if (comError) throw new COMException("private-driver-data");
                throw new InvalidOperationException("private-driver-data");
            }));
        Assert.Contains("Windows Communications", failure.Message);
        Assert.Contains("Windows Default", failure.Message);
        Assert.DoesNotContain("private-driver-data", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureTransitionsOccurAfterBothSourcesStartAndFinalizationCompletes(bool finalizationFails)
    {
        var me = new FakeSource(); var them = new FakeSource();
        bool finalized = false;
        using var capture = new CaptureCoordinator(() => new(me, them), stopped: _ =>
        {
            finalized = true;
            if (finalizationFails) throw new IOException("Fixture encoder failure");
        });
        var transitions = new List<CaptureStatus>();
        capture.StateChanged += status =>
        {
            if (status.IsActive) Assert.True(me.Running && them.Running); else Assert.True(finalized);
            transitions.Add(status);
        };
        capture.Start(); capture.Stop();
        Assert.Equal(2, transitions.Count);
        Assert.True(transitions[0].IsActive); Assert.Null(transitions[0].Error);
        Assert.False(transitions[1].IsActive);
        Assert.Equal(finalizationFails, transitions[1].Error is not null);
    }

    [Fact]
    public void FailedStartDoesNotPublishStartedAndNotificationFailureCannotStopCapture()
    {
        using var failing = new CaptureCoordinator(() => new(new FakeSource(), new FakeSource { FailStart = true }));
        int starts = 0;
        failing.StateChanged += status => { if (status.IsActive) starts++; };
        Assert.Throws<InvalidOperationException>(() => failing.Start()); Assert.Equal(0, starts);
        using var capture = new CaptureCoordinator(() => new(new FakeSource(), new FakeSource()));
        capture.StateChanged += _ => throw new IOException("Fixture notifier failure");
        capture.Start(); Assert.True(capture.Status.IsActive); Assert.Null(capture.Status.Error);
        capture.Stop(); Assert.Null(capture.Status.Error);
    }

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
    public void MeMuteRequiresActiveCaptureAndResetsOnStopAndNextStart()
    {
        using var capture = new CaptureCoordinator(() => new(new FakeSource(), new FakeSource()));
        Assert.Throws<InvalidOperationException>(() => capture.SetMeMuted(true));
        capture.Start();
        Assert.False(capture.Status.MeMuted);
        capture.SetMeMuted(true);
        Assert.True(capture.Status.MeMuted);
        capture.SetMeMuted(true);
        capture.SetMeMuted(false);
        Assert.False(capture.Status.MeMuted);
        capture.SetMeMuted(true);
        capture.Stop();
        Assert.False(capture.Status.MeMuted);
        Assert.Throws<InvalidOperationException>(() => capture.SetMeMuted(false));
        capture.Start();
        Assert.False(capture.Status.MeMuted);
        capture.Stop();
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
    public async Task DeviceDisconnectionKeepsOtherSourceAndContinuesCapture(bool microphone)
    {
        var me = new FakeSource();
        var them = new FakeSource();
        var notices = new List<CaptureStatus>();
        using var capture = new CaptureCoordinator(() => new(me, them));
        int failures = 0;
        capture.Failed += _ => Interlocked.Increment(ref failures);
        capture.StateChanged += notices.Add;
        capture.Start();
        (microphone ? me : them).Fail(new COMException("private meeting content", unchecked((int)0x88890004)));
        await WaitFor(() => (microphone ? capture.Status.MeUnavailable : capture.Status.ThemUnavailable) is not null);
        Assert.True(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
        Assert.True((microphone ? them : me).Running);
        Assert.Equal(0, failures);
        Assert.DoesNotContain(notices, n => (n.MeUnavailable ?? "") .Contains("private") || (n.ThemUnavailable ?? "").Contains("private"));
        capture.Stop();
        Assert.True(me.Disposed && them.Disposed);
        Assert.False(me.DisposedInsideCallback || them.DisposedInsideCallback);
    }

    [Fact]
    public async Task SwitchingDefaultReplacesOnlyThemIgnoresUnrelatedDevicesAndRetiredCallbacks()
    {
        var me = new FakeSource();
        var originalThem = new FakeSource();
        var replacementThem = new FakeSource();
        var fixedMe = new AudioDevice("me-id", "Me", CaptureChannel.Me, false);
        var initialThem = new AudioDevice("them-a", "Them", CaptureChannel.Them, true);
        Action<EndpointChange>? notify = null;
        var observer = new FakeObserver();
        int replaced = 0;
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me, info: fixedMe), new CaptureSource(originalThem, info: initialThem))
        {
            MeDevice = fixedMe, ThemDevice = initialThem,
            Reopen = channel =>
            {
                Assert.Equal(CaptureChannel.Them, channel);
                Interlocked.Increment(ref replaced);
                return new(replacementThem, info: new("them-b", "New playback", channel, true));
            },
            Observe = callback => { notify = callback; return observer; }
        });
        capture.Start();
        Assert.NotNull(notify);
        notify!(new(EndpointChangeKind.Removed, "unrelated"));
        notify(new(EndpointChangeKind.DefaultChanged, "unrelated", CaptureChannel.Me));
        await Task.Delay(50);
        Assert.Equal(0, replaced);
        Assert.True(me.Running && originalThem.Running);
        notify(new(EndpointChangeKind.DefaultChanged, "them-b", CaptureChannel.Them));
        await WaitFor(() => replacementThem.Running && originalThem.Disposed);
        Assert.Equal(1, replaced);
        Assert.True(me.Running);
        Assert.Null(capture.Status.Error);
        originalThem.Fail(new IOException("Old callback after replacement."));
        await Task.Delay(50);
        Assert.True(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
        capture.Stop();
        Assert.True(replacementThem.Disposed);
        Assert.True(observer.Disposed);
    }

    [Fact]
    public async Task PinnedEndpointLossWaitsForSameIdAndNeverFallsBackToNewDefault()
    {
        var me = new FakeSource();
        var originalThem = new FakeSource();
        var replacementThem = new FakeSource();
        Action<EndpointChange>? notify = null;
        int openings = 0;
        var pinned = new AudioDevice("fixed", "Playback", CaptureChannel.Them, false);
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me, info: new("me", "Microphone", CaptureChannel.Me, false)),
            new CaptureSource(originalThem, info: pinned))
        {
            MeDevice = new("me", "Microphone", CaptureChannel.Me, false),
            ThemDevice = pinned,
            Reopen = channel =>
            {
                Assert.Equal(CaptureChannel.Them, channel);
                Interlocked.Increment(ref openings);
                if (openings == 1) throw new IOException("Fixed endpoint is still offline");
                return new(replacementThem, info: pinned);
            },
            Observe = callback => { notify = callback; return new FakeObserver(); }
        });
        capture.Start();
        originalThem.Fail(new COMException("device gone", unchecked((int)0x88890004)));
        await WaitFor(() => capture.Status.ThemUnavailable is not null);
        Assert.True(me.Running && capture.Status.IsActive);
        notify!(new(EndpointChangeKind.DefaultChanged, "new-default", CaptureChannel.Them));
        notify(new(EndpointChangeKind.Added, "unrelated"));
        await Task.Delay(50);
        Assert.Equal(0, openings);
        notify(new(EndpointChangeKind.Added, "fixed"));
        await WaitFor(() => Volatile.Read(ref openings) == 1);
        Assert.NotNull(capture.Status.ThemUnavailable);
        capture.RetryDisconnected();
        await WaitFor(() => replacementThem.Running);
        Assert.Null(capture.Status.ThemUnavailable);
        Assert.Null(capture.Status.Error);
        capture.Stop();
    }

    [Fact]
    public async Task NewDefaultStartFailureKeepsExistingHealthyCaptureUntilRetrySucceeds()
    {
        var me = new FakeSource();
        var healthyThem = new FakeSource();
        var failedCandidate = new FakeSource { FailStart = true };
        var recoveredThem = new FakeSource();
        Action<EndpointChange>? notify = null;
        int attempts = 0;
        var defaultMe = new AudioDevice("me", "Mic", CaptureChannel.Me, true);
        var defaultThem = new AudioDevice("original", "Headphones", CaptureChannel.Them, true);
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me, info: defaultMe), new CaptureSource(healthyThem, info: defaultThem))
        {
            MeDevice = defaultMe, ThemDevice = defaultThem,
            Reopen = channel =>
            {
                Assert.Equal(CaptureChannel.Them, channel);
                return new(Interlocked.Increment(ref attempts) == 1 ? failedCandidate : recoveredThem,
                    info: new("new", "Speakers", channel, true));
            },
            Observe = callback => { notify = callback; return new FakeObserver(); }
        });
        capture.Start();
        notify!(new(EndpointChangeKind.DefaultChanged, "new", CaptureChannel.Them));
        await WaitFor(() => Volatile.Read(ref attempts) == 1);
        Assert.True(healthyThem.Running);
        Assert.False(healthyThem.Disposed);
        Assert.True(me.Running);
        Assert.True(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
        Assert.Contains("could not switch", capture.Status.ThemUnavailable!);
        Assert.True(failedCandidate.Disposed);

        capture.RetryDisconnected();
        await WaitFor(() => recoveredThem.Running && healthyThem.Disposed);
        Assert.Equal(2, attempts);
        Assert.Null(capture.Status.ThemUnavailable);
        Assert.Null(capture.Status.Error);
        capture.Stop();
        Assert.True(recoveredThem.Disposed);
    }

    [Fact]
    public async Task CandidateThatStopsBeforeActivationCannotReplaceHealthyDefault()
    {
        var me = new FakeSource();
        var old = new FakeSource();
        var candidate = new FakeSource { DisconnectOnStart = true };
        Action<EndpointChange>? notify = null;
        int reopenings = 0;
        var initial = new AudioDevice("old", "Working", CaptureChannel.Them, true);
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me), new CaptureSource(old, info: initial))
        {
            ThemDevice = initial,
            Reopen = channel =>
            {
                Interlocked.Increment(ref reopenings);
                return new(candidate, info: new("new", "Disappeared", channel, true));
            },
            Observe = callback => { notify = callback; return new FakeObserver(); }
        });
        capture.Start();
        notify!(new(EndpointChangeKind.DefaultChanged, "new", CaptureChannel.Them));
        await WaitFor(() => candidate.Disposed);
        Assert.Equal(1, reopenings);
        Assert.True(old.Running);
        Assert.False(old.Disposed);
        Assert.True(me.Running);
        Assert.True(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
        capture.Stop();
    }

    [Fact]
    public async Task RepeatedInvalidationDoesNotSpinOnEndpointReopenAndStopRemainsResponsive()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        int attempts = 0;
        var playback = new AudioDevice("playback", "Headphones", CaptureChannel.Them, true);
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me), new CaptureSource(them, info: playback))
        {
            ThemDevice = playback,
            Reopen = channel =>
            {
                Interlocked.Increment(ref attempts);
                return new(new FakeSource { FailStart = true }, info: playback);
            }
        });
        capture.Start();
        them.Fail(new COMException("device lost", unchecked((int)0x88890004)));
        await WaitFor(() => capture.Status.ThemUnavailable is not null);
        await Task.Delay(100);
        Assert.Equal(0, attempts); // No immediate callback-driven reopen loop.
        capture.RetryDisconnected();
        Assert.Equal(1, attempts);
        await Task.Delay(100);
        Assert.Equal(1, attempts);
        Assert.True(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
        var stopwatch = Stopwatch.StartNew();
        capture.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(me.Disposed && them.Disposed);
    }

    [Fact]
    public async Task DefaultThemDisconnectKeepsMeAndReconnectsToNewDefault()
    {
        var me = new FakeSource();
        var oldThem = new FakeSource();
        var newThem = new FakeSource();
        Action<EndpointChange>? notify = null;
        bool newDefaultAvailable = false;
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me, info: new("me", "Microphone", CaptureChannel.Me, true)),
            new CaptureSource(oldThem, info: new("a", "Bluetooth", CaptureChannel.Them, true)))
        {
            MeDevice = new("me", "Microphone", CaptureChannel.Me, true),
            ThemDevice = new("a", "Bluetooth", CaptureChannel.Them, true),
            Reopen = channel =>
            {
                Assert.Equal(CaptureChannel.Them, channel);
                if (!newDefaultAvailable) throw new IOException("No playback default yet");
                return new(newThem, info: new("b", "Speakers", CaptureChannel.Them, true));
            },
            Observe = callback => { notify = callback; return new FakeObserver(); }
        });
        capture.Start();
        oldThem.Fail(new COMException("private device error", unchecked((int)0x88890004)));
        await WaitFor(() => capture.Status.ThemUnavailable is not null);
        Assert.True(me.Running && capture.Status.IsActive);
        Assert.True(oldThem.Disposed);
        Assert.Null(capture.Status.Error);
        newDefaultAvailable = true;
        notify!(new(EndpointChangeKind.DefaultChanged, "b", CaptureChannel.Them));
        await WaitFor(() => newThem.Running && capture.Status.ThemUnavailable is null);
        Assert.True(me.Running);
        capture.Stop();
        Assert.True(newThem.Disposed);
    }

    [Fact]
    public async Task StaleEndpointNotificationAfterStopNeverRestartsCapture()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        Action<EndpointChange>? notify = null;
        int reopenings = 0;
        var observer = new FakeObserver();
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me, info: new("me", "Microphone", CaptureChannel.Me, true)),
            new CaptureSource(them, info: new("old", "Speakers", CaptureChannel.Them, true)))
        {
            MeDevice = new("me", "Microphone", CaptureChannel.Me, true),
            ThemDevice = new("old", "Speakers", CaptureChannel.Them, true),
            Reopen = channel =>
            {
                Interlocked.Increment(ref reopenings);
                return new(new FakeSource(), info: new("new", "Other", channel, true));
            },
            Observe = callback => { notify = callback; return observer; }
        });
        capture.Start();
        capture.Stop();
        Assert.True(observer.Disposed);
        notify!(new(EndpointChangeKind.DefaultChanged, "new", CaptureChannel.Them));
        await Task.Delay(50);
        Assert.Equal(0, reopenings);
        Assert.True(me.Disposed && them.Disposed);
        Assert.False(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
    }

    [Fact]
    public async Task EncoderAbortIsFatalEvenWhenTheOtherDeviceIsDisconnected()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Failed += diagnostic => failed.TrySetResult(diagnostic);
        capture.Start();
        them.Fail(new COMException("private error", unchecked((int)0x88890026)));
        await WaitFor(() => capture.Status.ThemUnavailable is not null);
        capture.Abort(CaptureChannel.Me, new RecordingException("Encoder failed; original audio retained."));
        string diagnostic = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Encoder failed", diagnostic);
        Assert.False(capture.Status.IsActive);
        Assert.True(me.Disposed && them.Disposed);
        Assert.NotNull(capture.Status.Error);
    }

    [Fact]
    public async Task FatalConsumerIOExceptionStillStopsBothTracksAndFailsSession()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them), (_, _, _, _) => throw new IOException("private IO path"));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Failed += message => failed.TrySetResult(message);
        capture.Start();
        me.Emit();
        string message = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(capture.Status.IsActive);
        Assert.True(me.Disposed && them.Disposed);
        Assert.NotNull(capture.Status.Error);
        Assert.DoesNotContain("private IO path", message);
    }

    [Fact]
    public async Task FatalDataCallbackRemainsFatalAfterItsEndpointIsReplaced()
    {
        var me = new FakeSource();
        var oldThem = new FakeSource();
        var newThem = new FakeSource();
        var initialThem = new AudioDevice("old", "Headphones", CaptureChannel.Them, true);
        Action<EndpointChange>? notify = null;
        using var switching = new ManualResetEventSlim();
        using var continueSwitch = new ManualResetEventSlim();
        using var capture = new CaptureCoordinator(() => new CaptureSources(
            new CaptureSource(me), new CaptureSource(oldThem, info: initialThem))
        {
            ThemDevice = initialThem,
            Reopen = channel =>
            {
                if (channel != CaptureChannel.Them) throw new InvalidOperationException("Unexpected channel.");
                switching.Set();
                if (!continueSwitch.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Switch was not released by the test.");
                return new(newThem, info: new("new", "Speakers", channel, true));
            },
            Observe = callback => { notify = callback; return new FakeObserver(); }
        }, (channel, _, _, _) =>
        {
            if (channel == CaptureChannel.Them)
                throw new RecordingException("Encoder cannot keep up; bounded audio queue exhausted. Partial originals retained.");
        });
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Failed += message => failed.TrySetResult(message);
        capture.Start();

        notify!(new(EndpointChangeKind.DefaultChanged, "new", CaptureChannel.Them));
        try
        {
            Assert.True(switching.Wait(TimeSpan.FromSeconds(5)), "Endpoint replacement did not acquire the lifecycle lock.");
            // The old endpoint is still current. Consume rejects its packet while the switch
            // holds the lifecycle lock, so fatal cleanup is queued until after replacement.
            oldThem.Emit();
        }
        finally { continueSwitch.Set(); }

        string diagnostic = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Encoder cannot keep up", diagnostic);
        Assert.False(capture.Status.IsActive);
        Assert.Contains("Encoder cannot keep up", capture.Status.Error);
        Assert.True(me.Disposed && oldThem.Disposed && newThem.Disposed);
    }

    private sealed class FakeObserver : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
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
    public void RepeatedTimingProblemsKeepAggregatedCountersWithoutWarnings()
    {
        var me = new FakeSource();
        var them = new FakeSource();
        using var capture = new CaptureCoordinator(() => new(me, them));
        capture.Start();
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 1, 480, "one frame"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 2, 480, "two frames"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 960, 480, "larger than packet"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Overlap, 970, 480, "still larger"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.InvalidTimestamp, 0, 480, "QPC invalid"));
        capture.ReportTiming(CaptureChannel.Them, new(AudioTimingIssueKind.Discontinuity, 0, 480, "WASAPI gap"));
        Assert.True(capture.Status.IsActive);
        Assert.Equal(5, capture.Status.ClockAdjustments);
        Assert.Equal(1, capture.Status.Discontinuities);
        Assert.Equal(1, capture.Status.ThemTiming.InvalidTimestamps);
        Assert.Equal(970, capture.Status.ThemTiming.LargestShiftFrames);
        Assert.Equal("WASAPI gap", capture.Status.ThemTiming.LastIssue);
        capture.Stop();
        Assert.Null(capture.Status.Error);
    }

    [Fact]
    public async Task ControllerDoesNotPublishTimingWarningsAndRetainsTimingCounters()
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
            Assert.Empty(notices);
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
        public bool DisconnectOnStart { get; init; }
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
            if (DisconnectOnStart)
                RecordingStopped?.Invoke(this, new StoppedEventArgs(
                    new COMException("private disappeared endpoint", unchecked((int)0x88890004))));
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
