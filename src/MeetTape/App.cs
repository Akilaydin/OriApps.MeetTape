using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace MeetTape;

public sealed class App : Application
{
    internal static string? StartupError { get; set; }
    internal static bool Smoke { get; set; }
    internal static DesktopInstance? Instance { get; set; }
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private MainWindow? window;
    private MainViewModel model = new();
    private DesktopController? controller;
    private DispatcherTimer? timer;
    private TrayIcon? tray;
    private NativeMenuItem? record;
    private NativeMenuItem? status;
    private bool allowShutdown;
    private bool openingSettings;
    private bool initialized;
    private IAppUpdates? updates;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;
            window.OpenSettings = OpenSettingsAsync;
            window.ExitApplication = ExitAsync;
            window.UpdateApplication = UpdateAsync;
            window.Opened += async (_, _) => await InitializeAsync();
            Instance?.Listen(() => Dispatcher.UIThread.Post(() => { if (!allowShutdown) ShowWindow(); }));
            var menu = new NativeMenu();
            status = new NativeMenuItem(model.CaptureText) { IsEnabled = false };
            var open = new NativeMenuItem("Open MeetTape");
            open.Click += (_, _) => ShowWindow();
            record = new NativeMenuItem("Start recording") { IsEnabled = false };
            record.Click += async (_, _) => { await model.ToggleAsync(); if (model.IsNaming) ShowWindow(); };
            var exit = new NativeMenuItem("Exit");
            exit.Click += async (_, _) => await ExitAsync();
            menu.Items.Add(status); menu.Items.Add(open); menu.Items.Add(record); menu.Items.Add(exit);
            using var iconStream = AssetLoader.Open(new Uri("avares://MeetTape/Assets/MeetTape.png"));
            var icon = new WindowIcon(iconStream);
            window.Icon = icon;
            tray = new TrayIcon { Icon = icon, ToolTipText = "MeetTape", Menu = menu };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, [tray]);
            model.PropertyChanged += ModelChanged;
            desktop.ShutdownRequested += (_, e) =>
            {
                if (allowShutdown) return;
                e.Cancel = true;
                _ = ExitAsync();
            };
            desktop.Exit += (_, _) => { timer?.Stop(); model.Detach(); tray.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (status is not null) status.Header = model.CaptureText;
        if (record is not null) { record.Header = model.RecordLabel; record.IsEnabled = model.CanRecord; }
        if (tray is not null) tray.ToolTipText = "MeetTape — " +
            (model.HasError || model.HasProcessingErrors ? "Error: open window" : model.HasWarnings || model.HasProcessingIssues ? "Warning: open window — " : "") + model.CaptureText;
    }

    private async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        if (StartupError is not null) { model.SetError(StartupError); return; }
        if (Smoke)
        {
            int tick = 0;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                if (++tick == 3)
                {
                    ShowWindow();
                    model.SetError("Smoke recording diagnostic. Original fixture detail retained for copying.");
                    window!.FindControl<Expander>("DiagnosticsExpander")!.IsExpanded = true;
                    var details = window!.FindControl<TextBox>("DiagnosticDetails")!;
                    details.SelectAll();
                    if (details.SelectedText != model.DiagnosticText)
                    { timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if (tick == 5) { timer.Stop(); await ExitAsync(); }
            };
            timer.Start();
            return;
        }
        if (!File.Exists(ConfigPath)) await OpenSettingsAsync();
        try
        {
            var config = await Task.Run(() => ConfigurationLoader.LoadForCapture(ConfigPath));
            controller = new DesktopController(config);
            model.PropertyChanged -= ModelChanged;
            model.Detach();
            model = new MainViewModel(controller);
            model.PropertyChanged += ModelChanged;
            window!.DataContext = model;
            await controller.InitializeAsync();
            await model.RefreshAsync();
            int captureTicks = 0;
            bool readingCapture = false;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += async (_, _) =>
            {
                if (readingCapture) return;
                readingCapture = true;
                try
                {
                    model.RefreshCapture(await controller.ReadCaptureAsync());
                    if (++captureTicks == 10) { captureTicks = 0; await model.RefreshAsync(); }
                }
                catch (Exception) { model.SetError("Cannot refresh recording status. Check Details and retry the operation."); }
                finally { readingCapture = false; }
            };
            timer.Start();
            updates = new AppUpdates();
            model.ReleaseText = await StartupReleaseNotice.GetAsync(updates);
            model.SetInfo("Close hides the window. Use Exit to stop MeetTape.");
        }
        catch (Exception ex) { model.SetError(ex is InvalidDataException ? ex.Message :
            "Cannot load settings or prompts. Open Settings; check configuration paths and permissions."); }
    }

    private static string ConfigPath => ConfigurationBootstrap.UserConfigurationPath;

    private async Task OpenSettingsAsync()
    {
        if (Smoke || StartupError is not null || openingSettings || window is null) return;
        openingSettings = true;
        try
        {
            await Task.Run(() => ConfigurationBootstrap.Initialize(ConfigPath, ConfigurationBootstrap.UserMeetingsRoot));
            IReadOnlyList<AudioDevice> devices;
            try { devices = await Task.Run(AudioDevices.List); }
            catch (Exception) { devices = []; model.SetError("Cannot enumerate devices. Check Windows Audio service and permissions."); }
            var settings = await Task.Run(() => new SettingsViewModel(ConfigPath, devices));
            var dialog = new SettingsWindow { DataContext = settings, Icon = window.Icon };
            ShowWindow();
            await dialog.ShowDialog(window);
            if (settings.Saved) model.SetInfo("Settings saved. Exit and reopen MeetTape to apply them.");
        }
        catch (Exception ex) { model.SetError(ex is InvalidDataException ? ex.Message : "Cannot open settings; check config.json and permissions."); }
        finally { openingSettings = false; }
    }

    private void ShowWindow() { window!.Show(); window.WindowState = WindowState.Normal; window.Activate(); }

    private async Task ExitAsync()
    {
        if (allowShutdown) return;
        if (!await model.ShutdownAsync()) { ShowWindow(); return; }
        allowShutdown = true;
        window!.AllowClose = true;
        desktop!.Shutdown();
    }

    private async Task UpdateAsync()
    {
        if (Smoke || StartupError is not null || window is null) return;
        updates ??= new AppUpdates();
        bool applied = await model.UpdateAsync(updates, async (current, available) =>
        {
            ShowWindow();
            return await new UpdateWindow(current, available) { Icon = window.Icon }.ShowDialog<bool>(window);
        });
        if (!applied) return;
        allowShutdown = true;
        window.AllowClose = true;
        desktop!.Shutdown();
    }
}
