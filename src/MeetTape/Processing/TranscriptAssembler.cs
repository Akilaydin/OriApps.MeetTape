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
    // Providers may round the end of a fractional-length chunk to a whole second.
    private const double TimestampRoundingToleranceSeconds = 1;
    private sealed record Utterance(long Frame, string Speaker, string Text, int Chunk, int Line, long? NextSameSourceFrame = null);
    private static readonly Regex LinePattern = new(@"^\[(\d+(?::\d+){1,2}(?:\.\d+)?)\]\s*([^:]+):\s*(.+)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex MonoWithoutLabel = new(@"^\[(\d+(?::\d+){1,2}(?:\.\d+)?)\]\s+(.+)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TimeMarkers = new(@"\[\d+(?::\d+){1,2}(?:\.\d+)?\]",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SilenceWrapper = new(@"^\s*(?:\[(?<time>\d+(?::\d+){1,2}(?:\.\d+)?)\]\s*)?(?:(?:Me|Them):\s*)?\[NO_SPEECH\]\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private const string LocalTimePattern = @"\d+(?::\d+){1,2}(?:\.\d+)?";
    private static readonly Regex LeadingTime = new(
        @"^(?<open>\[{0,2}|\()\s*(?<time>" + LocalTimePattern + @")\s*(?<close>\]{1,2}|\)|)(?<rest>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex KnownRolePrefix = new(
        @"^\s*(?:\*\*)?(?:Me|Them|Unknown)\b\s*(?:[:：\-–—]|\*\*\s*:)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownListPrefix = new(
        @"^(?:[-*•]\s+|\d{1,2}[.)]\s+)(?=\*{0,2}\[{1,2}(?:\s*\d|NO[_ -]SPEECH))",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex JoinedDecoratedTime = new(
        @"(?<=\s)\[\[(?<time>" + LocalTimePattern + @")\]\]?(?=\s*(?:Me|Them|Unknown)\s*:)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownTime = new(
        @"^\*\*(?<time>\[{1,2}" + LocalTimePattern + @"\]{1,2})\*\*",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DecoratedRole = new(
        @"^(?<time>\[" + LocalTimePattern + @"\])\s*\*\*(?<role>Me|Them|Unknown)(?<colon>:?)\*\*\s*:?\s*(?<speech>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex AlternativeRoleSeparator = new(
        @"^(?<time>\[" + LocalTimePattern + @"\])\s*(?<role>Me|Them|Unknown)\s*[-–—：]\s*(?<speech>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex WrappedSilenceTypo = new(
        @"^(?:\[(?<time>" + LocalTimePattern + @")\]\s*)?(?:(?<role>Me|Them):\s*)?(?<marker>\[{1,2}\s*NO[_ -]SPEECH\s*\]{0,2}|NO_SPEECH\]{0,2})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

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
        text = NormalizeStructuralFormatting(text);
        if (text.Trim() == ChatApiClient.NoSpeechMarker) return [];
        bool mono = chunk.Source is "Me" or "Them";
        if (mono && SilenceWrapper.Match(text) is { Success: true } silence)
        {
            string timestamp = silence.Groups["time"].Value;
            if (timestamp.Length == 0 || AudioTrimmer.TryParseTime(timestamp, out double at)
                && at <= (chunk.EndFrame - chunk.StartFrame) / 48000d + TimestampRoundingToleranceSeconds)
                return []; // A model silence claim is not independent acoustic evidence.
            throw new InvalidDataException("Chunk transcription has invalid/missing local timestamps; saved result retained for repair.");
        }
        if (text.Contains(ChatApiClient.NoSpeechMarker, StringComparison.Ordinal))
            throw new InvalidDataException("Chunk transcription combines [NO_SPEECH] with other content.");
        if (mono) text = NormalizeMonoTimestamps(text);
        var rows = new List<Utterance>();
        using var input = new StringReader(text);
        string? line;
        int index = 0;
        double? previousMonoTimestamp = null;
        while ((line = input.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.Length == 0 || line is "```" or "```markdown") continue;
            if (line.Length > 32768) throw new InvalidDataException("Transcription utterance exceeds its bounded line limit.");
            var markers = TimeMarkers.Matches(line);
            if (markers.Count == 0 || markers[0].Index != 0)
                throw new InvalidDataException("Chunk transcription has invalid/missing local timestamps; saved result retained for repair.");
            for (int i = 0; i < markers.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count >= 4096) throw new InvalidDataException("Transcription exceeds its bounded utterance-count limit.");
                int end = i + 1 < markers.Count ? markers[i + 1].Index : line.Length;
                string segment = line[markers[i].Index..end].Trim();
                if (segment == markers[i].Value)
                {
                    // A final orphan marker is harmless only after a complete utterance with no following speech.
                    if (i == markers.Count - 1 && rows.Count > 0 && HasCompleteEnding(rows[^1].Text)
                        && AudioTrimmer.TryParseTime(markers[i].Value[1..^1], out double orphanSeconds)
                        && orphanSeconds <= (chunk.EndFrame - chunk.StartFrame) / 48000d + TimestampRoundingToleranceSeconds
                        && string.IsNullOrWhiteSpace(input.ReadToEnd())) break;
                    throw new InvalidDataException("Chunk transcription ends in an incomplete or ambiguous timed utterance.");
                }
                var match = LinePattern.Match(segment);
                if (match.Success && mono && !IsKnownSource(match.Groups[2].Value.Trim())
                    && IsClearlySpokenPrefix(match.Groups[2].Value))
                    match = Match.Empty; // A spoken clause with ':' is not a speaker label.
                if (i > 0 && !match.Success && !HasCompleteEnding(rows[^1].Text))
                    throw new InvalidDataException("Joined timestamp has no clear utterance boundary or explicit source label.");
                var missingLabel = !match.Success && (chunk.Source is "Me" or "Them") ? MonoWithoutLabel.Match(segment) : Match.Empty;
                if (!match.Success && (!missingLabel.Success || missingLabel.Groups[2].Value.EndsWith(':')
                    && !IsClearlySpokenPrefix(missingLabel.Groups[2].Value.TrimEnd(':'))))
                    throw new InvalidDataException("Chunk transcription has invalid/missing local timestamps; saved result retained for repair.");
                string time = match.Success ? match.Groups[1].Value : missingLabel.Groups[1].Value;
                string utteranceText = match.Success ? match.Groups[3].Value : missingLabel.Groups[2].Value;
                if (!AudioTrimmer.TryParseTime(time, out double seconds)
                    || seconds > (chunk.EndFrame - chunk.StartFrame) / 48000d + TimestampRoundingToleranceSeconds)
                    throw new InvalidDataException("Chunk transcription has invalid/missing local timestamps; saved result retained for repair.");
                if (mono && previousMonoTimestamp is { } previous && seconds < previous)
                    throw new InvalidDataException("Chunk timestamps are out of chronological order; original response retained for repair.");
                if (mono) previousMonoTimestamp = seconds;
                string label = (match.Success ? match.Groups[2].Value.Trim() : chunk.Source!).ToLowerInvariant() switch
                {
                    "me" => "Me", "them" => "Them", "unknown" => "Unknown",
                    _ when chunk.Source is null => "Unknown", // Preserve existing stereo checkpoints without another paid request.
                    _ => throw new InvalidDataException($"Unexpected source label in chunk {chunk.Index}, utterance {index + 1}; " +
                        "only Me/Them/Unknown are accepted. Original response retained for diagnosis.")
                };
                long frame = chunk.SourceStartFrame + Math.Min((long)Math.Round(seconds * 48000), chunk.EndFrame - chunk.StartFrame);
                rows.Add(new(frame, chunk.Source ?? label, utteranceText, chunk.Index, index++));
            }
        }
        if (rows.Count == 0) throw new InvalidDataException("No timed utterances in saved chunk; full transcript not published.");
        // Keep legacy stereo checkpoints compatible; mono chronology was checked in input order.
        if (!mono) rows = rows.OrderBy(r => r.Frame).ThenBy(r => r.Line).ToList();
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

    // Only repair recognizable delimiters. Never invent or discard speech, timestamps or sources.
    // The raw ChunkResult.Text remains authoritative; validation and final assembly use this same parser.
    internal static string NormalizeStructuralFormatting(string text)
    {
        text = text.Trim().TrimStart('\uFEFF').TrimStart();
        int firstBreak = text.IndexOf('\n'), lastBreak = text.LastIndexOf('\n');
        if (firstBreak >= 0 && lastBreak > firstBreak)
        {
            string opening = text[..firstBreak].Trim();
            string closing = text[(lastBreak + 1)..].Trim();
            if (opening is "```" or "```markdown" or "```text" or "```plaintext" && closing == "```")
            {
                string inner = text[(firstBreak + 1)..lastBreak].Trim();
                if (!inner.Contains("```", StringComparison.Ordinal)) text = inner;
            }
        }
        var output = new StringBuilder(text.Length + 16);
        using var input = new StringReader(text);
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length > 32768)
                throw new InvalidDataException("Transcription utterance exceeds its bounded line limit.");
            line = line.Trim();
            line = MarkdownListPrefix.Replace(line, "");
            line = JoinedDecoratedTime.Replace(line, match => "[" + match.Groups["time"].Value + "]");
            // Bold decoration is unambiguous only when it encloses a leading timestamp.
            if (line.StartsWith("**[", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal)
                && LeadingTime.IsMatch(line[2..^2]))
                line = line[2..^2];
            line = MarkdownTime.Replace(line, match => match.Groups["time"].Value);
            var timestamp = LeadingTime.Match(line);
            if (timestamp.Success)
            {
                string opening = timestamp.Groups["open"].Value;
                string closing = timestamp.Groups["close"].Value;
                bool paired = opening == "[" && closing == "]";
                bool redundantBracket = opening is "[[" && closing is "]" or "]]"
                    || opening == "[" && closing == "]]";
                bool explicitSource = KnownRolePrefix.IsMatch(timestamp.Groups["rest"].Value);
                bool recoverMissingBracket = explicitSource && (
                    opening == "[" && closing.Length == 0
                    || opening.Length == 0 && closing is "" or "]"
                    || opening == "(" && closing == ")");
                if (paired && !line.StartsWith("[" + timestamp.Groups["time"].Value + "]", StringComparison.Ordinal)
                    || !paired && (redundantBracket || recoverMissingBracket))
                    line = "[" + timestamp.Groups["time"].Value + "]" + timestamp.Groups["rest"].Value;
            }
            var decorated = DecoratedRole.Match(line);
            if (decorated.Success)
                line = decorated.Groups["time"].Value + " " + decorated.Groups["role"].Value
                    + ": " + decorated.Groups["speech"].Value;
            var alternative = AlternativeRoleSeparator.Match(line);
            if (alternative.Success)
                line = alternative.Groups["time"].Value + " " + alternative.Groups["role"].Value
                    + ": " + alternative.Groups["speech"].Value;
            output.AppendLine(line);
        }
        text = output.ToString().Trim();
        if (text.Length <= 256 && WrappedSilenceTypo.Match(text) is { Success: true } silence)
        {
            string time = silence.Groups["time"].Value;
            string role = silence.Groups["role"].Value;
            return (time.Length == 0 ? "" : "[" + time + "] ")
                + (role.Length == 0 ? "" : role + ": ") + ChatApiClient.NoSpeechMarker;
        }
        return text;
    }

    // Normalize only unambiguous record boundaries. The raw model reply remains in ChunkResult.Text;
    // both validation and final assembly apply this identical deterministic interpretation.
    internal static string NormalizeMonoTimestamps(string text)
    {
        var normalized = new StringBuilder(text.Length + 16);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Normalization must not evade the original bounded input-line contract.
            if (line.Length > 32768)
                throw new InvalidDataException("Transcription utterance exceeds its bounded line limit.");
            var markers = TimeMarkers.Matches(line);
            if (markers.Count < 2 || markers[0].Index != 0)
            {
                normalized.AppendLine(line);
                continue;
            }
            bool unambiguous = true;
            for (int i = 1; i < markers.Count; i++)
            {
                string prior = line[(markers[i - 1].Index + markers[i - 1].Length)..markers[i].Index].Trim();
                int end = i + 1 < markers.Count ? markers[i + 1].Index : line.Length;
                string next = line[(markers[i].Index + markers[i].Length)..end].TrimStart();
                var explicitSource = LinePattern.Match(markers[i].Value + " " + next);
                bool explicitlyLabeled = explicitSource.Success && IsKnownSource(explicitSource.Groups[2].Value.Trim());
                // A lone time mention inside unfinished speech is ambiguous. A multi-phrase
                // sequence or a substantial next phrase provides a structural boundary.
                if (!explicitlyLabeled && !HasCompleteEnding(prior)
                    && (next.Length == 0 || markers.Count == 2 && Words.Matches(next).Count < 3 && !HasCompleteEnding(next)))
                { unambiguous = false; break; }
            }
            if (!unambiguous)
            {
                normalized.AppendLine(line);
                continue;
            }
            for (int i = 0; i < markers.Count; i++)
            {
                int end = i + 1 < markers.Count ? markers[i + 1].Index : line.Length;
                normalized.AppendLine(line[markers[i].Index..end].TrimEnd());
            }
        }
        return normalized.ToString();
    }

    private static bool IsKnownSource(string label) => label.Equals("Me", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Them", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsClearlySpokenPrefix(string text) => Words.Matches(text).Count >= 4
        && text.Any(c => c is >= '\u0400' and <= '\u052F')
        || text.Any(c => c is >= 'A' and <= 'Z') && !text.Any(c => c is >= 'a' and <= 'z');

    private static bool HasCompleteEnding(string text)
    {
        text = text.TrimEnd(' ', '\t', '"', '\'', '»', '”', ')');
        return text.EndsWith('.') && !text.EndsWith("..", StringComparison.Ordinal)
            || text.EndsWith('!') || text.EndsWith('?');
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
