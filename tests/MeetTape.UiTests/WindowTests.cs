using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(MeetTape.UiTests.TestApp))]

namespace MeetTape.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WindowTests
{
    [AvaloniaFact]
    public async Task StartStopNamingAndTrimUseFakeServicesThroughActualControls()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.RefreshAsync();
        Click(window, "RecordButton");
        Assert.Equal(1, fake.Starts);
        Assert.Contains("Recording", window.FindControl<TextBlock>("CaptureStatus")!.Text);
        Click(window, "RecordButton");
        Assert.Equal(1, fake.Stops);
        Assert.True(model.IsNaming);
        window.FindControl<TextBox>("MeetingTitle")!.Text = "Planning meeting";
        Click(window, "ContinuePreparation");
        Assert.Equal("Planning meeting", fake.Title);
        Assert.True(model.ShowTrim);
        window.FindControl<TextBox>("TrimStart")!.Text = "00:03";
        window.FindControl<TextBox>("TrimEnd")!.Text = "00:25";
        Click(window, "ManualTrim");
        Assert.Equal(TrimDecision.Manual, fake.Decision);
        Assert.Equal(3, fake.StartSeconds);
        Assert.Equal(25, fake.EndSeconds);
        Assert.False(model.ShowTrim);
        Assert.Equal(0, fake.Summaries);
        Close(window, model);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("Next planning meeting")]
    public async Task StartNextRecordingSavesOptionalTitleWithoutContinueAndRejectsSecondStart(string title)
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Click(window, "RecordButton");
        Click(window, "RecordButton");
        window.FindControl<TextBox>("MeetingTitle")!.Text = title;
        Assert.True(window.FindControl<Button>("StartNextRecording")!.IsEnabled);
        Click(window, "StartNextRecording");
        Assert.Equal(title, fake.Title);
        Assert.Equal(1, fake.Prepares);
        Assert.Equal(2, fake.Starts);
        Assert.True(model.IsRecording);
        Assert.False(model.IsNaming);
        Assert.False(model.CanTrim);
        Assert.False(model.CanStartNextRecording);
        Click(window, "StartNextRecording");
        Assert.Equal(2, fake.Starts);
        Assert.Equal(0, fake.Summaries);
        Assert.Equal(0, fake.Resumes);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task StartNextRecordingRejectsRepeatedClickWhilePreparing()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Click(window, "RecordButton"); Click(window, "RecordButton");
        fake.PrepareCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(window, "StartNextRecording");
        Assert.True(model.IsBusy);
        Assert.False(window.FindControl<Button>("StartNextRecording")!.IsEnabled);
        Click(window, "StartNextRecording");
        Assert.Equal(1, fake.Prepares);
        Assert.Equal(1, fake.Starts);
        fake.PrepareCompletion.SetResult();
        await WaitFor(() => model.IsRecording);
        Assert.Equal(2, fake.Starts);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task FailedNextStartRetainsPreviousMeetingAndAllowsRetryButNotAfterExit()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Click(window, "RecordButton"); Click(window, "RecordButton");
        Guid id = fake.Snapshot.Sessions[0].Data!.Id;
        fake.FailStart = true;
        Click(window, "StartNextRecording");
        Assert.True(model.HasError);
        Assert.DoesNotContain("private-secret", model.ErrorText);
        Assert.False(model.IsNaming);
        Assert.True(model.CanRecord);
        Assert.Equal(id, model.Sessions[0].Id);
        model.SelectedSession = model.Sessions[0];
        Assert.True(model.CanPrepare);
        fake.FailStart = false;
        Click(window, "RecordButton");
        Assert.True(model.IsRecording);
        Click(window, "RecordButton");
        Assert.True(await model.ShutdownAsync());
        Assert.False(model.CanStartNextRecording);
        Click(window, "StartNextRecording");
        Assert.Equal(2, fake.Starts);
        Close(window, model);
    }

    [AvaloniaTheory]
    [InlineData(TrimDecision.Accept)]
    [InlineData(TrimDecision.Skip)]
    public async Task DeferredTrimCanResumeAfterRestartAndChooseWithoutAutomaticSummary(TrimDecision decision)
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        Click(window, "PrepareSession");
        Assert.True(model.IsNaming);
        Click(window, "ContinuePreparation");
        Click(window, decision == TrimDecision.Accept ? "AcceptTrim" : "SkipTrim");
        Assert.Equal(decision, fake.Decision);
        Assert.False(model.ShowTrim);
        Assert.Equal(0, fake.Summaries);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task WindowCloseHidesDuringRecordingAndExplicitExitWaitsForFinalization()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync(); Click(window, "RecordButton");
        window.Close();
        Assert.False(window.IsVisible);
        Assert.True(fake.Snapshot.Capture.IsActive);
        Assert.Equal(0, fake.Shutdowns);
        fake.ShutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> exit = model.ShutdownAsync();
        Assert.False(exit.IsCompleted);
        Assert.True(model.IsBusy);
        fake.ShutdownCompletion.SetResult();
        Assert.True(await exit);
        Assert.False(fake.Snapshot.Capture.IsActive);
        Assert.Equal(1, fake.Shutdowns);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task CaptureFailuresAndWarningsStayVisibleAndProcessingControlsAreDisabledWhileRecording()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        Click(window, "RecordButton");
        Assert.False(window.FindControl<Button>("ResumeSession")!.IsEnabled);
        fake.Report(new(NoticeKind.Warning, "Them: invalid WASAPI timestamp. Recording continues."));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Recording continues", window.FindControl<TextBlock>("Warnings")!.Text);
        fake.Snapshot = fake.Snapshot with { Capture = new(false, TimeSpan.FromSeconds(9), 0, 0, "Device stopped; originals retained.") };
        await model.RefreshAsync();
        Assert.Contains("Device stopped", window.FindControl<TextBlock>("ErrorMessage")!.Text);
        Assert.Contains("Recording continues", model.WarningText);
        fake.FailStart = true;
        Click(window, "RecordButton");
        Assert.DoesNotContain("private-secret", model.ErrorText);
        Assert.Equal(0, fake.Summaries);
        Close(window, model);
    }

    [AvaloniaFact]
    [Trait("Category", "WindowsOnly")]
    public async Task SecondStartupReopensHiddenWindowWithoutInterruptingExistingRecording()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        string primaryName = @"Local\MeetTape.UiTest." + Guid.NewGuid().ToString("N");
        using var primary = new DesktopInstance(primaryName);
        try
        {
            window.Show();
            await model.RefreshAsync();
            await model.ToggleAsync();
            Assert.True(model.IsRecording);
            window.Close();
            Assert.False(window.IsVisible);
            primary.Listen(() => Dispatcher.UIThread.Post(window.Show));
            await Task.Run(() =>
            {
                using var second = new DesktopInstance(primaryName);
                Assert.False(second.IsPrimary);
                second.ActivateExisting();
            });
            await WaitFor(() => window.IsVisible);
            Assert.True(model.IsRecording);
            Assert.Equal(1, fake.Starts);
            Assert.Equal(0, fake.Stops);
            Assert.Equal(0, fake.Shutdowns);
        }
        finally { Close(window, model); }
    }

    [AvaloniaFact]
    public async Task RetryProgressResumeAndSummaryRequireExplicitActions()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var data = session.Data!;
        data = data with { Retry = new(3, 2, DateTimeOffset.UtcNow.AddSeconds(20)),
            Stages = new(data.Stages) { ["trim"] = new(false, StageState.Disabled), ["transcribe"] = new(true, StageState.Running, 2, 5) } };
        fake.Snapshot = fake.Snapshot with { Sessions = [session with { Data = data }] };
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Assert.Contains("Retry 2", model.Sessions[0].Status);
        Assert.Equal("Chunks: 2/5", model.Sessions[0].Progress);
        Assert.Equal(40, model.Sessions[0].ProgressPercent);
        Assert.Equal(0, fake.Resumes); Assert.Equal(0, fake.Summaries);
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        Click(window, "ResumeSession");
        Assert.Equal(0, fake.Resumes);
        fake.Snapshot = fake.Snapshot with { Sessions = [session with { Data = data with
        {
            JobState = JobState.Paused,
            Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Paused, 2, 5) }
        } }] };
        await model.RefreshAsync();
        Click(window, "ResumeSession");
        Assert.Equal(1, fake.Resumes);
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "transcription.md"), "Fixture transcript");
        fake.Snapshot = fake.Snapshot with { Sessions = [session with { Data = Published(data) }] };
        await model.RefreshAsync();
        Click(window, "RequestSummary");
        Assert.Equal(1, fake.Summaries);
        Click(window, "RequestSummary");
        Assert.Equal(1, fake.Summaries);
        Close(window, model);
    }

    [AvaloniaTheory]
    [InlineData("paused", true)]
    [InlineData("paused-with-retry", true)]
    [InlineData("retry", false)]
    [InlineData("completed", false)]
    [InlineData("interrupted", false)]
    [InlineData("failed", false)]
    [InlineData("invalid", false)]
    [InlineData("trim", false)]
    public async Task ResumeOnlyOffersRecoverablePausedProcessing(string state, bool allowed)
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var data = session.Data! with { JobState = JobState.Paused,
            Stages = new(session.Data!.Stages) { ["trim"] = new(false, StageState.Disabled), ["transcribe"] = new(true, StageState.Paused) } };
        data = state switch
        {
            "paused-with-retry" => data with { Retry = new(1, 2, DateTimeOffset.UtcNow.AddMinutes(1)) },
            "retry" => data with { JobState = JobState.NotStarted, Retry = new(1, 2, DateTimeOffset.UtcNow.AddMinutes(1)),
                Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Running) } },
            "completed" => Published(data),
            "interrupted" => data with { RecordingState = RecordingState.Interrupted },
            "failed" => data with { RecordingState = RecordingState.Failed },
            "trim" => data with { Stages = new(data.Stages) { ["trim"] = new(true, StageState.AwaitingUser) } },
            _ => data
        };
        fake.Snapshot = fake.Snapshot with { Sessions = [session with { Data = state == "invalid" ? null : data }] };
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        Assert.Equal(allowed, window.FindControl<Button>("ResumeSession")!.IsEnabled);
        Click(window, "ResumeSession");
        Assert.Equal(allowed ? 1 : 0, fake.Resumes);
        Close(window, model);
    }

    [AvaloniaTheory]
    [InlineData("ready", true)]
    [InlineData("disabled", false)]
    [InlineData("incomplete", false)]
    [InlineData("unpublished", false)]
    [InlineData("pending", false)]
    [InlineData("running", false)]
    [InlineData("paused", false)]
    [InlineData("saved", false)]
    public async Task SummaryRequiresCurrentPermissionAndPublishedTranscriptWithoutExistingRequest(string state, bool allowed)
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var data = Published(session.Data!);
        if (state == "incomplete") data = data with { Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Pending) } };
        if (state == "unpublished") data = data with { TranscriptSha256 = null };
        if (state is "pending" or "running" or "paused") data = data with
        { Stages = new(data.Stages) { ["summary"] = new(true, Enum.Parse<StageState>(state, true)) } };
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "transcription.md"), "Fixture transcript");
        if (state == "saved") File.WriteAllText(Path.Combine(fixture.SessionDirectory, "summary.md"), "Fixture summary");
        fake.Snapshot = fake.Snapshot with { SummaryEnabled = state != "disabled", Sessions = [session with { Data = data }] };
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        Assert.Equal(allowed, window.FindControl<Button>("RequestSummary")!.IsEnabled);
        Click(window, "RequestSummary");
        Assert.Equal(allowed ? 1 : 0, fake.Summaries);
        if (allowed)
        {
            await model.RefreshAsync();
            Assert.False(window.FindControl<Button>("RequestSummary")!.IsEnabled);
        }
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task ChangingSelectionAndBackgroundRefreshUpdateActions()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var first = fake.Snapshot.Sessions[0];
        var second = first with { Directory = Path.Combine(fixture.Root, "second"), Data = Published(first.Data!) with { Id = Guid.NewGuid() } };
        var paused = first with { Data = first.Data! with { JobState = JobState.Paused,
            Stages = new(first.Data!.Stages) { ["trim"] = new(false, StageState.Disabled), ["transcribe"] = new(true, StageState.Paused) } } };
        fake.Snapshot = fake.Snapshot with { Sessions = [paused, second] };
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        var list = window.FindControl<ListBox>("SessionList")!;
        list.SelectedIndex = 0;
        Assert.True(model.CanResume);
        list.SelectedIndex = 1;
        Assert.False(model.CanResume);
        list.SelectedIndex = 0;
        fake.Snapshot = fake.Snapshot with { Sessions = [first with { Data = Published(first.Data!) }, second] };
        await model.RefreshAsync();
        Assert.Equal(first.Data!.Id, model.SelectedSession!.Id);
        Assert.False(window.FindControl<Button>("ResumeSession")!.IsEnabled);
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "transcription.md"), "Fixture transcript");
        await model.RefreshAsync();
        Assert.True(window.FindControl<Button>("RequestSummary")!.IsEnabled);
        fake.Snapshot = fake.Snapshot with { SummaryEnabled = false };
        await model.RefreshAsync();
        Assert.False(window.FindControl<Button>("RequestSummary")!.IsEnabled);
        Close(window, model);
    }

    private static SessionData Published(SessionData data) => data with
    {
        JobState = JobState.Completed, Retry = null, TranscriptSha256 = new string('A', 64),
        Audio = new("processed", [new(1, "processed/chunk.mp3", 0, 48000, 0, 48000, 0)]),
        Stages = new(data.Stages) { ["trim"] = new(false, StageState.Disabled), ["transcribe"] = new(true, StageState.Completed, 1, 1),
            ["assemble"] = new(true, StageState.Completed), ["summary"] = new(true, StageState.AwaitingRequest) }
    };

    [AvaloniaFact]
    public async Task SettingsValidateBeforeSavingAndNeverSerializeOrDisplayExistingSecrets()
    {
        using var fixture = new Fixture();
        var writes = new List<(string Name, string Value, EnvironmentVariableTarget Target)>();
        var model = new SettingsViewModel(fixture.ConfigPath,
            [new("mic", "Fixture microphone", CaptureChannel.Me, true)],
            (name, value, target) => writes.Add((name, value, target)));
        string original = File.ReadAllText(fixture.ConfigPath);
        string prompt = File.ReadAllText(Path.Combine(fixture.Root, "prompts", "transcription.md"));
        Assert.Empty(model.NewTranscriptionKey);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        window.FindControl<TextBox>("ChunkDuration")!.Text = "9999";
        Click(window, "SaveSettings"); await WaitFor(() => model.CanSave);
        Assert.False(model.Saved); Assert.Empty(writes);
        Assert.Equal(original, File.ReadAllText(fixture.ConfigPath));
        Assert.Contains("chunkSeconds", window.FindControl<TextBlock>("SettingsMessage")!.Text);
        window.FindControl<TextBox>("ChunkDuration")!.Text = "90";
        window.FindControl<ComboBox>("MicrophoneChoice")!.SelectedIndex = 1;
        var key = window.FindControl<TextBox>("TranscriptionKey")!;
        Assert.Equal('●', key.PasswordChar);
        key.Text = "fixture-secret-4821";
        Click(window, "SaveSettings"); await WaitFor(() => model.Saved);
        Assert.Equal(2, writes.Count);
        Assert.Contains(writes, w => w.Target == EnvironmentVariableTarget.User);
        Assert.Contains(writes, w => w.Target == EnvironmentVariableTarget.Process);
        Assert.DoesNotContain("fixture-secret", File.ReadAllText(fixture.ConfigPath));
        Assert.Empty(key.Text!);
        Assert.Equal(prompt, File.ReadAllText(Path.Combine(fixture.Root, "prompts", "transcription.md")));
        Assert.Equal("mic", ConfigurationLoader.Load(fixture.ConfigPath).Settings.Capture.MicrophoneDeviceId);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyProcessingPromptDoesNotDisableRecordingOnGuiStartup(bool empty)
    {
        using var fixture = new Fixture();
        string prompt = Path.Combine(fixture.Root, "prompts", "transcription.md");
        if (empty) File.WriteAllText(prompt, " "); else File.Delete(prompt);
        var config = ConfigurationLoader.LoadForCapture(fixture.ConfigPath);
        var source = new CaptureSource();
        using var capture = new CaptureCoordinator(() => new(source, new CaptureSource()));
        using var http = new HttpClient(new NoRequests());
        var controller = new DesktopController(config, capture, http, _ => throw new Exception("No key read expected"));
        var model = new MainViewModel(controller);
        var window = new MainWindow { DataContext = model };
        try
        {
            await controller.InitializeAsync();
            window.Show();
            await model.RefreshAsync();
            Assert.True(window.FindControl<Button>("RecordButton")!.IsEnabled);
            await model.ToggleAsync();
            Assert.True(source.Started);
            Assert.True(capture.Status.IsActive);
            Assert.Empty(model.ErrorText);
        }
        finally { await controller.ShutdownAsync(); Close(window, model); }
    }

    private sealed class CaptureSource : NAudio.Wave.IWaveIn
    {
        public bool Started;
        public NAudio.Wave.WaveFormat WaveFormat { get; set; } = new(48000, 16, 1);
        public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped;
        public void StartRecording() => Started = true;
        public void StopRecording() => RecordingStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs());
        public void Dispose() { }
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new Exception("No API request expected");
    }

    [AvaloniaFact]
    public async Task SettingsRejectConcurrentChangesAndPreserveUserPrompt()
    {
        using var fixture = new Fixture();
        var model = new SettingsViewModel(fixture.ConfigPath, [], (_, _, _) => throw new Exception("No secret write expected"));
        model.ChunkSeconds = "90";
        string external = File.ReadAllText(fixture.ConfigPath).Replace("120", "80");
        File.WriteAllText(fixture.ConfigPath, external);
        await model.SaveAsync();
        Assert.False(model.Saved);
        Assert.Contains("changed during editing", model.Message);
        Assert.Equal(external, File.ReadAllText(fixture.ConfigPath));
    }

    [AvaloniaFact]
    public async Task CaptureLightDarkAndSettingsSnapshots()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../dist/screenshots"));
        Directory.CreateDirectory(output);
        foreach (var (theme, name) in new[] { (ThemeVariant.Light, "main-light.png"), (ThemeVariant.Dark, "main-dark.png") })
        {
            Application.Current!.RequestedThemeVariant = theme;
            window.Show(); await model.RefreshAsync();
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default); window.Hide();
        }
        var settings = new SettingsWindow { DataContext = new SettingsViewModel(fixture.ConfigPath, [], (_, _, _) => { }) };
        settings.Show();
        using (var frame = settings.CaptureRenderedFrame()) { Assert.NotNull(frame); frame.Save(Path.Combine(output, "settings.png"), PngBitmapEncoderOptions.Default); }
        settings.Close(); Close(window, model);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static void Click(Window window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Close(MainWindow window, MainViewModel model) { model.Detach(); window.AllowClose = true; window.Close(); }
    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeController : IDesktopController
    {
        public DesktopSnapshot Snapshot { get; set; }
        public int Starts, Stops, Summaries, Resumes, Shutdowns, Prepares;
        public bool FailStart;
        public string? Title;
        public TrimDecision? Decision;
        public double? StartSeconds, EndSeconds;
        public TaskCompletionSource? ShutdownCompletion;
        public TaskCompletionSource? PrepareCompletion;
        public event Action<OperationNotice>? Notice;
        public void Report(OperationNotice notice) => Notice?.Invoke(notice);
        public FakeController(Fixture fixture)
        {
            var config = ConfigurationLoader.Load(fixture.ConfigPath);
            var data = new SessionData
            {
                Id = Guid.NewGuid(), Title = "Design review", StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 30,
                RecordingState = RecordingState.Recorded,
                Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
                Processing = new(120, config.Settings.Transcription, config.Settings.Summary, config.Directory),
                Stages = new() { ["trim"] = new(true, StageState.AwaitingUser), ["transcribe"] = new(true, StageState.Pending),
                    ["summary"] = new(true, StageState.AwaitingRequest) }
            };
            Snapshot = new(new(false, TimeSpan.Zero, 0, 0, null),
                [new(fixture.SessionDirectory, data, true, true, null)], null, null, false, null) { SummaryEnabled = true };
        }
        public Task<DesktopSnapshot> ReadAsync() => Task.FromResult(Snapshot);
        public Task StartAsync()
        {
            if (FailStart) throw new IOException("private-secret diagnostic");
            Starts++; Snapshot = Snapshot with { Capture = new(true, TimeSpan.FromSeconds(9), 100, 100, null) }; return Task.CompletedTask;
        }
        public Task<string?> StopAsync()
        {
            Stops++; string directory = Snapshot.Sessions[0].Directory;
            Snapshot = Snapshot with { Capture = Snapshot.Capture with { IsActive = false }, AwaitingTitleDirectory = directory };
            return Task.FromResult<string?>(directory);
        }
        public async Task PrepareAsync(string directory, string? title)
        {
            Prepares++;
            if (PrepareCompletion is not null) await PrepareCompletion.Task;
            Title = title;
            Snapshot = Snapshot with { AwaitingTitleDirectory = null, PreparationDirectory = directory, Suggestion = new(2, 28, true) };
        }
        public Task ChooseTrimAsync(TrimDecision decision, double? start = null, double? end = null)
        {
            Decision = decision; StartSeconds = start; EndSeconds = end;
            var session = Snapshot.Sessions[0]; var data = session.Data!;
            Snapshot = Snapshot with { Sessions = [session with { Data = data with { Stages = new(data.Stages) { ["trim"] = new(true, StageState.Completed) } } }] };
            return Task.CompletedTask;
        }
        public Task ResumeAsync(string directory) { Resumes++; return Task.CompletedTask; }
        public Task SummaryAsync(string directory)
        {
            Summaries++;
            Snapshot = Snapshot with { Sessions = Snapshot.Sessions.Select(s => s.Directory == directory && s.Data is { } data
                ? s with { Data = data with { Stages = new(data.Stages) { ["summary"] = new(true, StageState.Pending) } } } : s).ToArray() };
            return Task.CompletedTask;
        }
        public async Task ShutdownAsync()
        {
            Shutdowns++; if (ShutdownCompletion is not null) await ShutdownCompletion.Task;
            Snapshot = Snapshot with { Capture = Snapshot.Capture with { IsActive = false } };
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../dist/ui-tests/" + Guid.NewGuid().ToString("N")));
        public string ConfigPath => Path.Combine(Root, "config.json");
        public string SessionDirectory => Path.Combine(Root, "Meetings", "fixture-session");
        public Fixture()
        {
            ConfigurationBootstrap.Initialize(ConfigPath, Path.Combine(Root, "Meetings"));
            Directory.CreateDirectory(SessionDirectory);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
