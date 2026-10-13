using System.Diagnostics;
using System.Threading.Channels;

namespace MeetTape;

public sealed class Mp3Track
{
    private readonly Channel<AudioPacket> packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(100)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly long origin100ns;
    private readonly Task worker;
    private readonly TaskCompletionSource<long> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<long> synchronizedEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long end100ns = -1;
    private RecordingException? failure;
    public int Discontinuities { get; private set; }
    public int ClockAdjustments { get; private set; }

    public Mp3Track(string executable, string outputPath, long origin100ns, Action<RecordingException> failed)
        : this(executable, outputPath, origin100ns, failed, null) { }

    internal Mp3Track(string executable, string outputPath, long origin100ns, Action<RecordingException> failed,
        Action<AudioTimingIssue>? onTimingIssue)
    {
        this.origin100ns = origin100ns;
        var encoder = new FfmpegEncoder(executable, outputPath);
        worker = Task.Run(() => RunAsync(encoder, failed, onTimingIssue));
    }

    public void Enqueue(AudioPacket packet)
    {
        if (Volatile.Read(ref failure) is { } error)
        {
            packet.Dispose();
            throw error;
        }
        if (!packets.Writer.TryWrite(packet))
        {
            packet.Dispose();
            throw new RecordingException("Encoder cannot keep up; bounded audio queue exhausted. Partial originals retained.");
        }
    }

    public async Task CompleteAsync(long ended100ns)
    {
        long frames = await DrainAsync(ended100ns);
        await FinishAsync(frames);
    }

    internal async Task<long> DrainAsync(long ended100ns)
    {
        Volatile.Write(ref end100ns, ended100ns);
        packets.Writer.TryComplete();
        return await drained.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    internal async Task FinishAsync(long commonEndFrame)
    {
        synchronizedEnd.TrySetResult(commonEndFrame);
        await worker.WaitAsync(TimeSpan.FromSeconds(15));
        if (failure is not null)
            throw failure;
    }

    private async Task RunAsync(FfmpegEncoder encoder, Action<RecordingException> failed, Action<AudioTimingIssue>? onTimingIssue)
    {
        AudioPacket? pending = null;
        var timeline = new PcmTimeline(encoder.Input, origin100ns);
        timeline.TimingIssue = onTimingIssue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        bool completionAttempted = false;
        try
        {
            while (true)
            {
                long ended = Volatile.Read(ref end100ns);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                int drained = 0;
                while (drained++ < 100 && packets.Reader.TryRead(out var next))
                {
                    if (pending is not null)
                    {
                        try { await timeline.AppendAsync(pending, next, long.MaxValue, timeout.Token); }
                        catch { next.Dispose(); throw; }
                        finally { pending.Dispose(); pending = null; }
                    }
                    pending = next;
                }
                long horizon = ended < 0 ? Math.Max(0, timeline.FrameAt(To100ns(Stopwatch.GetTimestamp())) - PcmTimeline.SampleRate / 2)
                    : timeline.FrameAt(ended);
                if (pending is not null && (ended >= 0 ||
                    timeline.PacketEndFrame(pending) <= horizon))
                {
                    try { await timeline.AppendAsync(pending, null, long.MaxValue, timeout.Token); }
                    finally { pending.Dispose(); pending = null; }
                }
                await timeline.SilenceUntilAsync(Math.Min(horizon, pending is null ? horizon : timeline.PacketStartFrame(pending)), timeout.Token);
                if (ended >= 0 && !packets.Reader.TryPeek(out _))
                    break;
                await timer.WaitForNextTickAsync();
            }
            drained.TrySetResult(timeline.FramesWritten);
            long commonEndFrame = await synchronizedEnd.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await timeline.SilenceUntilAsync(commonEndFrame);
            completionAttempted = true;
            await encoder.CompleteAsync();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("recording", "MP3 write/encoder", ex);
            var error = ex as RecordingException ?? new RecordingException(
                AppDiagnostics.UserMessage(ex, "MP3 write/encoder failed or stalled; check output permissions, disk space and FFmpeg. Partial originals retained."));
            Volatile.Write(ref failure, error);
            drained.TrySetException(error);
            // Close FFmpeg's stdin and let it flush the already written PCM before any forced disposal.
            // If the encoder itself failed, preserve the initial error and let Dispose kill a stalled process.
            if (!completionAttempted)
            {
                try { await encoder.CompleteAsync(); }
                catch (Exception) { }
            }
            failed(error);
        }
        finally
        {
            Discontinuities = timeline.Discontinuities;
            ClockAdjustments = timeline.ClockAdjustments;
            packets.Writer.TryComplete();
            pending?.Dispose();
            while (packets.Reader.TryRead(out var packet))
                packet.Dispose();
            encoder.Dispose();
        }
    }

    public static long To100ns(long stopwatchTicks) => (long)Math.Round(stopwatchTicks * (10_000_000d / Stopwatch.Frequency));
}
