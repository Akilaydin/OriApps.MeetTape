using Avalonia;
using Velopack;

namespace MeetTape;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        ConfigureStartup(args);
        using var instance = App.Smoke || App.StartupError is not null
            ? null : new DesktopInstance(@"Local\OriApps.MeetTape.Desktop");
        if (instance is { IsPrimary: false }) { instance.ActivateExisting(); return 0; }
        App.Instance = instance;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { App.Instance = null; }
    }

    internal static void ConfigureStartup(string[] args)
    {
        App.Smoke = args is ["--ui-smoke"];
        App.StartupError = args.Length > 0 && !App.Smoke
            ? "MeetTape no longer accepts commands or configuration arguments. Close this window and launch MeetTape without arguments."
            : null;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
