using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MeetTape;

public sealed class ProcessingJobs : IDisposable
{
    private static readonly string[] ProcessingStages = ["prepare", "transcribe", "assemble", "summary"];
    private volatile LoadedConfiguration config;
    private volatile string[] meetingsRoots;
    internal IReadOnlyList<string> MeetingsRoots => meetingsRoots;
    private readonly Func<bool> captureActive;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly ChatApiClient api;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<long> timestamp;
    private readonly Dictionary<string, (RetryProgress Retry, long ObservedAt, bool CapExpired)> observedRetries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime LastWriteTimeUtc, long Length)> completedSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private Task operation = Task.CompletedTask;

    public Task Completion => operation;
    public event Action<OperationNotice>? Notice;

    private void Report(string message, NoticeKind kind, Guid? sessionId = null)
    {
        try { Notice?.Invoke(new(kind, message, sessionId)); }
        catch (Exception) { /* Diagnostics cannot change checkpoint/processing outcomes. */ }
    }

    public ProcessingJobs(LoadedConfiguration config, Func<bool> captureActive,
        HttpClient? http = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<ApiSettings, string>? readKey = null,
        Func<DateTimeOffset>? utcNow = null, Func<long>? timestamp = null)
    {
        this.config = config; this.captureActive = captureActive;
        meetingsRoots = [config.MeetingsRoot];
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        api = new(this.http, captureActive, readKey);
    }

    internal void ApplyConfiguration(LoadedConfiguration updated)
    {
        string root = updated.MeetingsRoot;
        if (!meetingsRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) meetingsRoots = [.. meetingsRoots, root];
        config = updated;
    }

    public void Start()
    {
        if (!operation.IsCompleted || captureActive()) return;
        operation.GetAwaiter().GetResult();
        cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        operation = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (string root in meetingsRoots)
                    foreach (var session in SessionStore.Discover(root, completedSessions, Eligible))
                    {
                        token.ThrowIfCancellationRequested();
                        if (session.Data is { } data && !blocked.Contains(session.Directory) && Eligible(data) && !RetryPending(session.Directory, data))
                            await ProcessSessionAsync(session.Directory, token);
                    }
                    await delay(TimeSpan.FromSeconds(1), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { Report("Processing discovery paused; check meeting root and metadata permissions. Restart or resume after fixing storage.", NoticeKind.Error); }
        });
    }

    public void PauseAndWait()
    {
        cancellation?.Cancel();
        try { operation.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        { throw new AudioPreparationShutdownException("Processing did not stop; new capture remains blocked. Check the process before restarting MeetTape."); }
    }

    public void Resume(string directory)
    {
        PauseAndWait();
        using var lease = Lease(directory);
        var data = SessionStore.Read(directory);
        if (data.RecordingState != RecordingState.Recorded)
            throw new InvalidOperationException("Verify incomplete recording before processing.");
        var stages = new Dictionary<string, StageProgress>(data.Stages);
        foreach (string name in ProcessingStages)
            if (stages.TryGetValue(name, out var stage) && stage.Enabled && stage.Status == StageState.Paused)
                stages[name] = stage with { Status = StageState.Pending };
        SessionStore.Save(directory, data with { Stages = stages, JobState = JobState.NotStarted, Error = null,
            Processing = data.Processing with { Transcription = config.Settings.Transcription, Summary = config.Settings.Summary,
                ConfigurationDirectory = config.Directory } });
        blocked.Remove(directory);
        completedSessions.Remove(directory);
        observedRetries.Remove(directory);
        Report("Job resumed with current API settings; successful chunks retained.", NoticeKind.Information, data.Id);
    }

    public void RequestSummary(string directory)
    {
        PauseAndWait();
        using var lease = Lease(directory);
        var data = SessionStore.Read(directory);
        if (!config.Settings.Stages.Summary) throw new InvalidOperationException("Summary disabled; enable Summary in Settings before requesting it.");
        if (!HasPublishedTranscription(data))
            throw new InvalidOperationException("Summary requires a complete published transcription.md.");
        if (data.Stages.TryGetValue("summary", out var summary) && summary.Status == StageState.Completed && File.Exists(Path.Combine(directory, "summary.md")))
            throw new InvalidOperationException("Summary already saved; repeated request retains it without a new API call.");
        SessionStore.Save(directory, data with { Stages = new(data.Stages) { ["summary"] = new(true, StageState.Pending) },
            JobState = JobState.NotStarted, Error = null, Processing = data.Processing with
            { Summary = config.Settings.Summary with { PromptFile = config.Resolve(config.Settings.Summary.PromptFile) } } });
        blocked.Remove(directory);
        completedSessions.Remove(directory);
    }

    public async Task ProcessSessionAsync(string directory, CancellationToken token)
    {
        var currentConfig = config;
        token.ThrowIfCancellationRequested();
        if (captureActive()) return;
        FileStream lease;
        try { lease = Lease(directory); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { blocked.Add(directory); Report("Cannot acquire session lease; processing blocked in this process. Cannot save paused state without an exclusive lease. Check storage permissions.", NoticeKind.Error); return; }
        using (lease)
        {
            var data = SessionStore.Read(directory);
            if (!Eligible(data) || RetryPending(directory, data)) return;
            string stage = "prepare";
            try
            {
                if (data.Audio is null)
                {
                    data = Update(data, stage, StageState.Running);
                    SessionStore.Save(directory, data);
                    var audio = await new AudioChunker(currentConfig.Settings.Capture.FfmpegPath).PrepareAsync(directory, data, token);
                    data = Update(data with { Audio = audio }, stage, StageState.Completed, audio.Chunks.Count, audio.Chunks.Count);
                    SessionStore.Save(directory, data);
                }
                AudioChunker.ValidateManifest(data);
                stage = "transcribe";
                var chunks = data.Audio!.Chunks;
                data = Update(data, stage, StageState.Running, 0, chunks.Count);
                SessionStore.Save(directory, data);
                string? prompt = null;
                int completed = 0;
                foreach (var chunk in chunks)
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(ChunkStore.PathFor(directory, chunk.Index)))
                        _ = await ChunkStore.ReadAsync(directory, data, chunk, token);
                    else
                    {
                        prompt ??= ReadPrompt(data.Processing.Transcription, data.Processing.ConfigurationDirectory, "transcription");
                        int attempts = data.Retry?.ChunkIndex == chunk.Index ? data.Retry.Attempts : 0;
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            var reply = await api.TranscribeAsync(data.Processing.Transcription, prompt,
                                AudioChunker.ResolveProcessed(directory, chunk.RelativePath), token);
                            token.ThrowIfCancellationRequested();
                            ChunkStore.Save(directory, new(data.Id, chunk, reply.AudioSha256, data.Processing.Transcription.Model,
                                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt + ChatApiClient.SourceInstruction))), reply.Text));
                        }
                        catch (ChatApiException ex) when (ex.Kind == ApiFailureKind.Transient)
                        {
                            token.ThrowIfCancellationRequested();
                            attempts = Math.Min(attempts + 1, 30);
                            var backoff = Backoff(attempts, ex.RetryAfter);
                            data = data with { Retry = new(chunk.Index, attempts, utcNow() + backoff), Error = ex.Message };
                            SessionStore.Save(directory, data);
                            Report($"Job {data.Id}: chunk {chunk.Index} transient failure; retry in {backoff.TotalSeconds:F0}s. {ex.Message}", NoticeKind.Warning, data.Id);
                            return; // Leave this session ready for a later pass; process other meetings now.
                        }
                    }
                    // The flushed chunk checkpoint is authoritative if a crash happens before this metadata save.
                    // Checkpoints before the failing chunk must not reset its accumulated backoff.
                    var remainingRetry = data.Retry is { } retry && retry.ChunkIndex != chunk.Index ? retry : null;
                    data = Update(data with { Retry = remainingRetry, Error = remainingRetry is null ? null : data.Error },
                        stage, StageState.Running, ++completed, chunks.Count);
                    SessionStore.Save(directory, data);
                    Report($"Job {data.Id}: chunk {completed}/{chunks.Count} saved/validated.", NoticeKind.Information, data.Id);
                }
                data = Update(data, stage, StageState.Completed, completed, chunks.Count);
                SessionStore.Save(directory, data);
                stage = "assemble";
                data = Update(data, stage, StageState.Running);
                SessionStore.Save(directory, data);
                string hash = await new TranscriptAssembler().BuildAsync(directory, data, token);
                data = Update(data with { TranscriptSha256 = hash }, stage, StageState.Completed);
                SessionStore.Save(directory, data);
                Report($"Job {data.Id}: complete transcription.md saved.", NoticeKind.Information, data.Id);
                if (SummaryRequested(data))
                {
                    stage = "summary";
                    data = Update(data, stage, StageState.Running);
                    SessionStore.Save(directory, data);
                    if (!await GenerateSummaryAsync(directory, data, token)) return;
                    data = Update(data with { Retry = null, Error = null }, stage, StageState.Completed);
                    Report($"Job {data.Id}: requested summary.md saved.", NoticeKind.Information, data.Id);
                }
                SessionStore.Save(directory, data with { JobState = JobState.Completed });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SavePause(directory, data, stage, StageState.Pending, "Processing deferred for capture/exit; saved chunks retained.", permanent: false);
            }
            catch (Exception ex) when (ex is ChatApiException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
                || ex is InvalidOperationException and not AudioPreparationShutdownException)
            {
                blocked.Add(directory);
                SavePause(directory, data, stage, StageState.Paused,
                    ex is ChatApiException apiError ? apiError.Message : "Processing paused; check selected audio, saved chunks, FFmpeg, metadata and filesystem.", permanent: true);
            }
        }
    }

    public static TimeSpan Backoff(int attempts, TimeSpan? retryAfter = null)
    {
        double seconds = Math.Pow(2, Math.Clamp(attempts - 1, 0, 9));
        if (retryAfter is { } hint) seconds = Math.Max(seconds, hint.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300));
    }

    internal static bool Eligible(SessionData data) => data.RecordingState == RecordingState.Recorded && data.JobState != JobState.Paused
        && data.Stages.TryGetValue("transcribe", out var transcribe) && transcribe.Enabled
        && (transcribe.Status != StageState.Completed || !data.Stages.TryGetValue("assemble", out var assemble) || assemble.Status != StageState.Completed || SummaryRequested(data))
        && (!data.Stages.TryGetValue("trim", out var trim) || !trim.Enabled || trim.Status == StageState.Completed);

    internal static bool CanResume(SessionData data) => data.RecordingState == RecordingState.Recorded && data.JobState == JobState.Paused
        && data.Stages.TryGetValue("transcribe", out var transcribe) && transcribe.Enabled
        && (!data.Stages.TryGetValue("trim", out var trim) || !trim.Enabled || trim.Status == StageState.Completed)
        && ProcessingStages.Any(name => data.Stages.TryGetValue(name, out var stage) && stage.Enabled && stage.Status == StageState.Paused);

    internal static bool HasPublishedTranscription(SessionData data) => data.RecordingState == RecordingState.Recorded && data.Audio is not null
        && data.Stages.TryGetValue("transcribe", out var transcribe) && transcribe.Status == StageState.Completed
        && transcribe.CompletedChunks == data.Audio.Chunks.Count
        && data.Stages.TryGetValue("assemble", out var assemble) && assemble.Status == StageState.Completed && data.TranscriptSha256 is not null;

    private bool RetryPending(string directory, SessionData data)
    {
        // A successful checkpoint is authoritative even when the last saved retry deadline is still future.
        if (data.Retry is not { } retry || (retry.ChunkIndex == 0 ? File.Exists(Path.Combine(directory, "summary.json"))
            : File.Exists(ChunkStore.PathFor(directory, retry.ChunkIndex))) || retry.NextAttemptUtc <= utcNow())
        {
            observedRetries.Remove(directory);
            return false;
        }
        if (!observedRetries.TryGetValue(directory, out var observed) || observed.Retry != retry)
        {
            observed = (retry, timestamp(), false);
            observedRetries[directory] = observed;
        }
        if (observed.CapExpired) return false;
        // Wall-clock corrections can move the persisted UTC deadline far away. Do not wait more than
        // five minutes of monotonic time per process.
        if (Stopwatch.GetElapsedTime(observed.ObservedAt, timestamp()) < TimeSpan.FromMinutes(5)) return true;
        observedRetries[directory] = (retry, observed.ObservedAt, true);
        return false;
    }

    private void SavePause(string directory, SessionData data, string stage, StageState state, string diagnostic, bool permanent)
    {
        try { data = SessionStore.Read(directory); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        try { SessionStore.Save(directory, (data.Stages.ContainsKey(stage) ? Update(data, stage, state) : data)
            with { JobState = permanent ? JobState.Paused : JobState.NotStarted, Error = diagnostic }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { blocked.Add(directory); diagnostic += " Cannot save paused state; prior disk state retained, session blocked in this process."; }
        Report($"Job {data.Id}: {diagnostic}", permanent ? NoticeKind.Error : NoticeKind.Warning, data.Id);
    }

    private static SessionData Update(SessionData data, string stage, StageState state, int? completed = null, int? total = null)
    {
        var stages = new Dictionary<string, StageProgress>(data.Stages);
        if (!stages.TryGetValue(stage, out var progress)) throw new InvalidDataException("Required processing stage is missing.");
        stages[stage] = progress with { Status = state, CompletedChunks = completed ?? progress.CompletedChunks, TotalChunks = total ?? progress.TotalChunks };
        return data with { Stages = stages, JobState = JobState.Running };
    }

    private static bool SummaryRequested(SessionData data) => data.Stages.TryGetValue("summary", out var stage)
        && stage.Enabled && stage.Status is StageState.Pending or StageState.Running;

    private string ReadPrompt(ApiSettings settings, string directory, string stage)
    {
        try { return (config with { Directory = directory }).ReadPrompt(settings, stage); }
        catch (InvalidDataException ex)
        { throw new ChatApiException(ApiFailureKind.Configuration, ex.Message + " No request made."); }
    }

    private async Task<bool> GenerateSummaryAsync(string directory, SessionData data, CancellationToken token)
    {
        string checkpoint = Path.Combine(directory, "summary.json");
        SummaryResult? result;
        if (File.Exists(checkpoint)) result = await ChunkStore.ReadFileAsync<SummaryResult>(checkpoint, token);
        else
        {
            await using var file = new FileStream(Path.Combine(directory, "transcription.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 2 * 1024 * 1024) throw new InvalidDataException("Transcript exceeds 2 MiB; Summary paused without automatic truncation.");
            byte[] bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, token);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != data.TranscriptSha256)
                throw new InvalidDataException("Transcript changed; Summary paused.");
            string transcript = Encoding.UTF8.GetString(bytes);
            string prompt = ReadPrompt(data.Processing.Summary, data.Processing.ConfigurationDirectory, "summary");
            int attempts = data.Retry?.ChunkIndex == 0 ? data.Retry.Attempts : 0;
            try
            {
                string text = await api.SummarizeAsync(data.Processing.Summary, prompt, transcript, token);
                token.ThrowIfCancellationRequested();
                result = new(data.Id, data.TranscriptSha256!, data.Processing.Summary.Model,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))), text);
                ChunkStore.SaveFile(checkpoint, result);
            }
            catch (ChatApiException ex) when (ex.Kind == ApiFailureKind.Transient)
            {
                token.ThrowIfCancellationRequested();
                var backoff = Backoff(attempts = Math.Min(attempts + 1, 30), ex.RetryAfter);
                data = data with { Retry = new(0, attempts, utcNow() + backoff), Error = ex.Message };
                SessionStore.Save(directory, data);
                Report($"Job {data.Id}: Summary transient failure; retry in {backoff.TotalSeconds:F0}s. {ex.Message}", NoticeKind.Warning, data.Id);
                return false;
            }
        }
        if (result is null || result.SessionId != data.Id || result.TranscriptSha256 != data.TranscriptSha256
            || string.IsNullOrWhiteSpace(result.Text) || string.IsNullOrWhiteSpace(result.Model) || string.IsNullOrWhiteSpace(result.PromptSha256))
            throw new InvalidDataException("Saved Summary checkpoint is invalid/mismatched; it will not be resent automatically.");
        string temporary = Path.Combine(directory, ".summary-" + Guid.NewGuid().ToString("N") + ".tmp");
        string target = Path.Combine(directory, "summary.md");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] content = Encoding.UTF8.GetBytes(result.Text.TrimEnd() + "\n");
                await stream.WriteAsync(content, token); stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                if (await TranscriptAssembler.HashAsync(target, token) != await TranscriptAssembler.HashAsync(temporary, token))
                    throw new InvalidDataException("Existing summary.md differs; preserve/rename it before resuming. Refusing overwrite.");
            }
            else File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    private static FileStream Lease(string directory) => new(Path.Combine(directory, ".session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void Dispose() { PauseAndWait(); cancellation?.Dispose(); if (ownsHttp) http.Dispose(); }
}
