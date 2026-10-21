using OriApps.MeetTape.Audio;
using System.Buffers.Binary;
using System.Globalization;

namespace OriApps.MeetTape.Sessions;

internal static class SessionRecovery
{
    private const int SampleRate = 48000;
    private const double DurationTolerance = 0.05;

    internal static bool CanRecover(SessionData data) => data.RecordingState is RecordingState.Failed or RecordingState.Interrupted
        && data.JobState != JobState.Running && !data.Stages.Values.Any(stage => stage.Status == StageState.Running);

    internal static async Task RecoverAsync(string directory, Guid id, IReadOnlyList<string> meetingRoots, string ffmpegPath)
    {
        if (id == Guid.Empty || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Refresh and select a saved recording before recovering.");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string parent = Path.GetDirectoryName(directory) ?? "";
        if (!meetingRoots.Any(root => string.Equals(parent,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Only recordings inside a known meetings folder can be recovered.");
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Linked recording folders cannot be recovered.");
        if (!File.Exists(Path.Combine(directory, ".session.lock")))
            throw new InvalidDataException("Recording lease is missing; original files and metadata were retained.");

        using var lease = SessionStore.AcquireLease(directory);
        var data = SessionStore.Read(directory);
        if (data.Id != id) throw new InvalidOperationException("The selected recording changed. Refresh and try again.");
        if (!CanRecover(data)) throw new InvalidOperationException("Only inactive Failed or Interrupted recordings can be recovered.");
        if (data.Capture.SampleRate != SampleRate || data.Capture.Channels != 1 || data.Capture.Codec != "mp3")
            throw new InvalidDataException("The saved recording format is not supported (48 kHz mono MP3 required).");
        if (data.RecordingState == RecordingState.Failed && data.DurationSeconds is null)
            throw new InvalidDataException("Failed recording has no saved duration to verify against decoded audio.");

        string me = Path.Combine(directory, "me.mp3"), them = Path.Combine(directory, "them.mp3");
        using var meStream = OpenOriginal(me);
        using var themStream = OpenOriginal(them);
        var meFrames = ReadFinalizedFrameCount(meStream, "me.mp3");
        var themFrames = ReadFinalizedFrameCount(themStream, "them.mp3");
        double meDuration = await DecodeDurationAsync(ffmpegPath, me, meFrames);
        double themDuration = await DecodeDurationAsync(ffmpegPath, them, themFrames);
        if (Math.Abs(meDuration - themDuration) > DurationTolerance)
            throw new InvalidDataException("Original tracks have different decoded durations; synchronized audio cannot be recovered.");
        double duration = (meDuration + themDuration) / 2;
        if (duration <= 0 || duration > 10800)
            throw new InvalidDataException("Decoded audio duration is empty or exceeds the supported three hours.");
        if (data.DurationSeconds is { } expected && Math.Abs(expected - duration) > DurationTolerance)
            throw new InvalidDataException("Decoded audio is shorter or longer than the saved session duration; recovery refused.");
        if (data.EndedAtUtc is { } ended &&
            ((ended - data.StartedAtUtc).TotalSeconds < 0 || (ended - data.StartedAtUtc).TotalSeconds > duration + DurationTolerance))
            throw new InvalidDataException("Recorded start/stop timestamps disagree with decoded audio; recovery refused.");
        if (data.Trim.StartSeconds < 0 || data.Trim.StartSeconds > duration
            || data.Trim.EndSeconds is { } trimEnd && (trimEnd < data.Trim.StartSeconds || trimEnd > duration + DurationTolerance))
            throw new InvalidDataException("Saved trim boundaries disagree with the decoded audio duration.");

        var stages = new Dictionary<string, StageProgress>(data.Stages);
        bool trimPending = stages.TryGetValue("trim", out var trim) && trim.Enabled && trim.Status != StageState.Completed;
        if (!trimPending)
        {
            // Resume remains explicit; pending/running processing never starts immediately after recovery.
            foreach (string name in new[] { "prepare", "transcribe", "assemble", "summary" })
                if (stages.TryGetValue(name, out var stage) && stage.Enabled && stage.Status is StageState.Pending or StageState.Running)
                    stages[name] = stage with { Status = StageState.Paused };
        }
        bool pending = stages.Values.Any(stage => stage.Enabled && stage.Status is not (StageState.Completed or StageState.Disabled or StageState.AwaitingRequest));
        SessionStore.Save(directory, data with
        {
            RecordingState = RecordingState.Recorded,
            DurationSeconds = data.DurationSeconds ?? duration,
            JobState = pending ? JobState.Paused : JobState.Completed,
            Trim = data.Trim.EndSeconds is null ? data.Trim with { EndSeconds = data.DurationSeconds ?? duration } : data.Trim,
            Stages = stages,
            Error = null
        });
    }

    private static FileStream OpenOriginal(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"{Path.GetFileName(path)} is missing; recovery refused.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"{Path.GetFileName(path)} is a linked file; recovery refused.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 0) return file;
        file.Dispose();
        throw new InvalidDataException($"{Path.GetFileName(path)} is empty; recovery refused.");
    }

    private static uint ReadFinalizedFrameCount(FileStream file, string name)
    {
        // MeetTape's FFmpeg encoder writes a Xing/Info frame with the finalized byte and frame counts.
        // A file cut off on a valid MP3 frame may decode without errors; its count must still match.
        Span<byte> header = stackalloc byte[10];
        if (file.Length < header.Length) throw new InvalidDataException($"{name} has an incomplete MP3 header.");
        file.ReadExactly(header);
        long firstFrame = 0;
        if (header[..3].SequenceEqual("ID3"u8))
        {
            if ((header[6] | header[7] | header[8] | header[9]) >= 128)
                throw new InvalidDataException($"{name} has an invalid ID3 header.");
            firstFrame = 10L + (header[6] << 21) + (header[7] << 14) + (header[8] << 7) + header[9]
                + ((header[5] & 0x10) != 0 ? 10 : 0);
        }
        file.Position = firstFrame;
        Span<byte> frame = stackalloc byte[4];
        if (file.Length - firstFrame < 37) throw new InvalidDataException($"{name} is too short to contain finalized MP3 audio.");
        file.ReadExactly(frame);
        if (frame[0] != 0xff || (frame[1] & 0xfe) != 0xfa || (frame[2] & 0x0c) != 0x04
            || (frame[3] & 0xc0) != 0xc0)
            throw new InvalidDataException($"{name} has an unsupported MP3 header (48 kHz mono required).");
        file.Position = firstFrame + 4 + ((frame[1] & 1) == 0 ? 2 : 0) + 17;
        Span<byte> info = stackalloc byte[16];
        file.ReadExactly(info);
        if (!(info[..4].SequenceEqual("Info"u8) || info[..4].SequenceEqual("Xing"u8))
            || (BinaryPrimitives.ReadUInt32BigEndian(info[4..8]) & 3) != 3)
            throw new InvalidDataException($"{name} has no finalized MP3 frame/length header; recovery refused.");
        uint frames = BinaryPrimitives.ReadUInt32BigEndian(info[8..12]);
        uint bytes = BinaryPrimitives.ReadUInt32BigEndian(info[12..16]);
        if (frames == 0 || bytes != file.Length - firstFrame)
            throw new InvalidDataException($"{name} MP3 length differs from its finalized header; file may be truncated.");
        return frames;
    }

    private static async Task<double> DecodeDurationAsync(string ffmpegPath, string path, uint frames)
    {
        long durationUs = -1;
        bool complete = false;
        try
        {
            await AudioTrimmer.RunAsync(ffmpegPath, ["-hide_banner", "-nostats", "-nostdin", "-v", "error",
                "-xerror", "-err_detect", "explode", "-i", path, "-map", "0:a:0", "-f", "null", "-",
                "-progress", "pipe:2"], line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var us))
                    durationUs = us;
                if (line == "progress=end") complete = true;
            }, CancellationToken.None);
        }
        catch (RecordingException error) when (error.Message.StartsWith("FFmpeg audio preparation failed", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} failed full FFmpeg decoding; recovery refused.", error);
        }
        double duration = durationUs / 1_000_000d;
        if (!complete || durationUs <= 0 || Math.Abs(frames * 1152d / SampleRate - duration) > DurationTolerance)
            throw new InvalidDataException($"{Path.GetFileName(path)} decoded duration disagrees with its MP3 frame count; file may be damaged.");
        return duration;
    }
}
