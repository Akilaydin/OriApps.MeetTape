using System.Globalization;

namespace OriApps.MeetTape;

public sealed class AudioChunker(string ffmpegPath)
{
    public const int SampleRate = 48000;
    // 192 kbps CBR, up to two seconds of context per side, plus five seconds for MP3 overhead.
    public const int MaxChunkSeconds = ChatApiClient.MaxAudioBytes / (192_000 / 8) - 4 - 5;

    public static List<AudioChunk> Plan(long sourceStartFrame, long frameCount, int chunkSeconds, string relativeDirectory)
    {
        if (sourceStartFrame < 0 || frameCount <= 0 || frameCount > 10800L * SampleRate
            || chunkSeconds is < 1 or > MaxChunkSeconds)
            throw new InvalidOperationException("Invalid audio/chunk duration; require positive intervals up to three hours.");
        long width = (long)chunkSeconds * SampleRate;
        long context = Math.Min(2L * SampleRate, width / 4);
        var chunks = new List<AudioChunk>();
        for (long ownStart = 0; ownStart < frameCount; ownStart += width)
        {
            long ownEnd = Math.Min(frameCount, ownStart + width);
            long start = Math.Max(0, ownStart - context), end = Math.Min(frameCount, ownEnd + context);
            int index = chunks.Count + 1;
            chunks.Add(new(index, $"{relativeDirectory}/{index:D4}.mp3", start, end, ownStart, ownEnd, sourceStartFrame + start));
        }
        return chunks;
    }

    public async Task<PreparedAudio> PrepareAsync(string directory, SessionData data, CancellationToken token)
    {
        if (data.RecordingState != RecordingState.Recorded || data.DurationSeconds is not > 0
            || data.DurationSeconds > 10800 || data.Capture.SampleRate != SampleRate)
            throw new InvalidOperationException("Only finalized synchronized 48 kHz recordings can be chunked.");
        if (data.Processing.ChunkSeconds is < 1 or > MaxChunkSeconds)
            throw new InvalidDataException($"Saved chunkSeconds exceeds the supported 8 MiB limit; choose 1-{MaxChunkSeconds} seconds in Settings for new recordings.");
        if (data.Stages.TryGetValue("trim", out var trim) && trim.Enabled && trim.Status != StageState.Completed)
            throw new InvalidOperationException("Save the common trim choice before preparing chunks.");
        double duration = data.DurationSeconds.Value;
        double startSeconds = data.Trim.Applied ? data.Trim.StartSeconds : 0;
        double endSeconds = data.Trim.Applied ? data.Trim.EndSeconds ?? double.NaN : duration;
        AudioTrimmer.Validate(startSeconds, endSeconds, duration);
        string input = directory;
        if (data.Trim.Applied)
        {
            if (string.IsNullOrWhiteSpace(data.Trim.RelativeDirectory))
                throw new InvalidDataException("Selected trim directory is missing; originals retained.");
            input = ResolveProcessed(directory, data.Trim.RelativeDirectory);
        }
        foreach (string name in new[] { "me.mp3", "them.mp3" })
            if (new FileInfo(Path.Combine(input, name)).Length == 0)
                throw new InvalidDataException("Selected audio is empty; originals retained.");
        long first = (long)Math.Round(startSeconds * SampleRate), limit = (long)Math.Round(endSeconds * SampleRate);
        string relative = "processed/audio_" + Guid.NewGuid().ToString("N");
        var chunks = Plan(first, limit - first, data.Processing.ChunkSeconds, relative);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.Combine(directory, relative));
        try
        {
            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                string position = (chunk.StartFrame / (double)SampleRate).ToString("R", CultureInfo.InvariantCulture);
                long frames = chunk.EndFrame - chunk.StartFrame;
                string target = Path.Combine(directory, chunk.RelativePath);
                // Input -ss uses FFmpeg's default accurate seek while transcoding; trim the decoded output by sample count.
                string filter = FormattableString.Invariant($"[0:a][1:a]join=inputs=2:channel_layout=stereo:map=0.0-FL|1.0-FR,atrim=end_sample={frames},asetpts=PTS-STARTPTS[a]");
                await AudioTrimmer.RunAsync(ffmpegPath, ["-hide_banner", "-nostats", "-nostdin", "-v", "error", "-n",
                    "-ss", position, "-i", Path.Combine(input, "me.mp3"), "-ss", position, "-i", Path.Combine(input, "them.mp3"),
                    "-filter_complex", filter, "-map", "[a]", "-ar", "48000", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "192k",
                    "-write_xing", "1", target], null, token);
                ValidateUploadSize(target);
                await ValidateAsync(target, frames, token);
                using var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            return new PreparedAudio(relative, chunks);
        }
        catch (Exception error) when (error is not AudioPreparationShutdownException)
        {
            AudioTrimmer.DiscardAttempt(directory, relative);
            throw;
        }
    }

    public static void ValidateUploadSize(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > ChatApiClient.MaxAudioBytes)
            throw new InvalidDataException("Prepared stereo chunk exceeds the 8 MiB API limit; chunk set not selected.");
    }

    private async Task ValidateAsync(string path, long expected, CancellationToken token)
    {
        long samples = 0;
        bool wrongFormat = false;
        await AudioTrimmer.RunAsync(ffmpegPath, ["-hide_banner", "-nostats", "-nostdin", "-i", path,
            "-af", "ashowinfo", "-f", "null", "-"], line =>
        {
            if (!line.Contains("[Parsed_ashowinfo_", StringComparison.Ordinal)) return;
            if (!line.Contains("channels:2", StringComparison.Ordinal) || !line.Contains("rate:48000", StringComparison.Ordinal))
                wrongFormat = true;
            const string marker = "nb_samples:";
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return;
            var number = line.AsSpan(at + marker.Length);
            int space = number.IndexOf(' ');
            if (space >= 0) number = number[..space];
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) samples += count;
        }, token);
        if (wrongFormat || samples != expected)
            throw new InvalidDataException("Chunk format/duration mismatch; selected inputs may be truncated. No chunk set selected.");
    }

    internal static void ValidateManifest(SessionData data)
    {
        var audio = data.Audio ?? throw new InvalidDataException("Audio manifest missing.");
        long first = (long)Math.Round((data.Trim.Applied ? data.Trim.StartSeconds : 0) * 48000);
        double end = data.Trim.Applied ? data.Trim.EndSeconds ?? double.NaN : data.DurationSeconds ?? double.NaN;
        if (!double.IsFinite(end) || audio.Chunks is null || string.IsNullOrWhiteSpace(audio.RelativeDirectory))
            throw new InvalidDataException("Invalid selected audio manifest.");
        long total = (long)Math.Round(end * 48000) - first;
        var expected = Plan(first, total, data.Processing.ChunkSeconds, audio.RelativeDirectory);
        if (!audio.Chunks.SequenceEqual(expected)) throw new InvalidDataException("Selected chunk intervals do not cover the recording correctly.");
    }

    internal static string ResolveProcessed(string directory, string relative)
    {
        string root = Path.GetFullPath(Path.Combine(directory, "processed")) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Processed audio path escapes the meeting directory.");
        return path;
    }
}
