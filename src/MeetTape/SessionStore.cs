using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetTape;

public static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static (string Directory, FileStream Lease) Create(string root, SessionData data)
    {
        string directory = Path.Combine(root, $"{data.StartedAtUtc.ToLocalTime():yyyy-MM-dd_HHmmss}_{data.Id:N}");
        if (Directory.Exists(directory))
            throw new RecordingException("Session directory already exists; refusing to overwrite another recording.");
        Directory.CreateDirectory(directory);
        var lease = new FileStream(Path.Combine(directory, ".session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Save(directory, data, overwrite: false);
            return (directory, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static void Save(string directory, SessionData data, bool overwrite = true)
    {
        string temporary = Path.Combine(directory, ".session-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(directory, "session.json"), overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static SessionData Read(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, "session.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        SessionData? data;
        try { data = JsonSerializer.Deserialize<SessionData>(stream, Options); }
        catch (JsonException) { throw new InvalidDataException("Invalid session.json; original audio was not changed."); }
        if (data is null || data.SchemaVersion != 1 || data.Id == Guid.Empty || data.Capture is null || data.Processing is null
            || data.Capture.MeFormat is null || data.Capture.ThemFormat is null || data.Processing.Transcription is null || data.Processing.Summary is null
            || data.Trim is null || data.Stages is null || data.Stages.Values.Any(s => s is null)
            || data.DurationSeconds is < 0 || data.DurationSeconds is { } duration && !double.IsFinite(duration))
            throw new InvalidDataException("Unsupported or invalid session metadata; original files were not changed.");
        return data;
    }

    public static void ProtectOriginals(string directory)
    {
        foreach (string name in new[] { "me.mp3", "them.mp3" })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                if (!File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    stream.Flush(flushToDisk: true);
                }
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }
        }
    }

    public static IReadOnlyList<StoredSession> Discover(string root) => Discover(root, null);

    internal static IReadOnlyList<StoredSession> Discover(string root,
        Dictionary<string, (DateTime LastWriteTimeUtc, long Length)>? completed, Func<SessionData, bool>? eligible = null)
    {
        if (!Directory.Exists(root))
        {
            completed?.Clear();
            return [];
        }
        var seen = completed is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StoredSession>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            seen?.Add(directory);
            var metadata = new FileInfo(Path.Combine(directory, "session.json"));
            if (!metadata.Exists)
            {
                completed?.Remove(directory);
                continue;
            }
            bool me = false, them = false;
            try
            {
                var fingerprint = (metadata.LastWriteTimeUtc, metadata.Length);
                if (completed is not null && completed.TryGetValue(directory, out var cached) && cached == fingerprint)
                    continue;
                completed?.Remove(directory);
                me = File.Exists(Path.Combine(directory, "me.mp3"));
                them = File.Exists(Path.Combine(directory, "them.mp3"));
                var data = Read(directory);
                if (data.RecordingState == RecordingState.Recording)
                {
                    using var lease = AcquireAvailableLease(directory);
                    if (lease is not null)
                    {
                        // A recording may have finalized between our first read and acquiring its lease.
                        data = Read(directory);
                        if (data.RecordingState == RecordingState.Recording)
                        {
                            data = data with
                            {
                                RecordingState = RecordingState.Interrupted, JobState = JobState.Paused,
                                Error = "Previous process ended during capture. Partial originals retained; verify audio before processing."
                            };
                            ProtectOriginals(directory);
                            Save(directory, data);
                        }
                    }
                }
                if (completed is not null && data.RecordingState == RecordingState.Recorded && data.JobState == JobState.Completed
                    && !(eligible?.Invoke(data) ?? false))
                    completed[directory] = fingerprint;
                result.Add(new(directory, data, me, them, data.RecordingState == RecordingState.Interrupted ? data.Error :
                    !me || !them ? "One or both original audio files are missing." : data.Error));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                result.Add(new(directory, null, me, them, "Cannot read/update session.json; check metadata, permissions and free space. Original audio retained."));
            }
        }
        if (completed is not null)
            foreach (string removed in completed.Keys.Where(key => !seen!.Contains(key)).ToArray())
                completed.Remove(removed);
        return result;
    }

    private static FileStream? AcquireAvailableLease(string directory)
    {
        string path = Path.Combine(directory, ".session.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }
}
