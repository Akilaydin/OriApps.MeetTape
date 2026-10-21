using OriApps.MeetTape.Audio;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;

namespace OriApps.MeetTape.Platform.Windows;

internal interface IMicrophoneToastTransport : IDisposable
{
    event Action<string>? Activated;
    void Show(MicrophoneOffer offer);
}

internal sealed class WindowsMicrophoneNotifications : IDisposable
{
    private readonly Action<Guid> onStart;
    private readonly Action unavailable;
    private readonly IMicrophoneToastTransport transport;
    private bool disposed;

    internal WindowsMicrophoneNotifications(Action<Guid> onStart, Action unavailable,
        IMicrophoneToastTransport? transport = null)
    {
        this.onStart = onStart;
        this.unavailable = unavailable;
        this.transport = transport ?? new NativeMicrophoneToastTransport();
        this.transport.Activated += OnActivated;
    }

    // A successful Show only means Windows accepted the notification.
    internal bool Show(MicrophoneOffer offer)
    {
        if (disposed) return false;
        try { transport.Show(offer); return true; }
        catch (Exception) { unavailable(); return false; }
    }

    private void OnActivated(string arguments)
    {
        if (disposed || !TryParseStart(arguments, out Guid token)) return;
        void Deliver() { if (!disposed) onStart(token); }
        if (Dispatcher.UIThread.CheckAccess()) Deliver();
        else Dispatcher.UIThread.Post(Deliver);
    }

    internal static bool TryParseStart(string arguments, out Guid token)
    {
        token = Guid.Empty;
        try
        {
            var parsed = ToastArguments.Parse(arguments);
            return parsed.TryGetValue("source", out string? source) && source == "microphone-activity"
                && parsed.TryGetValue("action", out string? action) && action == "start"
                && parsed.TryGetValue("token", out string? id) && Guid.TryParseExact(id, "N", out token)
                && token != Guid.Empty;
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

internal sealed class NativeMicrophoneToastTransport : IMicrophoneToastTransport
{
    private readonly OnActivated nativeActivated;

    internal NativeMicrophoneToastTransport()
    {
        nativeActivated = e => Activated?.Invoke(e.Argument);
        ToastNotificationManagerCompat.OnActivated += nativeActivated;
    }

    public event Action<string>? Activated;

    public void Show(MicrophoneOffer offer)
    {
        string message = offer.Application is null
            ? "Another app is using a microphone."
            : $"{offer.Application} is using a microphone.";

        new ToastContentBuilder()
            .AddText("Start recording?")
            .AddText(message)
            .AddArgument("source", "microphone-activity")
            .AddArgument("action", "dismiss")
            .AddButton(new ToastButton().SetContent("Start recording")
                .AddArgument("source", "microphone-activity")
                .AddArgument("action", "start")
                .AddArgument("token", offer.Token.ToString("N")))
            .Show();
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= nativeActivated;
}
