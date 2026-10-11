using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetTape;

internal static class ConfigurationWizard
{
    internal static int Run(string configurationPath, TextReader input, TextWriter output, TextWriter error,
        Func<string, string?>? getKey = null,
        Action<string, string, EnvironmentVariableTarget>? setKey = null,
        Func<TextReader, string?>? readSecret = null)
    {
        getKey ??= name => Environment.GetEnvironmentVariable(name)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        setKey ??= (name, value, target) => Environment.SetEnvironmentVariable(name, value, target);
        readSecret ??= ReadHiddenSecret;

        string path = Path.GetFullPath(configurationPath);
        bool created = ConfigurationBootstrap.Initialize(path, ConfigurationBootstrap.UserMeetingsRoot);
        string originalText = File.ReadAllText(path);
        var settings = ConfigurationLoader.Parse(originalText, path, validatePrompts: false).Settings;
        var json = JsonNode.Parse(originalText)!.AsObject();

        output.WriteLine(created ? $"Created configuration: {path}" : $"Configuring existing file: {path}");
        output.WriteLine("Press Enter to keep a value. Use '-' to select the default audio device.");

        bool changed = false;
        var keysToSave = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Set(json, "meetingsRoot", settings.MeetingsRoot,
                AskString(input, output, "Meetings folder", settings.MeetingsRoot), ref changed);

            var capture = Object(json, "capture");
            Set(capture, "microphoneDeviceId", settings.Capture.MicrophoneDeviceId,
                AskDevice(input, output, "Microphone device ID", settings.Capture.MicrophoneDeviceId), ref changed);
            Set(capture, "playbackDeviceId", settings.Capture.PlaybackDeviceId,
                AskDevice(input, output, "Playback device ID", settings.Capture.PlaybackDeviceId), ref changed);
            string ffmpegPath = capture["ffmpegPath"]?.GetValue<string>() ?? "ffmpeg";
            Set(capture, "ffmpegPath", ffmpegPath,
                AskString(input, output, "FFmpeg executable", ffmpegPath), ref changed);

            var stages = Object(json, "stages");
            Set(stages, "trim", settings.Stages.Trim,
                AskBool(input, output, "Trim silence", settings.Stages.Trim), ref changed);
            bool transcribe = AskBool(input, output, "Enable transcription", settings.Stages.Transcribe);
            Set(stages, "transcribe", settings.Stages.Transcribe, transcribe, ref changed);
            bool summary = AskBool(input, output, "Enable optional summary", settings.Stages.Summary);
            Set(stages, "summary", settings.Stages.Summary, summary, ref changed);
            if (summary && !transcribe)
            {
                error.WriteLine("Summary requires transcription. Configuration was not changed.");
                return 2;
            }

            Set(json, "chunkSeconds", settings.ChunkSeconds,
                AskInt(input, output, "Chunk duration (seconds)", settings.ChunkSeconds, 1, AudioChunker.MaxChunkSeconds), ref changed);

            var transcription = Object(json, "transcription");
            string transcriptionKey = EditApi(input, output, transcription, settings.Transcription, "Transcription", ref changed);
            var summaryApi = Object(json, "summary");
            string summaryKey = EditApi(input, output, summaryApi, settings.Summary, "Summary", ref changed);

            foreach (string variable in new[] { transcriptionKey, summaryKey }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!ValidEnvironmentName(variable))
                {
                    error.WriteLine("Invalid API key environment variable name. Configuration was not changed.");
                    return 2;
                }

                bool configured = !string.IsNullOrWhiteSpace(getKey(variable));
                output.Write($"API key for {variable} [{(configured ? "configured" : "not configured")}; Enter to {(configured ? "keep" : "skip")}]: ");
                string? secret = readSecret(input);
                if (secret is null) throw new OperationCanceledException();
                output.WriteLine();
                if (secret.Length == 0) continue;
                if (secret.Length > 32767 || secret.Any(char.IsControl) || string.IsNullOrWhiteSpace(secret))
                {
                    error.WriteLine("Invalid API key. Configuration and environment were not changed.");
                    return 2;
                }
                keysToSave.Add(variable, secret);
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Configuration canceled. Existing settings and API keys were not changed.");
            return 1;
        }

        string updatedText = changed
            ? json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine
            : originalText;
        try
        {
            _ = ConfigurationLoader.Parse(updatedText, path, validatePrompts: true);
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"Invalid configuration: {ex.Message} Nothing saved.");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("Cannot read an enabled prompt file. Check prompt paths and permissions; nothing saved.");
            return 2;
        }
        // Serialize the final compare, replacement and environment update across
        // configure processes; a comparison alone permits concurrent lost updates.
        string lockName = "Local\\MeetTape.Configure." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, lockName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            error.WriteLine("Configuration is being saved by another process. Try again.");
            return 2;
        }
        try
        {
            if (changed || keysToSave.Count > 0)
            {
                if (!string.Equals(originalText, File.ReadAllText(path), StringComparison.Ordinal))
                {
                    error.WriteLine("Configuration changed during editing. Nothing saved; run configure again.");
                    return 2;
                }
                if (changed) SaveAtomically(path, updatedText);
            }

            foreach (var (variable, key) in keysToSave)
            {
                try
                {
                    setKey(variable, key, EnvironmentVariableTarget.User);
                    setKey(variable, key, EnvironmentVariableTarget.Process);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or NotSupportedException)
                {
                    error.WriteLine("Cannot save API key in the Windows user environment. Configuration settings may have been saved.");
                    return 2;
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        output.WriteLine(changed ? $"Configuration saved: {path}" : $"Configuration unchanged: {path}");
        if (keysToSave.Count > 0)
            output.WriteLine("API key saved to Windows user environment and current process. Open a new terminal for other processes.");
        output.WriteLine("Restart MeetTape to apply changes to a running session.");
        return 0;
    }

    private static string EditApi(TextReader input, TextWriter output, JsonObject node, ApiSettings settings,
        string label, ref bool changed)
    {
        output.WriteLine($"{label} API:");
        Set(node, "endpoint", settings.Endpoint,
            AskString(input, output, "  Endpoint", settings.Endpoint), ref changed);
        Set(node, "model", settings.Model,
            AskString(input, output, "  Model", settings.Model), ref changed);
        string variable = AskString(input, output, "  API key environment variable", settings.ApiKeyEnvironmentVariable);
        Set(node, "apiKeyEnvironmentVariable", settings.ApiKeyEnvironmentVariable, variable, ref changed);
        Set(node, "promptFile", settings.PromptFile,
            AskString(input, output, "  Prompt file", settings.PromptFile), ref changed);
        return variable;
    }

    private static JsonObject Object(JsonObject json, string name)
    {
        if (json[name] is JsonObject obj) return obj;
        obj = new JsonObject();
        json[name] = obj;
        return obj;
    }

    private static void Set<T>(JsonObject json, string name, T oldValue, T newValue, ref bool changed)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;
        json[name] = JsonSerializer.SerializeToNode(newValue);
        changed = true;
    }

    private static string AskString(TextReader input, TextWriter output, string label, string current)
    {
        output.Write($"{label} [{current}]: ");
        string? answer = input.ReadLine() ?? throw new OperationCanceledException();
        return string.IsNullOrWhiteSpace(answer) ? current : answer.Trim();
    }

    private static string? AskDevice(TextReader input, TextWriter output, string label, string? current)
    {
        output.Write($"{label} [{current ?? "Windows default"}]: ");
        string? answer = input.ReadLine() ?? throw new OperationCanceledException();
        if (string.IsNullOrWhiteSpace(answer)) return current;
        return answer.Trim() == "-" ? null : answer.Trim();
    }

    private static bool AskBool(TextReader input, TextWriter output, string label, bool current)
    {
        while (true)
        {
            output.Write($"{label} [{(current ? "y" : "n")}]: ");
            string answer = (input.ReadLine() ?? throw new OperationCanceledException()).Trim();
            if (answer.Length == 0) return current;
            if (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("true", StringComparison.OrdinalIgnoreCase) || answer == "1") return true;
            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("no", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("false", StringComparison.OrdinalIgnoreCase) || answer == "0") return false;
            output.WriteLine("Enter y or n (or press Enter to keep the current value).");
        }
    }

    private static int AskInt(TextReader input, TextWriter output, string label, int current, int min, int max)
    {
        while (true)
        {
            output.Write($"{label} [{current}]: ");
            string answer = (input.ReadLine() ?? throw new OperationCanceledException()).Trim();
            if (answer.Length == 0) return current;
            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && value >= min && value <= max) return value;
            output.WriteLine($"Enter a whole number from {min} to {max}.");
        }
    }

    private static bool ValidEnvironmentName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 32767
        && !name.Contains('=') && !name.Any(char.IsControl);

    private static string? ReadHiddenSecret(TextReader input)
    {
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected)
            return input.ReadLine();

        var value = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException)
            {
                throw new InvalidDataException("Secure API key input is unavailable in this terminal. Use an interactive Windows console.");
            }
            if (key.Key == ConsoleKey.Enter) return value.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                value.Append(key.KeyChar);
        }
    }

    private static void SaveAtomically(string path, string content)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporaryPath, path, null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
