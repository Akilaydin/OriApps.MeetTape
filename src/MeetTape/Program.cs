using Avalonia;

namespace MeetTape;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--ui-smoke"])
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return Cli.Run(args, Console.In, Console.Out, Console.Error);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
