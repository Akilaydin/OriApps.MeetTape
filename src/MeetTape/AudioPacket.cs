using System.Buffers;
using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetTape;

public sealed class RecordingException(string message) : IOException(message);

public sealed class AudioPacket(float[] samples, int frames, int sampleRate, long position100ns, long devicePosition,
    AudioClientBufferFlags flags) : IDisposable
{
    public float[] Samples { get; } = samples;
    public int Frames { get; } = frames;
    public int SampleRate { get; } = sampleRate;
    public long Position100ns { get; } = position100ns;
    public long DevicePosition { get; } = devicePosition;
    public AudioClientBufferFlags Flags { get; } = flags;
    public long Received100ns { get; init; }

    public static AudioPacket Copy(WaveInEventArgs args, WaveFormat format, long received100ns = 0)
    {
        if (args is not WasapiPacketEventArgs packet || (packet.Flags & AudioClientBufferFlags.TimestampError) != 0)
            throw new RecordingException("WASAPI timestamp is unavailable/invalid; partial originals retained.");
        int frames = args.BytesRecorded / format.BlockAlign;
        if (args.BytesRecorded % format.BlockAlign != 0 || frames <= 0 || frames > format.SampleRate / 5)
            throw new RecordingException("Invalid or oversized PCM packet; capture stopped.");
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
            encoding = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")
                    ? WaveFormatEncoding.Pcm : encoding;
        if (!(encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            && !(encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32))
            throw new RecordingException("Unsupported capture format; use float32 or PCM16/24/32 devices.");

        float[] mono = ArrayPool<float>.Shared.Rent(Math.Max(1, frames));
        try
        {
            var bytes = args.Buffer.AsSpan(0, args.BytesRecorded);
            int width = format.BitsPerSample / 8;
            for (int frame = 0; frame < frames; frame++)
            {
                double value = 0;
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int offset = frame * format.BlockAlign + channel * width;
                    value += encoding == WaveFormatEncoding.IeeeFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..])
                        : width switch
                        {
                            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) / 32768f,
                            3 => ((bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16) << 8 >> 8) / 8388608f,
                            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]) / 2147483648f
                        };
                }
                float sample = (float)(value / format.Channels);
                mono[frame] = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
            }
            return new(mono, frames, format.SampleRate, packet.QpcPosition100ns, packet.DevicePosition, packet.Flags)
            { Received100ns = received100ns };
        }
        catch { ArrayPool<float>.Shared.Return(mono); throw; }
    }

    public void Dispose() => ArrayPool<float>.Shared.Return(Samples);
}
