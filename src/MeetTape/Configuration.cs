using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetTape;

public sealed record Configuration
{
    public required string MeetingsRoot { get; init; }
    public CaptureSettings Capture { get; init; } = new();
    public string RecordingHotkey { get; init; } = MeetTape.RecordingHotkey.DefaultShortcut;
    public StageSettings Stages { get; init; } = new();
    public int ChunkSeconds { get; init; } = 120;
    public required ApiSettings Transcription { get; init; }
    public required ApiSettings Summary { get; init; }
}

public sealed record CaptureSettings
{
    public string? MicrophoneDeviceId { get; init; }
    public string? PlaybackDeviceId { get; init; }
    public string FfmpegPath { get; init; } = "ffmpeg";
}

public sealed record StageSettings
{
    public bool Trim { get; init; } = true;
    public bool Transcribe { get; init; } = true;
    public bool Summary { get; init; }
}

public sealed record ApiSettings
{
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public string ApiKeyEnvironmentVariable { get; init; } = "MEETTAPE_API_KEY";
    public required string PromptFile { get; init; }
}

public sealed record LoadedConfiguration(Configuration Settings, string Directory)
{
    public string MeetingsRoot => Resolve(Settings.MeetingsRoot);
    public string Resolve(string path) => Path.GetFullPath(path, Directory);
    public string ReadPrompt(ApiSettings api, string stage = "API") => ReadPrompt(api, stage, null);

    internal string ReadPrompt(ApiSettings api, string stage, IReadOnlyDictionary<string, string>? edits)
    {
        string prompt;
        try
        {
            string path = Resolve(api.PromptFile);
            prompt = edits is not null && edits.TryGetValue(path, out string? edited) ? edited : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidDataException($"The {stage} prompt file was not found. Check its path in Settings."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException($"Cannot read the {stage} prompt file. Check its path and permissions in Settings."); }
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidDataException($"The {stage} prompt file is empty. Add instructions to the file configured in Settings.");
        return prompt;
    }
}

public static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true
    };

    public static LoadedConfiguration Load(string path) =>
        Parse(File.ReadAllText(Path.GetFullPath(path)), path, validatePrompts: true);

    internal static LoadedConfiguration LoadForCapture(string path) =>
        Parse(File.ReadAllText(Path.GetFullPath(path)), path, validatePrompts: false);

    internal static LoadedConfiguration Parse(string json, string path, bool validatePrompts,
        IReadOnlyDictionary<string, string>? promptEdits = null)
    {
        string fullPath = Path.GetFullPath(path);
        Configuration settings;
        try
        {
            settings = JsonSerializer.Deserialize<Configuration>(json, Options)
                ?? throw new InvalidDataException("Configuration must be a JSON object.");
        }
        catch (JsonException)
        {
            // JSON exception messages can contain user-supplied secrets or content.
            throw new InvalidDataException("Invalid configuration JSON: check required fields, types and unknown properties.");
        }

        if (string.IsNullOrWhiteSpace(settings.MeetingsRoot))
            throw new InvalidDataException("meetingsRoot is required.");
        if (settings.ChunkSeconds <= 0 || settings.ChunkSeconds > AudioChunker.MaxChunkSeconds)
            throw new InvalidDataException($"chunkSeconds must be between 1 and {AudioChunker.MaxChunkSeconds} to stay within the 8 MiB stereo MP3 API limit; recommended 60-180.");
        if (settings.Capture is null || settings.Stages is null)
            throw new InvalidDataException("capture and stages must not be null.");
        if (settings.Stages.Summary && !settings.Stages.Transcribe)
            throw new InvalidDataException("stages.summary requires stages.transcribe to be enabled.");
        if (string.IsNullOrWhiteSpace(settings.Capture.FfmpegPath))
            throw new InvalidDataException("capture.ffmpegPath is required.");
        _ = RecordingHotkey.Parse(settings.RecordingHotkey);
        ValidateApi(settings.Transcription, "transcription");
        ValidateApi(settings.Summary, "summary");

        settings = settings with { Capture = settings.Capture with
        {
            FfmpegPath = ResolveFfmpegPath(settings.Capture.FfmpegPath, AppContext.BaseDirectory)
        } };
        var loaded = new LoadedConfiguration(settings, Path.GetDirectoryName(fullPath)!);
        _ = loaded.MeetingsRoot;
        if (validatePrompts)
        {
            if (settings.Stages.Transcribe)
                _ = loaded.ReadPrompt(settings.Transcription, "transcription", promptEdits);
            if (settings.Stages.Summary)
                _ = loaded.ReadPrompt(settings.Summary, "summary", promptEdits);
        }
        return loaded;
    }

    internal static string ResolveFfmpegPath(string configured, string executableDirectory)
    {
        if (configured.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            configured.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            string adjacent = Path.Combine(executableDirectory, "ffmpeg.exe");
            if (File.Exists(adjacent)) return adjacent;
        }
        return configured;
    }

    internal static void ValidateApi(ApiSettings api, string name)
    {
        if (api is null) throw new InvalidDataException($"{name} settings are required.");
        if (string.IsNullOrWhiteSpace(api.Model)) throw new InvalidDataException($"{name}.model is required.");
        if (string.IsNullOrWhiteSpace(api.PromptFile)) throw new InvalidDataException($"{name}.promptFile is required.");
        if (!ApiKeyProvider.ValidEnvironmentName(api.ApiKeyEnvironmentVariable))
            throw new InvalidDataException($"{name}.apiKeyEnvironmentVariable is missing or invalid.");
        if (!Uri.TryCreate(api.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidDataException($"{name}.endpoint must use HTTPS (HTTP allowed only for localhost), without credentials, query or fragment.");
    }
}

public static class ApiKeyProvider
{
    public static string Get(ApiSettings settings, Func<string, EnvironmentVariableTarget, string?>? readEnvironment = null)
    {
        string variable = settings.ApiKeyEnvironmentVariable;
        if (!ValidEnvironmentName(variable)) throw new InvalidDataException("API key environment variable name is missing or invalid.");
        readEnvironment ??= Environment.GetEnvironmentVariable;
        string? key;
        try
        {
            key = readEnvironment(variable, EnvironmentVariableTarget.Process);
            if (string.IsNullOrWhiteSpace(key)) key = readEnvironment(variable, EnvironmentVariableTarget.User);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { throw new InvalidDataException($"Cannot read API key environment variable '{variable}'. Check the key in Settings."); }
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new InvalidDataException($"API key environment variable '{variable}' is missing, empty or invalid. Set the key in Settings.");
        return key;
    }

    internal static bool ValidEnvironmentName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 32767 && !name.Contains('=') && !name.Any(char.IsControl);
}
