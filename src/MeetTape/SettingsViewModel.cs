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
    private string original;
    private string originalRecordingHotkey;
    private string[] originalFields;
    private PromptDocument transcriptionDocument;
    private PromptDocument summaryDocument;
    private readonly Action<string, string, EnvironmentVariableTarget>? setKey;
    private readonly Func<LoadedConfiguration, Task>? applySettings;
    private readonly Func<string, Action<bool>>? prepareHotkey;
    public string ConfigurationPath { get; }
    public IReadOnlyList<DeviceChoice> Microphones { get; }
    public IReadOnlyList<DeviceChoice> PlaybackDevices { get; }
    public DeviceChoice Microphone { get; set; }
    public DeviceChoice Playback { get; set; }
    public string MeetingsRoot { get; set; }
    public string FfmpegPath { get; set; }
    public string RecordingHotkey { get; set; }
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
    public string TranscriptionPromptText { get => transcriptionDocument.Text; set { transcriptionDocument.Text = value ?? ""; Changed(); } }
    public string SummaryPromptText { get => summaryDocument.Text; set { summaryDocument.Text = value ?? ""; Changed(); } }
    public string TranscriptionPromptError => transcriptionDocument.Error;
    public string SummaryPromptError => summaryDocument.Error;
    public bool HasUnsavedChanges => !Fields().SequenceEqual(originalFields) || NewTranscriptionKey.Length > 0 || NewSummaryKey.Length > 0
        || transcriptionDocument.IsDirty || summaryDocument.IsDirty;
    // Only newly entered secrets live here. Existing keys are never loaded into controls.
    public string NewTranscriptionKey { get; set; } = "";
    public string NewSummaryKey { get; set; } = "";
    public string Message { get; private set; } = "Changes apply to new recordings and explicit Resume. Empty key fields keep existing keys.";
    public bool CanSave { get; private set; } = true;
    public bool Saved { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(string path, IReadOnlyList<AudioDevice> devices,
        Action<string, string, EnvironmentVariableTarget>? setKey = null,
        Func<LoadedConfiguration, Task>? applySettings = null, Func<string, Action<bool>>? prepareHotkey = null)
    {
        ConfigurationPath = Path.GetFullPath(path);
        this.setKey = setKey;
        this.applySettings = applySettings;
        this.prepareHotkey = prepareHotkey;
        original = File.ReadAllText(ConfigurationPath);
        var settings = ConfigurationLoader.Parse(original, ConfigurationPath, validatePrompts: false).Settings;
        var json = JsonNode.Parse(original)!;
        MeetingsRoot = settings.MeetingsRoot;
        FfmpegPath = json["capture"]?["ffmpegPath"]?.GetValue<string>() ?? "ffmpeg";
        RecordingHotkey = MeetTape.RecordingHotkey.Parse(settings.RecordingHotkey).Text;
        originalRecordingHotkey = RecordingHotkey;
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
        string directory = Path.GetDirectoryName(ConfigurationPath)!;
        transcriptionDocument = new(Path.GetFullPath(TranscriptionPrompt, directory), "transcription");
        summaryDocument = new(Path.GetFullPath(SummaryPrompt, directory), "summary");
        originalFields = Fields();
    }

    private string[] Fields() => [Microphone.Id ?? "", Playback.Id ?? "", MeetingsRoot, FfmpegPath,
        Trim.ToString(), Transcribe.ToString(), Summary.ToString(), ChunkSeconds, RecordingHotkey,
        TranscriptionEndpoint, TranscriptionModel, TranscriptionPrompt, TranscriptionKeyVariable,
        SummaryEndpoint, SummaryModel, SummaryPrompt, SummaryKeyVariable];

    public void LoadTranscriptionPrompt() => ReloadPrompt(ref transcriptionDocument, TranscriptionPrompt);
    public void LoadSummaryPrompt() => ReloadPrompt(ref summaryDocument, SummaryPrompt);
    private void ReloadPrompt(ref PromptDocument document, string path)
    {
        if (!CanSave) return;
        if (document.IsDirty)
        { Message = "Unsaved prompt edits retained. Save them, or Close and explicitly Discard before loading a different file."; Changed(); return; }
        try { document = new(Path.GetFullPath(path, Path.GetDirectoryName(ConfigurationPath)!), document.Stage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { Message = "Cannot load prompt; check its path. Existing text retained."; }
        Changed();
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
        CanSave = false; Saved = false; Changed();
        Action<bool>? finishHotkey = null;
        try
        {
            RecordingHotkey = MeetTape.RecordingHotkey.Parse(RecordingHotkey).Text;
            if (!int.TryParse(ChunkSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                throw new InvalidDataException("Chunk duration must be a whole number between 1 and 340.");
            var json = JsonNode.Parse(original)!.AsObject();
            json["meetingsRoot"] = MeetingsRoot.Trim();
            json["capture"] ??= new JsonObject();
            json["capture"]!["microphoneDeviceId"] = Microphone.Id;
            json["capture"]!["playbackDeviceId"] = Playback.Id;
            json["capture"]!["ffmpegPath"] = FfmpegPath.Trim();
            if (json.ContainsKey("recordingHotkey") || RecordingHotkey != MeetTape.RecordingHotkey.DefaultShortcut)
                json["recordingHotkey"] = RecordingHotkey;
            json["stages"] = new JsonObject { ["trim"] = Trim, ["transcribe"] = Transcribe, ["summary"] = Summary };
            json["chunkSeconds"] = seconds;
            json["transcription"] = Api(TranscriptionEndpoint, TranscriptionModel, TranscriptionPrompt, TranscriptionKeyVariable);
            json["summary"] = Api(SummaryEndpoint, SummaryModel, SummaryPrompt, SummaryKeyVariable);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddKey(keys, TranscriptionKeyVariable.Trim(), NewTranscriptionKey);
            AddKey(keys, SummaryKeyVariable.Trim(), NewSummaryKey);
            var editedPrompts = new List<PromptDocument>();
            AddPromptEdit(editedPrompts, transcriptionDocument, TranscriptionPrompt);
            AddPromptEdit(editedPrompts, summaryDocument, SummaryPrompt);
            string updated = JsonNode.DeepEquals(json, JsonNode.Parse(original)) ? original :
                json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + (original.EndsWith('\n') ? Environment.NewLine : "");
            _ = ConfigurationLoader.Parse(updated, ConfigurationPath, validatePrompts: false);
            if (RecordingHotkey != originalRecordingHotkey) finishHotkey = prepareHotkey?.Invoke(RecordingHotkey);
            var result = await Task.Run(() =>
            {
                string? error = ConfigurationStore.Save(ConfigurationPath, original, updated, keys, setKey, editedPrompts);
                bool configurationSaved = error is null;
                if (error is not null)
                {
                    // Key persistence can fail after the configuration was replaced.
                    // Retrying must compare against that exact candidate, without
                    // accepting any differing external edit.
                    try { configurationSaved = File.ReadAllText(ConfigurationPath) == updated; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return (Error: error, ConfigurationSaved: configurationSaved);
            });
            string? error = result.Error;
            if (result.ConfigurationSaved) { original = updated; originalRecordingHotkey = RecordingHotkey; }
            var completeHotkey = finishHotkey; finishHotkey = null;
            completeHotkey?.Invoke(result.ConfigurationSaved);
            Saved = error is null;
            Message = error ?? "Saved. Capture changes apply to the next recording; running jobs keep their saved parameters. Resume uses current API settings. Only edited prompts were saved.";
            if (Saved)
            {
                original = updated; originalFields = Fields(); ClearSecrets();
                if (applySettings is not null)
                    await applySettings(ConfigurationLoader.Parse(updated, ConfigurationPath, validatePrompts: false));
            }
        }
        catch (InvalidDataException ex) { Message = ex.Message; }
        catch (Exception) { Message = Saved ? "Settings saved, but could not be applied to the running app. Reopen Settings and Save to retry; current recording retained." :
            "Cannot save all settings or prompts. Check paths and permissions; remaining edits are retained. Some files may already have been saved; secrets are not logged."; }
        finally { finishHotkey?.Invoke(false); CanSave = true; Changed(); }
    }

    private void AddPromptEdit(List<PromptDocument> edits, PromptDocument document, string configuredPath)
    {
        if (!document.IsDirty) return;
        string path = Path.GetFullPath(configuredPath.Trim(), Path.GetDirectoryName(ConfigurationPath)!);
        if (!path.Equals(document.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {document.Stage} prompt path changed after loading. Existing edits retained; save them to the loaded path or explicitly Discard and Load the new file.");
        edits.Add(document);
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
