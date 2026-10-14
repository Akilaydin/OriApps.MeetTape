using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Velopack;

namespace MeetTape.UiTests;

public sealed class UpdateTests
{
    [AvaloniaTheory]
    [InlineData(true, true, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, true, "check")]
    public async Task AboutCheckOnlyReportsAvailabilityAndNeverDownloadsOrShutsDown(bool installed, bool available, string? failure)
    {
        var controller = new Controller();
        var model = new MainViewModel(controller); await model.RefreshAsync();
        var updates = new Updates { IsInstalled = installed, Available = available, Failure = failure };
        int applications = 0;
        var settings = About(model, updates, () => { applications++; return Task.CompletedTask; }, out string root);
        try
        {
            Assert.False(settings.FindControl<Button>("UpdateButton")!.IsEnabled);
            Click(settings, "CheckUpdates");
            await Task.Yield(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(installed ? 1 : 0, updates.Checks);
            Assert.Equal(0, updates.Downloads); Assert.Equal(0, updates.Applies); Assert.Equal(0, controller.Shutdowns);
            Assert.Equal(0, applications);
            Assert.Equal(installed && available && failure is null, model.CanApplyUpdate);
            Assert.Contains("1.0.0", settings.FindControl<TextBlock>("CurrentVersion")!.Text);
            Assert.DoesNotContain("private-secret", model.UpdateStatus);
            Assert.True(model.CanRecord);
        }
        finally { settings.Close(); model.Detach(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task PendingCheckKeepsRecordingAvailableAndCannotStartAnUpdate()
    {
        var controller = new Controller();
        var model = new MainViewModel(controller); await model.RefreshAsync();
        var updates = new Updates { PendingCheck = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task check = model.CheckUpdatesAsync(updates);
        Assert.True(model.CanRecord); Assert.False(model.CanCheckUpdates); Assert.False(model.CanApplyUpdate);
        await model.ToggleAsync(); Assert.Equal(1, controller.Starts);
        updates.PendingCheck.SetResult(null); await check;
        Assert.Equal(0, updates.Downloads); Assert.Equal(0, updates.Applies); Assert.Equal(0, controller.Shutdowns);
        Assert.False(model.CanApplyUpdate); model.Detach();
    }

    [AvaloniaFact]
    public async Task AboutUpdateRefusesUnsavedSettingsAndUnsafeRecordingBeforeCallingTheExistingFlow()
    {
        var controller = new Controller();
        var model = new MainViewModel(controller); await model.RefreshAsync();
        var updates = new Updates();
        int applications = 0;
        var settings = About(model, updates, () => { applications++; return Task.CompletedTask; }, out string root);
        try
        {
            await model.CheckUpdatesAsync(updates);
            var edited = (SettingsViewModel)settings.DataContext!;
            edited.ChunkSeconds = "90";
            Click(settings, "UpdateButton");
            Assert.Equal(0, applications); Assert.Contains("Save settings", model.UpdateStatus);
            edited.ChunkSeconds = "120";
            controller.Recording = true; await model.RefreshAsync();
            Click(settings, "UpdateButton"); Assert.Equal(0, applications);
            controller.Recording = false; await model.RefreshAsync();
            Click(settings, "UpdateButton"); Assert.Equal(1, applications);
            Assert.Equal(0, updates.Downloads); Assert.Equal(0, controller.Shutdowns);
        }
        finally { settings.ConfirmDiscard = () => Task.FromResult(true); settings.Close(); model.Detach(); Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclineOrCloseDialogNeverDownloadsOrShutsDown(bool closeDialog)
    {
        var controller = new Controller();
        var updates = new Updates();
        var model = new MainViewModel(controller);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        UpdateWindow? dialog = null;
        Task<bool>? operation = null;
        SettingsWindow? settings = null;
        settings = About(model, updates, () => operation = model.UpdateAsync(updates, (current, available) =>
        {
            Assert.Equal("1.0.0", current); Assert.Equal("1.0.1", available);
            dialog = new UpdateWindow(current, available);
            return dialog.ShowDialog<bool>(settings!);
        }), out string root);
        await model.CheckUpdatesAsync(updates);
        Click(settings, "UpdateButton");
        Assert.NotNull(dialog);
        Assert.False(model.CanRecord);
        if (closeDialog) dialog.Close(); else Click(dialog, "DeclineUpdate");
        Assert.False(await operation!);
        Assert.Equal(0, updates.Downloads); Assert.Equal(0, updates.Applies); Assert.Equal(0, controller.Shutdowns);
        Assert.True(model.CanRecord);
        settings.Close(); Close(window, model); Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public async Task AcceptedUpdateWaitsForCheckpointJoinAndShowsProgress()
    {
        var controller = new Controller { Join = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var updates = new Updates();
        var model = new MainViewModel(controller);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        UpdateWindow? dialog = null;
        Task<bool>? operation = null;
        SettingsWindow? settings = null;
        settings = About(model, updates, () => operation = model.UpdateAsync(updates, (current, available) =>
        {
            dialog = new UpdateWindow(current, available);
            return dialog.ShowDialog<bool>(settings!);
        }), out string root);
        await model.CheckUpdatesAsync(updates);
        Click(settings, "UpdateButton");
        Assert.NotNull(dialog);
        Assert.Equal(0, updates.Downloads);
        Click(dialog, "ConfirmUpdate");
        for (int i = 0; i < 300 && controller.Shutdowns == 0; i++) await Task.Delay(10);
        Assert.Equal(1, updates.Downloads); Assert.Equal(1, controller.Shutdowns);
        Assert.Equal(0, updates.Applies); Assert.False(operation!.IsCompleted);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(75, model.UpdateProgress);
        Assert.Equal(75, settings.FindControl<ProgressBar>("UpdateProgress")!.Value);
        Assert.False(settings.IsEnabled);
        Assert.False(model.CanRecord); Assert.False(model.CanConfigure);
        await model.ToggleAsync(); Assert.Equal(0, controller.Starts);
        await model.ToggleFromHotkeyAsync(); Assert.Equal(0, controller.Starts);
        Assert.False(await model.ShutdownAsync());
        controller.Join.SetResult();
        Assert.True(await operation); Assert.Equal(1, updates.Applies);
        settings.Close(); Close(window, model); Directory.Delete(root, true);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingBlocksUpdateIncludingCaptureStartedDuringDownload(bool duringDownload)
    {
        var controller = new Controller { Recording = !duringDownload };
        var model = new MainViewModel(controller);
        await model.RefreshAsync();
        var updates = new Updates { AfterDownload = () => controller.Recording = true };
        int confirmations = 0;
        Assert.False(await model.UpdateAsync(updates, (_, _) => { confirmations++; return Task.FromResult(true); }));
        Assert.Equal(duringDownload ? 1 : 0, confirmations);
        Assert.Equal(duringDownload ? 1 : 0, updates.Downloads);
        Assert.Equal(0, updates.Applies); Assert.Equal(0, controller.Shutdowns);
        model.Detach();
    }

    [AvaloniaTheory]
    [InlineData("check")]
    [InlineData("download")]
    [InlineData("join")]
    [InlineData("apply")]
    public async Task FailuresNeverApplyBeforeSuccessfulShutdownOrExposeSecrets(string failure)
    {
        var controller = new Controller { FailJoin = failure == "join" };
        var updates = new Updates { Failure = failure };
        var model = new MainViewModel(controller);
        await model.RefreshAsync();
        Assert.False(await model.UpdateAsync(updates, (_, _) => Task.FromResult(true)));
        Assert.Equal(0, updates.Applies);
        Assert.DoesNotContain("private-secret", model.ErrorText);
        Assert.Contains(failure == "check" ? "connection" : "storage", model.ErrorText, StringComparison.OrdinalIgnoreCase);
        if (failure is "check" or "download") Assert.True(model.CanRecord);
        else Assert.False(model.CanRecord);
        model.Detach();
    }

    [AvaloniaFact]
    public async Task SourceBuildAndNoUpdateKeepNormalUiWithoutShutdown()
    {
        foreach (bool installed in new[] { false, true })
        {
            var controller = new Controller();
            var updates = new Updates { IsInstalled = installed, Available = false };
            var model = new MainViewModel(controller);
            await model.RefreshAsync();
            Assert.False(await model.UpdateAsync(updates, (_, _) => throw new Exception("No confirmation expected")));
            Assert.Equal(installed ? 1 : 0, updates.Checks);
            Assert.Equal(0, updates.Downloads); Assert.Equal(0, controller.Shutdowns);
            Assert.True(model.CanRecord); model.Detach();
        }
    }

    [AvaloniaFact]
    public async Task UpdaterAndFileOperationFailuresShowReasonsAndKeepRedactedLogsAfterRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetTapeUiDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var log = new FileDiagnostics(directory))
            using (AppDiagnostics.Use(log))
            {
                AppDiagnostics.ProtectSecret("private-secret");
                var model = new MainViewModel(new Controller()); await model.RefreshAsync();
                var updates = new Updates { Failure = "check" };
                Assert.False(await model.UpdateAsync(updates, (_, _) => Task.FromResult(true)));
                Assert.Contains("connection", model.ErrorText);
                string notice = await StartupReleaseNotice.GetAsync(updates);
                Assert.Contains("connection", notice);
                await model.ExecuteAsync(() => throw new UnauthorizedAccessException("private-secret"));
                Assert.Contains("Access denied", model.ErrorText);
                Assert.DoesNotContain("private-secret", model.ErrorText);
                Assert.True(model.CanRecord); model.Detach();
            }
            await using (var restarted = new FileDiagnostics(directory)) restarted.Write("Information", "startup", "Restarted fixture");
            string text = File.ReadAllText(Path.Combine(directory, "meettape.log"));
            Assert.Contains("HttpRequestException", text); Assert.Contains("UnauthorizedAccessException", text);
            Assert.Contains("Restarted fixture", text); Assert.DoesNotContain("private-secret", text);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void Click(Window window, string name) => window.GetLogicalDescendants().OfType<Button>().Distinct().Single(b => b.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static SettingsWindow About(MainViewModel model, Updates updates, Func<Task> apply, out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "MeetTapeAbout-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "config.json");
        ConfigurationBootstrap.Initialize(path, Path.Combine(root, "meetings"));
        var window = new SettingsWindow { DataContext = new SettingsViewModel(path, [], (_, _, _) => { }) };
        window.ConfigureAbout(model, updates, apply);
        window.Show(); window.FindControl<TabControl>("SettingsSections")!.SelectedIndex = 3;
        return window;
    }
    private static void Close(MainWindow window, MainViewModel model) { model.Detach(); window.AllowClose = true; window.Close(); }

    private sealed class Updates : IAppUpdates
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "1.0.0";
        public bool Available = true;
        public string? Failure;
        public int Checks, Downloads, Applies;
        public TaskCompletionSource<UpdateInfo?>? PendingCheck;
        public Action? AfterDownload;
        public Task<UpdateInfo?> CheckAsync()
        {
            Checks++;
            if (Failure == "check") throw new HttpRequestException("private-secret");
            if (PendingCheck is not null) return PendingCheck.Task;
            return Task.FromResult<UpdateInfo?>(Available ? new(new VelopackAsset { Version = SemanticVersion.Parse("1.0.1") }, false) : null);
        }
        public Task DownloadAsync(UpdateInfo update, Action<int> progress)
        {
            Downloads++;
            if (Failure == "download") throw new IOException("private-secret");
            progress(75); AfterDownload?.Invoke(); return Task.CompletedTask;
        }
        public void ApplyAndRestart(UpdateInfo update)
        {
            if (Failure == "apply") throw new IOException("private-secret");
            Applies++;
        }
    }

    private sealed class Controller : IDesktopController
    {
        public bool Recording, FailJoin;
        public int Starts, Shutdowns;
        public TaskCompletionSource? Join;
        public event Action<OperationNotice>? Notice { add { } remove { } }
        public event Action<CaptureStatus>? RecordingChanged { add { } remove { } }
        public Task<DesktopSnapshot> ReadAsync() => Task.FromResult(new DesktopSnapshot(new(Recording, TimeSpan.Zero, 0, 0, null), [], null, null, false, null));
        public Task StartAsync() { Starts++; Recording = true; return Task.CompletedTask; }
        public Task<string?> StopAsync() => Task.FromResult<string?>(null);
        public Task PrepareAsync(string directory, string? title) => Task.CompletedTask;
        public Task ChooseTrimAsync(TrimDecision decision, double? start = null, double? end = null) => Task.CompletedTask;
        public Task ResumeAsync(string directory) => Task.CompletedTask;
        public Task CancelProcessingAsync(string directory) => Task.CompletedTask;
        public Task DeleteRecordingAsync(string directory, Guid id) => Task.CompletedTask;
        public Task SummaryAsync(string directory) => Task.CompletedTask;
        public async Task ShutdownAsync()
        {
            Shutdowns++; if (FailJoin) throw new IOException("private-secret");
            if (Join is not null) await Join.Task;
        }
    }
}
