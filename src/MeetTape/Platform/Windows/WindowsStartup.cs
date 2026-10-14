using Microsoft.Win32;

namespace OriApps.MeetTape.Platform.Windows;

internal interface ILoginStartupStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class WindowsRunStartupStore : ILoginStartupStore
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "OriApps.MeetTape";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(EntryName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue(EntryName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        key?.DeleteValue(EntryName, throwOnMissingValue: false);
    }
}

internal sealed class LoginStartupRegistrar
{
    private readonly ILoginStartupStore store;
    private readonly Func<string?> processPath;
    private readonly Func<string, bool> fileExists;

    internal LoginStartupRegistrar(ILoginStartupStore store, Func<string?> processPath,
        Func<string, bool>? fileExists = null)
    {
        this.store = store;
        this.processPath = processPath;
        this.fileExists = fileExists ?? File.Exists;
    }

    internal bool IsEnabled() => TryGetCommand(out string? expected)
        && string.Equals(store.Read(), expected, StringComparison.OrdinalIgnoreCase);

    internal void SetEnabled(bool enabled)
    {
        if (!enabled) { store.Delete(); return; }
        if (!TryGetCommand(out string? command))
            throw new InvalidOperationException("Start with Windows requires a Velopack-installed MeetTape. Install MeetTape first.");
        store.Write(command!);
    }

    private bool TryGetCommand(out string? command)
    {
        command = null;
        string? executable = processPath();
        if (string.IsNullOrEmpty(executable)
            || !string.Equals(Path.GetFileName(executable), "MeetTape.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        string? current = Path.GetDirectoryName(executable);
        if (!string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase)) return false;
        string? root = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(root)) return false;
        string stub = Path.Combine(root, "MeetTape.exe");
        if (!fileExists(stub)) return false;
        command = $"\"{stub}\" {WindowsStartup.TrayLaunchArgument}";
        return true;
    }
}

internal static class WindowsStartup
{
    internal const string TrayLaunchArgument = "--tray";
    private static readonly LoginStartupRegistrar Registrar =
        new(new WindowsRunStartupStore(), () => Environment.ProcessPath);

    internal static bool IsEnabled() => Registrar.IsEnabled();
    internal static void SetEnabled(bool enabled) => Registrar.SetEnabled(enabled);
}
