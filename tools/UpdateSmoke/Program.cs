using OriApps.MeetTape;
using Velopack;
using Velopack.Locators;

// Test-only host. All packages/paths are supplied by Test-Package.ps1 inside dist.
if (args.Length != 4) throw new ArgumentException("Expected mode, feed directory, installation root, current version.");
var (mode, feed, root, version) = (args[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3]);
var packages = Path.Combine(root, "packages");
Directory.CreateDirectory(packages);
var locator = new TestVelopackLocator("OriApps.MeetTape", version, packages,
    Path.Combine(root, "current"), root, Path.Combine(root, "Update.exe"), "win");
var manager = new UpdateManager(feed, null, locator);
if (mode == "startup")
{
    if (manager.UpdatePendingRestart is null) throw new Exception("Expected a previously downloaded update.");
    typeof(VelopackLocator).GetMethod("SetCurrentLocator", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, [locator]);
    // Invoke real Main on STA and let its isolated Avalonia lifetime open and exit normally.
    int result = -1;
    Exception? failure = null;
    var desktop = new Thread(() =>
    {
        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new Exception("Desktop host is not STA.");
            result = (int)typeof(App).Assembly.EntryPoint!.Invoke(null, [new[] { "--ui-smoke" }])!;
        }
        catch (Exception ex) { failure = ex; }
    }) { IsBackground = true };
    desktop.SetApartmentState(ApartmentState.STA);
    desktop.Start();
    if (!desktop.Join(TimeSpan.FromSeconds(30))) throw new Exception("Real Avalonia desktop lifetime did not exit.");
    if (failure is not null) throw new Exception("Real desktop startup failed.", failure);
    if (result != 0 || locator.ExitCode is not null || manager.CurrentVersion?.ToString() != version)
        throw new Exception("Startup tried to apply or exit for a pending update.");
    if (manager.UpdatePendingRestart is null) throw new Exception("Pending update disappeared without consent.");
    Console.WriteLine("Real Main: pending update retained, no automatic apply/restart.");
}
else
{
    var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("Expected a newer full package.");
    if (mode == "decline")
    {
        if (manager.UpdatePendingRestart is not null) throw new Exception("Check unexpectedly downloaded an update.");
        Console.WriteLine("SDK check: update available, no package download.");
    }
    else if (mode == "download")
    {
        await manager.DownloadUpdatesAsync(update);
        if (manager.UpdatePendingRestart?.Version != update.TargetFullRelease.Version) throw new Exception("Download did not prepare update.");
        Console.WriteLine("SDK download/checksum: full update prepared without apply.");
    }
    else if (mode == "checksum")
    {
        try { await manager.DownloadUpdatesAsync(update); }
        catch (Velopack.Exceptions.ChecksumFailedException)
        { Console.WriteLine("SDK rejected a corrupted update package."); return; }
        throw new Exception("SDK accepted a corrupted update package.");
    }
    else throw new ArgumentException("Unknown smoke mode.");
}
