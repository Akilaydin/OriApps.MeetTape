using OriApps.MeetTape.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OriApps.MeetTape.Settings;

internal static class ConfigurationStore
{
    internal static string? Save(string path, string originalText, string updatedText,
        IReadOnlyDictionary<string, string> keysToSave,
        Action<string, string, EnvironmentVariableTarget>? setKey = null,
        IReadOnlyList<PromptDocument>? promptEdits = null)
    {
        setKey ??= (name, value, target) => Environment.SetEnvironmentVariable(name, value, target);
        foreach (string key in keysToSave.Values) AppDiagnostics.ProtectSecret(key);
        path = Path.GetFullPath(path);
        bool changed = !string.Equals(originalText, updatedText, StringComparison.Ordinal);
        if (keysToSave.Any(pair => !ApiKeyProvider.ValidEnvironmentName(pair.Key) || pair.Value.Length > 32767
            || pair.Value.Any(char.IsControl) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            return "Invalid API key or environment variable name. Nothing saved.";
        }
        try
        {
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prompt in promptEdits ?? [])
            {
                if (prompt.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A prompt file cannot replace config.json.");
                if (texts.TryGetValue(prompt.Path, out string? existing) && existing != prompt.Text)
                    throw new InvalidDataException("Both stages use the same prompt file; enter matching instructions.");
                texts[prompt.Path] = prompt.Text;
            }
            _ = ConfigurationLoader.Parse(updatedText, path, validatePrompts: true, texts);
        }
        catch (InvalidDataException ex)
        {
            AppDiagnostics.Error("settings", "Validate", ex);
            return $"Invalid configuration: {ex.Message} Nothing saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Error("settings", "Read enabled prompt", ex);
            return AppDiagnostics.UserMessage(ex, "Cannot read an enabled prompt file. Check prompt paths and permissions; nothing saved.");
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
            if (changed || keysToSave.Count > 0 || promptEdits?.Count > 0)
            {
                if (!string.Equals(originalText, File.ReadAllText(path), StringComparison.Ordinal))
                {
                    return "Configuration changed during editing. Nothing saved; reopen Settings.";
                }
                // Check every source before replacing any file. A failed write retains
                // unsaved text, and already saved prompts become their own new baseline.
                foreach (var prompt in promptEdits ?? []) prompt.VerifyUnchanged();
                foreach (var group in (promptEdits ?? []).GroupBy(prompt => prompt.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var prompt = group.First();
                    prompt.VerifyUnchanged();
                    prompt.Save();
                    foreach (var sameFile in group.Skip(1)) sameFile.AcceptSaved();
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
                    AppDiagnostics.Error("settings", "Save key environment", ex);
                    return "Cannot save API key in the Windows user environment. Configuration settings may have been saved.";
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        AppDiagnostics.Info("settings", "Configuration/prompts saved");
        return null;
    }

    internal static void SaveAtomically(string path, string content, bool create = false)
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
            if (create) File.Move(temporaryPath, path); // Never overwrite a concurrently created prompt.
            else File.Replace(temporaryPath, path, null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
