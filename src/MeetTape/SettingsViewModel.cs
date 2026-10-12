using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetTape;

public sealed record DeviceChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly string original;
    private readonly Action<string, string, EnvironmentVariableTarget>? setKey;
    public string ConfigurationPath { get; }
    public IReadOnlyList<DeviceChoice> Microphones { get; }
    public IReadOnlyList<DeviceChoice> PlaybackDevices { get; }
    public DeviceChoice Microphone { get; set; }
    public DeviceChoice Playback { get; set; }
    public string MeetingsRoot { get; set; }
    public string FfmpegPath { get; set; }
    public bool Trim { get; set; }
    public bool Transcribe { get; set; }
    public bool Summary { get; set; }
    public string ChunkSeconds { get; set; }
    public string TranscriptionEndpoint { get; set; }
    public string TranscriptionModel { get; set; }
    public string TranscriptionPrompt { get; set; }
    public string TranscriptionKeyVariable { get; set; }
    public string SummaryEndpoint { get; set; }
    public string SummaryModel { get; set; }
    public string SummaryPrompt { get; set; }
    public string SummaryKeyVariable { get; set; }
    // Only newly entered secrets live here. Existing keys are never loaded into controls.
    public string NewTranscriptionKey { get; set; } = "";
    public string NewSummaryKey { get; set; } = "";
    public string Message { get; private set; } = "Restart MeetTape after saving. Empty key fields keep existing keys.";
    public bool CanSave { get; private set; } = true;
    public bool Saved { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(string path, IReadOnlyList<AudioDevice> devices,
        Action<string, string, EnvironmentVariableTarget>? setKey = null)
    {
        ConfigurationPath = Path.GetFullPath(path);
        this.setKey = setKey;
        original = File.ReadAllText(ConfigurationPath);
        var settings = ConfigurationLoader.Parse(original, ConfigurationPath, validatePrompts: false).Settings;
        var json = JsonNode.Parse(original)!;
        MeetingsRoot = settings.MeetingsRoot;
        FfmpegPath = json["capture"]?["ffmpegPath"]?.GetValue<string>() ?? "ffmpeg";
        Trim = settings.Stages.Trim; Transcribe = settings.Stages.Transcribe; Summary = settings.Stages.Summary;
        ChunkSeconds = settings.ChunkSeconds.ToString(CultureInfo.InvariantCulture);
        TranscriptionEndpoint = settings.Transcription.Endpoint; TranscriptionModel = settings.Transcription.Model;
        TranscriptionPrompt = settings.Transcription.PromptFile; TranscriptionKeyVariable = settings.Transcription.ApiKeyEnvironmentVariable;
        SummaryEndpoint = settings.Summary.Endpoint; SummaryModel = settings.Summary.Model;
        SummaryPrompt = settings.Summary.PromptFile; SummaryKeyVariable = settings.Summary.ApiKeyEnvironmentVariable;
        Microphones = Choices(devices, CaptureChannel.Me, settings.Capture.MicrophoneDeviceId);
        PlaybackDevices = Choices(devices, CaptureChannel.Them, settings.Capture.PlaybackDeviceId);
        Microphone = Microphones.Single(d => d.Id == settings.Capture.MicrophoneDeviceId);
        Playback = PlaybackDevices.Single(d => d.Id == settings.Capture.PlaybackDeviceId);
    }

    private static IReadOnlyList<DeviceChoice> Choices(IReadOnlyList<AudioDevice> devices, CaptureChannel channel, string? selected)
    {
        var choices = new List<DeviceChoice> { new(null, "Windows default") };
        choices.AddRange(devices.Where(d => d.Channel == channel).Select(d => new DeviceChoice(d.Id, d.Name + (d.IsDefault ? " (default)" : ""))));
        if (selected is not null && choices.All(d => d.Id != selected))
            choices.Add(new(selected, "Configured device (currently unavailable)"));
        return choices;
    }

    public async Task SaveAsync()
    {
        if (!CanSave) return;
        CanSave = false; Changed();
        try
        {
            if (!int.TryParse(ChunkSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                throw new InvalidDataException("Chunk duration must be a whole number between 1 and 340.");
            var json = JsonNode.Parse(original)!.AsObject();
            json["meetingsRoot"] = MeetingsRoot.Trim();
            json["capture"] ??= new JsonObject();
            json["capture"]!["microphoneDeviceId"] = Microphone.Id;
            json["capture"]!["playbackDeviceId"] = Playback.Id;
            json["capture"]!["ffmpegPath"] = FfmpegPath.Trim();
            json["stages"] = new JsonObject { ["trim"] = Trim, ["transcribe"] = Transcribe, ["summary"] = Summary };
            json["chunkSeconds"] = seconds;
            json["transcription"] = Api(TranscriptionEndpoint, TranscriptionModel, TranscriptionPrompt, TranscriptionKeyVariable);
            json["summary"] = Api(SummaryEndpoint, SummaryModel, SummaryPrompt, SummaryKeyVariable);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddKey(keys, TranscriptionKeyVariable.Trim(), NewTranscriptionKey);
            AddKey(keys, SummaryKeyVariable.Trim(), NewSummaryKey);
            string updated = JsonNode.DeepEquals(json, JsonNode.Parse(original)) ? original :
                json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + (original.EndsWith('\n') ? Environment.NewLine : "");
            string? error = await Task.Run(() => ConfigurationStore.Save(ConfigurationPath, original, updated, keys, setKey));
            Saved = error is null;
            Message = error ?? "Saved. Restart MeetTape to apply settings. User prompts retained.";
        }
        catch (InvalidDataException ex) { Message = ex.Message; }
        catch (Exception) { Message = "Cannot save settings. Check paths and permissions; secrets are not logged."; }
        finally { ClearSecrets(); CanSave = !Saved; Changed(); }
    }

    private static JsonObject Api(string endpoint, string model, string prompt, string variable) => new()
    {
        ["endpoint"] = endpoint.Trim(), ["model"] = model.Trim(), ["promptFile"] = prompt.Trim(),
        ["apiKeyEnvironmentVariable"] = variable.Trim()
    };

    private static void AddKey(Dictionary<string, string> keys, string variable, string key)
    {
        if (key.Length == 0) return;
        if (keys.TryGetValue(variable, out string? existing) && existing != key)
            throw new InvalidDataException("Both API stages use the same environment variable; enter one matching key.");
        keys[variable] = key;
    }

    public void ClearSecrets() { NewTranscriptionKey = ""; NewSummaryKey = ""; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
}
