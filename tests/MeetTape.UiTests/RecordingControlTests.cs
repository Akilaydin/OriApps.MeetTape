using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace MeetTape.UiTests;

public sealed class RecordingControlTests
{
    [Fact]
    public void CancellationCallbackExposesTheRequiredWindowsComInterface()
    {
        using var cancellation = new CancellationTokenSource();
        var callback = new RecordingNotifications.ContinueNotification(cancellation.Token);
        nint unknown = Marshal.GetIUnknownForObject(callback), pointer = 0;
        try
        {
            var iid = new Guid("7307055C-B24A-486B-9F25-163E597A28A9");
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out pointer));
            Assert.Equal(0, callback.QueryContinue());
            cancellation.Cancel(); Assert.Equal(1, callback.QueryContinue());
        }
        finally { if (pointer != 0) Marshal.Release(pointer); Marshal.Release(unknown); }
    }

    [Fact]
    public void RegistrationFailureHasClearDiagnosticAndSuccessfulRegistrationIsReleasedOnce()
    {
        int releases = 0;
        var error = Assert.Throws<InvalidOperationException>(() => new RecordingHotkey(123,
            (_, _, _, _) => false, (_, _) => { releases++; return true; }));
        Assert.Contains("Ctrl+Shift+F9", error.Message);
        Assert.Contains("tray remain available", error.Message);
        Assert.Equal(0, releases);
        var hotkey = new RecordingHotkey(123, (window, id, modifiers, key) =>
        {
            Assert.Equal(123, window); Assert.Equal(RecordingHotkey.Id, id);
            Assert.Equal(0x4006u, modifiers); Assert.Equal(0x78u, key);
            return true;
        }, (window, id) => { Assert.Equal(123, window); Assert.Equal(RecordingHotkey.Id, id); releases++; return true; });
        Assert.True(hotkey.Matches(0x0312, RecordingHotkey.Id));
        Assert.False(hotkey.Matches(0x0312, RecordingHotkey.Id + 1));
        Assert.False(hotkey.Matches(0x0100, RecordingHotkey.Id));
        hotkey.Dispose(); hotkey.Dispose();
        Assert.Equal(1, releases); Assert.False(hotkey.Matches(0x0312, RecordingHotkey.Id));
    }

    [Fact]
    public async Task NewNotificationAndExitCancelPendingDeliveryWithoutCaptureDependencies()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int deliveries = 0, failures = 0;
        using var notifications = new RecordingNotifications(() => Interlocked.Increment(ref failures), (notice, token) =>
        {
            int delivery = Interlocked.Increment(ref deliveries);
            if (delivery == 1) first.SetResult(); else second.SetResult();
            Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            if (delivery == 2) finished.SetResult();
            token.ThrowIfCancellationRequested();
        });
        notifications.Show(new(NoticeKind.Information, "Recording started"));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        notifications.Show(new(NoticeKind.Information, "Recording stopped"));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        notifications.Dispose();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        notifications.Show(new(NoticeKind.Information, "Recording started"));
        Assert.Equal(2, deliveries); Assert.Equal(0, failures);
    }

    [Fact]
    public async Task UnavailableNotificationReportsSafeDiagnosticAndCanRetry()
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var notifications = new RecordingNotifications(() => reported.SetResult(), (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new COMException("Fixture unavailable");
            delivered.SetResult();
        });
        notifications.Show(new(NoticeKind.Information, "Recording started"));
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        notifications.Show(new(NoticeKind.Information, "Recording stopped"));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
    }

    [WindowsCiFact, Trait("Category", "NativeWindows")]
    public void WindowsRegistrationMessageRoutingReleaseAndNotificationApiSmoke()
    {
        // Only the disposable Windows CI runner. No key simulation, capture or user configuration.
        _ = PeekMessage(out _, 0, 0, 0, 0);
        using (var hotkey = new RecordingHotkey(0))
        {
            Assert.False(RegisterOnOtherThread());
            Assert.True(PostThreadMessage(GetCurrentThreadId(), 0x0312, RecordingHotkey.Id, 0));
            Assert.True(PeekMessage(out var message, 0, 0x0312, 0x0312, 1));
            Assert.True(hotkey.Matches(message.Id, message.WParam));
        }
        Assert.True(RegisterOnOtherThread());
        RecordingNotifications.ShowNative(new(NoticeKind.Information, "MeetTape CI recording status fixture"), TestContext.Current.CancellationToken);
        // Successful API delivery cannot prove visibility when Windows suppresses notifications.
    }

    private static bool RegisterOnOtherThread()
    {
        bool registered = false;
        var thread = new Thread(() =>
        {
            try { using var hotkey = new RecordingHotkey(0); registered = true; }
            catch (InvalidOperationException) { }
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        return registered;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
}

public sealed class WindowsCiFactAttribute : FactAttribute
{
    public WindowsCiFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Skip = "Native registration/notification is opt-in through isolated Windows CI only.";
    }
}
