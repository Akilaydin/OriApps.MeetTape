using System.Buffers.Binary;
using NAudio.CoreAudioApi;

namespace MeetTape;

public sealed class PcmTimeline(Stream destination, long origin100ns)
{
    public const int SampleRate = 48000;
    private readonly byte[] output = new byte[4096 * sizeof(float)];
    private double lastRate;
    public long FramesWritten { get; private set; }
    public int Discontinuities { get; private set; }

    public long FrameAt(long time100ns) => Math.Max(0, (long)Math.Round((time100ns - origin100ns) * (double)SampleRate / 10_000_000));

    public async ValueTask AppendAsync(AudioPacket packet, AudioPacket? next, long limit, CancellationToken cancellation = default)
    {
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
            && next.Position100ns > packet.Position100ns && endPosition > next.Position100ns)
        {
            // Fit the whole packet before its successor instead of committing audio over the next timestamp.
            endPosition = next.Position100ns;
            rate = packet.Frames * 10_000_000d / (endPosition - packet.Position100ns);
        }
        long end = Math.Min(limit, FrameAt(endPosition));
        if (start + 2 < FramesWritten && packet.Position100ns >= origin100ns)
            throw new RecordingException("Audio arrived after its timeline was committed; capture stopped instead of silently dropping speech. "
                + FormattableString.Invariant($"Late by {FramesWritten - start} frames ({(FramesWritten - start) * 1000d / SampleRate:F3} ms); packet at {start / (double)SampleRate:F6}s; committed {FramesWritten / (double)SampleRate:F6}s; ")
                + FormattableString.Invariant($"device frame {packet.DevicePosition}; flags {packet.Flags}; rate {rate:F3}; next anchor {next is not null}."));
        await SilenceUntilAsync(Math.Min(start, limit), cancellation);
        while (FramesWritten < end)
        {
            int count = (int)Math.Min(output.Length / sizeof(float), end - FramesWritten);
            for (int i = 0; i < count; i++)
            {
                double source = ((FramesWritten + i) * 10_000_000d / SampleRate - (packet.Position100ns - origin100ns)) * rate / 10_000_000;
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
