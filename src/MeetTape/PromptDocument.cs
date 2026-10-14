using System.Text;

namespace OriApps.MeetTape;

internal sealed class PromptDocument
{
    private byte[]? original;
    private string savedText = "";
    private bool readable;
    internal string Path { get; }
    internal string Stage { get; }
    internal string Text { get; set; } = "";
    internal string Error { get; private set; } = "";
    internal bool IsDirty => Text != savedText;

    internal PromptDocument(string path, string stage)
    {
        Path = System.IO.Path.GetFullPath(path); Stage = stage;
        try
        {
            original = File.ReadAllBytes(Path);
            using var stream = new MemoryStream(original);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            Text = savedText = reader.ReadToEnd();
            readable = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { readable = true; Error = $"The {stage} prompt file was not found. Enter instructions to create it, or correct the path and Load file."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { Error = $"Cannot read the {stage} prompt file. Check permissions, then Load file; existing contents will be retained."; }
    }

    internal void VerifyUnchanged()
    {
        if (!readable) throw new InvalidDataException($"Cannot save the unreadable {Stage} prompt. Fix permissions and Load file first; your edits are retained.");
        byte[]? current;
        try { current = File.ReadAllBytes(Path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { current = null; }
        if (original is null ? current is not null : current is null || !original.AsSpan().SequenceEqual(current))
            throw new InvalidDataException($"The {Stage} prompt changed during editing. Save stopped; your edits are retained. Reopen Settings to compare the file.");
    }

    internal void Save()
    {
        ConfigurationStore.SaveAtomically(Path, Text, create: original is null);
        AcceptSaved();
    }

    internal void AcceptSaved()
    {
        original = new UTF8Encoding(false).GetBytes(Text);
        savedText = Text; Error = "";
    }
}
