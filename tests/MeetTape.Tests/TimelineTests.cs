using System.Buffers;
using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetTape.Tests;

public sealed class TimelineTests
{
    private const long Origin = 1_000_000_000;

    [Fact]
    public async Task EmptyLoopbackStillProducesWholeMeetingSilence()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        await timeline.SilenceUntilAsync(48000);
        Assert.Equal(48000 * sizeof(float), stream.Length);
        Assert.All(stream.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task DeviceStartOffsetAndSampleRateKeepSignalAtSamePosition(int rate)
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var packet = Packet(rate, rate / 10, Origin + 2_500_000, 0);
        await timeline.AppendAsync(packet, null, 24000);
        await timeline.SilenceUntilAsync(24000);
        float[] samples = Samples(stream);
        Assert.Equal(24000, samples.Length);
        Assert.Equal(0, samples[11999]);
        Assert.Equal(1, samples[12000]);
        Assert.Equal(1, samples[16799]);
        Assert.Equal(0, samples[16800]);
    }

    [Fact]
    public async Task PacketPositionsCompensateForClockDriftInsteadOfCountingNominalFrames()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        for (int i = 0; i < 100; i++)
        {
            using var packet = Packet(48000, 480, Origin + i * 101000L, i * 480L);
            using var next = Packet(48000, 480, Origin + (i + 1) * 101000L, (i + 1) * 480L);
            await timeline.AppendAsync(packet, next, 48480);
        }
        Assert.Equal(48480, timeline.FramesWritten);
        Assert.Equal(48480, Samples(stream).Length);
        Assert.All(Samples(stream), sample => Assert.Equal(1, sample));
    }

    [Fact]
    public async Task AdjacentTimestampJitterDoesNotMakeContinuousAudioOverlap()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var a = Packet(48000, 480, Origin, 0);
        using var b = Packet(48000, 480, Origin + 106670, 480);
        using var c = Packet(48000, 480, Origin + 200120, 960);
        using var d = Packet(48000, 480, Origin + 300120, 1440);
        await timeline.AppendAsync(a, b, 2400);
        await timeline.AppendAsync(b, c, 2400);
        await timeline.AppendAsync(c, d, 2400);
        await timeline.AppendAsync(d, null, 2400);
        Assert.Equal(timeline.FrameAt(Origin + 400120), timeline.FramesWritten);
        Assert.All(Samples(stream), sample => Assert.Equal(1, sample));
    }

    [Fact]
    public async Task LoopbackClockRebaseDoesNotStopCaptureOrDiscardAnAudioPacket()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        // Packet B arrives only 3.6045 ms after A, while both contain 10 ms of audio.
        // This reproduces the observed 307-frame failure after an idle loopback interval.
        using var a = Packet(48000, 480, Origin + 149_131_875, 480);
        using var b = Packet(48000, 480, Origin + 149_167_920, 480);
        using var c = Packet(48000, 480, Origin + 149_267_884, 960);
        Array.Fill(a.Samples, 0.25f, 0, a.Frames);
        Array.Fill(b.Samples, 0.5f, 0, b.Frames);
        Array.Fill(c.Samples, 0.75f, 0, c.Frames);

        await timeline.AppendAsync(a, b, 800000);
        Assert.Equal(716313, timeline.FramesWritten);
        await timeline.AppendAsync(b, c, 800000);
        await timeline.AppendAsync(c, null, 800000);

        float[] decoded = Samples(stream);
        Assert.InRange(decoded.Count(value => Math.Abs(value - 0.25f) < 0.00001f), 470, 490);
        Assert.InRange(decoded.Count(value => Math.Abs(value - 0.5f) < 0.00001f), 470, 490);
        Assert.InRange(decoded.Count(value => Math.Abs(value - 0.75f) < 0.00001f), 470, 490);
        Assert.Equal(2, timeline.ClockAdjustments);
    }

    [Fact]
    public async Task SmallQpcRegressionRetainsOrderedPackets()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var a = Packet(48000, 480, Origin + 1_000_000, 0);
        using var b = Packet(48000, 480, Origin + 970_000, 480);
        using var c = Packet(48000, 480, Origin + 1_070_000, 960);
        Array.Fill(a.Samples, 0.25f, 0, a.Frames);
        Array.Fill(b.Samples, 0.5f, 0, b.Frames);

        await timeline.AppendAsync(a, b, 8000);
        await timeline.AppendAsync(b, c, 8000);
        Assert.True(Samples(stream).Count(value => Math.Abs(value - 0.5f) < 0.00001f) >= 470);
    }

    [Fact]
    public async Task BadAdjacentQpcAnchorDoesNotCompressTenMillisecondsIntoThree()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var a = Packet(48000, 480, Origin + 1_000_000, 0);
        using var b = Packet(48000, 480, Origin + 1_036_045, 480);
        using var c = Packet(48000, 480, Origin + 1_136_045, 960);
        Array.Fill(a.Samples, 0.25f, 0, a.Frames);
        Array.Fill(b.Samples, 0.5f, 0, b.Frames);

        await timeline.AppendAsync(a, b, 8000);
        await timeline.AppendAsync(b, c, 8000);
        Assert.True(Samples(stream).Count(value => Math.Abs(value - 0.25f) < 0.00001f) >= 470);
        Assert.True(Samples(stream).Count(value => Math.Abs(value - 0.5f) < 0.00001f) >= 470);
    }

    [Fact]
    public async Task LargeOverlapPreservesSpeechAndReportsCorrection()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        var warnings = new List<AudioTimingIssue>();
        timeline.TimingIssue = warnings.Add;
        await timeline.SilenceUntilAsync(9600);
        using var packet = Packet(48000, 480, Origin + 1_000_000, 0);
        Array.Fill(packet.Samples, 0.5f, 0, packet.Frames);
        await timeline.AppendAsync(packet, null, 12000);
        Assert.Equal(10080, timeline.FramesWritten);
        Assert.All(Samples(stream)[9600..10080], sample => Assert.Equal(0.5f, sample));
        var warning = Assert.Single(warnings);
        Assert.Equal(AudioTimingIssueKind.Overlap, warning.Kind);
        Assert.Equal(4800, warning.ShiftFrames);
        Assert.Contains("device=0", warning.Detail);
    }

    [Fact]
    public async Task CompressedTimestampIntervalResamplesWholePacketIntoItsAvailableTime()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var a = Packet(48000, 480, Origin, 0);
        using var b = Packet(48000, 480, Origin + 100000, 480);
        using var c = Packet(48000, 480, Origin + 180000, 960);
        using var d = Packet(48000, 480, Origin + 280000, 1440);
        Array.Fill(b.Samples, 0.5f, 432, 48);
        await timeline.AppendAsync(a, b, 2400);
        await timeline.AppendAsync(b, c, 2400);
        await timeline.AppendAsync(c, d, 2400);
        Assert.Equal(1344, timeline.FramesWritten);
        float[] samples = Samples(stream);
        Assert.Equal(1, samples[480]);
        Assert.Equal(0.5f, samples[863]); // Tail marker would disappear if overlap were simply clipped.
        Assert.Equal(1, samples[864]);
    }

    [Fact]
    public async Task DeviceClockPausedDuringSilenceDoesNotStretchLastAudioPacket()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var a = Packet(48000, 480, Origin, 0);
        using var b = Packet(48000, 480, Origin + 10_000_000, 480, AudioClientBufferFlags.DataDiscontinuity);
        await timeline.AppendAsync(a, b, 52800);
        await timeline.AppendAsync(b, null, 52800);
        await timeline.SilenceUntilAsync(52800);
        float[] samples = Samples(stream);
        Assert.Equal(1, samples[479]);
        Assert.All(samples[480..48000], sample => Assert.Equal(0, sample));
        Assert.Equal(1, samples[48000]);
        Assert.Equal(0, samples[48480]);
    }

    [Fact]
    public async Task MissingPacketCreatesGapAndDiscontinuityIsVisible()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var a = Packet(48000, 480, Origin, 0);
        using var b = Packet(48000, 480, Origin + 300000, 1440, AudioClientBufferFlags.DataDiscontinuity);
        await timeline.AppendAsync(a, b, 2400);
        await timeline.AppendAsync(b, null, 2400);
        await timeline.SilenceUntilAsync(2400);
        float[] samples = Samples(stream);
        Assert.Equal(1, samples[479]);
        Assert.Equal(0, samples[480]);
        Assert.Equal(0, samples[1439]);
        Assert.Equal(1, samples[1440]);
        Assert.Equal(1, timeline.Discontinuities);
    }

    [Fact]
    public async Task StopCutsBothSourcesAtExactlyRequestedMeetingFrame()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        using var packet = Packet(48000, 4800, Origin, 0);
        await timeline.AppendAsync(packet, null, 2400);
        Assert.Equal(2400, timeline.FramesWritten);
        Assert.Equal(9600, stream.Length);
    }

    [Fact]
    public async Task LateAudioContinuesAtCommittedFrame()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        await timeline.SilenceUntilAsync(4800);
        using var packet = Packet(48000, 480, Origin, 0);
        await timeline.AppendAsync(packet, null, 9600);
        Assert.Equal(5280, timeline.FramesWritten);
        Assert.Equal(1, timeline.ClockAdjustments);
    }

    [Theory]
    [InlineData(-15_000_000L)]
    [InlineData(400_000_000L)]
    public async Task UntrustworthyQpcUsesArrivalTimeAndContinues(long positionOffset)
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        var warnings = new List<AudioTimingIssue>();
        timeline.TimingIssue = warnings.Add;
        using var packet = Packet(48000, 480, Origin + positionOffset, 0, received: Origin + 20_000_000);
        await timeline.AppendAsync(packet, null, 200000);
        Assert.Equal(96000, timeline.FramesWritten);
        Assert.Equal(1, timeline.ClockAdjustments);
        Assert.Equal(AudioTimingIssueKind.InvalidTimestamp, Assert.Single(warnings).Kind);
        Assert.Equal(1, Samples(stream)[95520..96000].Min());
    }

    [Fact]
    public async Task TimestampErrorFlagUsesArrivalTimeWithoutStopping()
    {
        using var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        var warnings = new List<AudioTimingIssue>();
        timeline.TimingIssue = warnings.Add;
        using var packet = Packet(48000, 480, 0, 480, AudioClientBufferFlags.TimestampError,
            received: Origin + 10_000_000);
        await timeline.AppendAsync(packet, null, 96000);
        Assert.Equal(48000, timeline.FramesWritten);
        Assert.Equal(AudioTimingIssueKind.InvalidTimestamp, Assert.Single(warnings).Kind);
    }

    [Fact]
    public async Task RepeatedDeviceClockResetsDoNotAccumulateAnUnboundedPacketQueue()
    {
        var timeline = new PcmTimeline(Stream.Null, Origin);
        const int packets = 2500;
        for (int i = 0; i < packets; i++)
        {
            int deviceFrame = i % 25 * 480;
            using var packet = Packet(48000, 480, Origin + deviceFrame * 10_000_000L / 48000, deviceFrame);
            await timeline.AppendAsync(packet, null, long.MaxValue);
        }
        Assert.Equal(packets * 480L, timeline.FramesWritten);
        Assert.True(timeline.ClockAdjustments > 0);
    }

    [Fact]
    public async Task BrokenOutputStreamStillFailsInsteadOfDiscardingAudio()
    {
        var stream = new MemoryStream();
        var timeline = new PcmTimeline(stream, Origin);
        stream.Dispose();
        using var packet = Packet(48000, 480, Origin, 0);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await timeline.AppendAsync(packet, null, 48000));
    }

    [Fact]
    public void FloatStereoIsDownmixedWithoutRetainingNativeCallbackBuffer()
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, 0.75f);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), 0.25f);
        using var packet = AudioPacket.Copy(new WasapiPacketEventArgs(buffer, buffer.Length, 0, Origin, 0),
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Array.Clear(buffer);
        Assert.Equal(0.5f, packet.Samples[0]);
        Assert.Equal(1, packet.Frames);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void PcmSignedSamplesDecodeCorrectly(int bits)
    {
        byte[] buffer = new byte[bits / 8];
        buffer[^1] = 0x80;
        using var packet = AudioPacket.Copy(new WasapiPacketEventArgs(buffer, buffer.Length, 0, Origin, 0), new WaveFormat(48000, bits, 1));
        Assert.Equal(-1, packet.Samples[0]);
    }

    [Fact]
    public void TimestampErrorIsPassedToTimelineButInvalidPcmStillFails()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        using var packet = AudioPacket.Copy(new WasapiPacketEventArgs(new byte[4], 4, 0, Origin,
            AudioClientBufferFlags.TimestampError), format, Origin + 1_000_000);
        Assert.True(packet.Flags.HasFlag(AudioClientBufferFlags.TimestampError));
        byte[] large = new byte[48000 * sizeof(float)];
        Assert.Throws<RecordingException>(() => AudioPacket.Copy(new WasapiPacketEventArgs(large, large.Length, 0, Origin, 0), format));
    }

    private static AudioPacket Packet(int rate, int count, long position, long devicePosition,
        AudioClientBufferFlags flags = 0, long received = 0)
    {
        float[] samples = ArrayPool<float>.Shared.Rent(count);
        Array.Fill(samples, 1f, 0, count);
        return new(samples, count, rate, position, devicePosition, flags) { Received100ns = received };
    }

    private static float[] Samples(MemoryStream stream)
    {
        byte[] bytes = stream.ToArray();
        var result = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        return result;
    }
}
