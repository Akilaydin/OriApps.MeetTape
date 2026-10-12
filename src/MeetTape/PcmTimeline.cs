using System.Buffers.Binary;
using NAudio.CoreAudioApi;

namespace MeetTape;

public sealed class PcmTimeline(Stream destination, long origin100ns)
{
    public const int SampleRate = 48000;
    private const int MaxRecoverableOverlapFrames = SampleRate / 50; // 20 ms of clock jitter, not a late capture backlog.
    private const double MinAnchorIntervalRatio = 0.75;
    private const int DiagnosticHistorySize = 12;
    private readonly byte[] output = new byte[4096 * sizeof(float)];
    private readonly PacketTiming[] history = new PacketTiming[DiagnosticHistorySize];
    private int historyNext;
    private int historyCount;
    private double lastRate;
    public long FramesWritten { get; private set; }
    public int Discontinuities { get; private set; }
    public int ClockAdjustments { get; private set; }

    public long FrameAt(long time100ns) => Math.Max(0, (long)Math.Round((time100ns - origin100ns) * (double)SampleRate / 10_000_000));

    public async ValueTask AppendAsync(AudioPacket packet, AudioPacket? next, long limit, CancellationToken cancellation = default)
    {
        history[historyNext] = new(packet.Position100ns, packet.DevicePosition, packet.Frames, packet.Flags,
            packet.Received100ns, FramesWritten);
        historyNext = (historyNext + 1) % history.Length;
        historyCount = Math.Min(historyCount + 1, history.Length);
        if ((packet.Flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
            Discontinuities++;
        double rate = lastRate == 0 ? packet.SampleRate : lastRate;
        if (next is not null && next.DevicePosition >= packet.DevicePosition + packet.Frames
            && next.Position100ns > packet.Position100ns)
        {
            double measured = (next.DevicePosition - packet.DevicePosition) * 10_000_000d / (next.Position100ns - packet.Position100ns);
            // Timestamp jitter must not move the acceptance window with the previous packet's estimate.
            if (measured >= packet.SampleRate * 0.9 && measured <= packet.SampleRate * 1.1)
                rate = measured;
        }
        lastRate = rate;
        long start = FrameAt(packet.Position100ns);
        long endPosition = packet.Position100ns + (long)Math.Round(packet.Frames * 10_000_000d / rate);
        if (next is not null && next.DevicePosition >= packet.DevicePosition + packet.Frames
            && next.Position100ns > packet.Position100ns && endPosition > next.Position100ns
            && next.Position100ns - packet.Position100ns >= packet.Frames * 10_000_000d / packet.SampleRate * MinAnchorIntervalRatio)
        {
            // Trust the next boundary only with a consistent device counter and a plausible QPC interval.
            // A broken anchor must not compress a whole packet into a few milliseconds.
            endPosition = next.Position100ns;
            rate = packet.Frames * 10_000_000d / (endPosition - packet.Position100ns);
        }
        long end = Math.Min(limit, FrameAt(endPosition));
        long adjustment = 0;
        if (start < FramesWritten && packet.Position100ns >= origin100ns)
        {
            long overlap = FramesWritten - start;
            if (overlap > MaxRecoverableOverlapFrames)
                throw new RecordingException("Audio arrived after its timeline was committed; capture stopped instead of silently dropping speech. "
                    + FormattableString.Invariant($"Late by {overlap} frames ({overlap * 1000d / SampleRate:F3} ms); packet at {start / (double)SampleRate:F6}s; committed {FramesWritten / (double)SampleRate:F6}s; ")
                    + FormattableString.Invariant($"device frame {packet.DevicePosition}; flags {packet.Flags}; rate {rate:F3}; next anchor {next is not null}.")
                    + RecentPacketDiagnostic(next));
            // Preserve the complete packet by moving its playback start rather than dropping the overlapping samples.
            // A truly late callback (more than 20 ms) remains a hard error.
            adjustment = overlap;
            start = FramesWritten;
            end = Math.Min(limit, end + overlap);
            ClockAdjustments++;
        }
        await SilenceUntilAsync(Math.Min(start, limit), cancellation);
        while (FramesWritten < end)
        {
            int count = (int)Math.Min(output.Length / sizeof(float), end - FramesWritten);
            for (int i = 0; i < count; i++)
            {
                double source = ((FramesWritten + i - adjustment) * 10_000_000d / SampleRate - (packet.Position100ns - origin100ns)) * rate / 10_000_000;
                int left = Math.Clamp((int)Math.Floor(source), 0, packet.Frames - 1);
                int right = Math.Min(left + 1, packet.Frames - 1);
                float fraction = (float)Math.Clamp(source - left, 0, 1);
                float sample = packet.Samples[left] + fraction * (packet.Samples[right] - packet.Samples[left]);
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(i * sizeof(float)), sample);
            }
            await destination.WriteAsync(output.AsMemory(0, count * sizeof(float)), cancellation);
            FramesWritten += count;
        }
    }

    private string RecentPacketDiagnostic(AudioPacket? next)
    {
        var lines = new List<string>(historyCount + 1);
        for (int i = 0; i < historyCount; i++)
        {
            var entry = history[(historyNext - historyCount + i + history.Length) % history.Length];
            string latency = entry.Received100ns == 0 ? "n/a"
                : FormattableString.Invariant($"{(entry.Received100ns - entry.Position100ns) / 10_000d:F2}ms");
            lines.Add(FormattableString.Invariant(
                $"t={(entry.Position100ns - origin100ns) / 10_000_000d:F6}s device={entry.DevicePosition} frames={entry.Frames} flags={entry.Flags} lag={latency} committed={entry.CommittedBefore}"));
        }
        if (next is not null)
            lines.Add(FormattableString.Invariant(
                $"next t={(next.Position100ns - origin100ns) / 10_000_000d:F6}s device={next.DevicePosition} frames={next.Frames} flags={next.Flags}"));
        return " Recent WASAPI packet metadata (oldest to newest; no audio): " + string.Join(" | ", lines);
    }

    private readonly record struct PacketTiming(long Position100ns, long DevicePosition, int Frames,
        AudioClientBufferFlags Flags, long Received100ns, long CommittedBefore);

    public async ValueTask SilenceUntilAsync(long frame, CancellationToken cancellation = default)
    {
        Array.Clear(output);
        while (FramesWritten < frame)
        {
            int count = (int)Math.Min(output.Length / sizeof(float), frame - FramesWritten);
            await destination.WriteAsync(output.AsMemory(0, count * sizeof(float)), cancellation);
            FramesWritten += count;
        }
    }
}
