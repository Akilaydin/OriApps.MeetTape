using OriApps.MeetTape.Reminders;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Globalization;

namespace OriApps.MeetTape.Platform.Windows;

// The native transport is injectable: tests can exercise actions without showing a Windows notification.
internal interface IReminderToastTransport : IDisposable
{
    event Action<string>? Activated;
    void Show(ReminderAlert alert);
}

internal sealed class WindowsReminderNotifications : IDisposable
{
    private readonly Action<Guid, DateOnly, string> onAction;
    private readonly Action unavailable;
    private readonly IReminderToastTransport transport;
    private bool disposed;

    internal WindowsReminderNotifications(Action<Guid, DateOnly, string> onAction, Action unavailable,
        IReminderToastTransport? transport = null)
    {
        this.onAction = onAction;
        this.unavailable = unavailable;
        this.transport = transport ?? new NativeReminderToastTransport();
        this.transport.Activated += OnActivated;
    }

    // True means Windows accepted the toast submission, not that the user saw it.
    internal bool Show(ReminderAlert alert)
    {
        if (disposed) return false;
        try { transport.Show(alert); return true; }
        catch (Exception) { unavailable(); return false; }
    }

    private void OnActivated(string arguments)
    {
        if (disposed || !TryParseActivation(arguments, out var id, out var occurrence, out var action)) return;
        void Deliver()
        {
            if (!disposed) onAction(id, occurrence, action);
        }

        if (Dispatcher.UIThread.CheckAccess()) Deliver();
        else Dispatcher.UIThread.Post(Deliver);
    }

    internal static bool TryParseActivation(string arguments, out Guid id, out DateOnly occurrence, out string action)
    {
        id = default;
        occurrence = default;
        action = "";
        try
        {
            var parsed = ToastArguments.Parse(arguments);
            if (!parsed.TryGetValue("id", out string? idText) || !Guid.TryParseExact(idText, "N", out id)
                || !parsed.TryGetValue("occurrence", out string? dateText)
                || !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out occurrence)
                || !parsed.TryGetValue("action", out string? actionText)
                || actionText is not ("start" or "snooze" or "dismiss")) return false;
            action = actionText;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        transport.Activated -= OnActivated;
        transport.Dispose();
    }
}

internal sealed class NativeReminderToastTransport : IReminderToastTransport
{
    internal const string ToastActivationArgument = "-ToastActivated";
    internal static bool WasLaunchedByToast => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    private readonly OnActivated nativeActivated;

    internal NativeReminderToastTransport()
    {
        nativeActivated = e => Activated?.Invoke(e.Argument);
        ToastNotificationManagerCompat.OnActivated += nativeActivated;
    }

    public event Action<string>? Activated;

    public void Show(ReminderAlert alert)
    {
        string id = alert.Id.ToString("N");
        string occurrence = alert.Occurrence.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string caption = string.IsNullOrWhiteSpace(alert.MeetingName) ? "Meeting reminder" : alert.MeetingName;
        string description = alert.Snoozed ? "Meeting recording reminder" : "Meeting starts in 1 minute";

        new ToastContentBuilder()
            .AddText(caption)
            .AddText(description)
            .AddArgument("id", id)
            .AddArgument("occurrence", occurrence)
            .AddArgument("action", "dismiss")
            .AddButton(new ToastButton().SetContent("Start recording")
                .AddArgument("id", id).AddArgument("occurrence", occurrence).AddArgument("action", "start"))
            .AddButton(new ToastButton().SetContent("Remind in 5 min")
                .AddArgument("id", id).AddArgument("occurrence", occurrence).AddArgument("action", "snooze"))
            .SetToastScenario(ToastScenario.Reminder)
            .Show();
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= nativeActivated;
}
