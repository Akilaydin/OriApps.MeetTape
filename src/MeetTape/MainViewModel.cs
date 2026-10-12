using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;

namespace MeetTape;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDesktopController? controller;
    private readonly Queue<string> warnings = new();
    private DesktopSnapshot snapshot = new(new(false, TimeSpan.Zero, 0, 0, null), [], null, null, false, null);
    private bool refreshing;
    private bool busy;
    private bool updating;
    private bool shutdownStarted;
    private SessionRow? selected;
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public SessionRow? SelectedSession { get => selected; set { selected = value; Changed(); } }
    public string CaptureText => snapshot.Capture.IsActive ? "Recording — " + snapshot.Capture.Duration.ToString(@"hh\:mm\:ss") :
        snapshot.Capture.Error is not null ? "Recording stopped with errors" : controller is null ? "Open Settings to configure MeetTape" : "Ready";
    public string RecordLabel => snapshot.Capture.IsActive ? "Stop" : "Start recording";
    public bool CanRecord => controller is not null && !busy && !updating && !shutdownStarted && NamingDirectory is null && snapshot.AwaitingTitleDirectory is null;
    public bool CanProcess => controller is not null && !busy && !updating && !shutdownStarted && !snapshot.Capture.IsActive && selected is not null && NamingDirectory is null;
    public bool CanPrepare => CanProcess && selected!.NeedsPreparation;
    public bool CanSummary => CanProcess && selected!.HasTranscript && !selected.HasSummary;
    public bool CanOpen => selected is not null;
    public bool CanOpenTranscript => selected?.HasTranscript == true;
    public bool CanOpenSummary => selected?.HasSummary == true;
    public bool CanConfigure => !busy && !updating && !shutdownStarted;
    public bool CanUpdate => CanConfigure && !snapshot.Capture.IsActive && NamingDirectory is null && snapshot.AwaitingTitleDirectory is null;
    public bool IsUpdating => updating;
    public int UpdateProgress { get; private set; }
    public bool IsBusy => busy;
    public bool IsRecording => snapshot.Capture.IsActive;
    public string WarningText => string.Join(Environment.NewLine, warnings);
    public bool HasWarnings => warnings.Count > 0;
    public bool HasError => ErrorText.Length > 0;
    public bool HasTiming => TimingText.Length > 0;
    public bool IsPreparing => snapshot.Preparing;
    public string ErrorText { get; private set; } = "";
    public string InfoText { get; private set; } = "Close hides the window. Use Exit to stop MeetTape.";
    private string releaseText = "";
    public string ReleaseText { get => releaseText; set { releaseText = value; Changed(); } }
    public string SelectedDiagnostic => selected?.Diagnostic ?? "";
    public string DevicesText
    {
        get
        {
            var data = snapshot.Sessions.FirstOrDefault(s => s.Directory == snapshot.Capture.RecordingDirectory)?.Data;
            return data is null ? "Me: microphone · Them: system playback" :
                $"Me: {data.Capture.MeDevice?.Name ?? "microphone"} · Them: {data.Capture.ThemDevice?.Name ?? "system playback"}";
        }
    }
    public string TimingText
    {
        get
        {
            var capture = snapshot.Capture;
            string text = capture.ClockAdjustments == 0 && capture.Discontinuities == 0 ? "" :
                FormattableString.Invariant($"Timing: Me {capture.MeTiming.Corrections} corrections, {capture.MeTiming.InvalidTimestamps} invalid timestamps, max shift {capture.MeTiming.LargestShiftFrames * 1000d / PcmTimeline.SampleRate:F3} ms; ") +
                FormattableString.Invariant($"Them {capture.ThemTiming.Corrections} corrections, {capture.ThemTiming.InvalidTimestamps} invalid timestamps, max shift {capture.ThemTiming.LargestShiftFrames * 1000d / PcmTimeline.SampleRate:F3} ms. Discontinuities: {capture.Discontinuities}.") +
                $" {capture.MeTiming.LastIssue} {capture.ThemTiming.LastIssue}";
            if (!capture.IsActive && capture.AudioFrames > Math.Round(capture.Duration.TotalSeconds * PcmTimeline.SampleRate))
                text += FormattableString.Invariant($" Recovered audio: {capture.AudioFrames / (double)PcmTimeline.SampleRate:F3}s saved; both tracks retained at the same length.");
            return text;
        }
    }
    public string? NamingDirectory { get; private set; }
    public bool IsNaming => NamingDirectory is not null;
    public string MeetingTitle { get; set; } = "";
    public bool CanName => IsNaming && !busy && !updating;
    public bool CanStartNextRecording => CanName && controller is not null && !shutdownStarted && !snapshot.Capture.IsActive;
    public bool ShowTrim => snapshot.Suggestion is not null && snapshot.PreparationDirectory is { } directory
        && snapshot.Sessions.Any(s => s.Directory == directory && s.Data?.Stages.TryGetValue("trim", out var trim) == true
            && trim.Enabled && trim.Status != StageState.Completed);
    public bool CanTrim => ShowTrim && !busy && !updating && !snapshot.Preparing && !snapshot.Capture.IsActive;
    public string TrimText => snapshot.Suggestion is { } found ?
        FormattableString.Invariant($"Suggested interval: {found.StartSeconds:F3} – {found.EndSeconds:F3} seconds. Originals retained.") : "";
    public string TrimStart { get; set; } = "";
    public string TrimEnd { get; set; } = "";
    public string PreparationText => snapshot.Preparing ? "Preparing audio… Start can cancel preparation." : "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(IDesktopController? controller = null)
    {
        this.controller = controller;
        if (controller is not null) controller.Notice += OnNotice;
    }

    private void OnNotice(OperationNotice notice) => Dispatcher.UIThread.Post(() =>
    {
        if (notice.Kind == NoticeKind.Error) ErrorText = notice.Message;
        else if (notice.Kind == NoticeKind.Warning)
        {
            if (warnings.Count == 8) warnings.Dequeue();
            warnings.Enqueue(notice.Message);
        }
        else InfoText = notice.Message;
        Changed();
    });

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
            var rows = updated.Sessions.OrderByDescending(s => s.Data?.StartedAtUtc).Select(SessionRow.From).ToArray();
            if (!Sessions.SequenceEqual(rows))
            {
                Guid? id = selected?.Id;
                string? directory = selected?.Directory;
                Sessions.Clear();
                foreach (var row in rows) Sessions.Add(row);
                selected = Sessions.FirstOrDefault(s => id is not null ? s.Id == id : s.Directory == directory);
            }
            if (snapshot.AwaitingTitleDirectory is { } awaiting && NamingDirectory is null)
                BeginNaming(awaiting, null);
            if (snapshot.Capture.Error is { } error) ErrorText = error;
            Changed();
        }
        catch (Exception) { SetError("Cannot refresh sessions; check meeting folder and metadata permissions."); }
        finally { refreshing = false; }
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
            warnings.Clear();
            await controller!.StartAsync();
        }
    });

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
            warnings.Clear();
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

    public Task ResumeAsync() => ExecuteAsync(() => controller!.ResumeAsync(selected!.Directory));
    public Task SummaryAsync() => ExecuteAsync(() => controller!.SummaryAsync(selected!.Directory));

    public async Task ExecuteAsync(Func<Task> action)
    {
        if (busy || updating || shutdownStarted || controller is null) return;
        busy = true; ErrorText = ""; Changed();
        try { await action(); }
        catch (Exception ex) { SetError(ex is InvalidOperationException or InvalidDataException ? ex.Message :
            "Operation failed; audio and saved checkpoints retained. Check devices, FFmpeg and storage permissions."); }
        finally { busy = false; await RefreshAsync(); Changed(); }
    }

    public async Task<bool> UpdateAsync(IAppUpdates updates, Func<string, string, Task<bool>> confirm)
    {
        if (!CanUpdate) { SetError("Stop recording and finish the current operation before updating MeetTape."); return false; }
        updating = true; UpdateProgress = 0; ErrorText = ""; Changed();
        try
        {
            if (!updates.IsInstalled) { SetInfo("Updates require an installed MeetTape build. Download Setup from GitHub Releases."); return false; }
            SetInfo("Checking for updates…");
            var update = await updates.CheckAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (update is null) { SetInfo("No update available."); return false; }
            if (!await confirm(updates.CurrentVersion, update.TargetFullRelease.Version.ToString()))
            { SetInfo("Update declined. Current version retained."); return false; }
            SetInfo("Downloading update…");
            await updates.DownloadAsync(update, value => Dispatcher.UIThread.Post(() =>
            { UpdateProgress = value; Changed(); }));
            // Read live state again after network/dialog awaits; never apply during capture.
            if (controller is not null && (await controller.ReadAsync()).Capture.IsActive)
            { SetError("Stop recording before applying the update. Downloaded update will not apply on startup."); return false; }
            if (!await ShutdownCoreAsync()) return false;
            updates.ApplyAndRestart(update);
            return true;
        }
        catch (Exception)
        {
            SetError(shutdownStarted ? "Cannot apply update. Exit and reopen MeetTape; saved checkpoints retained. Downloaded updates never apply automatically." :
                "Update failed. Check GitHub connectivity and disk space, then retry. Current version and saved data retained.");
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
        catch (Exception) { SetError("Shutdown could not finish. Audio finalization was attempted; check FFmpeg and restart after this process exits."); return false; }
        finally { busy = false; Changed(); }
    }

    public void SetError(string message) { ErrorText = message; Changed(); }
    public void SetInfo(string message) { InfoText = message; Changed(); }
    public void Detach() { if (controller is not null) controller.Notice -= OnNotice; }
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
}

public sealed record SessionRow(string Directory, Guid? Id, string Title, string Date, string Status,
    string Progress, double ProgressPercent, string? Diagnostic, bool NeedsPreparation, bool HasTranscript, bool HasSummary)
{
    public static SessionRow From(StoredSession session)
    {
        var data = session.Data;
        StageProgress? trim = null, transcription = null;
        data?.Stages.TryGetValue("trim", out trim);
        data?.Stages.TryGetValue("transcribe", out transcription);
        bool needsPreparation = data?.RecordingState == RecordingState.Recorded && trim?.Enabled == true && trim.Status != StageState.Completed;
        string status = data is null ? "Invalid metadata" : data.RecordingState != RecordingState.Recorded ? data.RecordingState.ToString() :
            needsPreparation ? "Awaiting trim choice" : data.Retry is { } retry ? $"Retry {retry.Attempts}: {retry.NextAttemptUtc.ToLocalTime():HH:mm:ss}" :
            data.JobState == JobState.Paused ? "Paused" : data.Stages.Values.All(s => !s.Enabled || s.Status is StageState.Completed or StageState.AwaitingRequest) ?
            "Completed" : data.JobState.ToString();
        string progress = transcription?.TotalChunks is { } total ? $"Chunks: {transcription.CompletedChunks}/{total}" :
            data?.Stages.FirstOrDefault(s => s.Value.Status == StageState.Running).Key is { } stage ? stage : "";
        return new(session.Directory, data?.Id, data?.Title ?? "Unnamed meeting", data?.StartedAtUtc.ToLocalTime().ToString("g") ?? "",
            status, progress, transcription?.TotalChunks is > 0 ? 100d * transcription.CompletedChunks / transcription.TotalChunks.Value : 0,
            session.Diagnostic, needsPreparation, File.Exists(Path.Combine(session.Directory, "transcription.md")),
            File.Exists(Path.Combine(session.Directory, "summary.md")));
    }
}
