using OriApps.MeetTape.Api;
using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Sessions;
using OriApps.MeetTape.Settings;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace OriApps.MeetTape.Processing;

internal sealed class LocalTranscriptionClient
{
    private const int MaximumOutputLength = 2 * 1024 * 1024;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<string>> run;
    private readonly Func<ProcessStartInfo, Func<string, Task>, CancellationToken, Task> runBatch;

    internal LocalTranscriptionClient(Func<ProcessStartInfo, CancellationToken, Task<string>>? run = null,
        Func<ProcessStartInfo, Func<string, Task>, CancellationToken, Task>? runBatch = null)
    {
        this.run = run ?? RunProcessAsync;
        this.runBatch = runBatch ?? RunBatchProcessAsync;
    }

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

    internal sealed record BatchInput(AudioChunk Chunk, string AudioPath);

    internal async Task TranscribeBatchAsync(LocalTranscriptionSettings settings,
        string configurationDirectory, IReadOnlyList<BatchInput> inputs,
        Func<AudioChunk, TranscriptionReply, Task> onResult, CancellationToken token)
    {
        if (inputs.Count == 0) return;
        string model = ModelPath(settings, configurationDirectory);
        string runner = RunnerPath(settings, configurationDirectory);
        string folder = CreateBatchDirectory(Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        try
        {
            foreach (var input in inputs)
                if (input.Chunk.Source is not ("Me" or "Them") || !input.AudioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(input.AudioPath))
                    throw new InvalidDataException("Local batch contains an invalid or missing WAV source.");

            // Native CLI file opening does not handle Unicode Windows paths. Hardlinks avoid extra copies on NTFS.
            var aliases = new List<string>(inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string alias = Path.Combine(folder, $"{i + 1:D4}.wav");
                if (!CreateHardLink(alias, inputs[i].AudioPath, IntPtr.Zero))
                {
                    await using var source = new FileStream(inputs[i].AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await using var target = new FileStream(alias, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await source.CopyToAsync(target, token);
                    await target.FlushAsync(token);
                }
                aliases.Add(alias);
            }
            if (model.Any(c => c > 127))
            {
                string modelAlias = Path.Combine(folder, "model.gguf");
                if (!CreateHardLink(modelAlias, model, IntPtr.Zero))
                {
                    await using var source = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await using var target = new FileStream(modelAlias, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await source.CopyToAsync(target, token);
                    await target.FlushAsync(token);
                }
                model = modelAlias;
            }
            string manifest = Path.Combine(folder, "batch.list");
            await File.WriteAllLinesAsync(manifest, aliases, new UTF8Encoding(false), token);
            var start = new ProcessStartInfo(runner)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in new[] { "-m", model, "-l", settings.Language, "--backend", "cpu",
                         "--threads", settings.Threads.ToString(CultureInfo.InvariantCulture),
                         "--timestamps", "auto", "--quiet", "--batch", manifest, "--batch-jsonl" })
                start.ArgumentList.Add(argument);

            int next = 0;
            bool seenHeader = false;
            await runBatch(start, async line =>
            {
                token.ThrowIfCancellationRequested();
                if (line.Length > MaximumOutputLength)
                    throw new InvalidDataException("Local batch returned an oversized JSON line.");
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException) { throw new InvalidDataException("Local batch returned malformed JSON."); }
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Local batch returned an invalid JSON value.");
                if (root.TryGetProperty("type", out var type) && type.GetString() == "batch_header")
                {
                    if (seenHeader || next != 0)
                        throw new InvalidDataException("Local batch returned a duplicate or late header.");
                    seenHeader = true;
                    return;
                }
                if (next >= inputs.Count || !root.TryGetProperty("file", out var file)
                    || file.ValueKind != JsonValueKind.String
                    || !string.Equals(file.GetString(), aliases[next], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Local batch returned a duplicate or mismatched audio result.");
                var input = inputs[next];
                double duration = (input.Chunk.EndFrame - input.Chunk.StartFrame) / (double)AudioChunker.SampleRate;
                double ownStart = (input.Chunk.OwnStartFrame - input.Chunk.StartFrame) / (double)AudioChunker.SampleRate;
                string text = FormatJsonl(line, input.Chunk.Source, duration, ownStart);
                await using var stream = new FileStream(input.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                string audioHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                await onResult(input.Chunk, new TranscriptionReply(text, audioHash));
                next++;
            }, token);
            if (next != inputs.Count)
                throw new InvalidDataException($"Local batch ended after {next}/{inputs.Count} chunks; saved results retained.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    internal static string CreateBatchDirectory(string temporaryRoot, string commonDataRoot,
        Func<string, string?>? shortPath = null)
    {
        string suffix = "MeetTape-Batch-" + Guid.NewGuid().ToString("N");
        if (IsAscii(temporaryRoot))
        {
            string folder = Path.Combine(temporaryRoot, suffix);
            Directory.CreateDirectory(folder);
            return folder;
        }

        string? shortRoot = (shortPath ?? GetShortPath)(temporaryRoot);
        if (shortRoot is { Length: > 0 } && IsAscii(shortRoot) && Directory.Exists(shortRoot))
        {
            string folder = Path.Combine(shortRoot, suffix);
            Directory.CreateDirectory(folder);
            return folder;
        }

        if (!IsAscii(commonDataRoot))
            throw new InvalidDataException("No ASCII temporary directory is available for local ASR.");
        string fallback = Path.Combine(commonDataRoot, suffix);
        try
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidDataException("Cannot determine the Windows user for local ASR.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier identity in new[]
                     {
                         owner, new(WellKnownSidType.LocalSystemSid, null),
                         new(WellKnownSidType.BuiltinAdministratorsSid, null)
                     })
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(fallback).Create(security);
            return fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or PlatformNotSupportedException)
        {
            throw new InvalidDataException("Cannot create a private ASCII directory for local ASR. "
                + "Configure a writable ASCII TEMP path or check ProgramData permissions.", ex);
        }
    }

    private static bool IsAscii(string path) => path.All(c => c <= 127);

    private static string? GetShortPath(string longPath)
    {
        var buffer = new StringBuilder(32768);
        uint count = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
        return count > 0 && count < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint length);

    internal static string RunnerPath(LocalTranscriptionSettings settings, string configurationDirectory)
    {
        string runner = settings.RunnerPath is "transcribe-cli.exe" or "transcribe-cli"
            ? Path.Combine(AppContext.BaseDirectory, "transcribe-cli.exe")
            : Path.GetFullPath(settings.RunnerPath, configurationDirectory);
        if (!File.Exists(runner))
            throw new InvalidDataException("Local transcription engine is missing. Check Settings or reinstall MeetTape.");
        return runner;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string linkPath, string existingPath, IntPtr securityAttributes);

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

    private static async Task RunBatchProcessAsync(ProcessStartInfo start,
        Func<string, Task> onLine, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidDataException("Cannot start local batch engine; verify the executable in Settings."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(deadline.Token)) is not null)
                if (!string.IsNullOrWhiteSpace(line)) await onLine(line);
            await process.WaitForExitAsync(deadline.Token);
            await stderr;
            if (process.ExitCode != 0)
                throw new InvalidDataException("Local batch engine stopped with an error; completed checkpoints were retained.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidDataException("Local batch exceeded its two-hour processing deadline."); }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception) { }
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
        }
    }
}
