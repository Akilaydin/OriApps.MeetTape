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

    public static string DefaultConfigurationPath(string workingDirectory, string userConfigurationPath, string? executableDirectory = null)
    {
        string legacyPath = Path.Combine(workingDirectory, "config.json");
        // Velopack replaces current on update. Defaults always live outside that directory.
        if (executableDirectory is not null && File.Exists(Path.Combine(executableDirectory, "sq.version")))
            return userConfigurationPath;
        // Installed EXEs prefer user settings, even inside a source checkout.
        // Source builds prefer current-directory settings; installed EXEs fall back to them if user config is missing.
        if (executableDirectory is not null && File.Exists(userConfigurationPath) && !IsSourceBuild(executableDirectory))
            return userConfigurationPath;
        return File.Exists(legacyPath) ? legacyPath : userConfigurationPath;
    }

    private static bool IsSourceBuild(string executableDirectory)
    {
        for (DirectoryInfo? directory = new(executableDirectory); directory?.Parent is not null; directory = directory.Parent)
            if (directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(directory.Parent.FullName, "MeetTape.csproj")))
                return true;
        return false;
    }

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
