using System.Text.Json;
using System.Security.Cryptography;

namespace MeetTape;

public sealed record ChunkResult(Guid SessionId, AudioChunk Chunk, string AudioSha256, string Model, string PromptSha256, string Text);

public static class ChunkStore
{
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };

    public static string PathFor(string directory, int index) => Path.Combine(directory, "chunks", $"{index:D4}.json");

    public static void Save(string directory, ChunkResult result)
    {
        string target = PathFor(directory, result.Chunk.Index);
        SaveFile(target, result);
    }

    internal static void SaveFile<T>(string target, T result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, result, Options); stream.Flush(flushToDisk: true); }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<ChunkResult> ReadAsync(string directory, SessionData session, AudioChunk chunk, CancellationToken token)
    {
        string path = PathFor(directory, chunk.Index);
        var result = await ReadFileAsync<ChunkResult>(path, token);
        if (result is null || result.SessionId != session.Id || result.Chunk != chunk || string.IsNullOrWhiteSpace(result.Text)
            || string.IsNullOrWhiteSpace(result.Model) || string.IsNullOrWhiteSpace(result.PromptSha256))
            throw new InvalidDataException("Saved chunk does not match the selected session/audio; processing paused.");
        await using var audio = new FileStream(AudioChunker.ResolveProcessed(directory, chunk.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(audio, token));
        if (hash != result.AudioSha256) throw new InvalidDataException("Selected audio changed after transcription; saved result retained and processing paused.");
        return result;
    }

    internal static async Task<T?> ReadFileAsync<T>(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Chunk checkpoint exceeds its size limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        try { return await JsonSerializer.DeserializeAsync<T>(stream, Options, token); }
        catch (JsonException) { throw new InvalidDataException("Invalid saved chunk; restore its checkpoint before resuming. It will not be resent automatically."); }
    }
}
