using System.Diagnostics;
using NAudio.Wave;

namespace MeetTape;

public sealed record CaptureStatus(bool IsActive, TimeSpan Duration, long MeBytes, long ThemBytes, string? Error)
{
    public string? RecordingDirectory { get; init; }
    public int Discontinuities { get; init; }
}

public sealed class CaptureCoordinator(
    Func<CaptureSources> openSources,
    Action<CaptureChannel, WaveInEventArgs, WaveFormat, long>? consume = null,
    Action<CaptureSources, long>? started = null,
    Action<long>? stopped = null) : IDisposable
{
    private readonly object control = new();
    private CaptureSources? active;
    private long startedAt;
    private long stoppedAt;
    private long meBytes;
    private long themBytes;
    private string? lastError;
    private volatile bool stopping;
    private bool disposed;

    public event Action<string>? Failed;
    public event Action<CaptureStatus>? Finished;
    public string? RecordingDirectory { get; internal set; }
    public int Discontinuities { get; internal set; }

    public CaptureStatus Status
    {
        get
        {
            lock (control)
            {
                long end = active is null ? stoppedAt : Stopwatch.GetTimestamp();
                return new(active is not null, startedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(startedAt, end),
                    Interlocked.Read(ref meBytes), Interlocked.Read(ref themBytes), Volatile.Read(ref lastError))
                { RecordingDirectory = RecordingDirectory, Discontinuities = Discontinuities };
            }
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
                started?.Invoke(sources, startedAt);
                Subscribe(sources, sources.Me, CaptureChannel.Me);
                Subscribe(sources, sources.Them, CaptureChannel.Them);
                Volatile.Write(ref active, sources);
                sources.Me.StartRecording();
                sources.Them.StartRecording();
            }
            catch (Exception ex)
            {
                string diagnostic = ex is RecordingException ? ex.Message :
                    $"Cannot start capture (0x{ex.HResult:X8}). Check microphone permissions and selected active devices.";
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
            Volatile.Write(ref lastError, ex is RecordingException ? ex.Message : "Cannot finalize session metadata; originals retained. Check permissions and disk space.");
        }
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
        string diagnostic = error is RecordingException ? $"{channel}: {error.Message}" :
            $"{channel} capture stopped unexpectedly (0x{(error?.HResult ?? 0):X8}). Check the device connection and permissions.";
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
            Interlocked.CompareExchange(ref lastError, $"Capture stop failed (0x{ex.HResult:X8}). Check device connection.", null);
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
                Interlocked.CompareExchange(ref lastError, ex is RecordingException ? ex.Message :
                    $"Capture cleanup failed (0x{ex.HResult:X8}). Check device connection.", null);
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
