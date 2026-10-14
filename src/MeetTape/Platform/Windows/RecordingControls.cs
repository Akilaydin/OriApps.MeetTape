using OriApps.MeetTape.Desktop;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Win32.Input;

namespace OriApps.MeetTape.Platform.Windows;

internal sealed class RecordingHotkey : IDisposable
{
    internal const string DefaultShortcut = "Ctrl+Shift+F9";
    internal const int Id = 0x4D54;
    internal const uint Modifiers = 0x4006; // Control + Shift + MOD_NOREPEAT.
    internal const uint Key = 0x78; // F9.
    private readonly nint window;
    private readonly Func<nint, int, bool> unregister;
    internal int RegistrationId { get; }
    internal string Shortcut { get; }
    private bool disposed;

    internal RecordingHotkey(nint window, Func<nint, int, uint, uint, bool>? register = null,
        Func<nint, int, bool>? unregister = null)
        : this(window, DefaultShortcut, Id, register, unregister) { }

    internal RecordingHotkey(nint window, string shortcut, int id = Id,
        Func<nint, int, uint, uint, bool>? register = null, Func<nint, int, bool>? unregister = null)
    {
        this.window = window;
        this.unregister = unregister ?? UnregisterHotKey;
        var parsed = Parse(shortcut);
        Shortcut = parsed.Text; RegistrationId = id;
        if (!(register ?? RegisterHotKey)(window, id, parsed.Modifiers | 0x4000, parsed.Key))
            throw new InvalidOperationException($"{Shortcut} is unavailable or already in use. Start/Stop buttons and tray remain available.");
    }

    internal static (uint Modifiers, uint Key, string Text) Parse(string? text)
    {
        const string error = "Enter a recording hotkey such as Ctrl+Shift+F9, with a modifier and one key, or F1-F24.";
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException(error);
        text = text.Trim();
        try
        {
            var gesture = KeyGesture.Parse(text);
            string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
            foreach (string prefix in parts.Take(parts.Length - (text.EndsWith("++", StringComparison.Ordinal) ? 2 : 1)))
            {
                var modifier = KeyGesture.Parse(prefix);
                if (modifier.Key != Avalonia.Input.Key.None || modifier.KeyModifiers == KeyModifiers.None)
                    throw new InvalidDataException(error);
            }
            uint key = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
            uint modifiers = (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 1u : 0)
                | (gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? 2u : 0)
                | (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 4u : 0)
                | (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? 8u : 0);
            if (key is 0 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5
                || (modifiers == 0 && key is not (>= 0x70 and <= 0x87)))
                throw new InvalidDataException(error);
            return (modifiers, key, gesture.ToString().Replace("Cmd+", "Win+", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { throw new InvalidDataException(error); }
    }

    internal bool Matches(uint message, nint id) => !disposed && message == 0x0312 && id == RegistrationId;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        _ = unregister(window, RegistrationId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}

// COM can query the cancellation interface only when its declaring type is publicly visible.
[ComVisible(true), Guid("7307055C-B24A-486B-9F25-163E597A28A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRecordingNotificationContinue
{
    [PreserveSig] int QueryContinue();
}

internal sealed class RecordingNotifications(Action unavailable,
    Action<OperationNotice, CancellationToken>? display = null) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? current;
    private bool disposed;

    internal void Show(OperationNotice notice)
    {
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (disposed) return;
            current?.Cancel();
            current = cancellation = new();
        }
        _ = Task.Run(() =>
        {
            try { (display ?? ShowNative)(notice, cancellation.Token); }
            catch (Exception)
            {
                if (!cancellation.IsCancellationRequested)
                    try { unavailable(); } catch (Exception) { }
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(current, cancellation)) current = null;
                    cancellation.Dispose();
                }
            }
        });
    }

    internal static void ShowNative(OperationNotice notice, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var notification = (IUserNotification2)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("0010890E-8789-413C-ADBC-48F5B511B3AF"), throwOnError: true)!)!;
        try
        {
            uint kind = notice.Kind == NoticeKind.Error ? 3u : notice.Kind == NoticeKind.Warning ? 2u : 1u;
            Marshal.ThrowExceptionForHR(notification.SetBalloonInfo(notice.Message,
                "Recording status is also available in the MeetTape window and tray.", kind | 0x10));
            Marshal.ThrowExceptionForHR(notification.SetBalloonRetry(4000, 0, 0));
            Marshal.ThrowExceptionForHR(notification.SetIconInfo(LoadIcon(0, 32512), "MeetTape"));
            deadline.Token.ThrowIfCancellationRequested();
            int result = notification.Show(new ContinueNotification(deadline.Token), 100, 0);
            if (!deadline.IsCancellationRequested) Marshal.ThrowExceptionForHR(result);
        }
        finally { Marshal.FinalReleaseComObject(notification); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            current?.Cancel();
            current = null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint name);

    [ComImport, Guid("215913CC-57EB-4FAB-AB5A-E5FA7BEA2A6C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUserNotification2
    {
        [PreserveSig] int SetBalloonInfo([MarshalAs(UnmanagedType.LPWStr)] string title,
            [MarshalAs(UnmanagedType.LPWStr)] string text, uint flags);
        [PreserveSig] int SetBalloonRetry(uint showTime, uint interval, uint retryCount);
        [PreserveSig] int SetIconInfo(nint icon, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
        [PreserveSig] int Show(IRecordingNotificationContinue query, uint pollInterval, nint callback);
        [PreserveSig] int PlaySound([MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class ContinueNotification(CancellationToken token) : IRecordingNotificationContinue
    {
        public int QueryContinue() => token.IsCancellationRequested ? 1 : 0;
    }
}
