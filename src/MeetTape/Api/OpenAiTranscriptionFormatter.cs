using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OriApps.MeetTape.Api;

internal static class OpenAiTranscriptionFormatter
{
    internal static string Parse(byte[] raw, string? source, bool diarize, double chunkDurationSeconds, double ownStartSeconds,
        bool whisper = false)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid();
            string label = source is "Me" or "Them" ? source : "Unknown";
            if (!diarize && !whisper)
            {
                if (!root.TryGetProperty("text", out var result) || result.ValueKind != JsonValueKind.String)
                    return Invalid();
                string text = Flatten(result.GetString()!);
                // A valid empty text field means the ASR model detected no speech.
                if (text.Length == 0) return ChatApiClient.NoSpeechMarker;
                if (!double.IsFinite(ownStartSeconds) || ownStartSeconds < 0
                    || ownStartSeconds > chunkDurationSeconds + 1) return Invalid();
                return $"[{Timestamp(ownStartSeconds)}] {label}: {text}";
            }

            if (!root.TryGetProperty("duration", out var length) || !TryTime(length, out double duration)
                || !root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
                return Invalid();
            if (double.IsFinite(chunkDurationSeconds) && duration > chunkDurationSeconds + 1) return Invalid();
            if (segments.GetArrayLength() == 0)
            {
                if (root.TryGetProperty("text", out var text) && (text.ValueKind != JsonValueKind.String
                    || !string.IsNullOrWhiteSpace(text.GetString()))) return Invalid();
                return ChatApiClient.NoSpeechMarker;
            }

            var output = new StringBuilder();
            double previous = 0;
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object
                    || !segment.TryGetProperty("start", out var startValue) || !TryTime(startValue, out double start)
                    || !segment.TryGetProperty("end", out var endValue) || !TryTime(endValue, out double end)
                    || start < previous || end < start || end > duration + 1
                    || !segment.TryGetProperty("text", out var words) || words.ValueKind != JsonValueKind.String)
                    return Invalid();
                if (diarize && (!segment.TryGetProperty("speaker", out var speaker)
                    || speaker.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(speaker.GetString())))
                    return Invalid();
                string text = Flatten(words.GetString()!);
                if (text.Length == 0) return Invalid();
                if (output.Length > 0) output.Append('\n');
                // Me/Them come from the recorded mono source; Whisper has no speaker field.
                output.Append('[').Append(Timestamp(start)).Append("] ").Append(label).Append(": ").Append(text);
                previous = start;
            }
            return output.ToString();
        }
        catch (JsonException) { return Invalid(); }
    }

    private static bool TryTime(JsonElement value, out double time)
    {
        time = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out time)
            && double.IsFinite(time) && time >= 0;
    }

    private static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Timestamp(double seconds)
    {
        // Retain fractional seconds for diarization; only chunk-start timing is available for gpt-transcribe.
        long centiseconds = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        long whole = centiseconds / 100, fractions = centiseconds % 100;
        string clock = whole >= 3600
            ? FormattableString.Invariant($"{whole / 3600:D2}:{whole / 60 % 60:D2}:{whole % 60:D2}")
            : FormattableString.Invariant($"{whole / 60:D2}:{whole % 60:D2}");
        return fractions == 0 ? clock : clock + "." + fractions.ToString("D2", CultureInfo.InvariantCulture);
    }

    private static string Invalid() => throw new ChatApiException(ApiFailureKind.InvalidResponse,
        "OpenAI transcription returned malformed or inconsistent timed text; result not accepted.");
}
