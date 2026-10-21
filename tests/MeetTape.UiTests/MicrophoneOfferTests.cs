using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;

namespace MeetTape.UiTests;

public sealed class MicrophoneOfferTests
{
    [AvaloniaFact]
    public async Task ClickStartsOnceThroughStartOnlyAndCannotStopOngoingRecording()
    {
        var tracker = new MicrophoneOfferTracker(ownPid: 1);
        tracker.Observe([], true);
        tracker.Observe([new(2, "Teams")], true);
        var offer = Assert.IsType<MicrophoneOffer>(tracker.Observe([new(2, "Teams")], true));
        var controller = new Controller();
        var model = new MainViewModel(controller);
        await model.RefreshAsync();
        using var transport = new FakeToastTransport();
        var results = new List<bool>();
        using var notifications = new WindowsMicrophoneNotifications(
            token =>
            {
                results.Add(App.StartFromMicrophoneOfferAsync(token, tracker, model).GetAwaiter().GetResult());
            }, () => throw new Xunit.Sdk.XunitException("Unexpected native toast failure"), transport);
        Assert.True(notifications.Show(offer));
        Assert.Single(transport.Shown);

        transport.Activate(Arguments(offer.Token));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([true], results);
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(0, controller.StopCount);
        Assert.True(model.IsRecording);

        var nextTracker = new MicrophoneOfferTracker(1);
        nextTracker.Observe([], true);
        nextTracker.Observe([new(3, "Discord")], true);
        var nextOffer = Assert.IsType<MicrophoneOffer>(nextTracker.Observe([new(3, "Discord")], true));
        Assert.False(await App.StartFromMicrophoneOfferAsync(nextOffer.Token, nextTracker, model));
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(0, controller.StopCount);

        transport.Activate(Arguments(offer.Token));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([true, false], results); // The token was already consumed.
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(0, controller.StopCount);
        model.Detach();
    }

    [AvaloniaFact]
    public async Task ExpiredOfferAndBusyOrNamingStateNeverStartRecording()
    {
        var tracker = new MicrophoneOfferTracker(1);
        tracker.Observe([], true);
        tracker.Observe([new(2, "Chrome")], true);
        var offer = Assert.IsType<MicrophoneOffer>(tracker.Observe([new(2, "Chrome")], true));
        var controller = new Controller();
        var model = new MainViewModel(controller);
        await model.RefreshAsync();

        controller.Snapshot = controller.Snapshot with { AwaitingTitleDirectory = "fixture" };
        await model.RefreshAsync();
        Assert.False(await App.StartFromMicrophoneOfferAsync(offer.Token, tracker, model));
        Assert.Equal(0, controller.StartCount);
        Assert.False(await App.StartFromMicrophoneOfferAsync(offer.Token, tracker, model));

        controller.Snapshot = controller.Snapshot with { AwaitingTitleDirectory = null };
        await model.RefreshAsync();
        tracker.Observe([], true); // App closed the mic, stale notifications are rejected.
        Assert.False(await App.StartFromMicrophoneOfferAsync(offer.Token, tracker, model));
        Assert.Equal(0, controller.StartCount);

        tracker.Observe([new(3, "Teams")], true);
        var busyOffer = Assert.IsType<MicrophoneOffer>(tracker.Observe([new(3, "Teams")], true));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ongoing = model.ExecuteAsync(() => blocked.Task);
        Assert.True(model.IsBusy);
        Assert.False(await App.StartFromMicrophoneOfferAsync(busyOffer.Token, tracker, model));
        blocked.SetResult();
        await ongoing;
        Assert.Equal(0, controller.StartCount);
        model.Detach();
    }

    [AvaloniaFact]
    public void ToastIgnoresUntrustedActionsAndStopsDeliveringAfterDispose()
    {
        var transport = new FakeToastTransport();
        var seen = new List<Guid>();
        var notifications = new WindowsMicrophoneNotifications(seen.Add, () => { }, transport);
        Guid id = Guid.NewGuid();
        Assert.True(WindowsMicrophoneNotifications.TryParseStart(Arguments(id), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(WindowsMicrophoneNotifications.TryParseStart(
            new ToastArguments().Add("source", "reminder").Add("action", "start")
                .Add("token", id.ToString("N")).ToString(), out _));
        Assert.False(WindowsMicrophoneNotifications.TryParseStart(
            new ToastArguments().Add("source", "microphone-activity").Add("action", "dismiss")
                .Add("token", id.ToString("N")).ToString(), out _));
        Assert.False(WindowsMicrophoneNotifications.TryParseStart(
            new ToastArguments().Add("source", "microphone-activity").Add("action", "start")
                .Add("token", "invalid").ToString(), out _));
        transport.Activate(Arguments(id));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([id], seen);
        notifications.Dispose();
        transport.Activate(Arguments(id));
        Assert.Equal([id], seen);
        Assert.False(notifications.Show(new MicrophoneOffer(Guid.NewGuid(), null)));
    }

    private static string Arguments(Guid token) =>
        new ToastArguments().Add("source", "microphone-activity").Add("action", "start")
            .Add("token", token.ToString("N")).ToString();

    private sealed class FakeToastTransport : IMicrophoneToastTransport
    {
        public event Action<string>? Activated;
        public List<MicrophoneOffer> Shown { get; } = [];
        public void Show(MicrophoneOffer offer) => Shown.Add(offer);
        public void Activate(string args) => Activated?.Invoke(args);
        public void Dispose() { }
    }

    private sealed class Controller : IDesktopController
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public DesktopSnapshot Snapshot { get; set; } =
            new(new(false, TimeSpan.Zero, 0, 0, null), [], null, null, false, null);
        public event Action<OperationNotice>? Notice { add { } remove { } }
        public event Action<CaptureStatus>? RecordingChanged { add { } remove { } }
        public Task<DesktopSnapshot> ReadAsync() => Task.FromResult(Snapshot);
        public Task StartAsync()
        {
            StartCount++;
            Snapshot = Snapshot with { Capture = new(true, TimeSpan.Zero, 0, 0, null) };
            return Task.CompletedTask;
        }
        public Task<string?> StopAsync()
        {
            StopCount++;
            Snapshot = Snapshot with { Capture = new(false, TimeSpan.Zero, 0, 0, null) };
            return Task.FromResult<string?>(null);
        }
        public Task PrepareAsync(string directory, string? title) => throw new NotSupportedException();
        public Task ChooseTrimAsync(TrimDecision decision, double? start = null, double? end = null) => throw new NotSupportedException();
        public Task RecoverAsync(string directory, Guid id) => throw new NotSupportedException();
        public Task ResumeAsync(string directory) => throw new NotSupportedException();
        public Task CancelProcessingAsync(string directory) => throw new NotSupportedException();
        public Task DeleteRecordingAsync(string directory, Guid id) => throw new NotSupportedException();
        public Task SummaryAsync(string directory) => throw new NotSupportedException();
        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
