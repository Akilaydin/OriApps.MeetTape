using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Diagnostics;
using OriApps.MeetTape.Platform.Windows;
using OriApps.MeetTape.Processing;
using OriApps.MeetTape.Sessions;
using OriApps.MeetTape.Updates;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;

namespace OriApps.MeetTape.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDesktopController? controller;
    private readonly Action<OperationNotice>? notifyRecording;
    private string recordingShortcut = RecordingHotkey.DefaultShortcut;
    public string RecordingShortcut => recordingShortcut;
    public string RecordingShortcutText => recordingShortcut + " · Start / Stop";
    internal void SetRecordingShortcut(string shortcut)
    {
        string text = RecordingHotkey.Parse(shortcut).Text;
        if (recordingShortcut == text) return;
        recordingShortcut = text; Changed();
    }
    private bool notificationActive;
    private readonly Queue<OperationNotice> processingIssues = new();
    private DesktopSnapshot snapshot = new(new(false, TimeSpan.Zero, 0, 0, null), [], null, null, false, null);
    private bool refreshing;
    private bool busy;
    private bool updating;
    private bool checkingUpdates;
    private bool updateAvailable;
    private bool shutdownStarted;
    private SessionRow? selected;
    private SessionRow[] allSessions = [];
    private string searchText = "";
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText == value) return;
            searchText = value ?? "";
            UpdateSessionList();
            Changed();
        }
    }
    public SessionRow? SelectedSession { get => selected; set { selected = value; Changed(); } }
    public string CaptureText => snapshot.Capture.IsActive ? "Recording — " + snapshot.Capture.Duration.ToString(@"hh\:mm\:ss") :
        snapshot.Capture.Error is not null ? "Recording stopped with errors" : controller is null ? "Open Settings to configure MeetTape" : "Ready";
    public string RecordLabel => snapshot.Capture.IsActive ? "Stop recording" : "Start recording";
    public string CaptureHeading => IsRecording ? "Recording" : IsNaming ? "Recording saved" : IsPreparing ? "Preparing audio" :
        snapshot.Capture.Error is not null ? "Recording stopped with errors" : controller is null ? "Setup required" : "Ready";
    public string RecordingDuration => snapshot.Capture.Duration.ToString(@"hh\:mm\:ss");
    public string ProcessingText
    {
        get
        {
            int canceled = snapshot.Sessions.Count(s => s.Data?.JobState == JobState.Canceled);
            string active = ActiveProcessingText;
            return canceled == 0 ? active : active == "No active jobs" ? $"Processing canceled: {canceled}" : $"{active}; {canceled} canceled";
        }
    }
    private string ActiveProcessingText
    {
        get
        {
            if (IsRecording) return HasProcessingErrors || snapshot.Sessions.Any(s => s.Data is { RecordingState: RecordingState.Recorded, JobState: JobState.Paused })
                ? "Processing deferred; jobs paused" : "Processing deferred during recording";
            if (snapshot.AwaitingTitleDirectory is not null) return "Processing deferred: finish naming";
            var meetings = snapshot.Sessions.Select(s => s.Data).OfType<SessionData>()
                .Where(s => s.RecordingState == RecordingState.Recorded).ToArray();
            var jobs = meetings.Where(ProcessingJobs.Eligible).ToArray();
            int paused = meetings.Count(s => s.JobState == JobState.Paused);
            if (HasProcessingErrors && paused == 0) paused = 1;
            if (jobs.Length > 0 && paused > 0) return $"Processing: {jobs.Length} jobs; {paused} paused";
            if (paused > 0) return "Processing paused: open MeetTape";
            if (snapshot.Preparing) return "Preparing audio";
            if (jobs.Length > 0)
            {
                bool retrying = jobs.All(s => s.Retry is { } retry && retry.NextAttemptUtc > DateTimeOffset.UtcNow);
                if (retrying) return jobs.Length == 1 ? "Processing: retry scheduled" : $"Processing: {jobs.Length} jobs waiting to retry";
                if (jobs.Length > 1) return $"Processing: {jobs.Length} jobs";
                var job = jobs[0];
                if (job.Stages.TryGetValue("transcribe", out var transcription) && transcription.Status != StageState.Completed
                    && transcription.TotalChunks is > 0)
                    return $"Processing: {transcription.CompletedChunks}/{transcription.TotalChunks} chunks";
                return job.Stages.FirstOrDefault(s => s.Value.Status == StageState.Running).Key switch
                {
                    "prepare" => "Preparing audio",
                    "assemble" => "Assembling transcript",
                    "summary" => "Processing: Summary",
                    _ => "Processing queued"
                };
            }
            if (meetings.Any(s => s.JobState != JobState.Canceled && s.Stages.TryGetValue("trim", out var trim) && trim.Enabled && trim.Status != StageState.Completed))
                return "Waiting for trim choice";
            return meetings.Any(s => s.JobState == JobState.Completed) ? "Processing complete" : "No active jobs";
        }
    }
    public string TrayText => $"MeetTape — {CaptureText} · {ProcessingText}" +
        (HasError || HasProcessingErrors ? " · Error: open MeetTape" : "");
    public bool CanRecord => controller is not null && !busy && !updating && !shutdownStarted && NamingDirectory is null && snapshot.AwaitingTitleDirectory is null;
    public bool CanProcess => controller is not null && !busy && !updating && !shutdownStarted && !snapshot.Capture.IsActive && selected is not null && NamingDirectory is null;
    public bool CanPrepare => CanProcess && selected!.NeedsPreparation;
    public bool CanRecover => CanProcess && selected!.CanRecover && snapshot.AwaitingTitleDirectory is null
        && !(snapshot.Preparing && string.Equals(snapshot.PreparationDirectory, selected.Directory, StringComparison.OrdinalIgnoreCase));
    public bool CanResume => CanProcess && selected!.CanResume;
    public bool HasCancelableSelection => selected?.CanCancel == true;
    public bool CanCancelProcessing => controller is not null && !busy && !updating && !shutdownStarted && NamingDirectory is null
        && HasCancelableSelection && (!snapshot.Capture.IsActive || !string.Equals(selected!.Directory, snapshot.Capture.RecordingDirectory, StringComparison.OrdinalIgnoreCase));
    public bool CanCancelNamedRecording => CanName && controller is not null && !shutdownStarted && !snapshot.Capture.IsActive;
    public bool CanSummary => CanProcess && selected!.CanRequestSummary;
    public bool CanDeleteRecording => CanProcess && selected!.CanDelete && snapshot.AwaitingTitleDirectory is null
        && !(snapshot.Preparing && string.Equals(snapshot.PreparationDirectory, selected.Directory, StringComparison.OrdinalIgnoreCase));
    public string SummaryLabel => selected?.CanRestoreSummary == true ? "Restore Summary" : "Request Summary";
    public bool CanOpen => selected is not null;
    public bool CanOpenTranscript => selected?.HasTranscript == true;
    public bool CanOpenSummary => selected?.HasSummary == true;
    public bool CanCopyDocument => CanOpenTranscript || CanOpenSummary;
    public bool CanConfigure => !busy && !updating && !shutdownStarted;
    public bool CanUpdate => CanConfigure && !checkingUpdates && !snapshot.Capture.IsActive && NamingDirectory is null && snapshot.AwaitingTitleDirectory is null;
    public bool CanCheckUpdates => CanConfigure && !checkingUpdates;
    public bool CanApplyUpdate => CanUpdate && updateAvailable;
    private string currentVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public string CurrentVersion { get => currentVersion; internal set { currentVersion = value; Changed(); } }
    public string AvailableVersion { get; private set; } = "Not checked";
    private string updateStatus = "Check for updates to see availability.";
    public string UpdateStatus { get => updateStatus; internal set { updateStatus = value; Changed(); } }
    public bool IsUpdating => updating;
    public int UpdateProgress { get; private set; }
    public bool IsBusy => busy;
    public bool IsRecording => snapshot.Capture.IsActive;
    public double MeLevel => IsRecording ? snapshot.Capture.MeLevel * 100 : 0;
    public double ThemLevel => IsRecording ? snapshot.Capture.ThemLevel * 100 : 0;
    public bool HasError => ErrorText.Length > 0;
    public string ErrorSummary => Brief(ErrorText);
    public bool HasProcessingIssues => processingIssues.Count > 0;
    public bool HasProcessingErrors => processingIssues.Any(n => n.Kind == NoticeKind.Error);
    public string ProcessingIssueSummary => HasProcessingErrors
        ? "Background processing reported an error. Open the affected meeting's Details."
        : "Background processing reported warnings. Open the affected meeting's Details.";
    public bool IsPreparing => snapshot.Preparing;
    public string ErrorText { get; private set; } = "";
    public string InfoText { get; private set; } = "Close hides the window. Use tray Exit to stop MeetTape.";
    private string releaseText = "";
    public string ReleaseText { get => releaseText; set { releaseText = value; Changed(); } }
    public string SelectedDiagnostic => selected?.Diagnostic ?? "";
    public string SelectedDiagnosticSummary => Brief(SelectedDiagnostic);
    public string SelectedMeetingDetails => selected is null ? "The selected meeting is no longer available." :
        $"{selected.Title}\n{selected.Date}\nDuration: {selected.Duration}\nStatus: {selected.DisplayStatus}\n{selected.Status}\n{selected.Progress}\n\n{selected.Details}\n\n{selected.Diagnostic}";
    private static string Brief(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        string first = end < 0 ? text : text[..end];
        int sentence = first.IndexOf(". ", StringComparison.Ordinal);
        if (sentence >= 0) first = first[..(sentence + 1)];
        return first.Length <= 180 ? first : first[..180] + "…";
    }
    public string? NamingDirectory { get; private set; }
    public bool IsNaming => NamingDirectory is not null;
    public string MeetingTitle { get; set; } = "";
    public bool CanName => IsNaming && !busy && !updating;
    public bool CanStartNextRecording => CanName && controller is not null && !shutdownStarted && !snapshot.Capture.IsActive;
    public bool ShowTrim => snapshot.Suggestion is not null && snapshot.PreparationDirectory is { } directory
        && snapshot.Sessions.Any(s => s.Directory == directory && s.Data?.Stages.TryGetValue("trim", out var trim) == true
            && s.Data.JobState != JobState.Canceled && trim.Enabled && trim.Status != StageState.Completed);
    public bool CanTrim => ShowTrim && !busy && !updating && !snapshot.Preparing && !snapshot.Capture.IsActive;
    public string TrimText => snapshot.Suggestion is { } found ?
        FormattableString.Invariant($"Suggested interval: {found.StartSeconds:F3} – {found.EndSeconds:F3} seconds. Originals retained.") : "";
    public string TrimStart { get; set; } = "";
    public string TrimEnd { get; set; } = "";
    public string PreparationText => snapshot.Preparing ? "Preparing audio… Start can cancel preparation." : "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(IDesktopController? controller = null, Action<OperationNotice>? notifyRecording = null)
    {
        this.controller = controller;
        this.notifyRecording = notifyRecording;
        if (controller is not null)
        {
            controller.Notice += OnNotice;
            controller.RecordingChanged += OnRecordingChanged;
        }
    }

    private void OnRecordingChanged(CaptureStatus status) => Dispatcher.UIThread.Post(() =>
    {
        if (status.IsActive == notificationActive) return;
        notificationActive = status.IsActive;
        SendRecordingNotification(new(status.Error is null ? NoticeKind.Information : NoticeKind.Error,
            status.IsActive ? "Recording started" : status.Error is null ? "Recording stopped" : "Recording interrupted"));
    });

    private void SendRecordingNotification(OperationNotice notice)
    {
        try { notifyRecording?.Invoke(notice); }
        catch (Exception) { SetInfo("Windows notification unavailable. Use the window or tray for recording status."); }
    }

    private void OnNotice(OperationNotice notice) => Dispatcher.UIThread.Post(() =>
    {
        if (notice.SessionId is not null)
        {
            if (notice.Kind == NoticeKind.Information)
            {
                ClearProcessingIssues(notice.SessionId.Value);
                Changed();
                return;
            }
            if (processingIssues.Contains(notice)) return;
            if (processingIssues.Count == 8) processingIssues.Dequeue();
            processingIssues.Enqueue(notice);
        }
        else if (notice.Kind == NoticeKind.Error) ErrorText = notice.Message;
        else if (notice.Kind == NoticeKind.Warning) ErrorText = notice.Message;
        else InfoText = notice.Message;
        Changed();
    });

    private void ClearProcessingIssues(Guid id)
    {
        int count = processingIssues.Count;
        for (int i = 0; i < count; i++)
        {
            var issue = processingIssues.Dequeue();
            if (issue.SessionId != id) processingIssues.Enqueue(issue);
        }
    }

    public async Task RefreshAsync()
    {
        if (controller is null || refreshing) return;
        refreshing = true;
        try
        {
            var updated = await controller.ReadAsync();
            if (updated.Suggestion != snapshot.Suggestion && updated.Suggestion is { } suggestion)
            {
                TrimStart = suggestion.StartSeconds.ToString("F3", CultureInfo.InvariantCulture);
                TrimEnd = suggestion.EndSeconds.ToString("F3", CultureInfo.InvariantCulture);
            }
            snapshot = updated;
            foreach (var meeting in updated.Sessions)
                if (meeting.Data is { JobState: JobState.Completed or JobState.Canceled, Error: null } completed) ClearProcessingIssues(completed.Id);
            allSessions = updated.Sessions.OrderByDescending(s => s.Data?.StartedAtUtc).Select(session =>
            {
                var row = SessionRow.From(session);
                return updated.Capture.IsActive && string.Equals(session.Directory, updated.Capture.RecordingDirectory, StringComparison.OrdinalIgnoreCase)
                    ? row with { Duration = updated.Capture.Duration.ToString(updated.Capture.Duration.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss") } : row;
            }).ToArray();
            UpdateSessionList();
            if (snapshot.AwaitingTitleDirectory is { } awaiting && NamingDirectory is null)
                BeginNaming(awaiting, null);
            if (snapshot.Capture.Error is { } error) ErrorText = error;
            Changed();
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("sessions", "Refresh", error);
            SetError(AppDiagnostics.UserMessage(error, "Cannot refresh sessions; check meeting folder and metadata permissions."));
        }
        finally { refreshing = false; }
    }

    private static bool SameMeeting(SessionRow a, SessionRow b) => a.Id is { } id && b.Id is not null ? id == b.Id :
        string.Equals(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase);

    private void UpdateSessionList()
    {
        var previousSelection = selected;
        string query = searchText.Trim();
        var rows = allSessions.Where(s => s.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || s.Date.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (!rows.Any(row => SameMeeting(row, Sessions[i]))) Sessions.RemoveAt(i);
        for (int i = 0; i < rows.Length; i++)
        {
            int existing = -1;
            for (int j = i; j < Sessions.Count; j++)
                if (SameMeeting(Sessions[j], rows[i])) { existing = j; break; }
            if (existing < 0) Sessions.Insert(i, rows[i]);
            else
            {
                if (existing != i) Sessions.Move(existing, i);
                if (Sessions[i] != rows[i]) Sessions[i] = rows[i];
            }
        }
        selected = previousSelection is null ? null : Sessions.FirstOrDefault(s => SameMeeting(s, previousSelection));
    }

    internal void RefreshCapture(CaptureStatus capture)
    {
        if (capture == snapshot.Capture) return;
        snapshot = snapshot with { Capture = capture };
        if (capture.Error is { } error) ErrorText = error;
        Changed();
    }

    public Task ToggleAsync() => ExecuteAsync(async () =>
    {
        if (snapshot.Capture.IsActive)
        {
            string? directory = await controller!.StopAsync();
            if (directory is not null) BeginNaming(directory, null);
        }
        else
        {
            await controller!.StartAsync();
        }
    });

    // Notification actions must never use ToggleAsync: it stops an existing recording.
    public async Task<bool> StartOnlyAsync()
    {
        if (!CanRecord || IsRecording) return false;
        bool started = false;
        await ExecuteAsync(async () =>
        {
            if ((await controller!.ReadAsync()).Capture.IsActive) return;
            await controller.StartAsync();
            started = (await controller.ReadAsync()).Capture.IsActive;
        });
        return started;
    }

    public Task ToggleFromHotkeyAsync()
    {
        if (CanRecord) return ToggleAsync();
        if (IsNaming || snapshot.AwaitingTitleDirectory is not null)
        {
            SetInfo("Finish Name / Trim in the MeetTape window before starting another recording.");
            SendRecordingNotification(new(NoticeKind.Warning, "Name / Trim needed in MeetTape"));
        }
        return Task.CompletedTask;
    }

    public void PrepareSelected()
    {
        if (CanPrepare) BeginNaming(selected!.Directory, selected.Title == "Unnamed meeting" ? "" : selected.Title);
    }

    private void BeginNaming(string directory, string? title)
    {
        NamingDirectory = directory; MeetingTitle = title ?? ""; Changed();
    }

    public Task ContinuePreparationAsync() => ExecuteAsync(async () =>
    {
        if (NamingDirectory is null) return;
        await controller!.PrepareAsync(NamingDirectory, MeetingTitle);
        NamingDirectory = null;
    });

    public Task StartNextRecordingAsync()
    {
        if (!CanStartNextRecording) return Task.CompletedTask;
        return ExecuteAsync(async () =>
        {
            await controller!.PrepareAsync(NamingDirectory!, MeetingTitle);
            NamingDirectory = null;
            await controller.StartAsync();
        });
    }

    public Task ChooseAsync(TrimDecision decision) => ExecuteAsync(async () =>
    {
        double? start = null, end = null;
        if (decision == TrimDecision.Manual)
        {
            if (!AudioTrimmer.TryParseTime(TrimStart, out double a) || !AudioTrimmer.TryParseTime(TrimEnd, out double b))
                throw new InvalidOperationException("Enter seconds, MM:SS or HH:MM:SS for both trim boundaries.");
            start = a; end = b;
        }
        await controller!.ChooseTrimAsync(decision, start, end);
    });

    public Task ResumeAsync() => CanResume ? ExecuteAsync(() => controller!.ResumeAsync(selected!.Directory)) : Task.CompletedTask;
    public Task RecoverAsync()
    {
        if (!CanRecover || selected is not { Id: { } id } session) return Task.CompletedTask;
        return ExecuteAsync(async () =>
        {
            await controller!.RecoverAsync(session.Directory, id);
            InfoText = "Recording recovered. Continue with Name / Trim or Resume processing.";
        });
    }
    public Task SummaryAsync() => CanSummary ? ExecuteAsync(() => controller!.SummaryAsync(selected!.Directory)) : Task.CompletedTask;
    public Task CancelSelectedProcessingAsync() => CanCancelProcessing
        ? ExecuteAsync(() => controller!.CancelProcessingAsync(selected!.Directory)) : Task.CompletedTask;
    public Task CancelNamedRecordingAsync() => CanCancelNamedRecording ? ExecuteAsync(async () =>
    {
        await controller!.CancelProcessingAsync(NamingDirectory!);
        NamingDirectory = null;
    }) : Task.CompletedTask;

    public Task DeleteSelectedRecordingAsync(Func<SessionRow, Task<bool>> confirm)
    {
        if (!CanDeleteRecording || selected is not { Id: { } id } recording) return Task.CompletedTask;
        return ExecuteAsync(async () =>
        {
            if (!await confirm(recording)) return;
            await RefreshAsync();
            if (selected?.Id != id || !string.Equals(selected.Directory, recording.Directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected recording changed. Refresh and confirm it again.");
            await controller!.DeleteRecordingAsync(recording.Directory, id);
            selected = null;
            ClearProcessingIssues(id);
            InfoText = "Recording moved to Windows Recycle Bin.";
        });
    }

    public async Task ExecuteAsync(Func<Task> action)
    {
        if (busy || updating || shutdownStarted || controller is null) return;
        busy = true; ErrorText = ""; Changed();
        string? operationError = null;
        try { await action(); }
        catch (Exception error)
        {
            AppDiagnostics.Error("ui", "Recording/session operation", error);
            operationError = AppDiagnostics.UserMessage(error, "Operation failed; audio and saved checkpoints retained. Check devices, FFmpeg and storage permissions.");
            SetError(operationError);
        }
        finally
        {
            busy = false; await RefreshAsync();
            if (operationError is not null) ErrorText = operationError;
            Changed();
        }
    }

    public async Task CheckUpdatesAsync(IAppUpdates updates)
    {
        if (!CanCheckUpdates) return;
        checkingUpdates = true; updateAvailable = false; UpdateStatus = "Checking for updates..."; Changed();
        try
        {
            CurrentVersion = updates.CurrentVersion;
            if (!updates.IsInstalled)
            {
                AvailableVersion = "Unavailable";
                UpdateStatus = "Updates require an installed MeetTape build. Download Setup from GitHub Releases.";
                return;
            }
            var update = await updates.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            updateAvailable = update is not null;
            AvailableVersion = update?.TargetFullRelease.Version.ToString() ?? "None";
            UpdateStatus = updateAvailable ? "Update available. Stop recording, then choose Update to review and confirm." : "No update available.";
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("updater", "Check only", error);
            AvailableVersion = "Unknown";
            UpdateStatus = AppDiagnostics.UserMessage(error, "Cannot check for updates. Check GitHub connectivity, then retry.");
        }
        finally { checkingUpdates = false; Changed(); }
    }

    public async Task<bool> UpdateAsync(IAppUpdates updates, Func<string, string, Task<bool>> confirm)
    {
        if (!CanUpdate) { SetError("Stop recording and finish the current operation before updating MeetTape."); return false; }
        updating = true; UpdateProgress = 0; ErrorText = ""; Changed();
        try
        {
            if (!updates.IsInstalled) { SetInfo("Updates require an installed MeetTape build. Download Setup from GitHub Releases."); return false; }
            SetInfo("Checking for updates…");
            AppDiagnostics.Info("updater", "Checking for updates");
            var update = await updates.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (update is null) { SetInfo("No update available."); return false; }
            if (!await confirm(updates.CurrentVersion, update.TargetFullRelease.Version.ToString()))
            { SetInfo("Update declined. Current version retained."); return false; }
            SetInfo("Downloading update…");
            AppDiagnostics.Info("updater", "Downloading approved update");
            await updates.DownloadAsync(update, value => Dispatcher.UIThread.Post(() =>
            { UpdateProgress = value; Changed(); }));
            // Read live state again after network/dialog awaits; never apply during capture.
            if (controller is not null && (await controller.ReadAsync()).Capture.IsActive)
            { SetError("Stop recording before applying the update. Downloaded update will not apply on startup."); return false; }
            if (!await ShutdownCoreAsync()) return false;
            updates.ApplyAndRestart(update);
            return true;
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("updater", shutdownStarted ? "Apply/restart" : "Check/download", error);
            SetError(AppDiagnostics.UserMessage(error, shutdownStarted ? "Cannot apply update. Exit and reopen MeetTape; saved checkpoints retained. Downloaded updates never apply automatically." :
                "Update failed. Check GitHub connectivity and disk space, then retry. Current version and saved data retained."));
            return false;
        }
        finally { updating = false; Changed(); }
    }

    public Task<bool> ShutdownAsync()
    {
        if (updating) { SetError("Wait for the update operation before exiting."); return Task.FromResult(false); }
        return ShutdownCoreAsync();
    }

    private async Task<bool> ShutdownCoreAsync()
    {
        if (busy) { SetError("Wait for the current operation before exiting."); return false; }
        shutdownStarted = true;
        busy = true; InfoText = "Closing: finalizing audio and saving processing checkpoints…"; Changed();
        try { if (controller is not null) await controller.ShutdownAsync(); return true; }
        catch (Exception error)
        {
            AppDiagnostics.Error("shutdown", "Finalize/join", error);
            SetError(AppDiagnostics.UserMessage(error, "Shutdown could not finish. Audio finalization was attempted; check FFmpeg and restart after this process exits."));
            return false;
        }
        finally { busy = false; Changed(); }
    }

    public void SetError(string message) { ErrorText = message; Changed(); }
    public void SetInfo(string message) { InfoText = message; Changed(); }
    public void Detach()
    {
        if (controller is not null)
        {
            controller.Notice -= OnNotice;
            controller.RecordingChanged -= OnRecordingChanged;
        }
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
}

public sealed record SessionRow(string Directory, Guid? Id, string Title, string Date, string Status,
    string Progress, double ProgressPercent, string? Diagnostic, bool NeedsPreparation, bool HasTranscript, bool HasSummary)
{
    public string Duration { get; init; } = "--:--";
    public string Details { get; init; } = "";
    internal bool PausedJob { get; init; }
    public string DisplayStatus => PausedJob ? "Paused" : Status is "Running" or "NotStarted" ? "Processing" :
        Status.StartsWith("Retry ", StringComparison.Ordinal) ? "Retry" : Status == "Awaiting trim choice" ? "Needs trim" : Status;
    public string StatusForeground => DisplayStatus switch
    {
        "Recording" or "Failed" or "Interrupted" or "Invalid metadata" => "#DB2644",
        "Completed" => "#078A3C", "Paused" or "Needs trim" => "#A65009", "Canceled" => "#69778E", _ => "#1674E8"
    };
    public string StatusBackground => DisplayStatus switch
    {
        "Recording" or "Failed" or "Interrupted" or "Invalid metadata" => "#FFEBEF",
        "Completed" => "#E7F8ED", "Paused" or "Needs trim" => "#FFF0E3", "Canceled" => "#EEF1F5", _ => "#E8F2FF"
    };
    public bool CanResume { get; init; }
    public bool CanRecover { get; init; }
    public bool CanRequestSummary { get; init; }
    public bool CanRestoreSummary { get; init; }
    public bool CanCancel { get; init; }
    public bool CanDelete { get; init; }
    public static SessionRow From(StoredSession session)
    {
        var data = session.Data;
        StageProgress? trim = null, transcription = null;
        data?.Stages.TryGetValue("trim", out trim);
        data?.Stages.TryGetValue("transcribe", out transcription);
        bool needsPreparation = data?.RecordingState == RecordingState.Recorded && trim?.Enabled == true && trim.Status != StageState.Completed;
        string status = data is null ? "Invalid metadata" : data.RecordingState != RecordingState.Recorded ? data.RecordingState.ToString() :
            data.JobState == JobState.Canceled ? "Canceled" :
            needsPreparation ? "Awaiting trim choice" : data.Retry is { } retry ? $"Retry {retry.Attempts}: {retry.NextAttemptUtc.ToLocalTime():HH:mm:ss}" :
            data.JobState == JobState.Paused ? "Paused" : data.Stages.Values.All(s => !s.Enabled || s.Status is StageState.Completed or StageState.AwaitingRequest) ?
            "Completed" : data.JobState.ToString();
        string progress = transcription?.TotalChunks is { } total ? $"Chunks: {transcription.CompletedChunks}/{total}" :
            data?.Stages.FirstOrDefault(s => s.Value.Status == StageState.Running).Key is { } stage ? stage : "";
        bool hasTranscript = File.Exists(Path.Combine(session.Directory, "transcription.md"));
        bool hasSummary = File.Exists(Path.Combine(session.Directory, "summary.md"));
        StageProgress? summary = null;
        data?.Stages.TryGetValue("summary", out summary);
        bool canRestoreSummary = summary?.Status == StageState.Completed && !hasSummary && File.Exists(Path.Combine(session.Directory, "summary.json"));
        return new(session.Directory, data?.Id, data?.Title ?? "Unnamed meeting", data?.StartedAtUtc.ToLocalTime().ToString("g") ?? "",
            status, progress, transcription?.TotalChunks is > 0 ? 100d * transcription.CompletedChunks / transcription.TotalChunks.Value : 0,
            session.Diagnostic, needsPreparation, hasTranscript, hasSummary)
        {
            Duration = data?.DurationSeconds is { } seconds && seconds < TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss") : "--:--",
            Details = data is null ? "Metadata unavailable." : string.Join(Environment.NewLine,
                data.Stages.Select(stage => $"{stage.Key}: {stage.Value.Status}" +
                    (stage.Value.TotalChunks is { } count ? $" ({stage.Value.CompletedChunks}/{count} chunks)" : ""))),
            PausedJob = data is { RecordingState: RecordingState.Recorded, JobState: JobState.Paused },
            CanRecover = data is not null && SessionRecovery.CanRecover(data),
            CanResume = data is not null && ProcessingJobs.CanResume(data),
            CanCancel = data is not null && ProcessingJobs.CanCancel(data),
            CanDelete = data is not null && SessionRemoval.CanDelete(data),
            CanRequestSummary = data is not null && ProcessingJobs.HasPublishedTranscription(data) && hasTranscript && !hasSummary
                && data.JobState != JobState.Canceled
                && (canRestoreSummary || summary is null || summary.Status is StageState.Disabled or StageState.AwaitingRequest),
            CanRestoreSummary = canRestoreSummary
        };
    }
}
