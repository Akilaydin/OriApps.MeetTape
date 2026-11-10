using NAudio.Wave;
using OriApps.MeetTape.Sessions;
using System.Globalization;

namespace OriApps.MeetTape.Audio;

public sealed class LocalAudioChunker(string ffmpegPath)
{
    public const int WindowSeconds = 180;

    public static List<AudioChunk> Plan(long sourceStartFrame, long frameCount, string relativeDirectory) =>
        AudioChunker.PlanMono(sourceStartFrame, frameCount, WindowSeconds, relativeDirectory)
            .Select(chunk => chunk with { RelativePath = Path.ChangeExtension(chunk.RelativePath, ".wav")! }).ToList();

    public async Task<PreparedAudio> PrepareAsync(string directory, SessionData data, CancellationToken token)
    {
        if (data.RecordingState != RecordingState.Recorded || data.DurationSeconds is not > 0 or > 10800
            || data.Capture.SampleRate != AudioChunker.SampleRate)
            throw new InvalidDataException("Local preparation requires a finalized recording at 48 kHz.");
        if (data.Stages.TryGetValue("trim", out var trim) && trim.Enabled && trim.Status != StageState.Completed)
            throw new InvalidDataException("Save the trim decision before preparing local audio.");

        double duration = data.DurationSeconds.Value;
        double startSeconds = data.Trim.Applied ? data.Trim.StartSeconds : 0;
        double endSeconds = data.Trim.Applied ? data.Trim.EndSeconds ?? double.NaN : duration;
        AudioTrimmer.Validate(startSeconds, endSeconds, duration);
        string input = data.Trim.Applied
            ? AudioChunker.ResolveProcessed(directory, data.Trim.RelativeDirectory
                ?? throw new InvalidDataException("Trimmed recording is missing."))
            : directory;
        foreach (string name in new[] { "me.mp3", "them.mp3" })
            if (!File.Exists(Path.Combine(input, name)) || new FileInfo(Path.Combine(input, name)).Length == 0)
                throw new InvalidDataException("Selected recording track is missing or empty. Original files retained.");

        long first = (long)Math.Round(startSeconds * AudioChunker.SampleRate);
        long last = (long)Math.Round(endSeconds * AudioChunker.SampleRate);
        string relativeDirectory = "processed/local_" + Guid.NewGuid().ToString("N");
        var chunks = Plan(first, last - first, relativeDirectory);
        Directory.CreateDirectory(Path.Combine(directory, relativeDirectory));
        try
        {
            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                string path = AudioChunker.ResolveProcessed(directory, chunk.RelativePath);
                string position = (chunk.StartFrame / (double)AudioChunker.SampleRate)
                    .ToString("R", CultureInfo.InvariantCulture);
                long frames = chunk.EndFrame - chunk.StartFrame;
                string filter = FormattableString.Invariant($"atrim=end_sample={frames},asetpts=PTS-STARTPTS");
                await AudioTrimmer.RunAsync(ffmpegPath,
                    ["-hide_banner", "-nostats", "-nostdin", "-loglevel", "error", "-n",
                     "-ss", position, "-i", Path.Combine(input, chunk.Source == "Me" ? "me.mp3" : "them.mp3"),
                     "-af", filter, "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", path],
                    null, token);
                using (var wav = new WaveFileReader(path))
                {
                    long expected = (long)Math.Round(frames / (double)AudioChunker.SampleRate * 16000);
                    long actual = wav.Length / wav.BlockAlign;
                    if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.Channels != 1
                        || wav.WaveFormat.BitsPerSample != 16 || wav.WaveFormat.Encoding != WaveFormatEncoding.Pcm
                        || Math.Abs(expected - actual) > 160)
                        throw new InvalidDataException("Prepared local WAV has an unexpected format or duration.");
                }
                using var verified = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                verified.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            return new PreparedAudio(relativeDirectory, chunks);
        }
        catch (Exception ex) when (ex is not AudioPreparationShutdownException)
        {
            AudioTrimmer.DiscardAttempt(directory, relativeDirectory);
            throw;
        }
    }

    public static void ValidateManifest(SessionData data)
    {
        if (data.LocalAudio is not { Chunks.Count: > 0 } audio)
            throw new InvalidDataException("Local WAV manifest is missing.");
        double start = data.Trim.Applied ? data.Trim.StartSeconds : 0;
        double end = data.Trim.Applied ? data.Trim.EndSeconds ?? double.NaN : data.DurationSeconds ?? double.NaN;
        if (!double.IsFinite(end) || end <= start || string.IsNullOrWhiteSpace(audio.RelativeDirectory))
            throw new InvalidDataException("Local WAV manifest has invalid time boundaries.");
        long first = (long)Math.Round(start * AudioChunker.SampleRate);
        long total = (long)Math.Round(end * AudioChunker.SampleRate) - first;
        var expected = Plan(first, total, audio.RelativeDirectory);
        if (!audio.Chunks.SequenceEqual(expected))
            throw new InvalidDataException("Local WAV manifest is incompatible with the saved recording.");
    }
}
