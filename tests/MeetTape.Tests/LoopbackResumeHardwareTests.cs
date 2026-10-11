using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit.Abstractions;

namespace MeetTape.Tests;

[Collection("Hardware")]
public sealed class LoopbackResumeHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task LoopbackResumesAfterPlaybackPausesWithoutTimelineFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTapeLoopbackResume-" + Guid.NewGuid().ToString("N"));
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        var recent = new Queue<string>();
        long origin = 0;
        var recorder = new Mp3Recorder(config with { Settings = config.Settings with { MeetingsRoot = root } }, () =>
        {
            var sources = AudioDevices.Open(new());
            sources.Them.DataAvailable += (_, args) =>
            {
                var packet = (WasapiPacketEventArgs)args;
                lock (recent)
                {
                    if (recent.Count == 12) recent.Dequeue();
                    recent.Enqueue(FormattableString.Invariant($"Them: QPC={(packet.QpcPosition100ns - origin) / 10_000_000d:F6}s; device={packet.DevicePosition}; frames={args.BytesRecorded / sources.Them.WaveFormat.BlockAlign}; flags={packet.Flags}; delivery lag={(Mp3Track.To100ns(Stopwatch.GetTimestamp()) - packet.QpcPosition100ns) / 10_000d:F3}ms"));
                }
            };
            return sources;
        });
        using var capture = recorder.Capture;
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        try
        {
            origin = Mp3Track.To100ns(Stopwatch.GetTimestamp());
            capture.Start();
            for (int i = 0; i < 3 && capture.Status.IsActive; i++)
            {
                using (var player = new WasapiOut(render, AudioClientShareMode.Shared, false, 100))
                {
                    player.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.02 }.ToWaveProvider());
                    player.Play();
                    await Task.Delay(3000);
                    player.Stop();
                }
                // Dispose the render client so the audio engine can become idle, then resume playback.
                await Task.Delay(11000);
            }
            if (capture.Status.IsActive) capture.Stop();
            var status = capture.Status;
            output.WriteLine($"Capture: {status.Duration.TotalSeconds:F3}s; error={status.Error ?? "none"}");
            lock (recent) foreach (string packet in recent) output.WriteLine(packet);
            Assert.Null(status.Error);
            float[] me = await EncodingTests.DecodeAsync(Path.Combine(status.RecordingDirectory!, "me.mp3"));
            float[] them = await EncodingTests.DecodeAsync(Path.Combine(status.RecordingDirectory!, "them.mp3"));
            Assert.Equal(me.Length, them.Length);
            Assert.InRange(Math.Abs(me.Length / 48000d - status.Duration.TotalSeconds), 0, 1d / 48000);
        }
        finally
        {
            capture.Dispose();
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
