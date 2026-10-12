using System.Text;

namespace MeetTape;

public sealed class PostRecordingWorkflow(string ffmpegPath) : IDisposable
{
    private readonly AudioTrimmer trimmer = new(ffmpegPath);
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task operation = Task.CompletedTask;
    private string? directory;
    private TrimSuggestion? suggestion;

    public Task Completion => operation;
    public string? SelectedDirectory => directory;
    public TrimSuggestion? Suggestion { get { lock (gate) return suggestion; } }
    public event Action<OperationNotice>? Notice;

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
        if (!session.Stages.TryGetValue("trim", out var trim) || !trim.Enabled)
        {
            Report(new(NoticeKind.Information, "Trimming disabled; original tracks selected.", session.Id));
            return;
        }
        string selected = directory;
        Start(async token =>
        {
            using var lease = Lease(selected);
            var data = SessionStore.Read(selected);
            var found = await trimmer.SuggestAsync(selected, data.DurationSeconds!.Value, token);
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
        Start(async token =>
        {
            using var lease = Lease(selected);
            var current = SessionStore.Read(selected);
            EnsurePending(current);
            SessionStore.Save(selected, Update(current, StageState.Running, error: null));
            TrimParameters choice = a == 0 && b == duration ? new(0, duration, false) { Decision = decision } :
                await trimmer.ApplyAsync(selected, a, b, duration, decision, token);
            token.ThrowIfCancellationRequested();
            SessionStore.Save(selected, Update(current with { Trim = choice }, StageState.Completed, error: null));
            Report(new(NoticeKind.Information, "Trim choice saved; originals retained.", current.Id));
        });
    }

    public void PrepareChunks(string sessionDirectory)
    {
        CancelAndWait();
        var data = SessionStore.Read(sessionDirectory);
        if (!data.Stages.TryGetValue("prepare", out var stage) || !stage.Enabled)
            throw new InvalidOperationException("Audio chunk preparation is disabled for this session.");
        if (stage.Status == StageState.Completed || data.Audio is not null)
            throw new InvalidOperationException("Chunk set already selected; existing audio retained.");
        if (data.RecordingState != RecordingState.Recorded ||
            data.Stages.TryGetValue("trim", out var trim) && trim.Enabled && trim.Status != StageState.Completed)
            throw new InvalidOperationException("Finalize recording and save the trim choice before preparing chunks.");
        directory = sessionDirectory;
        Start(async token =>
        {
            using var lease = Lease(sessionDirectory);
            var current = SessionStore.Read(sessionDirectory);
            if (current.Audio is not null || current.Stages["prepare"].Status == StageState.Completed)
                throw new InvalidOperationException("Chunk set already selected.");
            SessionStore.Save(sessionDirectory, Update(current, StageState.Running, null, "prepare"));
            var audio = await new AudioChunker(ffmpegPath).PrepareAsync(sessionDirectory, current, token);
            token.ThrowIfCancellationRequested();
            var completed = Update(current with { Audio = audio }, StageState.Completed, null, "prepare");
            completed.Stages["prepare"] = completed.Stages["prepare"] with { CompletedChunks = audio.Chunks.Count, TotalChunks = audio.Chunks.Count };
            SessionStore.Save(sessionDirectory, completed);
            Report(new(NoticeKind.Information, $"Prepared {audio.Chunks.Count} stereo chunks (L=Me/R=Them); originals retained. Transcription pending.", current.Id));
        }, "prepare");
        Report(new(NoticeKind.Information, "Preparing stereo chunks with boundary context. New capture cancels preparation.", data.Id));
    }

    public void Rechunk(string sessionDirectory, int chunkSeconds, Action<string>? onCompleted = null)
    {
        CancelAndWait();
        if (chunkSeconds is < 1 or > AudioChunker.MaxChunkSeconds)
            throw new InvalidOperationException($"Set chunkSeconds to 1-{AudioChunker.MaxChunkSeconds} before rechunking.");
        using (var lease = Lease(sessionDirectory))
        {
            var data = SessionStore.Read(sessionDirectory);
            if (data.RecordingState != RecordingState.Recorded || data.DurationSeconds is not > 0
                || !data.Stages.TryGetValue("prepare", out var stage) || !stage.Enabled
                || !data.Stages.TryGetValue("transcribe", out var transcription) || !transcription.Enabled
                || transcription.Status == StageState.Completed || data.TranscriptSha256 is not null)
                throw new InvalidOperationException("Only incomplete transcription sessions with finalized audio can be rechunked.");
            if (data.Stages.TryGetValue("trim", out var trim) && trim.Enabled && trim.Status != StageState.Completed)
                throw new InvalidOperationException("Save the trim choice before rechunking.");
            if (data.Audio is not null)
            {
                AudioChunker.ValidateManifest(data);
                if (data.Audio.Recovery is not null)
                    throw new InvalidOperationException("This session has already been rechunked; existing checkpoints retained.");
                if (!data.Audio.Chunks.Any(chunk => !File.Exists(ChunkStore.PathFor(sessionDirectory, chunk.Index))
                    && new FileInfo(AudioChunker.ResolveProcessed(sessionDirectory, chunk.RelativePath)).Length > ChatApiClient.MaxAudioBytes))
                    throw new InvalidOperationException("No unprocessed chunk exceeds 8 MiB; existing audio retained.");
            }
            else if (data.Processing.ChunkSeconds <= AudioChunker.MaxChunkSeconds)
                throw new InvalidOperationException("Unselected audio already has a safe chunk duration; use chunks or resume.");
            var stages = new Dictionary<string, StageProgress>(data.Stages)
            { ["prepare"] = stage with { Status = StageState.Running } };
            // Block the automatic job runner until a replacement set is fully encoded and selected.
            SessionStore.Save(sessionDirectory, data with { Stages = stages, JobState = JobState.Paused, Error = null });
        }
        directory = sessionDirectory;
        Start(async token =>
        {
            using var lease = Lease(sessionDirectory);
            var current = SessionStore.Read(sessionDirectory);
            int preserved = await VerifiedCheckpointPrefixAsync(sessionDirectory, current, token);
            var chunker = new AudioChunker(ffmpegPath);
            var replacement = current.Audio is null
                ? await chunker.PrepareAsync(sessionDirectory, current with
                  { Processing = current.Processing with { ChunkSeconds = chunkSeconds } }, token)
                : await chunker.RecoverAsync(sessionDirectory, current, chunkSeconds, preserved, token);
            token.ThrowIfCancellationRequested();
            var stages = new Dictionary<string, StageProgress>(current.Stages)
            {
                ["prepare"] = current.Stages["prepare"] with
                { Status = StageState.Completed, CompletedChunks = replacement.Chunks.Count, TotalChunks = replacement.Chunks.Count },
                ["transcribe"] = current.Stages["transcribe"] with
                { Status = StageState.Pending, CompletedChunks = preserved, TotalChunks = replacement.Chunks.Count }
            };
            if (stages.TryGetValue("assemble", out var assemble))
                stages["assemble"] = assemble with { Status = StageState.Pending };
            var updated = current with
            {
                Processing = current.Processing with { ChunkSeconds = chunkSeconds },
                Audio = replacement, Stages = stages, Retry = null, Error = null, JobState = JobState.NotStarted
            };
            AudioChunker.ValidateManifest(updated);
            SessionStore.Save(sessionDirectory, updated);
            onCompleted?.Invoke(sessionDirectory);
            Report(new(NoticeKind.Information, $"Rechunked to {replacement.Chunks.Count} stereo chunks; {preserved} paid checkpoints retained. Transcription pending.", current.Id));
        }, "prepare");
        Report(new(NoticeKind.Information, "Rechunking in the background; original audio and saved results retained."));
    }

    private static async Task<int> VerifiedCheckpointPrefixAsync(string directory, SessionData data, CancellationToken token)
    {
        string savedDirectory = Path.Combine(directory, "chunks");
        var chunks = data.Audio?.Chunks;
        var known = chunks?.Select(chunk => Path.GetFullPath(ChunkStore.PathFor(directory, chunk.Index)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(savedDirectory) && Directory.EnumerateFiles(savedDirectory, "*.json")
            .Any(file => !known.Contains(Path.GetFullPath(file))))
            throw new InvalidDataException("Unexpected chunk checkpoints exist; no results or audio were replaced.");
        if (chunks is null) return 0;
        int preserved = 0;
        bool gap = false;
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(ChunkStore.PathFor(directory, chunk.Index))) { gap = true; continue; }
            if (gap) throw new InvalidDataException("Saved checkpoints are not a continuous prefix; preserving them without rechunking.");
            _ = await ChunkStore.ReadAsync(directory, data, chunk, token);
            preserved++;
        }
        if (preserved == chunks.Count)
            throw new InvalidOperationException("Every chunk is already checkpointed; no API requests need rechunking.");
        return preserved;
    }

    private void Start(Func<CancellationToken, Task> work, string stage = "trim")
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
                SavePaused(selected, stage == "trim" ? "Audio preparation canceled; reopen trim selection to continue."
                    : "Audio preparation canceled; saved audio and checkpoints retained. Retry local recovery to continue.",
                    stage == "trim" ? StageState.AwaitingUser : StageState.Pending, stage);
            }
            catch (Exception ex) when (ex is not AudioPreparationShutdownException && ex is (InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException))
            {
                SavePaused(selected, "Audio preparation paused; check originals, FFmpeg, permissions and free space.", StageState.Paused, stage);
            }
        });
    }

    private void SavePaused(string selected, string diagnostic, StageState state, string stage)
    {
        try
        {
            using var lease = Lease(selected);
            var data = SessionStore.Read(selected);
            if (data.Stages.TryGetValue(stage, out var progress) && progress.Status == StageState.Completed) return;
            SessionStore.Save(selected, Update(data, state, diagnostic, stage) with { JobState = JobState.Paused });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { diagnostic += " Could not update session.json; prior state retained."; }
        Report(new(NoticeKind.Warning, diagnostic));
    }

    private void Report(OperationNotice notice)
    {
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

    private static SessionData Update(SessionData data, StageState state, string? error, string stage = "trim")
    {
        var stages = new Dictionary<string, StageProgress>(data.Stages);
        stages[stage] = stages[stage] with { Status = state };
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
        title = title.Trim();
        if (title.Length > 200 || title.Any(char.IsControl))
            throw new InvalidOperationException("Title must contain at most 200 characters and no control characters.");
        var safe = new StringBuilder();
        foreach (var rune in title.EnumerateRunes())
        {
            if (safe.Length + rune.Utf16SequenceLength > 80) break;
            string text = rune.ToString();
            safe.Append(text.Length == 1 && Path.GetInvalidFileNameChars().Contains(text[0]) ? '_' : text);
        }
        string name = safe.ToString().Trim(' ', '.');
        string destination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(directory))!,
            $"{data.StartedAtUtc.ToLocalTime():yyyy-MM-dd_HHmmss}_{(name.Length == 0 ? "" : name + "_")}{data.Id:N}");
        bool move = !string.Equals(Path.GetFullPath(directory), destination, StringComparison.OrdinalIgnoreCase);
        if (move && (Directory.Exists(destination) || File.Exists(destination)))
            throw new RecordingException("Named session directory already exists; refusing to overwrite.");
        // Save first: failure to rename still leaves title and identity discoverable in the old folder.
        SessionStore.Save(directory, data with { Title = title.Length == 0 ? null : title });
        // Windows directory rename requires closing every handle inside the folder, including our lease.
        lease.Dispose();
        if (move) Directory.Move(directory, destination);
        return destination;
    }

    public void Dispose()
    {
        try { CancelAndWait(); }
        finally { cancellation?.Dispose(); }
    }
}
