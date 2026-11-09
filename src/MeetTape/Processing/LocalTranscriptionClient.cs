using OriApps.MeetTape.Api;
using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Settings;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OriApps.MeetTape.Processing;

internal sealed class LocalTranscriptionClient
{
    private const int MaximumOutputLength = 2 * 1024 * 1024;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<string>> run;

    internal LocalTranscriptionClient(Func<ProcessStartInfo, CancellationToken, Task<string>>? run = null)
        => this.run = run ?? RunProcessAsync;

    internal static string ModelPath(LocalTranscriptionSettings settings, string configurationDirectory)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelPath))
            throw new InvalidDataException("Choose an offline GGUF speech model in Settings before processing.");
        string path = Path.GetFullPath(settings.ModelPath, configurationDirectory);
        if (!File.Exists(path) || !path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local speech model not found. Choose a GGUF file in Settings.");
        if (new FileInfo(path).Length < 1024)
            throw new InvalidDataException("Local speech model is incomplete. Select a valid GGUF file.");
        return path;
    }

    internal static async Task<string> ModelIdentityAsync(LocalTranscriptionSettings settings,
        string configurationDirectory, CancellationToken token)
    {
        string model = ModelPath(settings, configurationDirectory);
        await using var stream = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        return $"local:transcribe.cpp:{hash}:{settings.Language}";
    }

    internal async Task<TranscriptionReply> TranscribeAsync(LocalTranscriptionSettings settings,
        string configurationDirectory, string ffmpegPath, string audioPath, string? source,
        double duration, double ownStart, CancellationToken token)
    {
        string model = ModelPath(settings, configurationDirectory);
        string runner = settings.RunnerPath is "transcribe-cli.exe" or "transcribe-cli"
            ? Path.Combine(AppContext.BaseDirectory, "transcribe-cli.exe")
            : Path.GetFullPath(settings.RunnerPath, configurationDirectory);
        if (!File.Exists(runner))
            throw new InvalidDataException("Local transcription engine is missing. Reinstall MeetTape or select an executable in Settings.");

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "MeetTape-ASR-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string wav = Path.Combine(temporaryDirectory, "audio.wav");
            string list = Path.Combine(temporaryDirectory, "input.list");
            await AudioTrimmer.RunAsync(ffmpegPath,
                ["-hide_banner", "-nostats", "-nostdin", "-loglevel", "error", "-i", audioPath,
                    "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav], null, token);
            await File.WriteAllTextAsync(list, wav + "\n", new UTF8Encoding(false), token);

            var start = new ProcessStartInfo(runner)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "-m", model, "-l", settings.Language, "--backend", "cpu",
                         "--threads", settings.Threads.ToString(CultureInfo.InvariantCulture),
                         "--timestamps", "auto", "--quiet", "--batch", list, "--batch-jsonl" })
                start.ArgumentList.Add(arg);
            string response = await run(start, token);
            string timedText = FormatJsonl(response, source, duration, ownStart);
            await using var audio = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            string audioHash = Convert.ToHexString(await SHA256.HashDataAsync(audio, token));
            return new(timedText, audioHash);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    internal static string FormatJsonl(string jsonl, string? source, double duration, double ownStart)
    {
        if (jsonl.Length > MaximumOutputLength)
            throw new InvalidDataException("Local transcription response exceeds the supported size.");
        using var lineReader = new StringReader(jsonl);
        string? line;
        JsonElement? result = null;
        while ((line = lineReader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var parsed = JsonDocument.Parse(line);
                var root = parsed.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Local transcription returned an invalid JSON object.");
                if (root.TryGetProperty("type", out var type) && type.GetString() == "batch_header") continue;
                if (result is not null) throw new InvalidDataException("Local transcription returned multiple results for one chunk.");
                result = root.Clone();
            }
            catch (JsonException) { throw new InvalidDataException("Local transcription returned malformed JSON."); }
        }
        if (result is not { } value || !value.TryGetProperty("text", out var words)
            || words.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Local transcription did not return text.");
        if (value.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
            && !(error.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(error.GetString())))
            throw new InvalidDataException("Local transcription was incomplete; model reported an error.");

        string text = Flatten(words.GetString()!);
        if (source is not ("Me" or "Them") || !double.IsFinite(duration) || duration < 0
            || !double.IsFinite(ownStart) || ownStart < 0 || ownStart > duration + 1)
            throw new InvalidDataException("Local transcript has invalid source or chunk duration.");

        if (value.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array
            && segments.GetArrayLength() > 0)
        {
            var output = new StringBuilder();
            long prior = -1;
            bool validSegments = true;
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object
                    || !segment.TryGetProperty("t0_ms", out var start)
                    || start.ValueKind != JsonValueKind.Number || !start.TryGetInt64(out long ms)
                    || ms < 0 || ms < prior || ms > (duration + 1) * 1000
                    || !segment.TryGetProperty("text", out var body) || body.ValueKind != JsonValueKind.String)
                {
                    validSegments = false;
                    break;
                }
                prior = ms;
                string segmentText = Flatten(body.GetString()!);
                if (segmentText.Length == 0) continue;
                if (output.Length > 0) output.Append('\n');
                output.Append('[').Append(Timestamp(ms / 1000d)).Append("] ").Append(source).Append(": ").Append(segmentText);
            }
            if (validSegments && output.Length > 0) return output.ToString();
            if (!validSegments && text.Length == 0)
                throw new InvalidDataException("Local transcription returned invalid timestamps without recoverable speech.");
        }
        if (text.Length == 0) return ChatApiClient.NoSpeechMarker;
        return $"[{Timestamp(ownStart)}] {source}: {text}";
    }

    private static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Timestamp(double seconds)
    {
        long cents = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        long value = cents / 100;
        string clock = value >= 3600
            ? FormattableString.Invariant($"{value / 3600:D2}:{value / 60 % 60:D2}:{value % 60:D2}")
            : FormattableString.Invariant($"{value / 60:D2}:{value % 60:D2}");
        return cents % 100 == 0 ? clock : clock + "." + (cents % 100).ToString("D2", CultureInfo.InvariantCulture);
    }

    private static async Task<string> RunProcessAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidDataException("Unable to start local transcription. Check the engine path in Settings."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, errors);
            if (process.ExitCode != 0)
                throw new InvalidDataException("Local transcription failed or generated an incomplete result. Check model compatibility and available memory.");
            return await output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidDataException("Local transcription exceeded the 10-minute limit for one chunk."); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
