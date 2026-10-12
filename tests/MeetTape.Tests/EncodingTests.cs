using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MeetTape.Tests;

public sealed class EncodingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MeetTapeEncoding-" + Guid.NewGuid().ToString("N"));

    public EncodingTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Mp3StreamsDecodeWithSameLengthAndPreservedSignalOffsets()
    {
        string mePath = Path.Combine(directory, "me.mp3");
        string themPath = Path.Combine(directory, "them.mp3");
        using var meEncoder = new FfmpegEncoder("ffmpeg", mePath);
        using var themEncoder = new FfmpegEncoder("ffmpeg", themPath);
        var me = new PcmTimeline(meEncoder.Input, 0);
        var them = new PcmTimeline(themEncoder.Input, 0);
        using var mePacket = Tone(16000, 4800, 2_500_000);
        using var themPacket = Tone(44100, 13230, 5_000_000);
        await me.AppendAsync(mePacket, null, 96000);
        await them.AppendAsync(themPacket, null, 96000);
        await me.SilenceUntilAsync(96000);
        await them.SilenceUntilAsync(96000);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(mePath) && new FileInfo(mePath).Length > 100, TimeSpan.FromSeconds(5)),
            "MP3 bytes must reach disk before input closes.");
        await Task.WhenAll(meEncoder.CompleteAsync(), themEncoder.CompleteAsync());
        float[] meDecoded = await DecodeAsync(mePath);
        float[] themDecoded = await DecodeAsync(themPath);
        Assert.Equal(96000, meDecoded.Length);
        Assert.Equal(meDecoded.Length, themDecoded.Length);
        Assert.True(Rms(meDecoded, 0.05, 0.15) < 0.001);
        Assert.True(Rms(meDecoded, 0.30, 0.45) > 0.05);
        Assert.True(Rms(themDecoded, 0.20, 0.35) < 0.001);
        Assert.True(Rms(themDecoded, 0.55, 0.70) > 0.05);
        byte[] original = File.ReadAllBytes(mePath);
        Assert.Throws<RecordingException>(() => new FfmpegEncoder("ffmpeg", mePath));
        Assert.Equal(original, File.ReadAllBytes(mePath));
    }

    [Fact]
    public async Task TrackWithoutCallbacksStillCreatesFullLengthSilentMp3()
    {
        string path = Path.Combine(directory, "them.mp3");
        var track = new Mp3Track("ffmpeg", path, 1_000_000, error => throw new Exception(error.Message));
        await track.CompleteAsync(21_000_000);
        float[] decoded = await DecodeAsync(path);
        Assert.Equal(96000, decoded.Length);
        Assert.All(decoded, sample => Assert.InRange(Math.Abs(sample), 0, 0.001));
    }

    [Fact]
    public async Task CaptureFailureFlushesAlreadyEncodedAudioBeforeClosingFfmpeg()
    {
        string path = Path.Combine(directory, "partial.mp3");
        var failure = new TaskCompletionSource<RecordingException>(TaskCreationOptions.RunContinuationsAsynchronously);
        var track = new Mp3Track("ffmpeg", path, 0, error => failure.TrySetResult(error));
        for (int i = 0; i < 10; i++)
            track.Enqueue(Tone(48000, 480, i * 100_000L));
        track.Enqueue(Tone(48000, 480, 0)); // One whole packet arrives behind 100 ms of committed audio.
        track.Enqueue(Tone(48000, 480, 1_100_000));

        var error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Audio arrived after its timeline was committed", error.Message);
        await Assert.ThrowsAsync<RecordingException>(() => track.CompleteAsync(10_000_000));
        float[] decoded = await DecodeAsync(path);
        Assert.Equal(4800, decoded.Length); // All 100 ms of accepted audio must survive FFmpeg's final flush.
    }

    [Fact]
    public async Task ImmediateSecondSessionUsesNewSourcesAndProducesIndependentSynchronizedTracks()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = directory } };
        var sources = new List<(SyntheticSource Me, SyntheticSource Them)>();
        using var capture = new Mp3Recorder(config, () =>
        {
            var pair = (new SyntheticSource(), new SyntheticSource());
            sources.Add(pair);
            return new CaptureSources(pair.Item1, pair.Item2);
        }).Capture;

        var folders = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            capture.Start();
            sources[i].Me.Emit();
            sources[i].Them.Emit();
            await Task.Delay(30);
            capture.Stop();
            Assert.Null(capture.Status.Error);
            Assert.Equal(RecordingState.Recorded, SessionStore.Read(capture.RecordingDirectory!).RecordingState);
            folders.Add(capture.RecordingDirectory!);
            float[] me = await DecodeAsync(Path.Combine(folders[i], "me.mp3"));
            float[] them = await DecodeAsync(Path.Combine(folders[i], "them.mp3"));
            Assert.Equal(me.Length, them.Length);
            Assert.True(me.Length > 0);
        }
        Assert.NotEqual(folders[0], folders[1]);
        Assert.Equal(2, sources.Count);
    }

    private sealed class SyntheticSource : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Emit()
        {
            var bytes = new byte[480 * sizeof(float)];
            DataAvailable?.Invoke(this, new WasapiPacketEventArgs(bytes, bytes.Length, 0,
                Mp3Track.To100ns(Stopwatch.GetTimestamp()), 0));
        }
        public void Dispose() { }
    }

    [Fact]
    public void MissingEncoderGivesSafeDiagnostic()
    {
        var error = Assert.Throws<RecordingException>(() => new FfmpegEncoder("missing-ffmpeg-" + Guid.NewGuid(), Path.Combine(directory, "me.mp3")));
        Assert.Contains("capture.ffmpegPath", error.Message);
    }

    private static AudioPacket Tone(int rate, int frames, long start)
    {
        float[] samples = ArrayPool<float>.Shared.Rent(frames);
        for (int i = 0; i < frames; i++)
            samples[i] = 0.25f * (float)Math.Sin(i * 2 * Math.PI * 440 / rate);
        return new(samples, frames, rate, start, 0, 0);
    }

    internal static async Task<float[]> DecodeAsync(string path)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-f", "f32le", "-ar", "48000", "-ac", "1", "pipe:1" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes).WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await errors);
        byte[] data = bytes.ToArray();
        float[] result = new float[data.Length / sizeof(float)];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * sizeof(float)));
        return result;
    }

    private static double Rms(float[] samples, double start, double end)
    {
        int a = (int)(start * 48000), b = (int)(end * 48000);
        double sum = 0;
        for (int i = a; i < b; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (b - a));
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}

[Collection("Hardware")]
public sealed class RecordingHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task RealCaptureProducesTwoDecodableMp3sWithSameTimelineLength()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTapeHardwareMp3-" + Guid.NewGuid().ToString("N"));
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        var recorder = new Mp3Recorder(config with { Settings = config.Settings with { MeetingsRoot = root } });
        using var capture = recorder.Capture;
        try
        {
            capture.Start();
            var initial = SessionStore.Read(capture.RecordingDirectory!);
            Assert.Equal(RecordingState.Recording, initial.RecordingState);
            Assert.NotNull(initial.Capture.MeDevice);
            Assert.NotNull(initial.Capture.ThemDevice);
            Thread.Sleep(3000);
            capture.Stop();
            var status = capture.Status;
            output.WriteLine($"Capture: {status.Duration.TotalSeconds:F3}s; Me={status.MeBytes}; Them={status.ThemBytes}; error={status.Error ?? "none"}");
            Assert.Null(status.Error);
            float[] me = await EncodingTests.DecodeAsync(Path.Combine(status.RecordingDirectory!, "me.mp3"));
            float[] them = await EncodingTests.DecodeAsync(Path.Combine(status.RecordingDirectory!, "them.mp3"));
            output.WriteLine($"Decoded samples: Me={me.Length}; Them={them.Length}");
            Assert.Equal(me.Length, them.Length);
            Assert.InRange(me.Length, 144000, 240000);
            Assert.InRange(Math.Abs(me.Length / 48000d - status.Duration.TotalSeconds), 0, 1d / 48000);
            var saved = SessionStore.Read(status.RecordingDirectory!);
            Assert.Equal(initial.Id, saved.Id);
            Assert.Equal(RecordingState.Recorded, saved.RecordingState);
            Assert.Null(saved.Title);
            Assert.Equal(status.Duration.TotalSeconds, saved.DurationSeconds);
            Assert.True(File.GetAttributes(Path.Combine(status.RecordingDirectory!, "me.mp3")).HasFlag(FileAttributes.ReadOnly));
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
