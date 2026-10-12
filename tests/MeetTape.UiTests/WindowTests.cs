using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Specialized;

[assembly: AvaloniaTestApplication(typeof(MeetTape.UiTests.TestApp))]

namespace MeetTape.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WindowTests
{
    [AvaloniaTheory]
    [InlineData("transcription.md")]
    [InlineData("summary.md")]
    public async Task CopyMarkdownReadsExactTextOnlyAfterAnExplicitActionAndPreservesFiles(string name)
    {
        using var fixture = new Fixture();
        const string markdown = "# PRIVATE fixture 🌍\r\n\r\n- **Decision**: keep Markdown unchanged.\n";
        string path = Path.Combine(fixture.SessionDirectory, name);
        File.WriteAllText(path, markdown);
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "session.json"), "Fixture metadata unchanged");
        File.WriteAllBytes(Path.Combine(fixture.SessionDirectory, "me.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(fixture.SessionDirectory, "them.mp3"), [4, 5, 6]);
        var originals = Directory.GetFiles(fixture.SessionDirectory).ToDictionary(p => p, File.ReadAllBytes);
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var copied = new List<string>();
        var window = new MainWindow { DataContext = model, WriteClipboard = text => { copied.Add(text); return Task.CompletedTask; } };
        await model.RefreshAsync();
        model.SelectedSession = model.Sessions[0];
        Assert.True(model.CanCopyDocument);
        Assert.Empty(copied);
        Assert.DoesNotContain("PRIVATE", model.InfoText);
        await window.CopyDocumentAsync(name);
        Assert.Equal(markdown, Assert.Single(copied));
        foreach (var (file, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(originals.Count, Directory.GetFiles(fixture.SessionDirectory).Length);
        Assert.DoesNotContain("PRIVATE", model.DiagnosticText);
        Assert.Equal(0, fake.Starts); Assert.Equal(0, fake.Resumes); Assert.Equal(0, fake.Summaries);
        Close(window, model);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingMarkdownAndClipboardFailureStaySafeWithoutLeakingContents(bool missing)
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.SessionDirectory, "transcription.md");
        const string markdown = "PRIVATE fixture transcript";
        File.WriteAllText(path, markdown);
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        int writes = 0;
        var window = new MainWindow { DataContext = model, WriteClipboard = _ =>
        {
            writes++;
            throw new InvalidOperationException("PRIVATE clipboard failure diagnostic");
        } };
        await model.RefreshAsync(); model.SelectedSession = model.Sessions[0];
        if (missing) File.Delete(path);
        await window.CopyDocumentAsync("transcription.md");
        Assert.True(model.HasError);
        Assert.DoesNotContain("PRIVATE", model.ErrorText);
        Assert.Equal(missing ? 0 : 1, writes);
        if (missing) Assert.False(model.CanOpenTranscript); else Assert.Equal(markdown, File.ReadAllText(path));
        model.SelectedSession = null;
        await window.CopyDocumentAsync("transcription.md");
        Assert.Equal(missing ? 0 : 1, writes);
        Assert.Equal(0, fake.Starts); Assert.Equal(0, fake.Summaries);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task CopyMenuEnablesExistingDocumentsAndCopiesThroughActualMenuItem()
    {
        using var fixture = new Fixture();
        var model = new MainViewModel(new FakeController(fixture));
        var copied = new List<string>();
        var window = new MainWindow { DataContext = model, WriteClipboard = text => { copied.Add(text); return Task.CompletedTask; } };
        window.Show(); await model.RefreshAsync();
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        var button = window.FindControl<Button>("CopyMenu")!;
        Assert.False(button.IsEnabled);
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "transcription.md"), "# PRIVATE fixture transcript");
        await model.RefreshAsync();
        Assert.True(button.IsEnabled);
        var menu = (MenuFlyout)button.Flyout!;
        menu.ShowAt(button); Dispatcher.UIThread.RunJobs();
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.True(items[0].IsEnabled);
        Assert.False(items[1].IsEnabled);
        Assert.Empty(copied);
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitFor(() => copied.Count == 1);
        Assert.Equal("# PRIVATE fixture transcript", copied[0]);
        menu.Hide(); Close(window, model);
    }

    [AvaloniaFact]
    public async Task SearchUsesTitlesAndDisplayedDatesAndClearsMissingSelectionWithoutReadingDocuments()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var first = session with { Data = session.Data! with { Title = "Roadmap review", StartedAtUtc = DateTimeOffset.UtcNow } };
        var second = session with { Directory = Path.Combine(fixture.Root, "second"), Data = session.Data! with
            { Id = Guid.NewGuid(), Title = "Standup", StartedAtUtc = first.Data!.StartedAtUtc.AddHours(-1) } };
        var unnamed = session with { Directory = Path.Combine(fixture.Root, "unnamed"), Data = session.Data! with
            { Id = Guid.NewGuid(), Title = null, StartedAtUtc = first.Data!.StartedAtUtc.AddDays(-1) } };
        fake.Snapshot = fake.Snapshot with { Sessions = [unnamed, first, second] };
        var model = new MainViewModel(fake);
        await model.RefreshAsync();
        Assert.Equal(new[] { first.Data!.Id, second.Data!.Id, unnamed.Data!.Id }, model.Sessions.Select(s => s.Id!.Value));
        model.SelectedSession = model.Sessions[0];
        model.SearchText = "  STAND  ";
        Assert.Equal(second.Data!.Id, Assert.Single(model.Sessions).Id);
        Assert.Null(model.SelectedSession);
        Assert.False(model.CanOpen); Assert.False(model.CanPrepare); Assert.False(model.CanResume);
        await model.ResumeAsync(); Assert.Equal(0, fake.Resumes);
        model.SearchText = "UNNAMED";
        Assert.Equal(unnamed.Data!.Id, Assert.Single(model.Sessions).Id);
        model.SearchText = "";
        string displayedDate = model.Sessions[1].Date;
        model.SearchText = displayedDate;
        Assert.Equal(second.Data!.Id, Assert.Single(model.Sessions).Id);
        model.SearchText = "";
        model.SelectedSession = model.Sessions[1];
        fake.Snapshot = fake.Snapshot with { Sessions = [first, unnamed] };
        await model.RefreshAsync();
        Assert.Null(model.SelectedSession);
        Assert.False(model.CanOpen); Assert.False(model.CanSummary);
        var newest = first with { Directory = Path.Combine(fixture.Root, "new"), Data = first.Data! with
            { Id = Guid.NewGuid(), Title = "New meeting", StartedAtUtc = first.Data!.StartedAtUtc.AddMinutes(1) } };
        fake.Snapshot = fake.Snapshot with { Sessions = [first, newest, unnamed] };
        await model.RefreshAsync();
        Assert.Equal(newest.Data!.Id, model.Sessions[0].Id);
        model.SearchText = "New meeting";
        fake.Snapshot = fake.Snapshot with { Sessions = [first, unnamed] };
        await model.RefreshAsync();
        Assert.Empty(model.Sessions);
        Assert.Equal(0, fake.Starts); Assert.Equal(0, fake.Summaries);
        model.Detach();
    }

    [AvaloniaFact]
    public async Task OtherMeetingProgressRefreshKeepsSelectedRowAndScrollPositionWithoutCollectionReset()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var basis = fake.Snapshot.Sessions[0];
        var meetings = Enumerable.Range(0, 40).Select(i => basis with
        {
            Directory = Path.Combine(fixture.Root, "meeting-" + i),
            Data = Published(basis.Data!) with { Id = Guid.NewGuid(), Title = "Meeting " + i,
                StartedAtUtc = basis.Data!.StartedAtUtc.AddMinutes(-i) }
        }).ToArray();
        fake.Snapshot = fake.Snapshot with { Sessions = meetings };
        var model = new MainViewModel(fake);
        var changes = new List<NotifyCollectionChangedAction>();
        model.Sessions.CollectionChanged += (_, e) => changes.Add(e.Action);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        var list = window.FindControl<ListBox>("SessionList")!;
        list.SelectedIndex = 20;
        list.ScrollIntoView(20); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(viewer.Offset.Y > 0);
        var offset = viewer.Offset;
        var selected = model.SelectedSession;
        var unchanged = model.Sessions[21];
        changes.Clear();
        var data = meetings[0].Data!;
        meetings[0] = meetings[0] with { Data = data with { JobState = JobState.Running,
            Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Running, 3, 8) } } };
        fake.Snapshot = fake.Snapshot with { Sessions = meetings };
        await model.RefreshAsync(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Same(selected, model.SelectedSession);
        Assert.Same(selected, list.SelectedItem);
        Assert.Same(unchanged, model.Sessions[21]);
        Assert.Equal(offset, viewer.Offset);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Replace }, changes);
        Assert.Equal("Chunks: 3/8", model.Sessions[0].Progress);
        Assert.True(model.CanOpen);
        window.FindControl<TextBox>("MeetingSearch")!.Text = "Meeting 20";
        Assert.Single(model.Sessions);
        Assert.Same(selected, model.SelectedSession);
        window.FindControl<TextBox>("MeetingSearch")!.Text = "not present";
        Assert.Empty(model.Sessions);
        Assert.Null(list.SelectedItem); Assert.False(model.CanOpen);
        window.FindControl<TextBox>("MeetingSearch")!.Text = "";
        Assert.Equal(40, model.Sessions.Count);
        Close(window, model);
    }

    [AvaloniaTheory]
    [InlineData("empty", "No active jobs")]
    [InlineData("queued", "Processing queued")]
    [InlineData("running", "Processing: 3/8 chunks")]
    [InlineData("completed", "Processing complete")]
    [InlineData("paused", "Processing paused: open MeetTape")]
    [InlineData("retry", "Processing: retry scheduled")]
    [InlineData("multiple", "Processing: 2 jobs")]
    [InlineData("recording", "Processing deferred during recording")]
    [InlineData("recording-paused", "Processing deferred; jobs paused")]
    [InlineData("trim", "Waiting for trim choice")]
    [InlineData("naming", "Processing deferred: finish naming")]
    [InlineData("preparing", "Preparing audio")]
    [InlineData("summary", "Processing: Summary")]
    public async Task TraySummarizesExistingSnapshotsWithoutPrivateContent(string state, string expected)
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var data = Published(session.Data!) with { Title = "PRIVATE meeting contents", JobState = JobState.NotStarted,
            Stages = new(Published(session.Data!).Stages) { ["transcribe"] = new(true, StageState.Pending) } };
        if (state is "running" or "retry") data = data with { JobState = state == "retry" ? JobState.NotStarted : JobState.Running,
            Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Running, 3, 8) },
            Retry = state == "retry" ? new(3, 2, DateTimeOffset.UtcNow.AddMinutes(1)) : null };
        if (state == "completed") data = Published(data);
        if (state is "paused" or "recording-paused") data = data with { JobState = JobState.Paused,
            Stages = new(data.Stages) { ["transcribe"] = new(true, StageState.Paused) } };
        if (state is "trim" or "preparing" or "naming") data = session.Data!;
        if (state == "summary") data = Published(data) with { JobState = JobState.Running,
            Stages = new(Published(data).Stages) { ["summary"] = new(true, StageState.Running) } };
        var first = session with { Data = data };
        fake.Snapshot = fake.Snapshot with
        {
            Sessions = state == "empty" ? [] : state == "multiple" ?
                [first, first with { Directory = Path.Combine(fixture.Root, "second"), Data = data with { Id = Guid.NewGuid() } }] : [first],
            Capture = new(state.StartsWith("recording"), TimeSpan.FromSeconds(9), 0, 0, null),
            AwaitingTitleDirectory = state == "naming" ? fixture.SessionDirectory : null,
            Preparing = state == "preparing"
        };
        var model = new MainViewModel(fake);
        await model.RefreshAsync();
        Assert.Equal(expected, model.ProcessingText);
        Assert.StartsWith("MeetTape — " + model.CaptureText, model.TrayText);
        Assert.DoesNotContain("PRIVATE", model.TrayText);
        Assert.DoesNotContain(data.Processing.Transcription.ApiKeyEnvironmentVariable, model.TrayText);
        Assert.True(model.TrayText.Length <= 127);
        Assert.Equal(0, fake.Starts); Assert.Equal(0, fake.Resumes); Assert.Equal(0, fake.Summaries);
        model.Detach();
    }

    [AvaloniaFact]
    public async Task TrayClearsRecoveredJobFailuresAndKeepsUnpersistedFailuresVisible()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var model = new MainViewModel(fake);
        await model.RefreshAsync();
        fake.Report(new(NoticeKind.Error, "Fixture storage failure: metadata could not be saved.", session.Data!.Id));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Processing paused: open MeetTape", model.ProcessingText);
        Assert.Contains("Error: open MeetTape", model.TrayText);
        fake.Report(new(NoticeKind.Information, "Job resumed with current settings.", session.Data!.Id));
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.HasProcessingErrors);
        fake.Report(new(NoticeKind.Error, "Fixture provider failure.", session.Data!.Id));
        Dispatcher.UIThread.RunJobs();
        fake.Snapshot = fake.Snapshot with { Sessions = [session with { Data = Published(session.Data!) }] };
        await model.RefreshAsync();
        Assert.Equal("Processing complete", model.ProcessingText);
        Assert.DoesNotContain("Error", model.TrayText);
        model.Detach();
    }

    [AvaloniaFact]
    public async Task AudioLevelBarsUpdateIndependentlyAndResetAcrossStopAndQuickStart()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        var levels = window.FindControl<Grid>("AudioLevels")!;
        var me = window.FindControl<ProgressBar>("MeLevel")!;
        var them = window.FindControl<ProgressBar>("ThemLevel")!;
        Assert.False(levels.IsVisible);
        Click(window, "RecordButton");
        model.RefreshCapture(fake.Snapshot.Capture with { MeLevel = 0.8, ThemLevel = 0.2 });
        Assert.True(levels.IsVisible);
        Assert.Equal(80d, me.Value);
        Assert.Equal(20d, them.Value);
        model.RefreshCapture(fake.Snapshot.Capture with { MeLevel = 0, ThemLevel = 0.4 });
        Assert.Equal(0d, me.Value);
        Assert.Equal(40d, them.Value);
        Assert.False(model.HasError);
        Assert.False(model.HasWarnings);
        Click(window, "RecordButton");
        Assert.False(levels.IsVisible);
        Assert.Equal(0d, me.Value);
        Assert.Equal(0d, them.Value);
        Click(window, "StartNextRecording");
        Assert.True(levels.IsVisible);
        Assert.Equal(0d, me.Value);
        Assert.Equal(0d, them.Value);
        Assert.Equal(2, fake.Starts);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task RecordingStatusStaysCompactWithTimingAndDevicesAvailableInCollapsedDetails()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var session = fake.Snapshot.Sessions[0];
        var data = session.Data! with { Capture = session.Data!.Capture with
        { MeDevice = new("mic", "Fixture microphone with a long device name", CaptureChannel.Me, true) } };
        fake.Snapshot = fake.Snapshot with
        {
            Sessions = [session with { Data = data }],
            Capture = new(true, TimeSpan.FromSeconds(9), 100, 100, null)
            {
                RecordingDirectory = fixture.SessionDirectory, ClockAdjustments = 2270,
                ThemTiming = new(2270, 2270, 0, 480, "Fixture QPC timing diagnostic")
            }
        };
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Assert.Equal("Recording — 00:00:09", window.FindControl<TextBlock>("CaptureStatus")!.Text);
        Assert.False(window.FindControl<Expander>("DiagnosticsExpander")!.IsExpanded);
        var details = window.FindControl<TextBox>("DiagnosticDetails")!;
        Assert.False(details.IsEffectivelyVisible && TopLevel.GetTopLevel(details) is not null);
        Assert.Contains("2270", details.Text);
        Assert.Contains("Fixture QPC timing diagnostic", details.Text);
        Assert.Contains("long device name", details.Text);
        Assert.True(window.FindControl<Button>("RecordButton")!.IsEnabled);
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task FullDiagnosticsCanBeSelectedAndCopiedEvenWhenTheVisibleErrorIsBrief()
    {
        using var fixture = new Fixture();
        var model = new MainViewModel(new FakeController(fixture));
        const string original = "Cannot start capture. Check microphone permissions and reconnect the selected device.\nOriginal diagnostic retained for investigation.";
        model.SetError(original);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Assert.Equal("Cannot start capture.", window.FindControl<TextBlock>("ErrorMessage")!.Text);
        Assert.True(window.FindControl<TextBlock>("ErrorMessage")!.IsVisible);
        window.FindControl<Expander>("DiagnosticsExpander")!.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var details = window.FindControl<TextBox>("DiagnosticDetails")!;
        Assert.True(details.IsReadOnly);
        Assert.True(details.Focus());
        details.SelectAll();
        Assert.Equal(model.DiagnosticText, details.SelectedText);
        Assert.Contains(original, details.SelectedText);
        var clipboard = window.Clipboard!;
        await clipboard.ClearAsync();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Assert.Equal(model.DiagnosticText, await clipboard.TryGetTextAsync());
        details.SelectionEnd = details.SelectionStart;
        await clipboard.ClearAsync();
        Click(window, "CopyDiagnostics");
        await WaitFor(() => model.InfoText == "Details copied.");
        Assert.Equal(model.DiagnosticText, await clipboard.TryGetTextAsync());
        await clipboard.ClearAsync();
        Close(window, model);
    }

    [AvaloniaFact]
    public async Task RepeatedCaptureWarningsAreBoundedAndBackgroundErrorsStaySeparate()
    {
        using var fixture = new Fixture();
        var fake = new FakeController(fixture);
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        Click(window, "RecordButton");
        const string warning = "Them: invalid WASAPI timestamp. Recording continues.";
        for (int i = 0; i < 20; i++) fake.Report(new(NoticeKind.Warning, warning));
        fake.Report(new(NoticeKind.Error, "Previous meeting: processing paused; saved chunks retained.", Guid.NewGuid()));
        fake.Report(new(NoticeKind.Information, "Job progress fixture diagnostic", Guid.NewGuid()));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(warning, model.WarningText);
        Assert.DoesNotContain("WASAPI", window.FindControl<TextBlock>("Warnings")!.Text);
        Assert.Empty(model.ErrorText);
        Assert.True(model.HasProcessingErrors);
        Assert.True(model.IsRecording);
        Assert.True(window.FindControl<TextBlock>("ProcessingIssues")!.IsVisible);
        Assert.Contains("processing paused; saved chunks retained", model.DiagnosticText);
        Assert.DoesNotContain("Job progress fixture", model.InfoText);
        for (int i = 0; i < 10; i++) fake.Report(new(NoticeKind.Warning, "Fixture warning " + i));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(8, model.WarningText.Split(Environment.NewLine).Length);
        Assert.DoesNotContain("Fixture warning 0" + Environment.NewLine, model.WarningText);
        Close(window, model);
    }

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
    [InlineData("restore", true)]
    [InlineData("lost-checkpoint", false)]
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
        if (state is "saved" or "restore" or "lost-checkpoint") data = data with
        { Stages = new(data.Stages) { ["summary"] = new(true, StageState.Completed) } };
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "transcription.md"), "Fixture transcript");
        if (state == "saved") File.WriteAllText(Path.Combine(fixture.SessionDirectory, "summary.md"), "Fixture summary");
        if (state == "restore") File.WriteAllText(Path.Combine(fixture.SessionDirectory, "summary.json"), "Fixture paid checkpoint");
        fake.Snapshot = fake.Snapshot with { SummaryEnabled = state != "disabled", Sessions = [session with { Data = data }] };
        var model = new MainViewModel(fake);
        var window = new MainWindow { DataContext = model };
        window.Show(); await model.RefreshAsync();
        window.FindControl<ListBox>("SessionList")!.SelectedIndex = 0;
        Assert.Equal(allowed, window.FindControl<Button>("RequestSummary")!.IsEnabled);
        Assert.Equal(state == "restore" ? "Restore Summary" : "Request Summary", window.FindControl<Button>("RequestSummary")!.Content);
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
