namespace MeetTape;

internal static class StartupReleaseNotice
{
    internal static async Task<string> GetAsync(IAppUpdates updates, TimeSpan? timeout = null)
    {
        string current = $"Current version: {updates.CurrentVersion}";
        if (!updates.IsInstalled)
            return current + " (development/portable build)\nUpdates require an installed MeetTape build.";
        try
        {
            var update = await updates.CheckAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(2));
            return current + (update is null ? "\nUp to date." :
                $"\nAvailable version: {update.TargetFullRelease.Version}. Use Update to review and confirm.");
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("updater", "Startup check", error);
            return current + "\n" + AppDiagnostics.UserMessage(error, "Update check unavailable. Use Update to retry.");
        }
    }
}
