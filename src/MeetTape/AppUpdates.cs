using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace OriApps.MeetTape;

public interface IAppUpdates
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    Task<UpdateInfo?> CheckAsync();
    Task DownloadAsync(UpdateInfo update, Action<int> progress);
    void ApplyAndRestart(UpdateInfo update);
}

public sealed class AppUpdates : IAppUpdates
{
    private readonly UpdateManager manager;
    public bool IsInstalled => manager.IsInstalled && !manager.IsPortable;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ??
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public AppUpdates() : this(new UpdateManager(
        new GithubSource("https://github.com/Akilaydin/OriApps.MeetTape", null, false))) { }

    public AppUpdates(UpdateManager manager)
    {
        this.manager = manager;
    }

    public async Task<UpdateInfo?> CheckAsync()
    {
        AppDiagnostics.Info("updater", "Feed check started");
        try { return await manager.CheckForUpdatesAsync().ConfigureAwait(false); }
        catch (Exception error) { AppDiagnostics.Error("updater", "Feed check", error); throw; }
    }

    public async Task DownloadAsync(UpdateInfo update, Action<int> progress)
    {
        RequireSingleInstance();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await manager.DownloadUpdatesAsync(update, progress, deadline.Token).ConfigureAwait(false); }
        catch (Exception error) { AppDiagnostics.Error("updater", "Package download", error); throw; }
    }

    public void ApplyAndRestart(UpdateInfo update)
    {
        RequireSingleInstance();
        // The controller has already joined capture/preparation/jobs. Let Avalonia close normally.
        try { manager.WaitExitThenApplyUpdates(update.TargetFullRelease); }
        catch (Exception error) { AppDiagnostics.Error("updater", "Schedule apply/restart", error); throw; }
    }

    private static void RequireSingleInstance()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Use Exit in every other MeetTape window or tray before updating.");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("Cannot verify other MeetTape processes. Close them before updating.");
                }
            }
        }
    }
}
