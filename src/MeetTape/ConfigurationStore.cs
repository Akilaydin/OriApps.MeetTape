using System.Security.Cryptography;
using System.Text;

namespace MeetTape;

internal static class ConfigurationStore
{
    internal static string? Save(string path, string originalText, string updatedText,
        IReadOnlyDictionary<string, string> keysToSave,
        Action<string, string, EnvironmentVariableTarget>? setKey = null)
    {
        setKey ??= (name, value, target) => Environment.SetEnvironmentVariable(name, value, target);
        path = Path.GetFullPath(path);
        bool changed = !string.Equals(originalText, updatedText, StringComparison.Ordinal);
        if (keysToSave.Any(pair => !ApiKeyProvider.ValidEnvironmentName(pair.Key) || pair.Value.Length > 32767
            || pair.Value.Any(char.IsControl) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            return "Invalid API key or environment variable name. Nothing saved.";
        }
        try
        {
            _ = ConfigurationLoader.Parse(updatedText, path, validatePrompts: true);
        }
        catch (InvalidDataException ex)
        {
            return $"Invalid configuration: {ex.Message} Nothing saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Cannot read an enabled prompt file. Check prompt paths and permissions; nothing saved.";
        }
        // Serialize the final compare, replacement and environment update across
        // Settings saves; a comparison alone permits concurrent lost updates.
        string lockName = "Local\\MeetTape.Settings." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, lockName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            return "Configuration is being saved by another process. Try again.";
        }
        try
        {
            if (changed || keysToSave.Count > 0)
            {
                if (!string.Equals(originalText, File.ReadAllText(path), StringComparison.Ordinal))
                {
                    return "Configuration changed during editing. Nothing saved; reopen Settings.";
                }
                if (changed) SaveAtomically(path, updatedText);
            }

            foreach (var (variable, key) in keysToSave)
            {
                try
                {
                    setKey(variable, key, EnvironmentVariableTarget.User);
                    setKey(variable, key, EnvironmentVariableTarget.Process);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or NotSupportedException)
                {
                    return "Cannot save API key in the Windows user environment. Configuration settings may have been saved.";
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return null;
    }

    private static void SaveAtomically(string path, string content)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporaryPath, path, null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
