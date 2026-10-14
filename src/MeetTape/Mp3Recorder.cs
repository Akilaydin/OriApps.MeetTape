using NAudio.Wave;

namespace OriApps.MeetTape;

public sealed class Mp3Recorder
{
    private LoadedConfiguration configuration;
    private long origin100ns;
    private Mp3Track? me;
    private Mp3Track? them;
    private SessionData? session;
    private FileStream? lease;
    public CaptureCoordinator Capture { get; }

    public Mp3Recorder(LoadedConfiguration configuration) : this(configuration, null) { }

    public Mp3Recorder(LoadedConfiguration configuration, Func<CaptureSources>? openSources)
    {
        this.configuration = configuration;
        Capture = new(openSources ?? (() => AudioDevices.Open(this.configuration.Settings.Capture)), Consume, Start, Stop);
        Capture.Finished += FinalizeSession;
    }

    internal void ApplyConfiguration(LoadedConfiguration updated) => configuration = updated;

    private void Start(CaptureSources sources, long origin)
    {
        origin100ns = Mp3Track.To100ns(origin);
        Capture.Discontinuities = 0;
        Capture.ClockAdjustments = 0;
        try
        {
            var stages = configuration.Settings.Stages;
            session = new()
            {
                Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow,
                Capture = new(sources.MeDevice, sources.ThemDevice, NativeAudioFormat.From(sources.Me.WaveFormat), NativeAudioFormat.From(sources.Them.WaveFormat)),
                Processing = new(configuration.Settings.ChunkSeconds, configuration.Settings.Transcription, configuration.Settings.Summary, configuration.Directory),
                Stages = new()
                {
                    ["trim"] = new(stages.Trim, stages.Trim ? StageState.AwaitingUser : StageState.Disabled),
                    ["prepare"] = new(stages.Transcribe, stages.Transcribe ? StageState.Pending : StageState.Disabled),
                    ["transcribe"] = new(stages.Transcribe, stages.Transcribe ? StageState.Pending : StageState.Disabled),
                    ["assemble"] = new(stages.Transcribe, stages.Transcribe ? StageState.Pending : StageState.Disabled),
                    ["summary"] = new(stages.Summary, stages.Summary ? StageState.Pending : StageState.Disabled)
                }
            };
            var created = SessionStore.Create(configuration.MeetingsRoot, session);
            string directory = created.Directory;
            lease = created.Lease;
            Capture.RecordingDirectory = directory;
            me = new(configuration.Settings.Capture.FfmpegPath, Path.Combine(directory, "me.mp3"), origin100ns,
                error => Capture.Abort(CaptureChannel.Me, error),
                issue => Capture.ReportTiming(CaptureChannel.Me, issue));
            them = new(configuration.Settings.Capture.FfmpegPath, Path.Combine(directory, "them.mp3"), origin100ns,
                error => Capture.Abort(CaptureChannel.Them, error),
                issue => Capture.ReportTiming(CaptureChannel.Them, issue));
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("recording", "Create output", ex);
            throw ex as RecordingException ?? new RecordingException(AppDiagnostics.UserMessage(ex, "Cannot create recording output; check meetingsRoot permissions and free space."));
        }
    }

    private void FinalizeSession(CaptureStatus status)
    {
        if (session is null || lease is null || status.RecordingDirectory is null)
            return;
        try
        {
            bool originalsExist = File.Exists(Path.Combine(status.RecordingDirectory, "me.mp3"))
                && File.Exists(Path.Combine(status.RecordingDirectory, "them.mp3"));
            string? error = status.Error ?? (originalsExist ? null : "Original MP3 files are incomplete or missing.");
            double audioDuration = status.AudioFrames > Math.Round(status.Duration.TotalSeconds * PcmTimeline.SampleRate)
                ? status.AudioFrames / (double)PcmTimeline.SampleRate
                : status.Duration.TotalSeconds;
            session = session with
            {
                EndedAtUtc = session.StartedAtUtc + status.Duration,
                DurationSeconds = audioDuration,
                RecordingState = error is null ? RecordingState.Recorded : RecordingState.Failed,
                JobState = error is null ? JobState.NotStarted : JobState.Paused,
                Trim = session.Trim with { EndSeconds = audioDuration },
                Discontinuities = status.Discontinuities, ClockAdjustments = status.ClockAdjustments,
                MeTiming = status.MeTiming, ThemTiming = status.ThemTiming, Error = error
            };
            SessionStore.ProtectOriginals(status.RecordingDirectory);
            SessionStore.Save(status.RecordingDirectory, session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Error("recording", "Finalize session", ex);
            throw new RecordingException(AppDiagnostics.UserMessage(ex, "Cannot finalize session.json or protect originals; audio retained. Check output permissions and disk space."));
        }
        finally { lease.Dispose(); lease = null; }
    }

    private void Consume(CaptureChannel channel, WaveInEventArgs args, WaveFormat format, long received)
    {
        var packet = AudioPacket.Copy(args, format, Mp3Track.To100ns(received));
        Capture.ReportLevel(channel, packet);
        (channel == CaptureChannel.Me ? me : them)!.Enqueue(packet);
    }

    private void Stop(long ended)
    {
        try { FinishTracksAsync(Mp3Track.To100ns(ended)).GetAwaiter().GetResult(); }
        finally
        {
            Capture.Discontinuities = (me?.Discontinuities ?? 0) + (them?.Discontinuities ?? 0);
            Capture.ClockAdjustments = (me?.ClockAdjustments ?? 0) + (them?.ClockAdjustments ?? 0);
            me = null; them = null;
        }
    }

    private async Task FinishTracksAsync(long end100ns)
    {
        Task<long>? meDrain = me?.DrainAsync(end100ns);
        Task<long>? themDrain = them?.DrainAsync(end100ns);
        var drainTasks = new[] { meDrain, themDrain }.OfType<Task<long>>().ToArray();
        Exception? error = null;
        try { await Task.WhenAll(drainTasks); }
        catch (Exception ex) { error = ex; }

        // Every audio sample accepted before stop must remain inside the stored session.
        // Both tracks get one common end frame even when only one clock needed correction.
        long endFrame = Math.Max(0, (long)Math.Round((end100ns - origin100ns) * (double)PcmTimeline.SampleRate / 10_000_000));
        if (meDrain is { IsCompletedSuccessfully: true }) endFrame = Math.Max(endFrame, meDrain.Result);
        if (themDrain is { IsCompletedSuccessfully: true }) endFrame = Math.Max(endFrame, themDrain.Result);
        var finishTasks = new[] { me?.FinishAsync(endFrame), them?.FinishAsync(endFrame) }.OfType<Task>().ToArray();
        try { await Task.WhenAll(finishTasks); }
        catch (Exception ex) { error ??= ex; }

        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        Capture.AudioFrames = endFrame;
    }
}
