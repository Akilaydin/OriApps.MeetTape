using NAudio.Wave;

namespace MeetTape;

public sealed class Mp3Recorder
{
    private readonly LoadedConfiguration configuration;
    private Mp3Track? me;
    private Mp3Track? them;
    private SessionData? session;
    private FileStream? lease;
    public CaptureCoordinator Capture { get; }

    public Mp3Recorder(LoadedConfiguration configuration) : this(configuration, null) { }

    public Mp3Recorder(LoadedConfiguration configuration, Func<CaptureSources>? openSources)
    {
        this.configuration = configuration;
        Capture = new(openSources ?? (() => AudioDevices.Open(configuration.Settings.Capture)), Consume, Start, Stop);
        Capture.Finished += FinalizeSession;
    }

    private void Start(CaptureSources sources, long origin)
    {
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
                    ["summary"] = new(stages.Summary, stages.Summary ? StageState.AwaitingRequest : StageState.Disabled)
                }
            };
            var created = SessionStore.Create(configuration.MeetingsRoot, session);
            string directory = created.Directory;
            lease = created.Lease;
            Capture.RecordingDirectory = directory;
            me = new(configuration.Settings.Capture.FfmpegPath, Path.Combine(directory, "me.mp3"), Mp3Track.To100ns(origin),
                error => Capture.Abort(CaptureChannel.Me, error));
            them = new(configuration.Settings.Capture.FfmpegPath, Path.Combine(directory, "them.mp3"), Mp3Track.To100ns(origin),
                error => Capture.Abort(CaptureChannel.Them, error));
        }
        catch (Exception ex)
        {
            throw ex as RecordingException ?? new RecordingException("Cannot create recording output; check meetingsRoot permissions and free space.");
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
            session = session with
            {
                EndedAtUtc = session.StartedAtUtc + status.Duration,
                DurationSeconds = status.Duration.TotalSeconds,
                RecordingState = error is null ? RecordingState.Recorded : RecordingState.Failed,
                JobState = error is null ? JobState.NotStarted : JobState.Paused,
                Trim = session.Trim with { EndSeconds = status.Duration.TotalSeconds },
                Discontinuities = status.Discontinuities, ClockAdjustments = status.ClockAdjustments, Error = error
            };
            SessionStore.ProtectOriginals(status.RecordingDirectory);
            SessionStore.Save(status.RecordingDirectory, session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RecordingException("Cannot finalize session.json or protect originals; audio retained. Check output permissions and disk space.");
        }
        finally { lease.Dispose(); lease = null; }
    }

    private void Consume(CaptureChannel channel, WaveInEventArgs args, WaveFormat format, long received)
    {
        var packet = AudioPacket.Copy(args, format, Mp3Track.To100ns(received));
        (channel == CaptureChannel.Me ? me : them)!.Enqueue(packet);
    }

    private void Stop(long ended)
    {
        var tasks = new List<Task>();
        if (me is not null) tasks.Add(me.CompleteAsync(Mp3Track.To100ns(ended)));
        if (them is not null) tasks.Add(them.CompleteAsync(Mp3Track.To100ns(ended)));
        try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
        finally
        {
            Capture.Discontinuities = (me?.Discontinuities ?? 0) + (them?.Discontinuities ?? 0);
            Capture.ClockAdjustments = (me?.ClockAdjustments ?? 0) + (them?.ClockAdjustments ?? 0);
            me = null; them = null;
        }
    }
}
