using Avalonia;
using System.Runtime.InteropServices;

namespace MeetTape;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--ui-smoke"])
        {
            App.Smoke = true;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        var positional = new List<string>();
        string? path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                path = args[++i];
            else positional.Add(args[i]);
        }
        if (positional.Count == 0 || positional is ["configure"])
        {
            App.ConfigurationPath = path;
            App.SettingsRequested = positional.Count > 0;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        AttachTerminal();
        return Cli.Run(args.Where(a => a != "--console").ToArray(), Console.In, Console.Out, Console.Error);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    private static void AttachTerminal()
    {
        // Preserve inherited pipe handles across AttachConsole so help works with redirection.
        var saved = new[] { GetStdHandle(-10), GetStdHandle(-11), GetStdHandle(-12) };
        if (AttachConsole(uint.MaxValue))
            for (int i = 0; i < saved.Length; i++)
                if (saved[i] != IntPtr.Zero && saved[i] != new IntPtr(-1) && GetFileType(saved[i]) != 0)
                    SetStdHandle(-10 - i, saved[i]);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int handle, IntPtr value);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
}
