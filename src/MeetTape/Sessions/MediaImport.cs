using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Diagnostics;
using OriApps.MeetTape.Processing;
using OriApps.MeetTape.Settings;

namespace OriApps.MeetTape.Sessions;

internal static class MediaImport
{
    internal static readonly string[] FilePatterns =
        ["*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.opus", "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi"];
    private const int MaxSeconds = 3 * 60 * 60;

    internal static async Task<string> ImportAsync(LoadedConfiguration config, string source, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(source) || !File.Exists(source) ||
            !FilePatterns.Contains("*" + Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing supported audio or video file.");

        // Keep unfinished output outside the flat meeting library. Publish the entire folder in one move.
        string stagingRoot = Path.Combine(config.MeetingsRoot, ".imports");
        Directory.CreateDirectory(stagingRoot);
        if (File.GetAttributes(stagingRoot).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The import staging folder cannot be a link.");
        Guid id = Guid.NewGuid();
        string staging = Path.Combine(stagingRoot, id.ToString("N"));
        string target = Path.Combine(staging, "them.mp3");
        Directory.CreateDirectory(staging);
        bool keepStaging = false;
        try
        {
            string ffmpeg = config.Settings.Capture.FfmpegPath;
            try
            {
                await AudioTrimmer.RunAsync(ffmpeg, ["-hide_banner", "-nostats", "-nostdin", "-v", "error", "-n", "-xerror",
                    "-protocol_whitelist", "file", "-format_whitelist", "mp3,wav,mov,mp4,m4a,3gp,3g2,mj2,aac,flac,ogg,matroska,webm,avi",
                    "-i", source, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1",
                    "-t", "10800.1", "-ac", "1", "-ar", "48000", "-c:a", "libmp3lame", "-b:a", "96k",
                    "-write_xing", "1", target], null, token);
            }
            catch (RecordingException)
            {
                throw new RecordingException("Cannot import audio. Check that the file contains a supported audio track, FFmpeg is available and the meetings folder has enough space.");
            }

            var (samples, wrongFormat) = await AudioChunker.MeasureDecodedAsync(ffmpeg, target, token);
            if (wrongFormat || samples <= 0)
                throw new RecordingException("The imported file did not produce valid audio.");
            if (samples > MaxSeconds * (long)AudioChunker.SampleRate)
                throw new RecordingException("Import supports up to three hours of audio. Split the source file and try again.");

            var settings = config.Settings;
            var stages = settings.Stages;
            double seconds = samples / (double)AudioChunker.SampleRate;
            var started = DateTimeOffset.UtcNow;
            var format = new NativeAudioFormat(48000, 16, 1, "PCM");
            var session = new SessionData
            {
                Id = id, ImportedMedia = true, StartedAtUtc = started, EndedAtUtc = started.AddSeconds(seconds),
                DurationSeconds = seconds, RecordingState = RecordingState.Recorded,
                JobState = stages.Transcribe ? JobState.Paused : JobState.NotStarted,
                Capture = new(null, null, format, format),
                Processing = new(settings.ChunkSeconds, settings.Transcription, settings.Summary, config.Directory, settings.MeDisplayName)
                { TranscriptionMode = settings.TranscriptionMode, LocalTranscription = settings.LocalTranscription },
                Stages = new()
                {
                    ["trim"] = new(stages.Trim, stages.Trim ? StageState.AwaitingUser : StageState.Disabled),
                    ["prepare"] = new(stages.Transcribe, stages.Transcribe ? StageState.Paused : StageState.Disabled),
                    ["transcribe"] = new(stages.Transcribe, stages.Transcribe ? StageState.Pending : StageState.Disabled),
                    ["assemble"] = new(stages.Transcribe, stages.Transcribe ? StageState.Pending : StageState.Disabled),
                    ["summary"] = new(stages.Summary, stages.Summary ? StageState.Pending : StageState.Disabled)
                }
            };
            string title = string.Concat(Path.GetFileNameWithoutExtension(source).Where(ch => !char.IsControl(ch))).Trim();
            if (title.Length > 120)
                title = title[..(char.IsHighSurrogate(title[119]) ? 119 : 120)];
            SessionStore.Save(staging, session, overwrite: false);
            SessionStore.ProtectOriginals(staging);
            token.ThrowIfCancellationRequested();
            return PostRecordingWorkflow.SetTitle(staging, title, config.MeetingsRoot);
        }
        catch (AudioPreparationShutdownException)
        {
            keepStaging = true; // Do not delete while an unconfirmed FFmpeg process could still be writing.
            throw;
        }
        finally
        {
            try
            {
                if (!keepStaging && Directory.Exists(staging) && !File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint))
                {
                    if (File.Exists(target) && !File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    foreach (string name in new[] { "them.mp3", "session.json", ".session.lock" })
                        File.Delete(Path.Combine(staging, name));
                    Directory.Delete(staging);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { AppDiagnostics.Error("import", "Remove unfinished media import", error); }
        }
    }
}
