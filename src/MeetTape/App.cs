using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace MeetTape;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open MeetTape");
            open.Click += (_, _) => { window.Show(); window.Activate(); };
            var record = new NativeMenuItem("Start / Stop");
            record.Click += (_, _) => model.Toggle();
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => desktop.Shutdown();
            menu.Items.Add(open); menu.Items.Add(record); menu.Items.Add(exit);
            using var iconStream = AssetLoader.Open(new Uri("avares://MeetTape/Assets/MeetTape.png"));
            var tray = new TrayIcon { Icon = new WindowIcon(iconStream), ToolTipText = "MeetTape", Menu = menu };
            TrayIcon.SetIcons(this, [tray]);
            desktop.Exit += (_, _) => tray.Dispose();
            if (desktop.Args is ["--ui-smoke"])
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); desktop.Shutdown(); };
                timer.Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
