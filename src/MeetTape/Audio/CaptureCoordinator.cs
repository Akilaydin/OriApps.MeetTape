using OriApps.MeetTape.Diagnostics;
using System.Diagnostics;
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
    private readonly Func<long> levelClock = levelTimestamp ?? Stopwatch.GetTimestamp;
    private float mePeak, themPeak;
    private long meLevelAt, themLevelAt;
    private AudioTimingStats meTiming = new();
    private AudioTimingStats themTiming = new();
    private int notifiedMe;
    private int notifiedThem;
    private CaptureSources? active;
    private long startedAt;
    private long stoppedAt;
    private long meBytes;
    private long themBytes;
    private string? lastError;
    private volatile bool stopping;
    private bool disposed;

    public event Action<string>? Failed;
    internal event Action<string>? Warning;
    public event Action<CaptureStatus>? Finished;
    internal event Action<CaptureStatus>? StateChanged;
    public string? RecordingDirectory { get; internal set; }
    public long AudioFrames { get; internal set; }
    public int Discontinuities { get; internal set; }
    public int ClockAdjustments { get; internal set; }

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
                        ThemLevel = active is null ? 0 : Decay(themPeak, themLevelAt, levelNow)
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
                AudioFrames = 0;
                lock (timingGate)
                {
                    meTiming = new(); themTiming = new();
                    mePeak = themPeak = 0;
                    meLevelAt = themLevelAt = 0;
                    notifiedMe = 0; notifiedThem = 0;
                    Discontinuities = 0; ClockAdjustments = 0;
                }
                started?.Invoke(sources, startedAt);
                Subscribe(sources, sources.Me, CaptureChannel.Me);
                Subscribe(sources, sources.Them, CaptureChannel.Them);
                Volatile.Write(ref active, sources);
                sources.Me.StartRecording();
                sources.Them.StartRecording();
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

    private void Subscribe(CaptureSources sources, IWaveIn source, CaptureChannel channel)
    {
        var format = source.WaveFormat;
        source.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded == 0)
                return;
            try
            {
                // NAudio reuses its buffer: consumers must finish before returning or copy into a bounded queue.
                consume?.Invoke(channel, args, format, Stopwatch.GetTimestamp());
                if (channel == CaptureChannel.Me)
                    Interlocked.Add(ref meBytes, args.BytesRecorded);
                else
                    Interlocked.Add(ref themBytes, args.BytesRecorded);
            }
            catch (Exception ex) { QueueFailure(sources, channel, ex); }
        };
        source.RecordingStopped += (_, args) =>
        {
            if (!stopping)
                QueueFailure(sources, channel, args.Exception);
        };
    }

    private void QueueFailure(CaptureSources sources, CaptureChannel channel, Exception? error)
    {
        if (error is not null) AppDiagnostics.Error("capture", channel + " stopped", error);
        string diagnostic = error is RecordingException ? $"{channel}: {error.Message}" :
            error is null ? $"{channel} capture stopped unexpectedly. Check the device connection and permissions." :
            AppDiagnostics.UserMessage(error, $"{channel} capture stopped unexpectedly. Check the device connection and permissions.");
        if (Interlocked.CompareExchange(ref lastError, diagnostic, null) is not null)
            return;
        // Never dispose/join a WASAPI thread from its own data/stopped callback.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (control)
            {
                if (!ReferenceEquals(active, sources))
                    return;
                StopCore();
                Failed?.Invoke(diagnostic);
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
        bool notify;
        bool significantIncrease;
        lock (timingGate)
        {
            AudioTimingStats previous = channel == CaptureChannel.Me ? meTiming : themTiming;
            bool discontinuity = issue.Kind == AudioTimingIssueKind.Discontinuity;
            // A correction exceeding the packet's own length is worth another warning, even if
            // smaller corrections of this type were already reported. No fixed time threshold.
            significantIncrease = issue.Kind == AudioTimingIssueKind.Overlap
                && previous.LargestShiftFrames < issue.PacketFrames && issue.ShiftFrames >= issue.PacketFrames;
            var updated = previous with
            {
                Corrections = previous.Corrections + (discontinuity ? 0 : 1),
                InvalidTimestamps = previous.InvalidTimestamps + (issue.Kind == AudioTimingIssueKind.InvalidTimestamp ? 1 : 0),
                Discontinuities = previous.Discontinuities + (discontinuity ? 1 : 0),
                LargestShiftFrames = Math.Max(previous.LargestShiftFrames, issue.ShiftFrames),
                LastIssue = issue.Detail
            };
            if (channel == CaptureChannel.Me)
            {
                meTiming = updated;
                notify = (notifiedMe & (int)issue.Kind) == 0;
                notifiedMe |= (int)issue.Kind;
            }
            else
            {
                themTiming = updated;
                notify = (notifiedThem & (int)issue.Kind) == 0;
                notifiedThem |= (int)issue.Kind;
            }
            Discontinuities = meTiming.Discontinuities + themTiming.Discontinuities;
            ClockAdjustments = meTiming.Corrections + themTiming.Corrections;
        }
        // Report the first incident of each kind per track, and keep counting later ones in status.
        // A slow/broken console subscriber must never abort audio encoding.
        if ((notify || significantIncrease) && !stopping)
        {
            string cause = issue.Kind switch
            {
                AudioTimingIssueKind.InvalidTimestamp => "invalid WASAPI timestamp; placed by arrival time",
                AudioTimingIssueKind.Discontinuity => "WASAPI reported an audio discontinuity",
                _ => FormattableString.Invariant($"audio timing moved by {issue.ShiftFrames * 1000d / PcmTimeline.SampleRate:F3} ms")
            };
            try { Warning?.Invoke($"{channel}: {cause}. Recording continues; see status for totals."); }
            catch (Exception) { /* Diagnostics must not interrupt recording. */ }
        }
    }

    private void StopCore()
    {
        var sources = active!;
        long endedAt = Stopwatch.GetTimestamp();
        stopping = true;
        try
        {
            try { sources.Me.StopRecording(); }
            finally { sources.Them.StopRecording(); }
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
