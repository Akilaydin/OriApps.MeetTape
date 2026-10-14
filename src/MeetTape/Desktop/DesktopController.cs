using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Diagnostics;
using OriApps.MeetTape.Platform.Windows;
using OriApps.MeetTape.Processing;
using OriApps.MeetTape.Sessions;
using OriApps.MeetTape.Settings;
namespace OriApps.MeetTape.Desktop;

public enum NoticeKind { Information, Warning, Error }
public sealed record OperationNotice(NoticeKind Kind, string Message, Guid? SessionId = null);
public enum TrimDecision { Accept, Skip, Manual }
public sealed record DesktopSnapshot(CaptureStatus Capture, IReadOnlyList<StoredSession> Sessions,
    string? PreparationDirectory, TrimSuggestion? Suggestion, bool Preparing, string? AwaitingTitleDirectory)
{
    public bool SummaryEnabled { get; init; }
}

public interface IDesktopController
{
    event Action<OperationNotice>? Notice;
    event Action<CaptureStatus>? RecordingChanged;
    Task<DesktopSnapshot> ReadAsync();
    Task StartAsync();
    Task<string?> StopAsync();
    Task PrepareAsync(string directory, string? title);
    Task ChooseTrimAsync(TrimDecision decision, double? start = null, double? end = null);
    Task ResumeAsync(string directory);
    Task CancelProcessingAsync(string directory);
    Task DeleteRecordingAsync(string directory, Guid id);
    Task SummaryAsync(string directory);
    Task ShutdownAsync();
}

// One gate owns all lifecycle transitions. Blocking capture/FFmpeg joins always run on a worker.
public sealed class DesktopController : IDesktopController
{
    private volatile LoadedConfiguration config;
    private readonly Mp3Recorder? recorder;
    private readonly CaptureCoordinator capture;
    private readonly PostRecordingWorkflow preparation;
    private readonly ProcessingJobs jobs;
    internal Func<string, Guid, string> RecycleRecording { get; set; } = (directory, id) => WindowsRecycleBin.Recycle(directory, id);
    private readonly SemaphoreSlim gate = new(1);
    private volatile bool closing;
    private string? awaitingTitle;
    private Task? shutdown;

    public event Action<OperationNotice>? Notice;
    public event Action<CaptureStatus>? RecordingChanged;

    public DesktopController(LoadedConfiguration config, CaptureCoordinator? capture = null,
        HttpClient? http = null, Func<ApiSettings, string>? readKey = null,
        Func<CaptureSettings, CaptureSources>? openSources = null)
    {
        this.config = config;
        if (capture is null)
        {
            recorder = new Mp3Recorder(config, openSources is null ? null : () => openSources(this.config.Settings.Capture));
            this.capture = recorder.Capture;
        }
        else this.capture = capture;
        preparation = new(config.Settings.Capture.FfmpegPath);
        jobs = new(config, () => this.capture.Status.IsActive || Volatile.Read(ref awaitingTitle) is not null,
            http: http, readKey: readKey);
        this.capture.Warning += Warn;
        this.capture.Failed += Fail;
        this.capture.Finished += Finished;
        this.capture.StateChanged += PublishRecording;
        preparation.Notice += Publish;
        jobs.Notice += Publish;
    }

    public Task InitializeAsync() => RunAsync(() => jobs.Start());
    internal Task ApplyConfigurationAsync(LoadedConfiguration updated) => RunAsync(() =>
    {
        recorder?.ApplyConfiguration(updated);
        preparation.ApplyFfmpeg(updated.Settings.Capture.FfmpegPath);
        jobs.ApplyConfiguration(updated);
        config = updated;
    });
    internal Task<CaptureStatus> ReadCaptureAsync() => Task.Run(() => capture.Status);

    public Task<DesktopSnapshot> ReadAsync() => Task.Run(() => new DesktopSnapshot(capture.Status,
        jobs.MeetingsRoots.SelectMany(root => SessionStore.Discover(root)).ToArray(), preparation.SelectedDirectory, preparation.Suggestion,
        !preparation.Completion.IsCompleted, Volatile.Read(ref awaitingTitle)) { SummaryEnabled = config.Settings.Stages.Summary });

    public Task StartAsync() => RunAsync(() =>
    {
        if (awaitingTitle is not null) throw new InvalidOperationException("Finish naming the stopped recording first.");
        try
        {
            jobs.PauseAndWait();
            preparation.CancelAndWait();
            capture.Start();
        }
        finally { if (!capture.Status.IsActive) jobs.Start(); }
    });

    public async Task<string?> StopAsync()
    {
        string? result = null;
        await RunAsync(() =>
        {
            capture.Stop();
            if (capture.Status.Error is null)
            {
                awaitingTitle = capture.RecordingDirectory;
                result = awaitingTitle = FinalizeFolderName();
            }
            else Fail(capture.Status.Error);
            if (awaitingTitle is null) jobs.Start();
        });
        return result;
    }

    private string? FinalizeFolderName()
    {
        if (capture.RecordingDirectory is not { } directory) return null;
        try
        {
            var data = SessionStore.Read(directory);
            capture.RecordingDirectory = PostRecordingWorkflow.SetTitle(directory, data.Title ?? "");
            return capture.RecordingDirectory;
        }
        catch (RecordingException error) { throw new InvalidOperationException(error.Message); }
    }

    public Task PrepareAsync(string directory, string? title) => RunAsync(() =>
    {
        RequireIdle();
        jobs.PauseAndWait();
        try
        {
            preparation.Begin(directory, title);
            if (awaitingTitle == directory) awaitingTitle = null;
            capture.RecordingDirectory = preparation.SelectedDirectory;
        }
        finally { jobs.Start(); }
    });

    public Task ChooseTrimAsync(TrimDecision decision, double? start = null, double? end = null) => RunAsync(() =>
    {
        RequireIdle();
        preparation.Choose(decision.ToString().ToLowerInvariant(), start, end);
        jobs.Start();
    });

    public Task ResumeAsync(string directory) => JobActionAsync(() => jobs.Resume(directory));
    public Task SummaryAsync(string directory) => JobActionAsync(() => jobs.RequestSummary(directory));

    public Task CancelProcessingAsync(string directory) => RunAsync(() =>
    {
        if (capture.Status.IsActive && string.Equals(capture.RecordingDirectory, directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stop this recording before canceling its processing.");
        if (string.Equals(preparation.SelectedDirectory, directory, StringComparison.OrdinalIgnoreCase))
            preparation.CancelAndWait();
        jobs.Cancel(directory);
        if (string.Equals(awaitingTitle, directory, StringComparison.OrdinalIgnoreCase)) awaitingTitle = null;
        jobs.Start();
    });

    public Task DeleteRecordingAsync(string directory, Guid id) => RunAsync(() =>
    {
        RequireIdle();
        if (awaitingTitle is not null) throw new InvalidOperationException("Finish naming the stopped recording before deleting.");
        if (!preparation.Completion.IsCompleted && string.Equals(preparation.SelectedDirectory, directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cancel this meeting's preparation before deleting.");
        jobs.RemoveInactiveSession(directory, () => SessionRemoval.Recycle(directory, id, jobs.MeetingsRoots, config.Directory, RecycleRecording,
            [config.Resolve(config.Settings.Transcription.PromptFile), config.Resolve(config.Settings.Summary.PromptFile)]));
        Publish(new(NoticeKind.Information, "Recording moved to Windows Recycle Bin.", id));
    });

    private Task JobActionAsync(Action action) => RunAsync(() =>
    {
        RequireIdle();
        if (awaitingTitle is not null) throw new InvalidOperationException("Finish naming the stopped recording first.");
        preparation.CancelAndWait();
        try { action(); }
        finally { jobs.Start(); }
    });

    private void RequireIdle()
    {
        if (capture.Status.IsActive) throw new InvalidOperationException("Stop recording before processing audio.");
    }

    private async Task RunAsync(Action action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closing) throw new InvalidOperationException("MeetTape is closing.");
            await Task.Run(action).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private void Finished(CaptureStatus status)
    {
        if (status.Error is null || closing) return;
        // Finished is raised under the capture lock. Do not take our gate or join jobs there.
        _ = RestartAfterFailureAsync();
    }

    private async Task RestartAfterFailureAsync()
    {
        try { await RunAsync(() => jobs.Start()).ConfigureAwait(false); }
        catch (Exception error)
        {
            AppDiagnostics.Error("processing", "Restart after capture", error);
            if (!closing) Fail(AppDiagnostics.UserMessage(error, "Deferred processing could not restart; restart MeetTape to resume saved jobs."));
        }
    }

    private void Warn(string message) => Publish(new(NoticeKind.Warning, message));
    private void PublishRecording(CaptureStatus status)
    {
        try { RecordingChanged?.Invoke(status); }
        catch (Exception) { /* Notifications cannot affect capture or finalization. */ }
    }
    private void Fail(string message) => Publish(new(NoticeKind.Error, message));
    private void Publish(OperationNotice notice)
    {
        try { Notice?.Invoke(notice); }
        catch (Exception) { /* UI diagnostics cannot interrupt capture or checkpoint saving. */ }
    }

    public Task ShutdownAsync()
    {
        lock (gate)
        {
            if (shutdown is null || shutdown.IsFaulted) shutdown = ShutdownCoreAsync();
            return shutdown;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        closing = true;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                // A failed job join must never skip finalizing the live MP3s.
                try { jobs.PauseAndWait(); }
                finally
                {
                    try { preparation.CancelAndWait(); }
                    finally
                    {
                        bool recording = capture.Status.IsActive;
                        capture.Dispose();
                        if (capture.Status.Error is null && (recording || awaitingTitle is not null))
                        {
                            awaitingTitle = capture.RecordingDirectory;
                            try { _ = FinalizeFolderName(); awaitingTitle = null; }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                            { AppDiagnostics.Error("recording", "Name finalized folder", error); Fail(AppDiagnostics.UserMessage(error, "Cannot apply readable folder name; finalized recording kept in its original folder. Check collisions and permissions.")); }
                        }
                    }
                }
                preparation.Dispose();
                jobs.Dispose();
                capture.Warning -= Warn;
                capture.Failed -= Fail;
                capture.Finished -= Finished;
                capture.StateChanged -= PublishRecording;
            }).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
