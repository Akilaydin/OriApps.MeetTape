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
        long origin = Mp3Track.To100ns(Stopwatch.GetTimestamp());
        var track = new Mp3Track("ffmpeg", path, origin, error => throw new Exception(error.Message));
        await track.CompleteAsync(origin + 20_000_000);
        float[] decoded = await DecodeAsync(path);
        Assert.Equal(96000, decoded.Length);
        Assert.All(decoded, sample => Assert.InRange(Math.Abs(sample), 0, 0.001));
    }

    [Fact]
    public async Task LateAudioKeepsMp3EncodingAndReportsRecovery()
    {
        string path = Path.Combine(directory, "recovered.mp3");
        var failure = new TaskCompletionSource<RecordingException>(TaskCreationOptions.RunContinuationsAsynchronously);
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<AudioTimingIssue>();
        long origin = Mp3Track.To100ns(Stopwatch.GetTimestamp());
        var track = new Mp3Track("ffmpeg", path, origin, error => failure.TrySetResult(error), warnings.Enqueue);
        for (int i = 0; i < 10; i++)
            track.Enqueue(Tone(48000, 480, origin + i * 100_000L));
        track.Enqueue(Tone(48000, 480, origin)); // One whole packet arrives behind 100 ms of committed audio.
        track.Enqueue(Tone(48000, 480, origin + 1_100_000));

        await track.CompleteAsync(origin + 10_000_000);
        Assert.False(failure.Task.IsCompleted);
        Assert.Contains(warnings, warning => warning.Kind == AudioTimingIssueKind.Overlap);
        float[] decoded = await DecodeAsync(path);
        Assert.Equal(48000, decoded.Length);
    }

    [Fact]
    public async Task TimingWarningIsVisibleDuringCaptureAndSavedInSession()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = directory } };
        SyntheticSource? them = null;
        using var capture = new Mp3Recorder(config, () =>
        {
            them = new SyntheticSource();
            return new CaptureSources(new SyntheticSource(), them);
        }).Capture;
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        capture.Warning += warnings.Enqueue;
        capture.Start();
        long qpc = Mp3Track.To100ns(Stopwatch.GetTimestamp());
        them!.Emit(qpc, devicePosition: 0);
        them.Emit(qpc, devicePosition: 0);
        them.Emit(qpc, devicePosition: 0);
        Assert.True(SpinWait.SpinUntil(() => capture.Status.ThemTiming.Corrections > 0,
            TimeSpan.FromSeconds(5)), "Audio timing warning was not reported during capture.");
        Assert.True(capture.Status.IsActive);
        Assert.Null(capture.Status.Error);
        Assert.Single(warnings);
        Assert.Contains("Recording continues", warnings.Single());
        capture.Stop();
        var session = SessionStore.Read(capture.RecordingDirectory!);
        Assert.Equal(RecordingState.Recorded, session.RecordingState);
        Assert.Equal(capture.Status.ThemTiming.Corrections, session.ThemTiming.Corrections);
        Assert.Equal(session.ThemTiming.Corrections, session.ClockAdjustments);
        Assert.NotNull(session.ThemTiming.LastIssue);
        float[] me = await DecodeAsync(Path.Combine(capture.RecordingDirectory!, "me.mp3"));
        float[] other = await DecodeAsync(Path.Combine(capture.RecordingDirectory!, "them.mp3"));
        Assert.Equal(me.Length, other.Length);
    }

    [Fact]
    public async Task TimestampErrorFromWasapiStillProducesCompleteRecordedSession()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = directory } };
        SyntheticSource? them = null;
        using var capture = new Mp3Recorder(config, () =>
        {
            them = new SyntheticSource();
            return new CaptureSources(new SyntheticSource(), them);
        }).Capture;
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        capture.Warning += warnings.Enqueue;
        capture.Start();
        them!.Emit(qpc: 0, flags: NAudio.CoreAudioApi.AudioClientBufferFlags.TimestampError);
        them.Emit();
        Assert.True(SpinWait.SpinUntil(() => capture.Status.ThemTiming.InvalidTimestamps == 1,
            TimeSpan.FromSeconds(5)), "TimestampError did not reach the recording timeline.");
        Assert.True(capture.Status.IsActive);
        Assert.Contains(warnings, warning => warning.Contains("invalid WASAPI timestamp"));
        capture.Stop();
        var saved = SessionStore.Read(capture.RecordingDirectory!);
        Assert.Equal(RecordingState.Recorded, saved.RecordingState);
        Assert.Null(saved.Error);
        Assert.Equal(1, saved.ThemTiming.InvalidTimestamps);
        Assert.True(saved.ClockAdjustments >= 1);
        Assert.Equal(saved.ThemTiming.Corrections, saved.ClockAdjustments);
        Assert.Equal((await DecodeAsync(Path.Combine(capture.RecordingDirectory!, "me.mp3"))).Length,
            (await DecodeAsync(Path.Combine(capture.RecordingDirectory!, "them.mp3"))).Length);
    }

    [Fact]
    public async Task BackloggedPacketsExtendBothTracksAndPersistFullTranscribableDuration()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = directory } };
        SyntheticSource? them = null;
        using var capture = new Mp3Recorder(config, () =>
        {
            them = new SyntheticSource();
            return new CaptureSources(new SyntheticSource(), them);
        }).Capture;

        capture.Start();
        long repeatedQpc = Mp3Track.To100ns(Stopwatch.GetTimestamp());
        // Amplify the synthetic backlog without sleeping, so even under parallel CI load
        // the recovered media is measurably longer than the wall-clock capture.
        for (int i = 0; i < 48; i++)
            them!.Emit(qpc: repeatedQpc, frames: 4800, amplitude: 0.5f);
        capture.Stop();

        var status = capture.Status;
        Assert.Null(status.Error);
        Assert.True(status.AudioFrames > Math.Round(status.Duration.TotalSeconds * PcmTimeline.SampleRate),
            "The test must create a backlog longer than the wall-clock capture.");
        string folder = capture.RecordingDirectory!;
        float[] me = await DecodeAsync(Path.Combine(folder, "me.mp3"));
        float[] received = await DecodeAsync(Path.Combine(folder, "them.mp3"));
        Assert.Equal(me.Length, received.Length);
        Assert.Equal(status.AudioFrames, received.LongLength);
        Assert.True(received[^480..].Any(sample => sample > 0.2f), "The recovered tail must contain real audio, not padded silence.");
        var session = SessionStore.Read(folder);
        Assert.Equal(RecordingState.Recorded, session.RecordingState);
        Assert.Equal(received.Length / (double)PcmTimeline.SampleRate, session.DurationSeconds!.Value, 6);
        Assert.Equal(session.DurationSeconds, session.Trim.EndSeconds);
        Assert.True(session.DurationSeconds > status.Duration.TotalSeconds);
        var stages = new Dictionary<string, StageProgress>(session.Stages)
        {
            ["trim"] = session.Stages["trim"] with { Status = StageState.Completed }
        };
        var ready = session with { Stages = stages, Trim = session.Trim with { Decision = "skip" } };
        var prepared = await new AudioChunker("ffmpeg").PrepareAsync(folder, ready, CancellationToken.None);
        Assert.Equal(status.AudioFrames, Assert.Single(prepared.Chunks).OwnEndFrame);
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
            var saved = SessionStore.Read(capture.RecordingDirectory!);
            Assert.Equal(RecordingState.Recorded, saved.RecordingState);
            Assert.Equal(capture.Status.Duration.TotalSeconds, saved.DurationSeconds);
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
        public void Emit(long? qpc = null, long devicePosition = 0,
            NAudio.CoreAudioApi.AudioClientBufferFlags flags = 0, float amplitude = 0, int frames = 480)
        {
            var bytes = new byte[frames * sizeof(float)];
            if (amplitude != 0)
                for (int i = 0; i < bytes.Length; i += sizeof(float))
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), amplitude);
            DataAvailable?.Invoke(this, new WasapiPacketEventArgs(bytes, bytes.Length, devicePosition,
                qpc ?? Mp3Track.To100ns(Stopwatch.GetTimestamp()), flags));
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
