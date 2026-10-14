using System.Text;
using System.Globalization;

namespace OriApps.MeetTape;

public sealed class PostRecordingWorkflow(string ffmpegPath) : IDisposable
{
    private AudioTrimmer trimmer = new(ffmpegPath);
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task operation = Task.CompletedTask;
    private string? directory;
    private TrimSuggestion? suggestion;

    public Task Completion => operation;
    public string? SelectedDirectory => directory;
    public TrimSuggestion? Suggestion { get { lock (gate) return suggestion; } }
    public event Action<OperationNotice>? Notice;
    internal void ApplyFfmpeg(string path) => trimmer = new(path);

    public void Begin(string sessionDirectory, string? title = null)
    {
        CancelAndWait();
        using (var lease = Lease(sessionDirectory))
        {
            var data = SessionStore.Read(sessionDirectory);
            if (data.RecordingState != RecordingState.Recorded || data.DurationSeconds is not > 0)
                throw new InvalidOperationException("Only a successfully finalized recording can be prepared; verify incomplete originals first.");
            if (data.Stages.TryGetValue("trim", out var existing) && existing.Status == StageState.Completed)
                throw new InvalidOperationException("Trim choice already saved; originals and selected derivatives retained.");
        }
        if (title is not null) sessionDirectory = SetTitle(sessionDirectory, title);
        directory = sessionDirectory;
        lock (gate) suggestion = null;
        var session = SessionStore.Read(directory);
        if (session.JobState == JobState.Canceled)
        {
            using var lease = Lease(directory);
            session = SessionStore.Read(directory);
            SessionStore.Save(directory, session = session with { JobState = JobState.NotStarted, Error = null, Retry = null });
        }
        if (!session.Stages.TryGetValue("trim", out var trim) || !trim.Enabled)
        {
            Report(new(NoticeKind.Information, "Trimming disabled; original tracks selected.", session.Id));
            return;
        }
        string selected = directory;
        var currentTrimmer = trimmer;
        Start(async token =>
        {
            using var lease = Lease(selected);
            var data = SessionStore.Read(selected);
            var found = await currentTrimmer.SuggestAsync(selected, data.DurationSeconds!.Value, token);
            token.ThrowIfCancellationRequested();
            SessionStore.Save(selected, Update(data, StageState.AwaitingUser, error: null));
            lock (gate) suggestion = found;
            Report(new(NoticeKind.Information, "Trim suggestion ready; choose Accept, Skip or a manual interval.", data.Id));
            if (!found.HasActivity)
                Report(new(NoticeKind.Warning, "No activity detected; keep the full recording.", data.Id));
        });
        Report(new(NoticeKind.Information, "Detecting leading/trailing silence in both tracks.", session.Id));
    }

    public void Choose(string decision, double? start = null, double? end = null)
    {
        if (directory is null) throw new InvalidOperationException("Select a finalized recording before choosing its trim interval.");
        if (!operation.IsCompleted) throw new InvalidOperationException("Audio preparation is still running; start can cancel it.");
        operation.GetAwaiter().GetResult();
        string selected = directory;
        var data = SessionStore.Read(selected);
        if (!data.Stages.TryGetValue("trim", out var trim) || !trim.Enabled || trim.Status == StageState.Completed)
            throw new InvalidOperationException("Trimming disabled or choice already saved.");
        double duration = data.DurationSeconds!.Value;
        if (decision == "skip")
        {
            using var lease = Lease(selected);
            data = SessionStore.Read(selected);
            EnsurePending(data);
            SessionStore.Save(selected, Update(data with { Trim = new(0, duration, false) { Decision = "skip" } },
                StageState.Completed, error: null));
            Report(new(NoticeKind.Information, "Trim skipped; original tracks selected.", data.Id));
            return;
        }
        if (decision == "accept")
        {
            TrimSuggestion? found;
            lock (gate) found = suggestion;
            if (found is null) throw new InvalidOperationException("No suggestion available; reopen trim selection, skip, or choose a manual interval.");
            start = found.StartSeconds; end = found.EndSeconds;
        }
        else if (decision != "manual") throw new ArgumentException("Unknown trim decision.", nameof(decision));
        AudioTrimmer.Validate(start ?? double.NaN, end ?? double.NaN, duration);
        double a = start!.Value, b = end!.Value;
        var currentTrimmer = trimmer;
        Start(async token =>
        {
            using var lease = Lease(selected);
            var current = SessionStore.Read(selected);
            EnsurePending(current);
            SessionStore.Save(selected, Update(current, StageState.Running, error: null));
            TrimParameters choice = a == 0 && b == duration ? new(0, duration, false) { Decision = decision } :
                await currentTrimmer.ApplyAsync(selected, a, b, duration, decision, token);
            try
            {
                token.ThrowIfCancellationRequested();
                SessionStore.Save(selected, Update(current with { Trim = choice }, StageState.Completed, error: null));
            }
            catch
            {
                if (choice.RelativeDirectory is { } relative) AudioTrimmer.DiscardAttempt(selected, relative);
                throw;
            }
            Report(new(NoticeKind.Information, "Trim choice saved; originals retained.", current.Id));
        });
    }

    private void Start(Func<CancellationToken, Task> work)
    {
        cancellation?.Dispose();
        cancellation = new();
        var token = cancellation.Token;
        string selected = directory!;
        operation = Task.Run(async () =>
        {
            try { await work(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SavePaused(selected, "Audio preparation canceled; reopen trim selection to continue.", StageState.AwaitingUser);
            }
            catch (Exception ex) when (ex is not AudioPreparationShutdownException && ex is (InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException))
            {
                AppDiagnostics.Error("preparation", "Trim", ex);
                SavePaused(selected, AppDiagnostics.UserMessage(ex, "Audio preparation paused; check originals, FFmpeg, permissions and free space."), StageState.Paused);
            }
        });
    }

    private void SavePaused(string selected, string diagnostic, StageState state)
    {
        try
        {
            using var lease = Lease(selected);
            var data = SessionStore.Read(selected);
            if (data.Stages.TryGetValue("trim", out var progress) && progress.Status == StageState.Completed) return;
            SessionStore.Save(selected, Update(data, state, diagnostic) with { JobState = JobState.Paused });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { AppDiagnostics.Error("preparation", "Save paused state", ex); diagnostic += " Could not update session.json; prior state retained."; }
        Report(new(NoticeKind.Warning, diagnostic));
    }

    private void Report(OperationNotice notice)
    {
        AppDiagnostics.Info("preparation", notice.Message);
        try { Notice?.Invoke(notice); }
        catch (Exception) { /* Diagnostics must not change preparation outcomes. */ }
    }

    public void CancelAndWait()
    {
        cancellation?.Cancel();
        try { operation.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        { throw new AudioPreparationShutdownException("Audio preparation did not stop; capture remains blocked. Check FFmpeg and restart MeetTape."); }
    }

    private static SessionData Update(SessionData data, StageState state, string? error)
    {
        var stages = new Dictionary<string, StageProgress>(data.Stages);
        stages["trim"] = stages["trim"] with { Status = state };
        return data with { Stages = stages, Error = error, JobState = JobState.NotStarted };
    }

    private static void EnsurePending(SessionData data)
    {
        if (data.RecordingState != RecordingState.Recorded || data.DurationSeconds is not > 0
            || !data.Stages.TryGetValue("trim", out var trim) || !trim.Enabled || trim.Status == StageState.Completed)
            throw new InvalidOperationException("Session is incomplete, trimming disabled, or a choice has already been saved.");
    }

    private static FileStream Lease(string directory) => new(Path.Combine(directory, ".session.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static string SetTitle(string directory, string title)
    {
        using var lease = Lease(directory);
        var data = SessionStore.Read(directory);
        if (data.RecordingState != RecordingState.Recorded)
            throw new InvalidOperationException("Only a finalized recording can be named.");
        if (data.DurationSeconds is null)
            throw new InvalidOperationException("Finalized audio duration is missing; original files retained.");
        title = title.Trim();
        if (title.Length > 200 || title.Any(char.IsControl))
            throw new InvalidOperationException("Title must contain at most 200 characters and no control characters.");
        var safe = new StringBuilder();
        foreach (var rune in title.EnumerateRunes())
        {
            string text = rune.ToString();
            safe.Append(text.Length == 1 && Path.GetInvalidFileNameChars().Contains(text[0]) ? '_' : text);
        }
        string name = safe.ToString().Trim(' ', '.');
        var duration = TimeSpan.FromSeconds(data.DurationSeconds.Value);
        string source = Path.GetFullPath(directory);
        string baseDestination = Path.Combine(Path.GetDirectoryName(source)!,
            string.Create(CultureInfo.InvariantCulture,
                $"{(name.Length == 0 ? "Unnamed meeting" : name)}_{data.StartedAtUtc.ToLocalTime():dd.MM.yyyy HH-mm} {(long)duration.TotalHours:00}h{duration.Minutes:00}m"));
        string destination = baseDestination;
        // Save first: failure to rename still leaves title and identity discoverable in the old folder.
        SessionStore.Save(directory, data with { Title = title.Length == 0 ? null : title });
        // Windows directory rename requires closing every handle inside the folder, including our lease.
        lease.Dispose();
        int suffix = 1;
        while (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.Move(source, destination); break; }
            catch (IOException) when (Directory.Exists(source) && (Directory.Exists(destination) || File.Exists(destination)))
            {
                destination = baseDestination + " (" + (++suffix).ToString(CultureInfo.InvariantCulture) + ")";
            }
        }
        return destination;
    }

    public void Dispose()
    {
        try { CancelAndWait(); }
        finally { cancellation?.Dispose(); }
    }
}
