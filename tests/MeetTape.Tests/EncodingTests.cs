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
        Assert.True(SpinWait.SpinUntil(() => warnings.Any(warning => warning.Contains("invalid WASAPI timestamp")),
            TimeSpan.FromSeconds(5)), "Timestamp warning did not reach the recording observer.");
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
        // FFmpeg can round encoder delay/padding by a few decoded samples; require sub-millisecond accuracy.
        const long maxMp3RoundingFrames = PcmTimeline.SampleRate / 1000;
        Assert.InRange(Math.Abs(status.AudioFrames - received.LongLength), 0, maxMp3RoundingFrames);
        Assert.True(received[^480..].Any(sample => sample > 0.2f), "The recovered tail must contain real audio, not padded silence.");
        var session = SessionStore.Read(folder);
        Assert.Equal(RecordingState.Recorded, session.RecordingState);
        Assert.InRange(Math.Abs(received.Length / (double)PcmTimeline.SampleRate - session.DurationSeconds!.Value),
            0, maxMp3RoundingFrames / (double)PcmTimeline.SampleRate);
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
    public async Task ImmediateSecondSessionAfterPreparationRetainsOriginalsAndPendingTrim()
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

        int keyReads = 0;
        var controller = new DesktopController(config, capture, readKey: _ =>
        {
            Interlocked.Increment(ref keyReads);
            throw new Exception("No API key read expected");
        });
        try
        {
            await controller.StartAsync();
            sources[0].Me.Emit(); sources[0].Them.Emit();
            await controller.StopAsync();
            Assert.Null(capture.Status.Error);
            string previous = capture.RecordingDirectory!;
            Guid id = SessionStore.Read(previous).Id;
            byte[] originalMe = File.ReadAllBytes(Path.Combine(previous, "me.mp3"));
            byte[] originalThem = File.ReadAllBytes(Path.Combine(previous, "them.mp3"));
            await controller.PrepareAsync(previous, "Planning");
            previous = capture.RecordingDirectory!;
            await controller.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
            Assert.NotEqual(id, SessionStore.Read(capture.RecordingDirectory!).Id);
            Assert.Equal(id, SessionStore.Read(previous).Id);
            Assert.Equal("Planning", SessionStore.Read(previous).Title);
            Assert.NotEqual(StageState.Completed, SessionStore.Read(previous).Stages["trim"].Status);
            Assert.Equal(0, keyReads);
            sources[1].Me.Emit(); sources[1].Them.Emit();
            await controller.StopAsync();
            Assert.Null(capture.Status.Error);
            Assert.Equal(originalMe, File.ReadAllBytes(Path.Combine(previous, "me.mp3")));
            Assert.Equal(originalThem, File.ReadAllBytes(Path.Combine(previous, "them.mp3")));
            float[] me = await DecodeAsync(Path.Combine(capture.RecordingDirectory!, "me.mp3"));
            float[] them = await DecodeAsync(Path.Combine(capture.RecordingDirectory!, "them.mp3"));
            Assert.Equal(me.Length, them.Length);
            Assert.True(me.Length > 0);
            await controller.PrepareAsync(capture.RecordingDirectory!, "");
            await controller.PrepareAsync(previous, null);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await controller.ReadAsync()).Preparing && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.False((await controller.ReadAsync()).Preparing);
            await controller.ChooseTrimAsync(TrimDecision.Skip);
            Assert.Equal(StageState.Completed, SessionStore.Read(previous).Stages["trim"].Status);
            Assert.Equal(originalMe, File.ReadAllBytes(Path.Combine(previous, "me.mp3")));
            Assert.Equal(2, sources.Count);
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Fact]
    public async Task SettingsApplyToNextRecordingWithoutReopeningActiveSourcesOrLosingOldRoot()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = directory } };
        var first = config with { Settings = config.Settings with
        {
            MeetingsRoot = Path.Combine(directory, "first"), ChunkSeconds = 60,
            Capture = config.Settings.Capture with { MicrophoneDeviceId = "first-mic" }
        } };
        var next = first with { Settings = first.Settings with
        {
            MeetingsRoot = Path.Combine(directory, "next"), ChunkSeconds = 90,
            Capture = first.Settings.Capture with { MicrophoneDeviceId = "next-mic", PlaybackDeviceId = "next-playback" },
            Stages = new() { Trim = false, Transcribe = false },
            Transcription = first.Settings.Transcription with { Model = "next-model" }
        } };
        var opened = new List<CaptureSettings>();
        var sources = new List<(SyntheticSource Me, SyntheticSource Them)>();
        var controller = new DesktopController(config, readKey: _ => throw new Exception("No API expected"), openSources: settings =>
        {
            opened.Add(settings);
            var pair = (new SyntheticSource(), new SyntheticSource());
            sources.Add(pair);
            return new CaptureSources(pair.Item1, pair.Item2);
        });
        try
        {
            await controller.ApplyConfigurationAsync(first);
            await controller.StartAsync();
            string previous = (await controller.ReadAsync()).Capture.RecordingDirectory!;
            var recordedContract = SessionStore.Read(previous).Processing;
            sources[0].Me.Emit(amplitude: 0.2f); sources[0].Them.Emit(amplitude: 0.4f);
            await controller.ApplyConfigurationAsync(next);
            Assert.Single(opened);
            Assert.True((await controller.ReadAsync()).Capture.IsActive);
            Assert.Equal(recordedContract, SessionStore.Read(previous).Processing);
            sources[0].Me.Emit(); sources[0].Them.Emit();
            previous = (await controller.StopAsync())!;
            Assert.Equal(RecordingState.Recorded, SessionStore.Read(previous).RecordingState);
            byte[] originalMe = File.ReadAllBytes(Path.Combine(previous, "me.mp3"));
            byte[] originalThem = File.ReadAllBytes(Path.Combine(previous, "them.mp3"));
            await controller.PrepareAsync(previous, "First meeting");
            previous = Assert.Single((await controller.ReadAsync()).Sessions).Directory;
            await controller.StartAsync();
            var snapshot = await controller.ReadAsync();
            string current = snapshot.Capture.RecordingDirectory!;
            Assert.StartsWith(next.MeetingsRoot + Path.DirectorySeparatorChar, current);
            Assert.Equal(new[] { first.Settings.Capture, next.Settings.Capture }, opened);
            var data = SessionStore.Read(current);
            Assert.Equal(90, data.Processing.ChunkSeconds);
            Assert.Equal("next-model", data.Processing.Transcription.Model);
            Assert.False(data.Stages["transcribe"].Enabled);
            Assert.Equal(2, snapshot.Sessions.Count);
            Assert.Contains(snapshot.Sessions, session => session.Directory == previous);
            sources[1].Me.Emit(); sources[1].Them.Emit();
            current = (await controller.StopAsync())!;
            Assert.Equal(originalMe, File.ReadAllBytes(Path.Combine(previous, "me.mp3")));
            Assert.Equal(originalThem, File.ReadAllBytes(Path.Combine(previous, "them.mp3")));
            Assert.Equal((await DecodeAsync(Path.Combine(current, "me.mp3"))).Length,
                (await DecodeAsync(Path.Combine(current, "them.mp3"))).Length);
        }
        finally { await controller.ShutdownAsync(); }
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
