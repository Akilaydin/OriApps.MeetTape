using System.Diagnostics;
using System.Globalization;

namespace MeetTape;

public sealed record TrimSuggestion(double StartSeconds, double EndSeconds, bool HasActivity);
public sealed class AudioPreparationShutdownException(string message) : InvalidOperationException(message);

public sealed class AudioTrimmer(string ffmpegPath)
{
    private const int SampleRate = 48000;

    public async Task<TrimSuggestion> SuggestAsync(string directory, double duration, CancellationToken cancellationToken)
    {
        var me = await DetectAsync(Path.Combine(directory, "me.mp3"), duration, cancellationToken);
        var them = await DetectAsync(Path.Combine(directory, "them.mp3"), duration, cancellationToken);
        bool activity = me.HasActivity || them.HasActivity;
        // Keep a small margin around detected activity; detection is an amplitude heuristic, not ASR.
        return activity ? new(Math.Max(0, Math.Min(me.StartSeconds, them.StartSeconds) - 0.1),
            Math.Min(duration, Math.Max(me.EndSeconds, them.EndSeconds) + 0.1), true) : new(0, duration, false);
    }

    private async Task<TrimSuggestion> DetectAsync(string path, double duration, CancellationToken cancellationToken)
    {
        double first = 0, last = duration;
        double? silenceStart = null;
        bool seenSilence = false;
        await RunAsync(ffmpegPath, ["-hide_banner", "-nostats", "-nostdin", "-i", path, "-af",
            "asetpts=PTS-STARTPTS,silencedetect=noise=-45dB:d=0.5", "-f", "null", "-"], line =>
        {
            if (NumberAfter(line, "silence_start:") is { } start)
            {
                silenceStart = Math.Clamp(start, 0, duration);
                seenSilence = true;
            }
            if (NumberAfter(line, "silence_end:") is { } end && silenceStart is { } began)
            {
                end = Math.Clamp(end, 0, duration);
                if (began <= 1d / SampleRate && first == 0) first = end;
                last = duration - end <= 2d / SampleRate ? began : duration;
                silenceStart = null;
            }
        }, cancellationToken);
        // Some FFmpeg versions report only silence_start for the final silent interval.
        if (silenceStart is { } trailing)
        {
            last = trailing;
            if (trailing <= 1d / SampleRate) first = duration;
        }
        bool active = !seenSilence || first < last;
        return active ? new(first, last, true) : new(duration, 0, false);
    }

    public async Task<TrimParameters> ApplyAsync(string directory, double start, double end, double duration,
        string decision, CancellationToken cancellationToken)
    {
        Validate(start, end, duration);
        long first = (long)Math.Round(start * SampleRate), limit = (long)Math.Round(end * SampleRate);
        if (limit <= first) throw new InvalidOperationException("Trim interval must include at least one sample.");
        string relative = Path.Combine("processed", "trim_" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, relative);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (string name in new[] { "me.mp3", "them.mp3" })
        {
            string target = Path.Combine(destination, name);
            string filter = FormattableString.Invariant($"atrim=start_sample={first}:end_sample={limit},asetpts=PTS-STARTPTS");
            await RunAsync(ffmpegPath, ["-hide_banner", "-nostats", "-nostdin", "-v", "error", "-n", "-i",
                Path.Combine(directory, name), "-af", filter, "-ar", "48000", "-ac", "1", "-c:a", "libmp3lame",
                "-b:a", "96k", "-write_xing", "1", target], null, cancellationToken);
            using var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length == 0) throw new RecordingException("Trim output is empty; originals retained.");
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(first / (double)SampleRate, limit / (double)SampleRate, true)
        { Decision = decision, RelativeDirectory = relative.Replace('\\', '/') };
    }

    public static void Validate(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(duration)
            || start < 0 || start >= end || end > duration || duration <= 0 || duration > 10800)
            throw new InvalidOperationException("Invalid trim boundaries: require 0 <= start < end <= recording duration (up to 3 hours).");
    }

    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        string[] parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            var style = i == parts.Length - 1 ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out double part) || !double.IsFinite(part)
                || part < 0 || i > 0 && part >= 60) return false;
            seconds = seconds * 60 + part;
        }
        return double.IsFinite(seconds);
    }

    private static double? NumberAfter(string line, string marker)
    {
        int position = line.IndexOf(marker, StringComparison.Ordinal);
        if (position < 0) return null;
        ReadOnlySpan<char> number = line.AsSpan(position + marker.Length).TrimStart();
        int separator = number.IndexOfAny(' ', '|');
        if (separator >= 0) number = number[..separator];
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) ? value : null;
    }

    internal static async Task RunAsync(string ffmpegPath, string[] arguments, Action<string>? inspectLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(ffmpegPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppDiagnostics.Error("ffmpeg", "Start preparation", ex);
            throw new RecordingException(AppDiagnostics.UserMessage(ex, "Cannot start FFmpeg; check capture.ffmpegPath. Originals retained."));
        }
        AppDiagnostics.Info("ffmpeg", "Preparation started");
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        var details = new System.Text.StringBuilder();
        var errors = ReadLinesAsync(process.StandardError, line =>
        {
            inspectLine?.Invoke(line);
            if (details.Length < 4096) details.AppendLine(line[..Math.Min(line.Length, 4096 - details.Length)]);
        }, cancellationToken);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), output, errors);
            if (process.ExitCode != 0)
            {
                AppDiagnostics.Error("ffmpeg", $"Preparation exit {process.ExitCode}", new IOException(details.ToString()));
                throw new RecordingException("FFmpeg audio preparation failed; check source files, permissions and free space. Originals retained.");
            }
            AppDiagnostics.Info("ffmpeg", "Preparation completed");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
            { AppDiagnostics.Error("ffmpeg", "Join preparation", ex); throw new AudioPreparationShutdownException("Cannot confirm FFmpeg stopped; capture remains blocked. Close the stalled FFmpeg process before restarting MeetTape."); }
            // Observe canceled/faulted readers before disposing their streams.
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            { throw new AudioPreparationShutdownException("FFmpeg readers did not finish; capture remains blocked. Restart MeetTape after checking its FFmpeg processes."); }
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string>? inspectLine, CancellationToken token)
    {
        // Bounded even if a malformed input/protocol emits an enormous stderr line.
        char[] buffer = new char[1024];
        var line = new System.Text.StringBuilder(512);
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] is '\n' or '\r')
                {
                    if (line.Length > 0) inspectLine?.Invoke(line.ToString());
                    line.Clear();
                }
                else if (line.Length < 2048) line.Append(buffer[i]);
            }
        }
        if (line.Length > 0) inspectLine?.Invoke(line.ToString());
    }
}
