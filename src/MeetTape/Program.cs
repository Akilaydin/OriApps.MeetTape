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
        using var diagnostics = App.Smoke || App.StartupError is not null ? null : new FileDiagnostics(AppDiagnostics.DirectoryPath);
        AppDiagnostics.ProcessLog = diagnostics;
        void Unhandled(object sender, UnhandledExceptionEventArgs e)
        { if (e.ExceptionObject is Exception error) AppDiagnostics.Error("startup", "Unhandled exception", error); }
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
        AppDiagnostics.Info("startup", "Desktop starting");
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error) { AppDiagnostics.Error("startup", "Desktop failed", error); throw; }
        finally
        {
            AppDiagnostics.Info("startup", "Desktop exiting");
            AppDomain.CurrentDomain.UnhandledException -= Unhandled;
            AppDiagnostics.ProcessLog = null;
            App.Instance = null;
        }
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
