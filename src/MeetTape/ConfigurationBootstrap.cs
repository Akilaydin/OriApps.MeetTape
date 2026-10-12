using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetTape;

public static class ConfigurationBootstrap
{
    public static string UserConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetTape", "config.json");

    public static string UserMeetingsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetTape", "Meetings");

    public static bool Initialize(string configurationPath, string meetingsRoot)
    {
        string fullPath = Path.GetFullPath(configurationPath);
        if (File.Exists(fullPath))
            return false;

        string directory = Path.GetDirectoryName(fullPath)!;
        string promptsDirectory = Path.Combine(directory, "prompts");
        Directory.CreateDirectory(promptsDirectory);

        WriteNewIfMissing(Path.Combine(promptsDirectory, "transcription.md"), ReadTemplate("transcription.md"));
        WriteNewIfMissing(Path.Combine(promptsDirectory, "summary.md"), ReadTemplate("summary.md"));

        var json = JsonNode.Parse(ReadTemplate("config.json"))!.AsObject();
        json["meetingsRoot"] = Path.GetFullPath(meetingsRoot);
        string content = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        WriteNewIfMissing(fullPath, content);
        return true;
    }

    private static void WriteNewIfMissing(string path, string content)
    {
        if (File.Exists(path))
            return;

        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another configuration run created the destination; preserve its contents.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ReadTemplate(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MeetTape.Defaults." + name)
            ?? throw new InvalidDataException("Embedded configuration template is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
