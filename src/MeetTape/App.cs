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
    private WindowIcon? idleIcon;
    private WindowIcon? recordingIcon;
    private NativeMenuItem? record;
    private NativeMenuItem? status;
    private NativeMenuItem? processingStatus;
    private bool allowShutdown;
    private bool openingSettings;
    private bool initialized;
    private bool initializingController;
    private IAppUpdates? updates;
    private RecordingHotkey? hotkey;
    private RecordingNotifications? notifications;
    private RecordingTaskbar? taskbar;
    private uint taskbarButtonCreated;

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
            window.Opened += async (_, _) =>
            {
                InitializeTaskbar();
                await InitializeAsync();
                if (!Smoke && StartupError is null) InitializeHotkey();
            };
            Instance?.Listen(() => Dispatcher.UIThread.Post(() => { if (!allowShutdown) ShowWindow(); }));
            var menu = new NativeMenu();
            status = new NativeMenuItem(model.CaptureText) { IsEnabled = false };
            processingStatus = new NativeMenuItem(model.ProcessingText) { IsEnabled = false };
            var open = new NativeMenuItem("Open MeetTape");
            open.Click += (_, _) => ShowWindow();
            record = new NativeMenuItem("Start recording") { IsEnabled = false };
            record.Click += async (_, _) => { await model.ToggleAsync(); if (model.IsNaming) ShowWindow(); };
            var exit = new NativeMenuItem("Exit");
            exit.Click += async (_, _) => await ExitAsync();
            menu.Items.Add(status); menu.Items.Add(processingStatus); menu.Items.Add(open); menu.Items.Add(record); menu.Items.Add(exit);
            using var iconStream = AssetLoader.Open(new Uri("avares://MeetTape/Assets/MeetTape.ico"));
            var icon = new WindowIcon(iconStream);
            idleIcon = icon;
            using var recordingStream = AssetLoader.Open(new Uri("avares://MeetTape/Assets/MeetTape-recording.ico"));
            recordingIcon = new WindowIcon(recordingStream);
            window.Icon = icon;
            tray = new TrayIcon { Icon = icon, ToolTipText = model.TrayText, Menu = menu };
            if (!Smoke && StartupError is null)
                notifications = new(() => Dispatcher.UIThread.Post(() =>
                { if (!allowShutdown) model.SetInfo("Windows notifications unavailable. Use the window or tray for recording status."); }));
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, [tray]);
            model.PropertyChanged += ModelChanged;
            desktop.ShutdownRequested += (_, e) =>
            {
                if (allowShutdown) return;
                e.Cancel = true;
                _ = ExitAsync();
            };
            desktop.Exit += (_, _) =>
            {
                hotkey?.Dispose();
                taskbar?.Dispose();
                Win32Properties.RemoveWndProcHookCallback(window, TaskbarMessage);
                Win32Properties.RemoveWndProcHookCallback(window, HotkeyMessage);
                notifications?.Dispose();
                timer?.Stop(); model.Detach(); tray.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (status is not null) status.Header = model.CaptureText;
        if (processingStatus is not null) processingStatus.Header = model.ProcessingText;
        if (record is not null) { record.Header = model.RecordLabel + " (Ctrl+Shift+F9)"; record.IsEnabled = model.CanRecord; }
        if (tray is not null) tray.ToolTipText = model.TrayText;
        taskbar?.Update(model.IsRecording);
        if (window is not null && tray is not null && idleIcon is not null && recordingIcon is not null)
        {
            var icon = model.IsRecording ? recordingIcon : idleIcon;
            if (!ReferenceEquals(tray.Icon, icon)) { tray.Icon = icon; window.Icon = icon; }
        }
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
                tick++;
                if (tick == 1 || tick == 4)
                {
                    model.RefreshCapture(new(true, TimeSpan.FromSeconds(tick), 0, 0, null));
                    if (!ReferenceEquals(tray!.Icon, recordingIcon) || !ReferenceEquals(window!.Icon, recordingIcon) || taskbar?.Applied != true)
                    { timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if (tick == 2)
                {
                    model.RefreshCapture(new(false, TimeSpan.FromSeconds(1), 0, 0, "Fixture interruption"));
                    if (!ReferenceEquals(tray!.Icon, idleIcon) || !ReferenceEquals(window!.Icon, idleIcon) || taskbar?.Applied != true)
                    { timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if (tick == 3)
                {
                    ShowWindow();
                    model.SetError("Smoke recording diagnostic. Original fixture detail retained for copying.");
                    window!.FindControl<Expander>("DiagnosticsExpander")!.IsExpanded = true;
                    var details = window!.FindControl<TextBox>("DiagnosticDetails")!;
                    details.SelectAll();
                    if (details.SelectedText != model.DiagnosticText || !Equals(status!.Header, model.CaptureText)
                        || !Equals(processingStatus!.Header, model.ProcessingText) || tray!.ToolTipText != model.TrayText
                        || window!.Icon is null || !ReferenceEquals(window.Icon, tray.Icon))
                    { timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if (tick == 5)
                {
                    model.RefreshCapture(new(false, TimeSpan.FromSeconds(1), 0, 0, null));
                    if (!ReferenceEquals(tray!.Icon, idleIcon) || !ReferenceEquals(window!.Icon, idleIcon) || taskbar?.Applied != true)
                    { timer.Stop(); desktop!.Shutdown(1); return; }
                    timer.Stop(); await ExitAsync();
                }
            };
            timer.Start();
            return;
        }
        initializingController = true;
        try
        {
            if (!File.Exists(ConfigPath)) await OpenSettingsAsync();
            var config = await Task.Run(() => ConfigurationLoader.LoadForCapture(ConfigPath));
            controller = new DesktopController(config);
            model.PropertyChanged -= ModelChanged;
            model.Detach();
            model = new MainViewModel(controller, notice => notifications?.Show(notice));
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
        finally { initializingController = false; }
    }

    private static string ConfigPath => ConfigurationBootstrap.UserConfigurationPath;

    private void InitializeTaskbar()
    {
        if (taskbar is not null || window?.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
        try
        {
            taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
            taskbar = new RecordingTaskbar(handle);
            Win32Properties.AddWndProcHookCallback(window, TaskbarMessage);
            taskbar.Update(model.IsRecording);
        }
        catch (Exception) { taskbar?.Dispose(); taskbar = null; }
    }

    private nint TaskbarMessage(nint handle, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (taskbarButtonCreated != 0 && message == taskbarButtonCreated) taskbar?.Restore();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private void InitializeHotkey()
    {
        if (hotkey is not null || window is null) return;
        try
        {
            nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            if (handle == 0) throw new InvalidOperationException("Ctrl+Shift+F9 is unavailable. Use Start/Stop buttons or the tray.");
            hotkey = new(handle);
            Win32Properties.AddWndProcHookCallback(window, HotkeyMessage);
        }
        catch (Exception)
        {
            hotkey?.Dispose(); hotkey = null;
            model.SetInfo("Ctrl+Shift+F9 is unavailable or already in use. Start/Stop buttons and tray remain available.");
        }
    }

    private nint HotkeyMessage(nint handle, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (hotkey?.Matches(message, wParam) == true)
        {
            handled = true;
            Dispatcher.UIThread.Post(async () => await model.ToggleFromHotkeyAsync());
        }
        return 0;
    }

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
            var settings = await Task.Run(() => new SettingsViewModel(ConfigPath, devices, applySettings: ApplySettingsAsync));
            var dialog = new SettingsWindow { DataContext = settings, Icon = window.Icon };
            ShowWindow();
            await dialog.ShowDialog(window);
            if (settings.Saved) model.SetInfo(settings.Message);
        }
        catch (Exception ex) { model.SetError(ex is InvalidDataException ? ex.Message : "Cannot open settings; check config.json and permissions."); }
        finally { openingSettings = false; }
    }

    private async Task ApplySettingsAsync(LoadedConfiguration updated)
    {
        if (controller is not null)
        {
            await controller.ApplyConfigurationAsync(updated);
            await model.RefreshAsync();
        }
        else if (!initializingController)
        {
            initialized = false;
            await InitializeAsync();
        }
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
