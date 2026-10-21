using OriApps.MeetTape.Audio;

namespace MeetTape.Tests;

public sealed class MicrophoneActivityTests
{
    private readonly Clock clock = new();
    private readonly MicrophoneOfferTracker tracker;

    public MicrophoneActivityTests() => tracker = new(ownPid: 100, clock);

    [Fact]
    public void InitialActivityAndOwnCaptureNeverTriggerAnOffer()
    {
        Assert.Null(tracker.Observe([new(100, "MeetTape"), new(200, "Teams")], true));
        Assert.Null(tracker.Observe([new(100, "MeetTape"), new(200, "Teams")], true));
        Assert.Null(tracker.Observe([new(100, "MeetTape")], true));
        Assert.Null(tracker.Observe([new(100, "MeetTape"), new(200, "Teams")], true));
        Assert.NotNull(tracker.Observe([new(100, "MeetTape"), new(200, "Teams")], true));
    }

    [Fact]
    public void NewlyActiveMicrophoneMustPersistAcrossTwoSuccessfulScans()
    {
        Assert.Null(tracker.Observe([], true));
        Assert.Null(tracker.Observe([new(201, "Chrome")], true));
        Assert.Null(tracker.Observe([], true));
        Assert.Null(tracker.Observe([new(201, "Chrome")], true));
        var offer = tracker.Observe([new(201, "Chrome")], true);
        Assert.NotNull(offer);
        Assert.Equal("Chrome", offer.Application);
        Assert.False(tracker.TryConsume(Guid.NewGuid()));
        Assert.True(tracker.TryConsume(offer.Token));
        Assert.False(tracker.TryConsume(offer.Token));
        Assert.Null(tracker.Observe([new(201, "Chrome")], true));
    }

    [Fact]
    public void MultipleStreamsAndPidsOfOneApplicationCauseOneOffer()
    {
        tracker.Observe([], true);
        tracker.Observe([new(200, "Teams"), new(201, "teams"), new(200, "Teams")], true);
        var offer = tracker.Observe([new(200, "Teams"), new(201, "teams")], true);
        Assert.NotNull(offer);
        Assert.Null(tracker.Observe([new(200, "Teams"), new(201, "teams"), new(203, "Teams")], true));
        Assert.Null(tracker.Observe([new(203, "Teams")], true));
        Assert.Null(tracker.Observe([], true));
        Assert.False(tracker.TryConsume(offer.Token));
    }

    [Fact]
    public void DisabledOrRecordingSuppressesTheSameContinuousActivity()
    {
        tracker.Observe([], true);
        tracker.Observe([new(200, "Zoom")], canOffer: false);
        Assert.Null(tracker.Observe([new(200, "Zoom")], canOffer: false));
        Assert.Null(tracker.Observe([new(200, "Zoom")], canOffer: true));
    }

    [Fact]
    public void NoRepeatedOffersAfterDismissOrManualStopUntilActivityReallyEnds()
    {
        tracker.Observe([], true);
        tracker.Observe([new(200, "Discord")], true);
        var offer = Assert.IsType<MicrophoneOffer>(tracker.Observe([new(200, "Discord")], true));
        tracker.CancelPending(); // User dismissed, or recording began via another control.
        Assert.False(tracker.TryConsume(offer.Token));
        Assert.Null(tracker.Observe([new(200, "Discord")], true));
        tracker.Observe([], true);
        clock.Advance(TimeSpan.FromMinutes(4));
        tracker.Observe([new(200, "Discord")], true);
        Assert.NotNull(tracker.Observe([new(200, "Discord")], true));
    }

    [Fact]
    public void NewActivityWithinCooldownIsSuppressedButCanReofferLater()
    {
        tracker.Observe([], true);
        tracker.Observe([new(201, "Chrome")], true);
        Assert.NotNull(tracker.Observe([new(201, "Chrome")], true));
        tracker.Observe([], true);
        tracker.Observe([new(201, "Chrome")], true);
        Assert.Null(tracker.Observe([new(201, "Chrome")], true));
        tracker.Observe([], true);
        clock.Advance(TimeSpan.FromMinutes(4));
        tracker.Observe([new(201, "Chrome")], true);
        Assert.NotNull(tracker.Observe([new(201, "Chrome")], true));
    }

    [Fact]
    public void UnidentifiableApplicationFallsBackToGenericOffer()
    {
        tracker.Observe([], true);
        tracker.Observe([new(200, null)], true);
        var offer = Assert.IsType<MicrophoneOffer>(tracker.Observe([new(200, null)], true));
        Assert.Null(offer.Application);
    }

    [Fact]
    public async Task ScannerErrorDoesNotClearTheBaselineOrCreateSpuriousReentry()
    {
        var scanner = new ScriptedScanner();
        var poller = new MicrophoneActivityPoller(scanner, tracker);
        scanner.Next = [new(200, "Chrome")];
        Assert.Null(await poller.PollAsync(() => true)); // Startup baseline.
        scanner.Fails = true;
        Assert.Null(await poller.PollAsync(() => true));
        scanner.Fails = false;
        Assert.Null(await poller.PollAsync(() => true)); // Still the original activity.
        scanner.Next = [];
        Assert.Null(await poller.PollAsync(() => true));
        scanner.Next = [new(201, "Teams")];
        Assert.Null(await poller.PollAsync(() => true));
        Assert.NotNull(await poller.PollAsync(() => true));
    }

    private sealed class ScriptedScanner : IMicrophoneSessionScanner
    {
        public IReadOnlyList<MicrophoneSession> Next { get; set; } = [];
        public bool Fails { get; set; }
        public IReadOnlyList<MicrophoneSession> Scan() =>
            Fails ? throw new InvalidOperationException("Synthetic COM failure") : Next;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
