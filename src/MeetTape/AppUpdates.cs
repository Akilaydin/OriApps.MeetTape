using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MeetTape;

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
    private readonly string[] restartArgs;
    public bool IsInstalled => manager.IsInstalled && !manager.IsPortable;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ??
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public AppUpdates(string[]? restartArgs = null) : this(new UpdateManager(
        new GithubSource("https://github.com/Akilaydin/OriApps.MeetTape", null, false)), restartArgs) { }

    public AppUpdates(UpdateManager manager, string[]? restartArgs = null)
    {
        this.manager = manager;
        this.restartArgs = restartArgs ?? [];
    }

    public Task<UpdateInfo?> CheckAsync() => manager.CheckForUpdatesAsync();

    public async Task DownloadAsync(UpdateInfo update, Action<int> progress)
    {
        RequireSingleInstance();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await manager.DownloadUpdatesAsync(update, progress, deadline.Token).ConfigureAwait(false);
    }

    public void ApplyAndRestart(UpdateInfo update)
    {
        RequireSingleInstance();
        // The controller has already joined capture/preparation/jobs. Let Avalonia close normally.
        manager.WaitExitThenApplyUpdates(update.TargetFullRelease, restartArgs: restartArgs);
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
