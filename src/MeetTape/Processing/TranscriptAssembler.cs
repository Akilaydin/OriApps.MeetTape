using OriApps.MeetTape.Api;
using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Sessions;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OriApps.MeetTape.Processing;

public sealed class TranscriptAssembler
{
    private const long BoundaryMatchToleranceFrames = AudioChunker.SampleRate;
    private sealed record Utterance(long Frame, string Speaker, string Text, int Chunk, int Line, long? NextSameSourceFrame = null);
    private static readonly Regex LinePattern = new(@"^\[(\d+(?::\d+){1,2}(?:\.\d+)?)\]\s*([^:]+):\s*(.+)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<string> BuildAsync(string directory, SessionData data, CancellationToken token)
    {
        if (data.Audio is not { Chunks.Count: > 0 } audio || !data.Stages.TryGetValue("transcribe", out var stage)
            || stage.Status != StageState.Completed || stage.CompletedChunks != audio.Chunks.Count)
            throw new InvalidDataException("Full transcript requires all completed chunk checkpoints.");
        AudioChunker.ValidateManifest(data);
        string temporary = Path.Combine(directory, ".transcription-" + Guid.NewGuid().ToString("N") + ".tmp");
        string target = Path.Combine(directory, "transcription.md");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(file, new UTF8Encoding(false), 8192, leaveOpen: true) { NewLine = "\n" };
                writer.WriteLine("# " + (data.Title ?? "MeetTape").Replace('\r', ' ').Replace('\n', ' '));
                writer.WriteLine();
                writer.WriteLine("> Full transcript. Timestamps are approximate and relative to the original recording. Me/Them identify recording sources, not verified identities.");
                writer.WriteLine();
                var pending = new List<Utterance>();
                var previousByTrack = new Dictionary<string, (AudioChunk Chunk, List<Utterance> Rows)>();
                for (int i = 0; i < audio.Chunks.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var chunk = audio.Chunks[i];
                    var result = await ChunkStore.ReadAsync(directory, data, chunk, token);
                    var current = Parse(result.Text, chunk, token);
                    string track = chunk.Source ?? "Stereo";
                    bool hasPrior = previousByTrack.TryGetValue(track, out var prior);
                    AudioChunk? priorChunk = hasPrior ? prior.Chunk : null;
                    var previous = hasPrior ? prior.Rows : [];
                    var consumedPreviousLines = new HashSet<int>();
                    foreach (var row in current)
                    {
                        string? text = Reconcile(row, previous, consumedPreviousLines, priorChunk, chunk, token);
                        if (text is not null) pending.Add(row with { Text = text });
                    }
                    pending.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) :
                        a.Chunk != b.Chunk ? a.Chunk.CompareTo(b.Chunk) : a.Line.CompareTo(b.Line));
                    long threshold = i + 1 < audio.Chunks.Count ? audio.Chunks[i + 1].SourceStartFrame - 48000 : long.MaxValue;
                    int flushed = 0;
                    foreach (var row in pending)
                    {
                        token.ThrowIfCancellationRequested();
                        if (row.Frame >= threshold) break;
                        string display = row.Speaker == "Me" ? EscapeName(data.Processing.MeDisplayName ?? "Me") : row.Speaker;
                        writer.WriteLine($"[{FormatTime(row.Frame)}] **{display}:** {row.Text}");
                        writer.WriteLine();
                        flushed++;
                    }
                    pending.RemoveRange(0, flushed);
                    previousByTrack[track] = (chunk, current);
                }
                await writer.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            string hash = await HashAsync(temporary, token);
            if (File.Exists(target))
            {
                if (await HashAsync(target, token) != hash)
                    throw new InvalidDataException("Existing transcription.md differs; preserve/rename it before assembly. Refusing overwrite.");
            }
            else File.Move(temporary, target, overwrite: false);
            return hash;
        }
        catch (RegexMatchTimeoutException)
        { throw new InvalidDataException("Timed transcript parsing exceeded its limit; result retained for repair."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void ValidateChunk(string text, AudioChunk chunk)
    {
        try { _ = Parse(text, chunk, CancellationToken.None); }
        catch (RegexMatchTimeoutException)
        { throw new InvalidDataException("Timed transcript parsing exceeded its limit."); }
    }

    private static List<Utterance> Parse(string text, AudioChunk chunk, CancellationToken token)
    {
        if (text.Trim() == ChatApiClient.NoSpeechMarker) return [];
        var rows = new List<Utterance>();
        using var input = new StringReader(text);
        string? line;
        int index = 0;
        while ((line = input.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.Length == 0 || line is "```" or "```markdown") continue;
            if (line.Length > 32768) throw new InvalidDataException("Transcription utterance exceeds its bounded line limit.");
            if (rows.Count >= 4096) throw new InvalidDataException("Transcription exceeds its bounded utterance-count limit.");
            var match = LinePattern.Match(line);
            if (!match.Success || !AudioTrimmer.TryParseTime(match.Groups[1].Value, out double seconds)
                || seconds > (chunk.EndFrame - chunk.StartFrame) / 48000d + 1)
                throw new InvalidDataException("Chunk transcription has invalid/missing local timestamps; saved result retained for repair.");
            string label = match.Groups[2].Value.Trim().ToLowerInvariant() switch
            {
                "me" => "Me", "them" => "Them", "unknown" => "Unknown",
                _ when chunk.Source is null => "Unknown", // Preserve existing stereo checkpoints without another paid request.
                _ => throw new InvalidDataException($"Unexpected source label in chunk {chunk.Index}, utterance {index + 1}; " +
                    "only Me/Them/Unknown are accepted. Original response retained for diagnosis.")
            };
            long frame = chunk.SourceStartFrame + Math.Min((long)Math.Round(seconds * 48000), chunk.EndFrame - chunk.StartFrame);
            rows.Add(new(frame, chunk.Source ?? label, match.Groups[3].Value, chunk.Index, index++));
        }
        if (rows.Count == 0) throw new InvalidDataException("No timed utterances in saved chunk; full transcript not published.");
        rows = rows.OrderBy(r => r.Frame).ThenBy(r => r.Line).ToList();
        var nextBySpeaker = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (nextBySpeaker.TryGetValue(row.Speaker, out long nextFrame))
                rows[i] = row with { NextSameSourceFrame = nextFrame };
            nextBySpeaker[row.Speaker] = row.Frame;
        }
        return rows;
    }

    private static string? Reconcile(Utterance row, List<Utterance> previous, HashSet<int> consumedPreviousLines,
        AudioChunk? priorChunk, AudioChunk chunk, CancellationToken token)
    {
        if (priorChunk is null) return row.Text;
        long overlapStart = Math.Max(priorChunk.SourceStartFrame, chunk.SourceStartFrame);
        long overlapEnd = Math.Min(priorChunk.SourceStartFrame + priorChunk.EndFrame - priorChunk.StartFrame,
            chunk.SourceStartFrame + chunk.EndFrame - chunk.StartFrame);
        if (row.Frame < overlapStart || row.Frame >= overlapEnd) return row.Text;
        var words = Words.Matches(row.Text).Cast<Match>().ToArray();
        Utterance? closest = null;
        int closestOverlap = 0;
        long closestDistance = long.MaxValue;
        foreach (var old in previous)
        {
            token.ThrowIfCancellationRequested();
            if (old.Chunk != priorChunk.Index || old.Speaker != row.Speaker || consumedPreviousLines.Contains(old.Line)
                || old.Frame >= overlapEnd || old.NextSameSourceFrame < row.Frame) continue;
            bool closeStarts = Math.Abs(old.Frame - row.Frame) <= BoundaryMatchToleranceFrames;
            bool earlierBoundaryStart = old.Frame < overlapStart && old.Frame >= overlapStart - (overlapEnd - overlapStart)
                && row.Frame <= overlapStart + BoundaryMatchToleranceFrames;
            if (!closeStarts && !earlierBoundaryStart) continue;
            long distance = Math.Abs(old.Frame - row.Frame);
            if (distance >= closestDistance) continue;
            var prior = Words.Matches(old.Text).Cast<Match>().ToArray();
            int limit = Math.Min(100, Math.Min(prior.Length, words.Length)), overlap = 0;
            for (int count = limit; count >= 3; count--)
            {
                bool same = true;
                for (int i = 0; i < count; i++)
                    if (!string.Equals(prior[prior.Length - count + i].Value, words[i].Value, StringComparison.OrdinalIgnoreCase))
                    { same = false; break; }
                if (same) { overlap = count; break; }
            }
            // Without word timestamps, only reconcile a longer prior sentence's distinctive boundary suffix.
            if (!closeStarts && (overlap < 5 || prior.Length - overlap < 3)) continue;
            if ((overlap == words.Length && overlap >= 3) || overlap >= 5)
            {
                closest = old;
                closestOverlap = overlap;
                closestDistance = distance;
            }
        }
        if (closest is not null)
        {
            consumedPreviousLines.Add(closest.Line);
            return closestOverlap == words.Length ? null : row.Text[words[closestOverlap].Index..];
        }
        return row.Text;
    }

    private static string FormatTime(long frame)
    {
        long seconds = frame / 48000;
        return seconds >= 3600 ? FormattableString.Invariant($"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}") :
            FormattableString.Invariant($"{seconds / 60:D2}:{seconds % 60:D2}");
    }

    private static string EscapeName(string name) => name.Trim().Replace("\\", "\\\\").Replace("*", "\\*")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
