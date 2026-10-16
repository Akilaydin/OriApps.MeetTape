using OriApps.MeetTape.Audio;
using OriApps.MeetTape.Diagnostics;
using OriApps.MeetTape.Platform.Windows;
using OriApps.MeetTape.Reminders;
using OriApps.MeetTape.Settings;
using OriApps.MeetTape.Updates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace OriApps.MeetTape.Desktop;

public sealed class App : Application
{
    internal static string? StartupError { get; set; }
    internal static bool Smoke { get; set; }
    internal static bool StartInTray { get; set; }
    internal static DesktopInstance? Instance { get; set; }
    private static bool TaskbarSmoke => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
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
    private bool controllerReady;
    private IAppUpdates? updates;
    private RecordingHotkey? hotkey;
    private bool hotkeyHooked;
    internal Func<string, int, RecordingHotkey>? HotkeyFactory { get; set; }
    private RecordingNotifications? notifications;
    private WindowsReminderNotifications? reminderNotifications;
    private ReminderScheduler? reminderScheduler;
    private readonly List<(Guid Id, DateOnly Occurrence, string Action)> pendingReminderActions = [];
    private RecordingTaskbar? taskbar;
    private uint taskbarButtonCreated;
    private bool taskbarCreated;

    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow { DataContext = model, ShowInTaskbar = !StartInTray,
                WindowState = StartInTray ? WindowState.Minimized : WindowState.Normal };
            taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
            Win32Properties.AddWndProcHookCallback(window, TaskbarMessage);
            desktop.MainWindow = window;
            window.OpenSettings = OpenSettingsAsync;
            window.ExitApplication = ExitAsync;
            window.UpdateApplication = UpdateAsync;
            window.Opened += async (_, _) =>
            {
                InitializeTaskbar();
                await InitializeAsync();
                if (!Smoke && StartupError is null) InitializeHotkey();
                if (StartInTray && controller is not null) window.Hide();
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
            {
                notifications = new(() => Dispatcher.UIThread.Post(() =>
                { if (!allowShutdown) model.SetInfo("Windows notifications unavailable. Use the window or tray for recording status."); }));
                try
                {
                    reminderNotifications = new((id, occurrence, action) =>
                        Dispatcher.UIThread.Post(() => _ = HandleReminderActionAsync(id, occurrence, action)),
                        () => Dispatcher.UIThread.Post(() =>
                        { if (!allowShutdown) model.SetInfo("Windows reminder notifications are unavailable. Check Windows notifications settings."); }));
                }
                catch (Exception error)
                {
                    AppDiagnostics.Error("reminders", "Initialize Windows notifications", error);
                    reminderNotifications = null;
                    model.SetInfo("Windows reminder notifications are unavailable. Check Windows notifications settings.");
                }
            }
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, [tray]);
            model.PropertyChanged += ModelChanged;
            InitializeTaskbar();
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
                reminderNotifications?.Dispose();
                timer?.Stop(); model.Detach(); tray.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (status is not null) status.Header = model.CaptureText;
        if (processingStatus is not null) processingStatus.Header = model.ProcessingText;
        if (record is not null) { record.Header = model.RecordLabel + " (" + model.RecordingShortcut + ")"; record.IsEnabled = model.CanRecord; }
        if (tray is not null) tray.ToolTipText = model.TrayText;
        if (window is not null && tray is not null && idleIcon is not null && recordingIcon is not null)
        {
            var icon = model.IsRecording ? recordingIcon : idleIcon;
            if (!ReferenceEquals(tray.Icon, icon)) { tray.Icon = icon; window.Icon = icon; }
        }
        // Avalonia SetIcon clears the taskbar overlay to redraw the window icon.
        taskbar?.Update(model.IsRecording);
    }

    private static async Task<bool> WaitForTaskbarSnapshotAsync(int stage)
    {
        string? directory = Environment.GetEnvironmentVariable("MEETTAPE_TASKBAR_PROBE_DIRECTORY");
        if (!TaskbarSmoke || string.IsNullOrEmpty(directory)) return true;
        Console.Error.WriteLine($"Icon smoke stage {stage} ready.");
        string acknowledgement = Path.Combine(directory, $"taskbar-{stage}.ready");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!File.Exists(acknowledgement))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Console.Error.WriteLine($"Icon smoke stage {stage} was not rendered before the deadline.");
                return false;
            }
            await Task.Delay(50);
        }
        return true;
    }

    private async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        if (StartupError is not null) { model.SetError(StartupError); return; }
        if (Smoke)
        {
            int tick = 0;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                tick++;
                Console.Error.WriteLine($"Icon smoke tick {tick} begins; taskbarApplied={taskbar?.Applied}, buttonCreated={taskbarCreated}.");
                if (tick == 1 && !await WaitForTaskbarSnapshotAsync(0)) { desktop!.Shutdown(1); return; }
                if (tick == 1 || tick == 4)
                {
                    model.RefreshCapture(new(true, TimeSpan.FromSeconds(tick), 0, 0, null));
                    if (!ReferenceEquals(tray!.Icon, recordingIcon) || !ReferenceEquals(window!.Icon, recordingIcon) || (TaskbarSmoke && taskbar?.Applied != true))
                    { Console.Error.WriteLine($"Icon smoke tick {tick}: recording={model.IsRecording}, taskbarApplied={taskbar?.Applied}, buttonCreated={taskbarCreated}."); timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if (tick == 2)
                {
                    model.RefreshCapture(new(false, TimeSpan.FromSeconds(1), 0, 0, "Fixture interruption"));
                    if (!ReferenceEquals(tray!.Icon, idleIcon) || !ReferenceEquals(window!.Icon, idleIcon) || (TaskbarSmoke && taskbar?.Applied != true))
                    { Console.Error.WriteLine($"Icon smoke tick {tick}: recording={model.IsRecording}, taskbarApplied={taskbar?.Applied}, buttonCreated={taskbarCreated}."); timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if ((tick == 1 || tick == 2) && !await WaitForTaskbarSnapshotAsync(tick)) { desktop!.Shutdown(1); return; }
                if (tick == 3)
                {
                    ShowWindow();
                    model.SetError("Smoke recording failure. Check the selected device and retry.");
                    if (window!.FindControl<TextBlock>("ErrorMessage")?.Text != model.ErrorText
                        || window!.FindControl<Expander>("DiagnosticsExpander") is not null
                        || !Equals(status!.Header, model.CaptureText)
                        || !Equals(processingStatus!.Header, model.ProcessingText) || tray!.ToolTipText != model.TrayText
                        || window!.Icon is null || !ReferenceEquals(window.Icon, tray.Icon))
                    { Console.Error.WriteLine($"Icon smoke tick {tick}: recording={model.IsRecording}, taskbarApplied={taskbar?.Applied}, buttonCreated={taskbarCreated}."); timer.Stop(); desktop!.Shutdown(1); return; }
                }
                if (tick == 5)
                {
                    model.RefreshCapture(new(false, TimeSpan.FromSeconds(1), 0, 0, null));
                    if (!ReferenceEquals(tray!.Icon, idleIcon) || !ReferenceEquals(window!.Icon, idleIcon) || (TaskbarSmoke && taskbar?.Applied != true))
                    { Console.Error.WriteLine($"Icon smoke tick {tick} failed; taskbarApplied={taskbar?.Applied}, buttonCreated={taskbarCreated}."); timer.Stop(); desktop!.Shutdown(1); return; }
                    timer.Stop(); await ExitAsync(); return;
                }
                timer.Start();
            };
            timer.Start();
            return;
        }
        initializingController = true;
        controllerReady = false;
        try
        {
            if (!File.Exists(ConfigPath)) await OpenSettingsAsync();
            var config = await Task.Run(() => ConfigurationLoader.LoadForCapture(ConfigPath));
            reminderScheduler = new ReminderScheduler(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "reminder-progress.json"), config.Settings.Reminders);
            reminderScheduler.Alert = alert => reminderNotifications?.Show(alert) == true;
            controller = new DesktopController(config);
            model.PropertyChanged -= ModelChanged;
            model.Detach();
            model = new MainViewModel(controller, notice => notifications?.Show(notice));
            model.SetRecordingShortcut(config.Settings.RecordingHotkey);
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
                    if (++captureTicks == 10)
                    {
                        captureTicks = 0;
                        await model.RefreshAsync();
                        reminderScheduler?.Tick(model.IsRecording);
                    }
                }
                catch (Exception error)
                {
                    AppDiagnostics.Error("ui", "Refresh capture", error);
                    model.SetError(AppDiagnostics.UserMessage(error, "Cannot refresh recording status. Check Details and retry the operation."));
                }
                finally { readingCapture = false; }
            };
            timer.Start();
            updates = new AppUpdates();
            model.ReleaseText = await StartupReleaseNotice.GetAsync(updates);
            model.SetInfo("Close hides the window. Use tray Exit to stop MeetTape.");
            controllerReady = true;
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("startup", "Initialize settings/controller", error);
            model.SetError(AppDiagnostics.UserMessage(error, "Cannot load settings or prompts. Open Settings; check configuration paths and permissions."));
        }
        finally { initializingController = false; }
        await DrainPendingReminderActionsAsync(pendingReminderActions,
            () => controllerReady && controller is not null && reminderScheduler is not null && !initializingController && !allowShutdown,
            HandleReminderActionAsync);
    }

    internal static async Task DrainPendingReminderActionsAsync(
        List<(Guid Id, DateOnly Occurrence, string Action)> pending,
        Func<bool> ready, Func<Guid, DateOnly, string, Task> handle)
    {
        if (!ready() || pending.Count == 0) return;
        var actions = pending.ToArray();
        pending.Clear();
        foreach (var action in actions)
            await handle(action.Id, action.Occurrence, action.Action);
    }

    private static string ConfigPath => ConfigurationBootstrap.UserConfigurationPath;

    private void InitializeTaskbar()
    {
        if (Smoke && !TaskbarSmoke) return;
        if (taskbar is not null || window?.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
        try
        {
            _ = ChangeWindowMessageFilterEx(handle, taskbarButtonCreated, 1, 0);
            if (Smoke) Console.Error.WriteLine($"Taskbar shell handle: {GetShellWindow()}.");
            taskbar = new RecordingTaskbar(handle);
            taskbar.Update(model.IsRecording);
            if (taskbarCreated) taskbar.Restore();
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("taskbar", "Initialize", error);
            taskbar?.Dispose(); taskbar = null;
            if (Smoke) Console.Error.WriteLine($"Taskbar initialization failed: {error.GetType().Name}, HRESULT 0x{error.HResult:X8}.");
        }
    }

    private nint TaskbarMessage(nint handle, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (taskbarButtonCreated != 0 && message == taskbarButtonCreated)
        {
            taskbarCreated = true;
            taskbar?.Restore();
        }
        else if (message is 0x0080 or 0x02E0 or 0x031A)
            Dispatcher.UIThread.Post(() => taskbar?.Restore(created: false), DispatcherPriority.Background);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint change);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    private void InitializeHotkey()
    {
        if (hotkey is not null || window is null) return;
        try
        {
            hotkey = CreateHotkey(model.RecordingShortcut, RecordingHotkey.Id);
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("hotkey", "Register", error);
            hotkey?.Dispose(); hotkey = null;
            model.SetInfo($"{model.RecordingShortcut} is unavailable or already in use. Start/Stop buttons and tray remain available.");
        }
    }

    private RecordingHotkey CreateHotkey(string shortcut, int id)
    {
        if (HotkeyFactory is { } factory) return factory(shortcut, id);
        nint handle = window?.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) throw new InvalidOperationException("Recording hotkey is unavailable. Use Start/Stop buttons or the tray.");
        if (!hotkeyHooked)
        {
            Win32Properties.AddWndProcHookCallback(window!, HotkeyMessage);
            hotkeyHooked = true;
        }
        return new RecordingHotkey(handle, shortcut, id);
    }

    internal Action<bool> PrepareHotkey(string shortcut)
    {
        if (hotkey?.Shortcut == shortcut) return _ => { };
        RecordingHotkey candidate;
        try { candidate = CreateHotkey(shortcut, hotkey?.RegistrationId == RecordingHotkey.Id ? RecordingHotkey.Id + 1 : RecordingHotkey.Id); }
        catch (InvalidOperationException error) { throw new InvalidDataException(error.Message); }
        return saved =>
        {
            if (!saved || allowShutdown) { candidate.Dispose(); return; }
            var previous = hotkey; hotkey = candidate;
            previous?.Dispose();
            model.SetRecordingShortcut(candidate.Shortcut);
        };
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
            catch (Exception error)
            {
                AppDiagnostics.Error("devices", "Enumerate", error);
                devices = []; model.SetError(AppDiagnostics.UserMessage(error, "Cannot enumerate devices. Check Windows Audio service and permissions."));
            }
            var settings = new SettingsViewModel(ConfigPath, devices, applySettings: ApplySettingsAsync,
                prepareHotkey: PrepareHotkey, nextReminder: reminderScheduler is null ? null : reminderScheduler.NextOccurrenceText,
                actualRunAtStartup: WindowsStartup.IsEnabled());
            var dialog = new SettingsWindow { DataContext = settings, Icon = window.Icon };
            updates ??= new AppUpdates();
            dialog.ConfigureAbout(model, updates, () => UpdateAsync(dialog));
            ShowWindow();
            await dialog.ShowDialog(window);
            if (settings.Saved) model.SetInfo(settings.Message);
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("settings", "Open", error);
            model.SetError(AppDiagnostics.UserMessage(error, "Cannot open settings; check config.json and permissions."));
        }
        finally { openingSettings = false; }
    }

    private async Task ApplySettingsAsync(LoadedConfiguration updated)
    {
        reminderScheduler?.Configure(updated.Settings.Reminders);
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
        if (!Smoke && WindowsStartup.IsEnabled() != updated.Settings.Reminders.RunAtWindowsStartup)
        {
            try { WindowsStartup.SetEnabled(updated.Settings.Reminders.RunAtWindowsStartup); }
            catch (Exception error)
            {
                AppDiagnostics.Error("reminders", "Windows startup registration", error);
                throw new InvalidOperationException("Settings saved, but Windows startup registration failed. Reopen Settings and retry.", error);
            }
        }
    }

    private void ShowWindow()
    {
        window!.ShowInTaskbar = true;
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }

    private async Task HandleReminderActionAsync(Guid id, DateOnly occurrence, string action)
    {
        if (allowShutdown) return;
        if (reminderScheduler is null || controller is null || initializingController || !controllerReady)
        {
            pendingReminderActions.Add((id, occurrence, action));
            return;
        }
        try
        {
            switch (action)
            {
                case "snooze":
                    reminderScheduler.Snooze(id, occurrence);
                    break;
                case "dismiss":
                    reminderScheduler.Dismiss(id, occurrence);
                    break;
                case "start":
                    if (reminderScheduler.TryStart(id, occurrence))
                    {
                        bool started = false;
                        try { started = await model.StartOnlyAsync(); }
                        finally { reminderScheduler.CompleteStart(id, occurrence, started); }
                        if (model.IsNaming) ShowWindow();
                    }
                    break;
            }
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("reminders", "Notification action", error);
            model.SetError("Cannot complete reminder action. Open MeetTape to check recording status.");
        }
    }

    private async Task ExitAsync()
    {
        if (allowShutdown) return;
        if (!await model.ShutdownAsync()) { ShowWindow(); return; }
        allowShutdown = true;
        window!.AllowClose = true;
        desktop!.Shutdown();
    }

    private Task UpdateAsync() => window is null ? Task.CompletedTask : UpdateAsync(window);

    private async Task UpdateAsync(Window owner)
    {
        if (Smoke || StartupError is not null || window is null) return;
        updates ??= new AppUpdates();
        bool applied = await model.UpdateAsync(updates, async (current, available) =>
        {
            if (ReferenceEquals(owner, window)) ShowWindow();
            return await new UpdateWindow(current, available) { Icon = window.Icon }.ShowDialog<bool>(owner);
        });
        if (!applied) return;
        allowShutdown = true;
        window.AllowClose = true;
        desktop!.Shutdown();
    }
}
