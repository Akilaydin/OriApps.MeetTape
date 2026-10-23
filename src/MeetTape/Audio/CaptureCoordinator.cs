using OriApps.MeetTape.Diagnostics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OriApps.MeetTape.Audio;

internal enum AudioTimingIssueKind { Overlap = 1, InvalidTimestamp = 2, Discontinuity = 4 }
internal sealed record AudioTimingIssue(AudioTimingIssueKind Kind, long ShiftFrames, int PacketFrames, string Detail);

public sealed record AudioTimingStats(int Corrections = 0, int InvalidTimestamps = 0,
    int Discontinuities = 0, long LargestShiftFrames = 0, string? LastIssue = null);

public sealed record CaptureStatus(bool IsActive, TimeSpan Duration, long MeBytes, long ThemBytes, string? Error)
{
    public string? RecordingDirectory { get; init; }
    public long AudioFrames { get; init; }
    public int Discontinuities { get; init; }
    public int ClockAdjustments { get; init; }
    public AudioTimingStats MeTiming { get; init; } = new();
    public AudioTimingStats ThemTiming { get; init; } = new();
    public double MeLevel { get; init; }
    public double ThemLevel { get; init; }
    public bool MeMuted { get; init; }
    public string? MeUnavailable { get; init; }
    public string? ThemUnavailable { get; init; }
}

public sealed class CaptureCoordinator(
    Func<CaptureSources> openSources,
    Action<CaptureChannel, WaveInEventArgs, WaveFormat, long>? consume = null,
    Action<CaptureSources, long>? started = null,
    Action<long>? stopped = null,
    Func<long>? levelTimestamp = null) : IDisposable
{
    private readonly object control = new();
    private readonly object timingGate = new();
    private readonly object meMuteGate = new();
    private readonly Func<long> levelClock = levelTimestamp ?? Stopwatch.GetTimestamp;
    private float mePeak, themPeak;
    private long meLevelAt, themLevelAt;
    private AudioTimingStats meTiming = new();
    private AudioTimingStats themTiming = new();
    private CaptureSources? active;
    private long startedAt;
    private long stoppedAt;
    private long meBytes;
    private long themBytes;
    private string? lastError;
    private string? meUnavailable, themUnavailable;
    private string? meEndpointId, themEndpointId;
    private int meEpoch, themEpoch;
    private bool meSwitchPending, themSwitchPending;
    private IDisposable? endpointObserver;
    private Timer? reconnectTimer;
    private bool reconnectScheduled;
    private volatile bool stopping;
    private bool meMuted;
    private bool disposed;

    public event Action<string>? Failed;
    public event Action<CaptureStatus>? Finished;
    internal event Action<CaptureStatus>? StateChanged;
    public string? RecordingDirectory { get; internal set; }
    public long AudioFrames { get; internal set; }
    public int Discontinuities { get; internal set; }
    public int ClockAdjustments { get; internal set; }
    internal bool IsMeMuted => Volatile.Read(ref meMuted);

    public CaptureStatus Status
    {
        get
        {
            lock (control)
            {
                long end = active is null ? stoppedAt : Stopwatch.GetTimestamp();
                long levelNow = levelClock();
                lock (timingGate)
                    return new(active is not null, startedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startedAt, end),
                        Interlocked.Read(ref meBytes), Interlocked.Read(ref themBytes), Volatile.Read(ref lastError))
                    {
                        RecordingDirectory = RecordingDirectory, AudioFrames = AudioFrames, Discontinuities = Discontinuities,
                        ClockAdjustments = ClockAdjustments, MeTiming = meTiming, ThemTiming = themTiming,
                        MeLevel = active is null ? 0 : Decay(mePeak, meLevelAt, levelNow),
                        ThemLevel = active is null ? 0 : Decay(themPeak, themLevelAt, levelNow),
                        MeMuted = active is not null && IsMeMuted,
                        MeUnavailable = meUnavailable, ThemUnavailable = themUnavailable
                    };
            }
        }
    }

    private static double Decay(float peak, long observedAt, long now) =>
        peak * Math.Clamp(1 - Stopwatch.GetElapsedTime(observedAt, now).TotalSeconds / 0.3, 0, 1);

    internal void ReportLevel(CaptureChannel channel, AudioPacket packet)
    {
        float peak = 0;
        if (!packet.Flags.HasFlag(AudioClientBufferFlags.Silent))
            for (int i = 0; i < packet.Frames; i++) peak = Math.Max(peak, Math.Abs(packet.Samples[i]));
        lock (timingGate)
        {
            if (stopping) return;
            if (channel == CaptureChannel.Me) { mePeak = peak; meLevelAt = levelClock(); }
            else { themPeak = peak; themLevelAt = levelClock(); }
        }
    }

    public void SetMeMuted(bool muted)
    {
        lock (control)
        {
            if (active is null || stopping) throw new InvalidOperationException("No capture is active.");
            // Serialize the switch with Me packet delivery so no unmuted packet can pass
            // the recorder after Mute has completed. Them is never held by this gate.
            lock (meMuteGate)
            {
                if (meMuted == muted) return;
                Volatile.Write(ref meMuted, muted);
                lock (timingGate) { mePeak = 0; meLevelAt = levelClock(); }
            }
            NotifyStateChanged();
        }
    }

    public void Start()
    {
        lock (control)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is not null)
                throw new InvalidOperationException("A capture is already active.");
            CaptureSources? sources = null;
            try
            {
                sources = openSources();
                startedAt = Stopwatch.GetTimestamp();
                stoppedAt = startedAt;
                Interlocked.Exchange(ref meBytes, 0);
                Interlocked.Exchange(ref themBytes, 0);
                Volatile.Write(ref lastError, null);
                stopping = false;
                meUnavailable = themUnavailable = null;
                meEndpointId = sources.MeDevice?.Id;
                themEndpointId = sources.ThemDevice?.Id;
                meEpoch = themEpoch = 0;
                meSwitchPending = themSwitchPending = false;
                lock (meMuteGate) Volatile.Write(ref meMuted, false);
                AudioFrames = 0;
                lock (timingGate)
                {
                    meTiming = new(); themTiming = new();
                    mePeak = themPeak = 0;
                    meLevelAt = themLevelAt = 0;
                    Discontinuities = 0; ClockAdjustments = 0;
                }
                started?.Invoke(sources, startedAt);
                var initialMe = sources.Current(CaptureChannel.Me)!;
                var initialThem = sources.Current(CaptureChannel.Them)!;
                Subscribe(sources, initialMe, CaptureChannel.Me, meEpoch);
                Subscribe(sources, initialThem, CaptureChannel.Them, themEpoch);
                initialMe.Activate();
                initialThem.Activate();
                Volatile.Write(ref active, sources);
                sources.Me.StartRecording();
                sources.Them.StartRecording();
                if (sources.Observe is not null)
                {
                    try { endpointObserver = sources.Observe(change => OnEndpointChanged(sources, change)); }
                    catch (Exception ex)
                    {
                        AppDiagnostics.Error("capture", "Observe endpoints", ex);
                        AppDiagnostics.Info("capture", "Device change notifications unavailable; recording continues on selected endpoints");
                    }
                }
                AppDiagnostics.Info("capture", "Me/Them capture started");
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error("capture", "Start Me/Them", ex);
                string diagnostic = ex is RecordingException ? ex.Message :
                    AppDiagnostics.UserMessage(ex, "Cannot start capture. Check microphone permissions and selected active devices.");
                Volatile.Write(ref lastError, diagnostic);
                stopping = true;
                try { endpointObserver?.Dispose(); }
                catch (Exception disposeError) { AppDiagnostics.Error("capture", "Dispose device observer", disposeError); }
                finally { endpointObserver = null; }
                reconnectTimer?.Dispose();
                reconnectTimer = null;
                reconnectScheduled = false;
                try { sources?.Dispose(); }
                catch { /* Preserve the original startup failure if cleanup also fails. */ }
                try { stopped?.Invoke(Stopwatch.GetTimestamp()); }
                catch { /* Preserve the original startup failure. */ }
                finally { Volatile.Write(ref active, null); stoppedAt = Stopwatch.GetTimestamp(); }
                NotifyFinished();
                throw new InvalidOperationException(diagnostic);
            }
        }
    }

    private void NotifyFinished()
    {
        try { Finished?.Invoke(Status); }
        catch (Exception ex)
        {
            AppDiagnostics.Error("capture", "Finalize metadata", ex);
            Volatile.Write(ref lastError, AppDiagnostics.UserMessage(ex, "Cannot finalize session metadata; originals retained. Check permissions and disk space."));
        }
        AppDiagnostics.Info("capture", "Me/Them capture finalized");
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(Status); }
        catch (Exception) { /* Status notifications cannot affect capture or saved data. */ }
    }

    public void Stop()
    {
        lock (control)
        {
            if (active is null)
                throw new InvalidOperationException("No capture is active.");
            StopCore();
        }
    }

    private void Subscribe(CaptureSources sources, CaptureSource captureSource, CaptureChannel channel, int epoch)
    {
        var source = captureSource.Audio;
        var format = source.WaveFormat;
        source.DataAvailable += (_, args) =>
        {
            if (channel == CaptureChannel.Me)
            {
                lock (meMuteGate) Receive();
            }
            else Receive();

            void Receive()
            {
                if (args.BytesRecorded == 0 || stopping || !ReferenceEquals(Volatile.Read(ref active), sources)
                    || !ReferenceEquals(sources.Current(channel)?.Audio, source)) return;
                try
                {
                    // NAudio reuses its buffer: consumers must finish before returning or copy into a bounded queue.
                    if (args is WasapiPacketEventArgs packet) packet.SourceEpoch = epoch;
                    consume?.Invoke(channel, args, format, Stopwatch.GetTimestamp());
                    if (channel == CaptureChannel.Me) Interlocked.Add(ref meBytes, args.BytesRecorded);
                    else Interlocked.Add(ref themBytes, args.BytesRecorded);
                }
                catch (Exception ex)
                {
                    // The callback already passed the active-source check. A failed enqueue may have lost audio,
                    // even if this endpoint is replaced before the queued failure handler can run.
                    QueueFailure(sources, channel, ex);
                }
            }
        };
        source.RecordingStopped += (_, args) =>
        {
            bool activated = captureSource.ObserveStop();
            if (!activated || stopping) return;
            if (args.Exception is null || IsDeviceInvalidated(args.Exception))
                QueueDeviceLoss(sources, source, channel, args.Exception);
            else
                QueueFailure(sources, channel, args.Exception, source);
        };
    }

    private static bool IsDeviceInvalidated(Exception error) => error is COMException
        && error.HResult is unchecked((int)0x88890004) or unchecked((int)0x88890026);

    private void QueueDeviceLoss(CaptureSources sources, IWaveIn source, CaptureChannel channel, Exception? error)
    {
        if (error is not null) AppDiagnostics.Error("capture", channel + " disconnected", error);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (control)
            {
                if (stopping || !ReferenceEquals(active, sources) ||
                    !ReferenceEquals(sources.Current(channel)?.Audio, source)) return;
                ReplaceChannel(sources, channel, lost: true, reopenNow: false);
            }
        });
    }

    private void OnEndpointChanged(CaptureSources sources, EndpointChange change)
    {
        // Endpoint notifications arrive on a Core Audio callback thread; all joins/opening run elsewhere.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (control)
            {
                if (stopping || !ReferenceEquals(active, sources)) return;
                if (change.Kind == EndpointChangeKind.DefaultChanged)
                {
                    if (change.Channel is { } channel && IsDefaultSelection(sources, channel))
                    {
                        bool changed = !string.Equals(EndpointId(channel), change.Id, StringComparison.OrdinalIgnoreCase);
                        SetSwitchPending(channel, changed);
                        if (changed || sources.Current(channel) is null)
                            ReplaceChannel(sources, channel, lost: sources.Current(channel) is null);
                        else
                        {
                            SetUnavailable(channel, null);
                            NotifyStateChanged();
                            StopReconnectIfReady(sources);
                        }
                    }
                    return;
                }
                foreach (CaptureChannel channel in new[] { CaptureChannel.Me, CaptureChannel.Them })
                {
                    var current = sources.Current(channel);
                    bool selected = string.Equals(EndpointId(channel), change.Id, StringComparison.OrdinalIgnoreCase);
                    if (current is not null)
                    {
                        if (selected && change.Kind == EndpointChangeKind.Removed ||
                            selected && change.Kind == EndpointChangeKind.StateChanged && !change.Active)
                            ReplaceChannel(sources, channel, lost: true, reopenNow: false);
                    }
                    else if ((change.Kind == EndpointChangeKind.Added
                                 || change.Kind == EndpointChangeKind.StateChanged && change.Active)
                             && (IsDefaultSelection(sources, channel) || selected))
                        ReplaceChannel(sources, channel, lost: true);
                }
            }
        });
    }

    private static bool IsDefaultSelection(CaptureSources sources, CaptureChannel channel) =>
        (channel == CaptureChannel.Me ? sources.MeDevice : sources.ThemDevice)?.IsDefault == true;

    private string? EndpointId(CaptureChannel channel) => channel == CaptureChannel.Me ? meEndpointId : themEndpointId;

    private void ReplaceChannel(CaptureSources sources, CaptureChannel channel, bool lost, bool reopenNow = true)
    {
        var old = sources.Current(channel);
        CaptureSource? replacement = null;
        if (reopenNow)
        {
            try { replacement = sources.Reopen?.Invoke(channel); }
            catch (Exception error) { AppDiagnostics.Error("capture", channel + " endpoint unavailable", error); }
        }
        if (!lost && replacement is null)
        {
            if (old is not null) WarnPendingSwitch(channel);
            ScheduleReconnect(sources); // Keep a healthy source while the new default is not ready.
            return;
        }
        if (!lost && old?.Info?.Id is { } currentId
            && string.Equals(replacement?.Info?.Id, currentId, StringComparison.OrdinalIgnoreCase))
        {
            DisposeCandidate(replacement, channel);
            SetSwitchPending(channel, false);
            SetUnavailable(channel, null);
            NotifyStateChanged();
            StopReconnectIfReady(sources);
            return;
        }

        int epoch = channel == CaptureChannel.Me ? meEpoch + 1 : themEpoch + 1;
        if (replacement is not null)
        {
            try
            {
                // Pre-start in quarantine. Its callbacks are ignored until the old source has joined.
                Subscribe(sources, replacement, channel, epoch);
                replacement.Audio.StartRecording();
            }
            catch (Exception error)
            {
                AppDiagnostics.Error("capture", channel + " endpoint restart", error);
                try { replacement.Dispose(); }
                catch (Exception disposeError) { AppDiagnostics.Error("capture", "Dispose failed endpoint", disposeError); }
                replacement = null;
                if (!lost)
                {
                    WarnPendingSwitch(channel);
                    ScheduleReconnect(sources);
                    return;
                }
            }
        }
        if (replacement is not null && !replacement.Activate())
        {
            AppDiagnostics.Info("capture", channel + " candidate disconnected before endpoint switch");
            DisposeCandidate(replacement, channel);
            replacement = null;
            if (!lost)
            {
                WarnPendingSwitch(channel);
                ScheduleReconnect(sources);
                return;
            }
        }

        if (old is not null)
        {
            sources.Replace(channel, null); // Obsolete callbacks are ignored before joining this worker.
            if (lost) SetUnavailable(channel, $"{channel} audio device unavailable. Missing audio is recorded as silence.");
            ReportTiming(channel, new(AudioTimingIssueKind.Discontinuity, 0, 0,
                $"Audio endpoint interrupted: {channel} changed or disconnected; part of this track may contain silence."));
            AppDiagnostics.Info("capture", channel + " endpoint unavailable; continuing other track");
            if (lost) NotifyStateChanged();
            try
            {
                try { old.Audio.StopRecording(); }
                finally { old.Dispose(); }
            }
            catch (Exception error)
            {
                AppDiagnostics.Error("capture", channel + " endpoint cleanup", error);
                try { replacement?.Dispose(); }
                catch (Exception disposeError) { AppDiagnostics.Error("capture", "Dispose replacement", disposeError); }
                StopOnFailure($"{channel} capture device could not stop safely; original audio retained.");
                return;
            }
        }
        if (replacement is null)
        {
            ScheduleReconnect(sources);
            return;
        }
        sources.Replace(channel, replacement);
        if (channel == CaptureChannel.Me) { meEndpointId = replacement.Info?.Id; meEpoch = epoch; }
        else { themEndpointId = replacement.Info?.Id; themEpoch = epoch; }
        SetSwitchPending(channel, false);
        SetUnavailable(channel, null);
        AppDiagnostics.Info("capture", channel + " endpoint connected");
        NotifyStateChanged();
        StopReconnectIfReady(sources);
    }

    private void ScheduleReconnect(CaptureSources sources)
    {
        if (stopping || sources.Reopen is null) return;
        reconnectTimer ??= new Timer(_ => RetryDisconnected(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (reconnectScheduled) return;
        reconnectTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        reconnectScheduled = true;
    }

    private bool SwitchPending(CaptureChannel channel) => channel == CaptureChannel.Me ? meSwitchPending : themSwitchPending;
    private void SetSwitchPending(CaptureChannel channel, bool value)
    {
        if (channel == CaptureChannel.Me) meSwitchPending = value;
        else themSwitchPending = value;
    }

    private void StopReconnectIfReady(CaptureSources sources)
    {
        if (sources.Current(CaptureChannel.Me) is null || sources.Current(CaptureChannel.Them) is null
            || meSwitchPending || themSwitchPending) return;
        reconnectTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        reconnectScheduled = false;
    }

    internal void RetryDisconnected()
    {
        lock (control)
        {
            var sources = active;
            if (stopping || sources?.Reopen is null) return;
            foreach (var channel in new[] { CaptureChannel.Me, CaptureChannel.Them })
                if (sources.Current(channel) is null || SwitchPending(channel))
                    ReplaceChannel(sources, channel, lost: sources.Current(channel) is null);
        }
    }

    private void SetUnavailable(CaptureChannel channel, string? message)
    {
        if (channel == CaptureChannel.Me) meUnavailable = message;
        else themUnavailable = message;
    }

    private void WarnPendingSwitch(CaptureChannel channel)
    {
        bool firstWarning = channel == CaptureChannel.Me ? meUnavailable is null : themUnavailable is null;
        if (firstWarning)
            ReportTiming(channel, new(AudioTimingIssueKind.Discontinuity, 0, 0,
                $"Audio endpoint interrupted: {channel} could not switch to the new Windows default; some audio may be missing."));
        SetUnavailable(channel, $"{channel} could not switch to the new Windows default. Previous device remains active; audio from the new default may be missing.");
        NotifyStateChanged();
    }

    private static void DisposeCandidate(CaptureSource? source, CaptureChannel channel)
    {
        try { source?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppDiagnostics.Error("capture", channel + " replacement cleanup", error);
        }
    }

    private void StopOnFailure(string diagnostic)
    {
        if (lastError is not null) return;
        lastError = diagnostic;
        StopCore();
        Failed?.Invoke(diagnostic);
    }

    private void QueueFailure(CaptureSources sources, CaptureChannel channel, Exception? error, IWaveIn? source = null)
    {
        if (error is not null) AppDiagnostics.Error("capture", channel + " stopped", error);
        string diagnostic = error is RecordingException ? $"{channel}: {error.Message}" :
            error is null ? $"{channel} capture stopped unexpectedly. Check the device connection and permissions." :
            AppDiagnostics.UserMessage(error, $"{channel} capture stopped unexpectedly. Check the device connection and permissions.");
        // Never dispose/join a WASAPI thread from its own data/stopped callback.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (control)
            {
                if (stopping || !ReferenceEquals(active, sources) || lastError is not null
                    || source is not null && !ReferenceEquals(sources.Current(channel)?.Audio, source)) return;
                StopOnFailure(diagnostic);
            }
        });
    }

    public void Abort(CaptureChannel channel, RecordingException error)
    {
        var sources = Volatile.Read(ref active);
        if (sources is not null)
            QueueFailure(sources, channel, error);
    }

    internal void ReportTiming(CaptureChannel channel, AudioTimingIssue issue)
    {
        lock (timingGate)
        {
            AudioTimingStats previous = channel == CaptureChannel.Me ? meTiming : themTiming;
            bool discontinuity = issue.Kind == AudioTimingIssueKind.Discontinuity;
            var updated = previous with
            {
                Corrections = previous.Corrections + (discontinuity ? 0 : 1),
                InvalidTimestamps = previous.InvalidTimestamps + (issue.Kind == AudioTimingIssueKind.InvalidTimestamp ? 1 : 0),
                Discontinuities = previous.Discontinuities + (discontinuity ? 1 : 0),
                LargestShiftFrames = Math.Max(previous.LargestShiftFrames, issue.ShiftFrames),
                // Preserve the more important device-loss warning if later packets have clock anomalies.
                LastIssue = previous.LastIssue?.StartsWith("Audio endpoint interrupted:", StringComparison.Ordinal) == true
                    ? previous.LastIssue : issue.Detail
            };
            if (channel == CaptureChannel.Me)
                meTiming = updated;
            else
                themTiming = updated;
            Discontinuities = meTiming.Discontinuities + themTiming.Discontinuities;
            ClockAdjustments = meTiming.Corrections + themTiming.Corrections;
        }
    }

    private void StopCore()
    {
        var sources = active!;
        long endedAt = Stopwatch.GetTimestamp();
        stopping = true;
        try { endpointObserver?.Dispose(); }
        catch (Exception ex) { AppDiagnostics.Error("capture", "Stop device monitor", ex); }
        finally { endpointObserver = null; }
        reconnectTimer?.Dispose();
        reconnectTimer = null;
        reconnectScheduled = false;
        try
        {
            try { sources.Current(CaptureChannel.Me)?.Audio.StopRecording(); }
            finally { sources.Current(CaptureChannel.Them)?.Audio.StopRecording(); }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("capture", "Stop devices", ex);
            Interlocked.CompareExchange(ref lastError, AppDiagnostics.UserMessage(ex, "Capture stop failed. Check device connection."), null);
        }
        finally
        {
            try
            {
                try { sources.Dispose(); }
                finally
                {
                    try { stopped?.Invoke(endedAt); }
                    finally { stoppedAt = endedAt; Volatile.Write(ref active, null); }
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error("capture", "Cleanup/finalize", ex);
                Interlocked.CompareExchange(ref lastError, AppDiagnostics.UserMessage(ex, "Capture cleanup failed. Check device connection."), null);
            }
        }
        NotifyFinished();
    }

    public void Dispose()
    {
        lock (control)
        {
            if (disposed)
                return;
            disposed = true;
            if (active is not null)
                StopCore();
        }
    }
}
