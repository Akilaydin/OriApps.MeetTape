using System.Buffers.Binary;
using NAudio.CoreAudioApi;

namespace OriApps.MeetTape.Audio;

public sealed class PcmTimeline(Stream destination, long origin100ns)
{
    public const int SampleRate = 48000;
    private const double MinAnchorIntervalRatio = 0.75;
    private readonly byte[] output = new byte[4096 * sizeof(float)];
    private double lastRate;
    internal Action<AudioTimingIssue>? TimingIssue { get; set; }
    public long FramesWritten { get; private set; }
    public int Discontinuities { get; private set; }
    public int ClockAdjustments { get; private set; }

    public long FrameAt(long time100ns) => Math.Max(0, (long)Math.Round((time100ns - origin100ns) * (double)SampleRate / 10_000_000));

    private static long PacketDuration100ns(AudioPacket packet) =>
        (long)Math.Round(packet.Frames * 10_000_000d / packet.SampleRate);

    private bool TimestampUnreliable(AudioPacket packet) =>
        (packet.Flags & AudioClientBufferFlags.TimestampError) != 0
        || packet.Position100ns < origin100ns
        || packet.Received100ns > 0 && packet.Position100ns > packet.Received100ns;

    private long EffectiveStart100ns(AudioPacket packet) =>
        TimestampUnreliable(packet)
            ? packet.Received100ns > 0
                ? Math.Max(origin100ns, packet.Received100ns - PacketDuration100ns(packet))
                : origin100ns + (long)Math.Round(FramesWritten * 10_000_000d / SampleRate)
            : packet.Position100ns;

    internal long PacketStartFrame(AudioPacket packet) => FrameAt(EffectiveStart100ns(packet));

    internal long PacketEndFrame(AudioPacket packet) => FrameAt(EffectiveStart100ns(packet) + PacketDuration100ns(packet));

    public async ValueTask AppendAsync(AudioPacket packet, AudioPacket? next, long limit, CancellationToken cancellation = default)
    {
        if ((packet.Flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
        {
            Discontinuities++;
            TimingIssue?.Invoke(new(AudioTimingIssueKind.Discontinuity, 0, packet.Frames,
                $"WASAPI flagged a gap at device frame {packet.DevicePosition}."));
        }
        bool badTimestamp = TimestampUnreliable(packet);
        long position = EffectiveStart100ns(packet);
        double rate = badTimestamp || lastRate == 0 ? packet.SampleRate : lastRate;
        if (!badTimestamp && next is not null && !TimestampUnreliable(next)
            && next.DevicePosition >= packet.DevicePosition + packet.Frames
            && next.Position100ns > packet.Position100ns)
        {
            double measured = (next.DevicePosition - packet.DevicePosition) * 10_000_000d / (next.Position100ns - packet.Position100ns);
            // Timestamp jitter must not move the acceptance window with the previous packet's estimate.
            if (measured >= packet.SampleRate * 0.9 && measured <= packet.SampleRate * 1.1)
                rate = measured;
        }
        lastRate = rate;
        long start = FrameAt(position);
        long endPosition = position + (long)Math.Round(packet.Frames * 10_000_000d / rate);
        if (!badTimestamp && next is not null && !TimestampUnreliable(next)
            && next.DevicePosition >= packet.DevicePosition + packet.Frames
            && next.Position100ns > packet.Position100ns && endPosition > next.Position100ns
            && next.Position100ns - packet.Position100ns >= packet.Frames * 10_000_000d / packet.SampleRate * MinAnchorIntervalRatio)
        {
            // Trust the next boundary only with a consistent device counter and a plausible QPC interval.
            // A broken anchor must not compress a whole packet into a few milliseconds.
            endPosition = next.Position100ns;
            rate = packet.Frames * 10_000_000d / (endPosition - packet.Position100ns);
        }
        long end = Math.Min(limit, FrameAt(endPosition));
        long adjustment = Math.Max(0, FramesWritten - start);
        if (badTimestamp || adjustment > 0)
        {
            // Timestamp metadata must not abort capture or silently remove the overlapping speech.
            // Append the entire packet after the committed PCM, even after a large clock reset.
            end = Math.Min(limit, end + adjustment);
            ClockAdjustments++;
            TimingIssue?.Invoke(new(badTimestamp ? AudioTimingIssueKind.InvalidTimestamp : AudioTimingIssueKind.Overlap,
                adjustment, packet.Frames, FormattableString.Invariant(
                    $"raw={FrameAt(packet.Position100ns) / (double)SampleRate:F6}s; selected={start / (double)SampleRate:F6}s; received={(packet.Received100ns > 0 ? FrameAt(packet.Received100ns) / (double)SampleRate : -1):F6}s; committed={FramesWritten / (double)SampleRate:F6}s; device={packet.DevicePosition}; flags={packet.Flags}.")));
        }
        await SilenceUntilAsync(Math.Min(start, limit), cancellation);
        while (FramesWritten < end)
        {
            int count = (int)Math.Min(output.Length / sizeof(float), end - FramesWritten);
            for (int i = 0; i < count; i++)
            {
                double source = ((FramesWritten + i - adjustment) * 10_000_000d / SampleRate - (position - origin100ns)) * rate / 10_000_000;
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
