using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using System.Text;
using System.Text.Json.Nodes;

namespace MeetTape.UiTests;

public sealed class SettingsTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../dist/prompt-editor-tests/" + Guid.NewGuid().ToString("N")));
    private string ConfigPath => Path.Combine(root, "config.json");
    private string TranscriptionPath => Path.Combine(root, "prompts", "transcription.md");
    private string SummaryPath => Path.Combine(root, "prompts", "summary.md");

    public SettingsTests() => ConfigurationBootstrap.Initialize(ConfigPath, Path.Combine(root, "Meetings"));
    private SettingsViewModel Model(Func<LoadedConfiguration, Task>? apply = null) => new(ConfigPath, [], (_, _, _) => { }, apply);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenLogsCreatesOnlyMissingFolderAndPreservesUnsavedSettingsAndExistingLog(bool existing)
    {
        string directory = Path.Combine(root, "fixture-logs");
        string logPath = Path.Combine(directory, "meettape.log");
        if (existing) { Directory.CreateDirectory(directory); File.WriteAllText(logPath, "Existing diagnostics"); }
        string configuration = File.ReadAllText(ConfigPath);
        string prompt = File.ReadAllText(TranscriptionPath);
        var model = Model(); model.ChunkSeconds = "90";
        string message = model.Message;
        string? opened = null;
        var window = new SettingsWindow
        {
            DataContext = model, LogsDirectory = () => directory, ConfirmDiscard = () => Task.FromResult(true),
            OpenFolder = path => { Assert.True(Directory.Exists(path)); opened = path; }
        };
        window.Show();
        var button = window.FindControl<Button>("OpenLogsFolder")!;
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(directory, opened);
        Assert.False(window.FindControl<TextBlock>("LogsFolderError")!.IsVisible);
        Assert.Equal(configuration, File.ReadAllText(ConfigPath));
        Assert.Equal(prompt, File.ReadAllText(TranscriptionPath));
        Assert.Equal(message, model.Message);
        Assert.Equal("90", model.ChunkSeconds);
        Assert.True(model.HasUnsavedChanges); Assert.False(model.Saved);
        if (existing) Assert.Equal("Existing diagnostics", File.ReadAllText(logPath));
        else Assert.Empty(Directory.GetFiles(directory));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenLogsFailureShowsSafeMessageWithoutSavingOrDisablingSettings(bool shellFailure)
    {
        string directory = Path.Combine(root, "fixture-logs");
        if (!shellFailure) File.WriteAllText(directory, "Occupied path");
        string configuration = File.ReadAllText(ConfigPath);
        var model = Model();
        int opens = 0;
        var window = new SettingsWindow
        {
            DataContext = model, LogsDirectory = () => directory,
            OpenFolder = _ => { opens++; throw new UnauthorizedAccessException("private-shell-error"); }
        };
        window.Show();
        window.FindControl<Button>("OpenLogsFolder")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(shellFailure ? 1 : 0, opens);
        var error = window.FindControl<TextBlock>("LogsFolderError")!;
        Assert.True(error.IsVisible);
        Assert.NotEmpty(error.Text!); Assert.DoesNotContain("private-shell-error", error.Text);
        Assert.True(model.CanSave); Assert.False(model.HasUnsavedChanges); Assert.False(model.Saved);
        Assert.Equal(configuration, File.ReadAllText(ConfigPath));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RecordingShortcutSaveUpdatesRuntimeLabelsAndReloadsThePersistedChoice()
    {
        using var keys = new HotkeyFixture();
        var model = new SettingsViewModel(ConfigPath, [], (_, _, _) => { }, prepareHotkey: keys.App.PrepareHotkey);
        Assert.Equal(RecordingHotkey.DefaultShortcut, model.RecordingHotkey);
        var settings = new SettingsWindow { DataContext = model }; settings.Show();
        var main = new MainWindow { DataContext = keys.Model }; main.Show();
        settings.FindControl<TextBox>("RecordingHotkey")!.Text = "ctrl + alt + F10";
        Assert.True(model.HasUnsavedChanges);
        await model.SaveAsync();
        Assert.True(model.Saved); Assert.False(model.HasUnsavedChanges);
        Assert.Equal("Ctrl+Alt+F10", keys.Model.RecordingShortcut);
        Assert.Equal("Ctrl+Alt+F10 · Start / Stop", main.FindControl<TextBlock>("RecordingShortcut")!.Text);
        Assert.Single(keys.Registered); Assert.Contains((0x4003u, 0x79u), keys.Registered.Values);
        Assert.Equal("Ctrl+Alt+F10", Model().RecordingHotkey);
        Assert.Equal("Ctrl+Alt+F10", ConfigurationLoader.Load(ConfigPath).Settings.RecordingHotkey);
        settings.Close(); main.AllowClose = true; main.Close();
    }

    [AvaloniaFact]
    public async Task MicrophoneProcessingCanBeSelectedSavedAndReopenedWithoutChangingThem()
    {
        var model = Model();
        var window = new SettingsWindow { DataContext = model }; window.Show();
        var combo = window.FindControl<ComboBox>("MicrophoneModeChoice")!;
        Assert.Equal("Windows Default", Assert.IsType<MicrophoneModeChoice>(combo.SelectedItem).Label);
        Assert.False(model.HasUnsavedChanges);
        combo.SelectedIndex = 1;
        Assert.True(model.HasUnsavedChanges);
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(MicrophoneCaptureMode.WindowsCommunications, ConfigurationLoader.Load(ConfigPath).Settings.Capture.MicrophoneMode);
        Assert.Null(ConfigurationLoader.Load(ConfigPath).Settings.Capture.PlaybackDeviceId);
        Assert.Equal("windowsCommunications", JsonNode.Parse(File.ReadAllText(ConfigPath))!["capture"]!["microphoneMode"]!.GetValue<string>());
        window.Close();

        var reloaded = Model();
        Assert.Equal(MicrophoneCaptureMode.WindowsCommunications, reloaded.MicrophoneMode.Value);
        reloaded.MicrophoneMode = reloaded.MicrophoneModes.Single(x => x.Value == MicrophoneCaptureMode.WindowsDefault);
        Assert.True(reloaded.HasUnsavedChanges);
        await reloaded.SaveAsync();
        Assert.Equal(MicrophoneCaptureMode.WindowsDefault, ConfigurationLoader.Load(ConfigPath).Settings.Capture.MicrophoneMode);
        Assert.Null(ConfigurationLoader.Load(ConfigPath).Settings.Capture.PlaybackDeviceId);
    }

    [AvaloniaFact]
    public async Task MicrophoneOfferIsOffByDefaultAndSavedWithoutChangingCaptureSettings()
    {
        var model = Model();
        var window = new SettingsWindow { DataContext = model }; window.Show();
        var checkbox = window.FindControl<CheckBox>("MicrophoneOffer")!;
        Assert.False(checkbox.IsChecked);
        Assert.False(model.HasUnsavedChanges);
        checkbox.IsChecked = true;
        Assert.True(model.HasUnsavedChanges);
        Assert.False(ConfigurationLoader.LoadForCapture(ConfigPath).Settings.OfferRecordingOnMicrophoneUse);

        await model.SaveAsync();
        Assert.True(model.Saved, model.Message);
        Assert.False(model.HasUnsavedChanges);
        Assert.True(ConfigurationLoader.LoadForCapture(ConfigPath).Settings.OfferRecordingOnMicrophoneUse);
        Assert.Equal(MicrophoneCaptureMode.WindowsDefault,
            ConfigurationLoader.LoadForCapture(ConfigPath).Settings.Capture.MicrophoneMode);
        window.Close();

        var again = Model();
        Assert.True(again.OfferRecordingOnMicrophoneUse);
        again.OfferRecordingOnMicrophoneUse = false;
        Assert.True(again.HasUnsavedChanges);
        await again.SaveAsync();
        Assert.True(again.Saved, again.Message);
        Assert.False(ConfigurationLoader.LoadForCapture(ConfigPath).Settings.OfferRecordingOnMicrophoneUse);
    }

    [AvaloniaFact]
    public async Task OccupiedShortcutAndFailedPersistenceKeepTheOldBindingAndReleaseTheCandidate()
    {
        using var keys = new HotkeyFixture();
        string original = File.ReadAllText(ConfigPath);
        keys.Registered[99] = (0x4003u, 0x79u);
        var model = new SettingsViewModel(ConfigPath, [], (_, _, _) => { }, prepareHotkey: keys.App.PrepareHotkey)
            { RecordingHotkey = "Ctrl+Alt+F10" };
        await model.SaveAsync();
        Assert.False(model.Saved); Assert.Contains("already in use", model.Message);
        Assert.Equal(original, File.ReadAllText(ConfigPath)); Assert.Equal(RecordingHotkey.DefaultShortcut, keys.Model.RecordingShortcut);
        keys.Registered.Remove(99);
        var priorFactory = keys.App.HotkeyFactory!;
        keys.App.HotkeyFactory = (text, id) =>
        {
            var candidate = priorFactory(text, id);
            var external = JsonNode.Parse(original)!; external["meetingsRoot"] = "External fixture folder";
            File.WriteAllText(ConfigPath, external.ToJsonString());
            return candidate;
        };
        await model.SaveAsync();
        Assert.False(model.Saved); Assert.Contains("changed during editing", model.Message);
        Assert.Equal(RecordingHotkey.DefaultShortcut, keys.Model.RecordingShortcut);
        Assert.Single(keys.Registered); Assert.Contains((0x4006u, 0x78u), keys.Registered.Values);
    }

    [AvaloniaFact]
    public void DiscardingAnEditedShortcutDoesNotApplyItOrChangeExistingConfiguration()
    {
        using var keys = new HotkeyFixture();
        string original = File.ReadAllText(ConfigPath);
        var model = new SettingsViewModel(ConfigPath, [], (_, _, _) => { }, prepareHotkey: keys.App.PrepareHotkey);
        var settings = new SettingsWindow { DataContext = model, ConfirmDiscard = () => Task.FromResult(true) }; settings.Show();
        settings.FindControl<TextBox>("RecordingHotkey")!.Text = "Ctrl+Alt+F10";
        settings.Close();
        Assert.False(model.Saved); Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Equal(RecordingHotkey.DefaultShortcut, keys.Model.RecordingShortcut);
        Assert.Single(keys.Registered); Assert.Contains((0x4006u, 0x78u), keys.Registered.Values);
    }

    [AvaloniaFact]
    public async Task SavedConfigurationKeepsTheNewShortcutAppliedEvenWhenKeyPersistenceFailsAfterReplacement()
    {
        using var keys = new HotkeyFixture();
        var model = new SettingsViewModel(ConfigPath, [], (_, _, _) => throw new UnauthorizedAccessException("Fixture key failure"),
            prepareHotkey: keys.App.PrepareHotkey)
        { RecordingHotkey = "Ctrl+Alt+F10", NewTranscriptionKey = "fixture-key" };
        await model.SaveAsync();
        Assert.False(model.Saved); Assert.Contains("Cannot save API key", model.Message);
        Assert.Equal("Ctrl+Alt+F10", keys.Model.RecordingShortcut);
        Assert.Equal("Ctrl+Alt+F10", ConfigurationLoader.Load(ConfigPath).Settings.RecordingHotkey);
        Assert.Single(keys.Registered); Assert.Contains((0x4003u, 0x79u), keys.Registered.Values);
    }

    [AvaloniaFact]
    public async Task SavingOtherSettingsDoesNotRequireRegisteringAnUnchangedUnavailableShortcut()
    {
        int prepares = 0;
        var model = new SettingsViewModel(ConfigPath, [], (_, _, _) => { }, prepareHotkey: _ =>
        { prepares++; throw new InvalidDataException("Fixture occupied shortcut"); }) { ChunkSeconds = "90" };
        await model.SaveAsync();
        Assert.True(model.Saved); Assert.Equal(0, prepares);
        Assert.Equal(90, ConfigurationLoader.Load(ConfigPath).Settings.ChunkSeconds);
    }

    private sealed class HotkeyFixture : IDisposable
    {
        internal App App { get; } = (App)Avalonia.Application.Current!;
        internal MainViewModel Model { get; }
        internal Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = [];
        private readonly System.Reflection.FieldInfo field = typeof(App).GetField("hotkey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        private readonly object? original;
        private readonly Func<string, int, RecordingHotkey>? originalFactory;
        private readonly string originalShortcut;
        internal HotkeyFixture()
        {
            Model = (MainViewModel)typeof(App).GetField("model", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(App)!;
            original = field.GetValue(App); originalFactory = App.HotkeyFactory; originalShortcut = Model.RecordingShortcut;
            App.HotkeyFactory = (text, id) => new RecordingHotkey(123, text, id, (_, slot, modifiers, key) =>
            {
                if (Registered.Values.Contains((modifiers, key))) return false;
                Registered.Add(slot, (modifiers, key)); return true;
            }, (_, slot) => Registered.Remove(slot));
            field.SetValue(App, App.HotkeyFactory(RecordingHotkey.DefaultShortcut, RecordingHotkey.Id));
            Model.SetRecordingShortcut(RecordingHotkey.DefaultShortcut);
        }
        public void Dispose()
        {
            ((RecordingHotkey?)field.GetValue(App))?.Dispose();
            field.SetValue(App, original); App.HotkeyFactory = originalFactory; Model.SetRecordingShortcut(originalShortcut);
        }
    }

    [AvaloniaFact]
    public async Task BothPromptEditorsLoadRelativeFilesAndSaveExactUtf8WithoutRestart()
    {
        string before = File.ReadAllText(ConfigPath);
        string transcription = "# Fixture transcription\r\nKeep 中文 and русский speech.\r\n";
        string summary = "# Fixture summary\nKeep user instructions.\n";
        File.WriteAllText(TranscriptionPath, transcription, new UTF8Encoding(true));
        File.WriteAllText(SummaryPath, summary);
        int applies = 0;
        var model = Model(updated =>
        {
            applies++;
            Assert.Equal(modelText(), updated.ReadPrompt(updated.Settings.Transcription));
            return Task.CompletedTask;
        });
        string modelText() => "# Edited transcription\r\nαβ 中文\r\n";
        Assert.Equal(transcription, model.TranscriptionPromptText);
        Assert.Equal(summary, model.SummaryPromptText);
        Assert.False(model.HasUnsavedChanges);
        model.TranscriptionPromptText = modelText();
        model.SummaryPromptText = "# Edited summary\nKeep original language.\n";
        await model.SaveAsync();
        Assert.True(model.Saved); Assert.True(model.CanSave);
        Assert.Equal(1, applies);
        Assert.Equal(Encoding.UTF8.GetBytes(modelText()), File.ReadAllBytes(TranscriptionPath));
        Assert.Equal(Encoding.UTF8.GetBytes(model.SummaryPromptText), File.ReadAllBytes(SummaryPath));
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.False(model.HasUnsavedChanges);
        model.ChunkSeconds = "90";
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.Equal(2, applies);
        Assert.Equal(90, ConfigurationLoader.Load(ConfigPath).Settings.ChunkSeconds);
        Assert.False(model.HasUnsavedChanges);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [AvaloniaFact]
    public async Task ChangedPathCannotWriteLoadedEditsToAnotherFileAndReloadUsesNewRelativePath()
    {
        string other = Path.Combine(root, "prompts", "custom.md");
        File.WriteAllText(other, "Another file must survive");
        byte[] original = File.ReadAllBytes(TranscriptionPath);
        var model = Model();
        model.TranscriptionPromptText = "Pending edits";
        model.TranscriptionPrompt = "prompts/custom.md";
        model.LoadTranscriptionPrompt();
        Assert.Equal("Pending edits", model.TranscriptionPromptText);
        await model.SaveAsync();
        Assert.False(model.Saved);
        Assert.Contains("path changed", model.Message);
        Assert.Equal("Another file must survive", File.ReadAllText(other));
        Assert.Equal(original, File.ReadAllBytes(TranscriptionPath));
        model.TranscriptionPrompt = "prompts/transcription.md";
        await model.SaveAsync();
        Assert.True(model.Saved);
        model.TranscriptionPrompt = "prompts/custom.md";
        model.LoadTranscriptionPrompt();
        Assert.Equal("Another file must survive", model.TranscriptionPromptText);
        model.TranscriptionPromptText = "Explicitly edited custom file";
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.Equal("Explicitly edited custom file", File.ReadAllText(other));
        Assert.Equal("Pending edits", File.ReadAllText(TranscriptionPath));
        Assert.Equal("prompts/custom.md", ConfigurationLoader.Load(ConfigPath).Settings.Transcription.PromptFile);
    }

    [AvaloniaFact]
    public async Task ConcurrentPromptChangeStopsBeforeWritingAnyOtherFileAndKeepsEdits()
    {
        byte[] transcription = File.ReadAllBytes(TranscriptionPath);
        string config = File.ReadAllText(ConfigPath);
        var model = Model();
        model.ChunkSeconds = "90";
        model.TranscriptionPromptText = "Unsaved transcription";
        model.SummaryPromptText = "Unsaved summary";
        File.WriteAllText(SummaryPath, "External summary edits");
        await model.SaveAsync();
        Assert.False(model.Saved);
        Assert.Contains("summary prompt changed during editing", model.Message);
        Assert.Equal(transcription, File.ReadAllBytes(TranscriptionPath));
        Assert.Equal(config, File.ReadAllText(ConfigPath));
        Assert.Equal("External summary edits", File.ReadAllText(SummaryPath));
        Assert.Equal("Unsaved transcription", model.TranscriptionPromptText);
        Assert.Equal("Unsaved summary", model.SummaryPromptText);
        Assert.Equal("90", model.ChunkSeconds);
        Assert.True(model.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public async Task UnreadablePromptDoesNotWipeOtherEditorAndFailedSaveRetainsEdits()
    {
        string transcription = File.ReadAllText(TranscriptionPath);
        var model = Model();
        model.SummaryPromptText = "Pending summary edits";
        model.TranscriptionPromptText = "Pending transcription edits";
        using (var locked = new FileStream(SummaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unreadable = Model();
            Assert.Contains("Cannot read the summary", unreadable.SummaryPromptError);
            Assert.Equal(transcription, unreadable.TranscriptionPromptText);
            await model.SaveAsync();
            Assert.False(model.Saved);
            Assert.Contains("retained", model.Message);
            Assert.Equal("Pending summary edits", model.SummaryPromptText);
            Assert.Equal("Pending transcription edits", model.TranscriptionPromptText);
            Assert.Equal(transcription, File.ReadAllText(TranscriptionPath));
        }
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.Equal("Pending summary edits", File.ReadAllText(SummaryPath));
    }

    [AvaloniaFact]
    public async Task MissingEnabledPromptCanBeCreatedFromEditorAndEmptyInstructionsAreRejected()
    {
        byte[] summary = File.ReadAllBytes(SummaryPath);
        File.Delete(TranscriptionPath);
        var model = Model();
        Assert.Contains("not found", model.TranscriptionPromptError);
        Assert.Equal(File.ReadAllText(SummaryPath), model.SummaryPromptText);
        model.TranscriptionPromptText = " ";
        await model.SaveAsync();
        Assert.False(model.Saved);
        Assert.Contains("prompt file is empty", model.Message);
        Assert.False(File.Exists(TranscriptionPath));
        model.TranscriptionPromptText = "New explicit instructions";
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.Equal("New explicit instructions", File.ReadAllText(TranscriptionPath));
        Assert.Equal(summary, File.ReadAllBytes(SummaryPath));
        Assert.Empty(model.TranscriptionPromptError);
    }

    [AvaloniaFact]
    public async Task SharedPromptPathRejectsConflictingEditorsAndWritesMatchingTextOnce()
    {
        var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        json["summary"]!["promptFile"] = "prompts/transcription.md";
        File.WriteAllText(ConfigPath, json.ToJsonString());
        var model = Model();
        string original = File.ReadAllText(TranscriptionPath);
        model.TranscriptionPromptText = "First edit"; model.SummaryPromptText = "Different edit";
        await model.SaveAsync();
        Assert.False(model.Saved);
        Assert.Contains("matching instructions", model.Message);
        Assert.Equal(original, File.ReadAllText(TranscriptionPath));
        model.SummaryPromptText = "First edit";
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal("First edit", File.ReadAllText(TranscriptionPath));
    }

    [AvaloniaFact]
    public void EveryEditableSettingsFieldParticipatesInUnsavedChangeDetection()
    {
        var model = Model();
        foreach (var field in typeof(SettingsViewModel).GetProperties().Where(p => p.SetMethod?.IsPublic == true))
        {
            object? value = field.GetValue(model);
            object changed = field.PropertyType == typeof(bool) ? !(bool)value! :
                field.PropertyType == typeof(DeviceChoice) ? new DeviceChoice("fixture-device", "Fixture device") :
                field.PropertyType == typeof(MicrophoneModeChoice) ? model.MicrophoneModes.Single(mode => mode.Value != ((MicrophoneModeChoice)value!).Value) :
                (string)value! + "fixture-edit";
            field.SetValue(model, changed);
            Assert.True(model.HasUnsavedChanges, field.Name);
            field.SetValue(model, value);
            Assert.False(model.HasUnsavedChanges, field.Name);
        }
    }

    [AvaloniaFact]
    public async Task MeDisplayNameIsEditableInProcessingTabAndPersistsWithoutChangingCustomPrompt()
    {
        var model = Model();
        var window = new SettingsWindow { DataContext = model };
        string originalPrompt = File.ReadAllText(TranscriptionPath);
        window.Show();
        window.FindControl<TabControl>("SettingsSections")!.SelectedIndex = 1;
        var name = window.FindControl<TextBox>("MeDisplayName")!;
        Assert.Equal("Me", name.Text);
        name.Text = "Артём О";
        Assert.True(model.HasUnsavedChanges);
        Assert.Equal("Артём О", model.MeDisplayName);
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal("Артём О", ConfigurationLoader.Load(ConfigPath).Settings.MeDisplayName);
        Assert.Equal(originalPrompt, File.ReadAllText(TranscriptionPath));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "WindowsOnly")]
    public async Task FailedAtomicWriteRetainsTextAndCanBeRetriedAfterFixingPermissions(bool secondFile)
    {
        string readonlyPath = secondFile ? SummaryPath : TranscriptionPath;
        string original = File.ReadAllText(readonlyPath);
        var model = Model();
        model.TranscriptionPromptText = "Preserve this edit";
        model.SummaryPromptText = "Preserve the summary edit";
        model.ChunkSeconds = "90";
        File.SetAttributes(readonlyPath, FileAttributes.ReadOnly);
        try
        {
            await model.SaveAsync();
            Assert.False(model.Saved);
            Assert.Equal(original, File.ReadAllText(readonlyPath));
            Assert.Equal("Preserve this edit", model.TranscriptionPromptText);
            Assert.Equal("Preserve the summary edit", model.SummaryPromptText);
            Assert.True(model.HasUnsavedChanges);
        }
        finally { File.SetAttributes(readonlyPath, FileAttributes.Normal); }
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.Equal("Preserve this edit", File.ReadAllText(TranscriptionPath));
        Assert.Equal("Preserve the summary edit", File.ReadAllText(SummaryPath));
        Assert.Equal(90, ConfigurationLoader.Load(ConfigPath).Settings.ChunkSeconds);
    }

    [AvaloniaFact]
    public async Task KeySaveFailureRetainsPendingFieldsAndRetryRecognizesAlreadySavedFiles()
    {
        bool fail = true;
        int applies = 0;
        var model = new SettingsViewModel(ConfigPath, [], (_, _, _) =>
        { if (fail) throw new UnauthorizedAccessException("fixture-secret"); },
            _ => { applies++; return Task.CompletedTask; });
        model.ChunkSeconds = "90";
        model.TranscriptionPromptText = "Saved prompt before key failure";
        model.NewTranscriptionKey = "fixture-secret";
        await model.SaveAsync();
        Assert.False(model.Saved);
        Assert.Equal(0, applies);
        Assert.DoesNotContain("fixture-secret", model.Message);
        Assert.Equal("fixture-secret", model.NewTranscriptionKey);
        Assert.Equal("90", model.ChunkSeconds);
        Assert.True(model.HasUnsavedChanges);
        fail = false;
        await model.SaveAsync();
        Assert.True(model.Saved);
        Assert.Equal(1, applies);
        Assert.Equal("Saved prompt before key failure", File.ReadAllText(TranscriptionPath));
        Assert.Equal(90, ConfigurationLoader.Load(ConfigPath).Settings.ChunkSeconds);
        Assert.Empty(model.NewTranscriptionKey);
        Assert.False(model.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public async Task ActualCloseDialogKeepsEditingUntilDiscardIsClicked()
    {
        var model = Model();
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Summary = !model.Summary;
        window.Close();
        Assert.True(window.IsVisible);
        var confirmation = Assert.Single(window.OwnedWindows);
        confirmation.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "KeepEditingSettings")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10);
        Assert.True(window.IsVisible);
        Assert.True(model.HasUnsavedChanges);
        window.Close();
        confirmation = Assert.Single(window.OwnedWindows);
        confirmation.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "DiscardSettings")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 100 && window.IsVisible; i++) await Task.Delay(10);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task SettingsTabsBindPromptEditorsAndCloseRequiresExplicitDiscardOfAllEdits()
    {
        var model = Model();
        var window = new SettingsWindow { DataContext = model, Width = 520, Height = 480 };
        string original = File.ReadAllText(ConfigPath);
        string prompt = File.ReadAllText(TranscriptionPath);
        int confirmations = 0;
        window.ConfirmDiscard = () => { confirmations++; return Task.FromResult(false); };
        window.Show();
        var tabs = window.FindControl<TabControl>("SettingsSections")!;
        Assert.Equal(5, tabs.ItemCount);
        window.FindControl<TextBox>("MeetingsFolder")!.Text = "Unsaved folder";
        window.Close();
        Assert.True(window.IsVisible);
        Assert.Equal(1, confirmations);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        tabs.SelectedIndex = 3;
        var editor = window.FindControl<TextBox>("TranscriptionPromptEditor")!;
        Assert.Equal(prompt, editor.Text);
        Assert.True(editor.AcceptsReturn);
        editor.Text = "Unsaved prompt";
        editor.SelectAll();
        Assert.Equal("Unsaved prompt", editor.SelectedText);
        Assert.Equal("Unsaved prompt", model.TranscriptionPromptText);
        window.FindControl<TabControl>("PromptSections")!.SelectedIndex = 1;
        Assert.Equal(model.SummaryPromptText, window.FindControl<TextBox>("SummaryPromptEditor")!.Text);
        Assert.Equal(prompt, File.ReadAllText(TranscriptionPath));
        model.NewTranscriptionKey = "fixture-secret";
        window.ConfirmDiscard = () => Task.FromResult(true);
        window.Close();
        Assert.False(window.IsVisible);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Equal(prompt, File.ReadAllText(TranscriptionPath));
        Assert.Empty(model.NewTranscriptionKey);
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
