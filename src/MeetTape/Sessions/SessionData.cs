using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Settings;
using NAudio.Wave;

namespace OriApps.MeetTape.Sessions;

public enum RecordingState { Recording, Recorded, Interrupted, Failed }
public enum JobState { NotStarted, Running, Paused, Completed, Canceled }
public enum StageState { Disabled, Pending, AwaitingUser, AwaitingRequest, Running, Completed, Paused }

public sealed record StageProgress(bool Enabled, StageState Status, int CompletedChunks = 0, int? TotalChunks = null);
public sealed record TrimParameters(double StartSeconds = 0, double? EndSeconds = null, bool Applied = false)
{
    public string? Decision { get; init; }
    public string? RelativeDirectory { get; init; }
}
public sealed record NativeAudioFormat(int SampleRate, int BitsPerSample, int Channels, string Encoding)
{
    public static NativeAudioFormat From(WaveFormat format) => new(format.SampleRate, format.BitsPerSample, format.Channels,
        format is WaveFormatExtensible extended ? extended.SubFormat.ToString() : format.Encoding.ToString());
}
public sealed record CaptureParameters(AudioDevice? MeDevice, AudioDevice? ThemDevice, NativeAudioFormat MeFormat,
    NativeAudioFormat ThemFormat, int SampleRate = 48000, int Channels = 1, int BitRateKbps = 96, string Codec = "mp3");
public sealed record ProcessingParameters(int ChunkSeconds, ApiSettings Transcription, ApiSettings Summary,
    string ConfigurationDirectory, string MeDisplayName = "Me");
public sealed record AudioChunk(int Index, string RelativePath, long StartFrame, long EndFrame,
    long OwnStartFrame, long OwnEndFrame, long SourceStartFrame, string? Source = null);
public sealed record PreparedAudio(string RelativeDirectory, List<AudioChunk> Chunks);
public sealed record RetryProgress(int ChunkIndex, int Attempts, DateTimeOffset NextAttemptUtc);
public sealed record SummaryResult(Guid SessionId, string TranscriptSha256, string Model, string PromptSha256, string Text);

public sealed record SessionData
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public string? Title { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public double? DurationSeconds { get; init; }
    public RecordingState RecordingState { get; init; } = RecordingState.Recording;
    public JobState JobState { get; init; } = JobState.NotStarted;
    public required CaptureParameters Capture { get; init; }
    public required ProcessingParameters Processing { get; init; }
    public TrimParameters Trim { get; init; } = new();
    public PreparedAudio? Audio { get; init; }
    public RetryProgress? Retry { get; init; }
    public string? TranscriptSha256 { get; init; }
    public required Dictionary<string, StageProgress> Stages { get; init; }
    public int Discontinuities { get; init; }
    public int ClockAdjustments { get; init; }
    public AudioTimingStats MeTiming { get; init; } = new();
    public AudioTimingStats ThemTiming { get; init; } = new();
    public string? Error { get; init; }
}

public sealed record StoredSession(string Directory, SessionData? Data, bool MePresent, bool ThemPresent, string? Diagnostic);
