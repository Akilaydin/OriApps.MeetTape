using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit.Abstractions;

namespace MeetTape.Tests;

public sealed class LongHardwareFactAttribute : FactAttribute
{
    public LongHardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEETTAPE_RUN_HARDWARE_TESTS") != "1" ||
            Environment.GetEnvironmentVariable("MEETTAPE_RUN_LONG_TESTS") != "1")
            Skip = "Requires both hardware and long-run opt-in; captures local audio for nearly three hours.";
    }
}

[Collection("Hardware")]
public sealed class LongRecordingTests(ITestOutputHelper output)
{
    [LongHardwareFact]
    [Trait("Category", "Hardware")]
    [Trait("Category", "LongRunning")]
    public void LongCaptureKeepsBoundedResourcesAndEqualDecodedTimelines()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        string meetings = Path.Combine(Path.GetDirectoryName(example)!, "Meetings") + Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEETTAPE_LONG_ROOT") ??
            Path.Combine(meetings, ".validation-long-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(meetings, root);
        Assert.StartsWith(".validation-long-", Path.GetFileName(root));
        Assert.False(File.Exists(Path.Combine(root, "stop.request")), "Stale stop.request: choose a fresh validation directory or remove the old marker before running.");
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MEETTAPE_LONG_SECONDS"), out int configured) ? configured : 10795;
        Assert.InRange(seconds, 1, 10795);
        Assert.True(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace > 1024L * 1024 * 1024, "Require 1 GiB free disk for long capture.");
        Directory.CreateDirectory(root);
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = root,
            Stages = new() { Trim = false, Transcribe = false, Summary = false } } };
        using var capture = new Mp3Recorder(config).Capture;
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var current = Process.GetCurrentProcess();
        var samples = new List<object>();
        var encoders = new List<Process>();
        WasapiOut? player = null;
        var watch = new Stopwatch();
        string? failure = null;
        long? meFrames = null, themFrames = null;
        uint previousExecution = SetThreadExecutionState(0x80000001); // This synchronous test restores the same thread's state.
        try
        {
            Assert.NotEqual(0u, previousExecution);
            capture.Start(); watch.Start();
            encoders = EncoderProcesses(current.Id);
            Assert.Equal(2, encoders.Count);
            long nextSample = 0;
            int lastCycle = -1;
            while (watch.Elapsed.TotalSeconds < seconds && capture.Status.IsActive && !File.Exists(Path.Combine(root, "stop.request")))
            {
                int cycle = (int)(watch.Elapsed.TotalSeconds / 600);
                bool play = watch.Elapsed.TotalSeconds % 600 < 3;
                if (play && player is null && cycle != lastCycle)
                {
                    player = new WasapiOut(render, AudioClientShareMode.Shared, false, 100);
                    player.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.02 }.ToWaveProvider());
                    player.Play(); lastCycle = cycle;
                }
                if (!play && player is not null) { player.Stop(); player.Dispose(); player = null; }
                if (watch.Elapsed.TotalSeconds >= nextSample)
                {
                    current.Refresh();
                    foreach (var encoder in encoders) encoder.Refresh();
                    var sample = new { elapsedSeconds = watch.Elapsed.TotalSeconds, managedBytes = GC.GetTotalMemory(false),
                        workingSetBytes = current.WorkingSet64, privateBytes = current.PrivateMemorySize64,
                        handles = current.HandleCount, threads = current.Threads.Count, cpuSeconds = current.TotalProcessorTime.TotalSeconds,
                        encoderWorkingSetBytes = encoders.Sum(p => p.WorkingSet64), encoderPrivateBytes = encoders.Sum(p => p.PrivateMemorySize64),
                        encoderCpuSeconds = encoders.Sum(p => p.TotalProcessorTime.TotalSeconds),
                        diskBytes = new FileInfo(Path.Combine(capture.RecordingDirectory!, "me.mp3")).Length +
                            new FileInfo(Path.Combine(capture.RecordingDirectory!, "them.mp3")).Length };
                    samples.Add(sample);
                    File.WriteAllText(Path.Combine(root, "progress.json"), JsonSerializer.Serialize(sample));
                    File.AppendAllText(Path.Combine(root, "metrics.jsonl"), JsonSerializer.Serialize(sample) + Environment.NewLine);
                    nextSample += 60;
                    Assert.True(current.PrivateMemorySize64 < 512L * 1024 * 1024, "Capture private memory must stay below 512 MiB.");
                    Assert.True(sample.encoderPrivateBytes < 512L * 1024 * 1024, "Combined encoder private memory must stay below 512 MiB.");
                }
                Thread.Sleep(500);
            }
            player?.Stop(); player?.Dispose(); player = null;
            double elapsedAtStop = watch.Elapsed.TotalSeconds;
            if (capture.Status.IsActive) capture.Stop();
            Assert.Null(capture.Status.Error);
            double expectedSeconds = File.Exists(Path.Combine(root, "stop.request")) ? elapsedAtStop : seconds;
            Assert.InRange(capture.Status.Duration.TotalSeconds, expectedSeconds, expectedSeconds + 5);
            meFrames = CountDecoded(Path.Combine(capture.RecordingDirectory!, "me.mp3"));
            themFrames = CountDecoded(Path.Combine(capture.RecordingDirectory!, "them.mp3"));
            Assert.Equal(meFrames, themFrames);
            Assert.InRange(Math.Abs(meFrames.Value / 48000d - capture.Status.Duration.TotalSeconds), 0, 1d / 48000);
            output.WriteLine($"Long capture: {capture.Status.Duration.TotalSeconds:F3}s; decoded frames={meFrames}; samples={samples.Count}; artifacts={root}");
        }
        catch (Exception ex) { failure = capture.Status.Error ?? ex.GetType().Name; throw; }
        finally
        {
            player?.Stop(); player?.Dispose();
            capture.Dispose();
            foreach (var encoder in encoders) encoder.Dispose();
            SetThreadExecutionState(previousExecution);
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            { requestedSeconds = seconds, actualSeconds = capture.Status.Duration.TotalSeconds, error = failure,
                stopRequested = File.Exists(Path.Combine(root, "stop.request")),
                directory = capture.RecordingDirectory, meFrames, themFrames, samples }));
        }
    }

    private static List<Process> EncoderProcesses(int parent)
    {
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"Get-CimInstance Win32_Process -Filter \"ParentProcessId={parent} AND Name='ffmpeg.exe'\" | Select-Object -ExpandProperty ProcessId");
        using var query = Process.Start(start)!;
        var result = query.StandardOutput.ReadToEndAsync(); var errors = query.StandardError.ReadToEndAsync();
        if (!query.WaitForExit(10000)) { query.Kill(true); throw new TimeoutException("Encoder process query timed out."); }
        Assert.Equal(0, query.ExitCode); Assert.Empty(errors.GetAwaiter().GetResult());
        return result.GetAwaiter().GetResult().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(id => Process.GetProcessById(int.Parse(id, CultureInfo.InvariantCulture))).ToList();
    }

    private static long CountDecoded(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-hide_banner", "-nostats", "-nostdin", "-i", path, "-af", "astats=metadata=0:reset=0", "-f", "null", "-" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var discard = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        long count = -1;
        try
        {
            while (process.StandardError.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() is { } line)
            {
                const string marker = "Number of samples:";
                int at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0 && double.TryParse(line[(at + marker.Length)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double samples))
                    count = (long)Math.Round(samples);
            }
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            discard.GetAwaiter().GetResult();
            Assert.Equal(0, process.ExitCode); Assert.True(count > 0);
            return count;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
