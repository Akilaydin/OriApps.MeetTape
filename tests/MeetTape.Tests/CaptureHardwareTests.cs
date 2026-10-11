using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit.Abstractions;

namespace MeetTape.Tests;

// Opt in with MEETTAPE_RUN_HARDWARE_TESTS=1 and --filter Category=Hardware.
[Collection("Hardware")]
public sealed class CaptureHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public void CapturesDefaultMicrophoneAndLoopbackWithoutGeneratedPlayback()
    {
        using var capture = new CaptureCoordinator(() => AudioDevices.Open(new()));
        capture.Start();
        Thread.Sleep(3000);
        capture.Stop();
        var status = capture.Status;
        output.WriteLine($"Default capture: {status.Duration.TotalSeconds:F3}s; Me={status.MeBytes}; Them={status.ThemBytes}; error={status.Error ?? "none"}");
        Assert.Null(status.Error);
        Assert.True(status.MeBytes > 0);
        Assert.False(status.IsActive);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public void CapturesSelectedEndpointsAndSyntheticPlaybackWithPacketTimestamps()
    {
        var devices = AudioDevices.List();
        var me = Assert.Single(devices, d => d.Channel == CaptureChannel.Me && d.IsDefault);
        var them = Assert.Single(devices, d => d.Channel == CaptureChannel.Them && d.IsDefault);
        using var enumerator = new MMDeviceEnumerator();
        using var renderDevice = enumerator.GetDevice(them.Id);
        using var player = new WasapiOut(renderDevice, AudioClientShareMode.Shared, false, 100);
        var tone = new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.02, Type = SignalGeneratorType.Sin };
        player.Init(tone.ToWaveProvider());
        long packets = 0;
        long themPackets = 0;
        long nonzeroThemPackets = 0;
        var meBuffers = new HashSet<byte[]>();
        var themBuffers = new HashSet<byte[]>();
        using var capture = new CaptureCoordinator(() => AudioDevices.Open(new()
        {
            MicrophoneDeviceId = me.Id, PlaybackDeviceId = them.Id
        }), (channel, args, _, _) =>
        {
            var packet = Assert.IsType<WasapiPacketEventArgs>(args);
            Assert.True(packet.QpcPosition100ns > 0);
            Interlocked.Increment(ref packets);
            if (channel == CaptureChannel.Them)
            {
                Interlocked.Increment(ref themPackets);
                themBuffers.Add(args.Buffer);
                if (args.Buffer.AsSpan(0, args.BytesRecorded).IndexOfAnyExcept((byte)0) >= 0)
                    Interlocked.Increment(ref nonzeroThemPackets);
            }
            else
                meBuffers.Add(args.Buffer);
        });
        capture.Start();
        player.Play();
        Thread.Sleep(3000);
        player.Stop();
        capture.Stop();
        var status = capture.Status;
        output.WriteLine($"Selected capture: {status.Duration.TotalSeconds:F3}s; Me={status.MeBytes}; Them={status.ThemBytes}; packets={packets}; loopbackPackets={themPackets}; error={status.Error ?? "none"}");
        Assert.Null(status.Error);
        Assert.True(status.MeBytes > 0 && status.ThemBytes > 0);
        Assert.True(themPackets > 0);
        Assert.True(nonzeroThemPackets > 0);
        Assert.Single(meBuffers);
        Assert.Single(themBuffers);
    }
}

public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEETTAPE_RUN_HARDWARE_TESTS") != "1")
            Skip = "Set MEETTAPE_RUN_HARDWARE_TESTS=1 to capture local audio and play a quiet test tone.";
    }
}

[CollectionDefinition("Hardware", DisableParallelization = true)]
public sealed class HardwareCollection { }
