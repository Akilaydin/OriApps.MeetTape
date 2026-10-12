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
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
