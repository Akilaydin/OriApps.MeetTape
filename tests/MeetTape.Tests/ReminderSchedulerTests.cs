using OriApps.MeetTape.Reminders;

namespace MeetTape.Tests;

public sealed class ReminderSchedulerTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../dist/reminder-scheduler-tests/" + Guid.NewGuid().ToString("N")));

    public ReminderSchedulerTests() => Directory.CreateDirectory(root);

    private string StatePath => Path.Combine(root, "reminders-state.json");

    private ReminderScheduler NewScheduler(ReminderSettings settings, Func<DateTimeOffset> clock) =>
        new(StatePath, settings, clock, TimeZoneInfo.Utc);

    [Fact]
    public void OneTimeReminderArrivesOneMinuteEarlyAndDoesNotRepeatAfterRestart()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 58, 0, TimeSpan.Zero);
        var settings = Settings(OneTime(new DateOnly(2026, 10, 14), new TimeOnly(10, 0)));
        var alerts = new List<ReminderAlert>();
        var scheduler = NewScheduler(settings, () => now);
        scheduler.Alert = Collect(alerts);
        scheduler.Tick(false);
        Assert.Empty(alerts);

        now = now.AddMinutes(1);
        scheduler.Tick(false);
        scheduler.Tick(false);
        Assert.Single(alerts);
        Assert.False(alerts[0].Snoozed);

        var restarted = NewScheduler(settings, () => now);
        restarted.Alert = Collect(alerts);
        restarted.Tick(false);
        Assert.Single(alerts);
        Assert.Contains("Completed", restarted.NextOccurrenceText(settings.Items[0]));
    }

    [Fact]
    public void NextOccurrenceTextShowsNotificationTimeIncludingPreviousDay()
    {
        var now = new DateTimeOffset(2026, 10, 18, 22, 0, 0, TimeSpan.Zero); // Sunday.
        var mondayMidnight = Repeat(new TimeOnly(0, 0), RecordingReminder.DayBit(DayOfWeek.Monday));
        var mondayAfternoon = OneTime(new DateOnly(2026, 10, 19), new TimeOnly(14, 30));
        var scheduler = NewScheduler(Settings(mondayMidnight, mondayAfternoon), () => now);

        Assert.Equal(new DateTimeOffset(2026, 10, 18, 23, 59, 0, TimeSpan.Zero)
            .ToString("ddd, MMM d, HH:mm"), scheduler.NextOccurrenceText(mondayMidnight));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 29, 0, TimeSpan.Zero)
            .ToString("ddd, MMM d, HH:mm"), scheduler.NextOccurrenceText(mondayAfternoon));
    }

    [Fact]
    public void MondayMidnightRepeatNotifiesOnSundayAndAllDaysCanRepeat()
    {
        var now = new DateTimeOffset(2026, 10, 18, 23, 59, 0, TimeSpan.Zero); // Sunday.
        var monday = Repeat(new TimeOnly(0, 0), RecordingReminder.DayBit(DayOfWeek.Monday));
        var daily = Repeat(new TimeOnly(0, 0), 0b1111111);
        var settings = Settings(monday, daily);
        var alerts = new List<ReminderAlert>();
        var scheduler = NewScheduler(settings, () => now);
        scheduler.Alert = Collect(alerts);

        scheduler.Tick(false);
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, item => Assert.Equal(new DateOnly(2026, 10, 19), item.Occurrence));
        now = now.AddDays(1);
        scheduler.Tick(false);
        Assert.Equal(3, alerts.Count); // Daily meets on Tuesday; Monday remains consumed.
        Assert.Equal(daily.Id, alerts[^1].Id);
        Assert.Equal(new DateOnly(2026, 10, 20), alerts[^1].Occurrence);
    }

    [Fact]
    public void SnoozeDeliversOnlyCurrentOccurrenceAfterFiveMinutesThenRecurringNextDay()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 59, 0, TimeSpan.Zero);
        var reminder = Repeat(new TimeOnly(10, 0), 0b1111111);
        var scheduler = NewScheduler(Settings(reminder), () => now);
        var alerts = new List<ReminderAlert>();
        scheduler.Alert = Collect(alerts);
        scheduler.Tick(false);
        Assert.Single(alerts);
        Assert.True(scheduler.Snooze(reminder.Id, alerts[0].Occurrence));

        now = now.AddMinutes(4);
        scheduler.Tick(false);
        Assert.Single(alerts);
        now = now.AddMinutes(1);
        scheduler.Tick(false);
        Assert.Equal(2, alerts.Count);
        Assert.True(alerts[1].Snoozed);
        scheduler.Dismiss(reminder.Id, alerts[1].Occurrence);
        scheduler.Tick(false);
        Assert.Equal(2, alerts.Count);

        now = new DateTimeOffset(2026, 10, 15, 9, 59, 0, TimeSpan.Zero);
        scheduler.Tick(false);
        Assert.Equal(3, alerts.Count);
        Assert.Equal(new DateOnly(2026, 10, 15), alerts[^1].Occurrence);
        Assert.False(alerts[^1].Snoozed);
    }

    [Fact]
    public void ActiveRecordingSuppressesReminderWithoutDeliveringItOnRestart()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 59, 0, TimeSpan.Zero);
        var settings = Settings(OneTime(new DateOnly(2026, 10, 14), new TimeOnly(10, 0)));
        var alerts = new List<ReminderAlert>();
        var scheduler = NewScheduler(settings, () => now);
        scheduler.Alert = Collect(alerts);
        scheduler.Tick(recording: true);
        scheduler.Tick(recording: false);
        Assert.Empty(alerts);
        var restarted = NewScheduler(settings, () => now);
        restarted.Alert = Collect(alerts);
        restarted.Tick(recording: false);
        Assert.Empty(alerts);
    }

    [Fact]
    public void LaunchingAfterMissedOneTimeMeetingDoesNotShowStaleNotification()
    {
        var now = new DateTimeOffset(2026, 10, 14, 10, 15, 0, TimeSpan.Zero);
        var scheduler = NewScheduler(Settings(OneTime(new DateOnly(2026, 10, 14), new TimeOnly(10, 0))), () => now);
        int alerts = 0;
        scheduler.Alert = _ => { alerts++; return true; };
        scheduler.Tick(false);
        Assert.Equal(0, alerts);
    }

    [Fact]
    public void RejectedNotificationRetriesWithinDueMinuteAndAcceptedDeliverySurvivesRestart()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 59, 0, TimeSpan.Zero);
        var reminder = OneTime(new DateOnly(2026, 10, 14), new TimeOnly(10, 0));
        var settings = Settings(reminder);
        int attempts = 0;
        var scheduler = NewScheduler(settings, () => now);
        scheduler.Alert = _ => ++attempts == 3;

        scheduler.Tick(false);
        Assert.Equal(1, attempts);
        Assert.Contains("09:59", scheduler.NextOccurrenceText(reminder));
        now = now.AddSeconds(9);
        scheduler.Tick(false);
        Assert.Equal(1, attempts);
        now = now.AddSeconds(1);
        scheduler.Tick(false);
        Assert.Equal(2, attempts);
        now = now.AddSeconds(10);
        scheduler.Tick(false);
        Assert.Equal(3, attempts);

        int afterRestart = 0;
        var restarted = NewScheduler(settings, () => now);
        restarted.Alert = _ => { afterRestart++; return true; };
        restarted.Tick(false);
        Assert.Equal(0, afterRestart);
        Assert.Equal("Completed", restarted.NextOccurrenceText(reminder));
    }

    [Fact]
    public void NotificationFailureAtEndOfDueWindowDoesNotNotifyLate()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 59, 55, TimeSpan.Zero);
        var reminder = OneTime(new DateOnly(2026, 10, 14), new TimeOnly(10, 0));
        var scheduler = NewScheduler(Settings(reminder), () => now);
        int attempts = 0;
        scheduler.Alert = _ => { attempts++; return false; };
        scheduler.Tick(false);
        now = now.AddSeconds(10);
        scheduler.Tick(false);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void RejectedSnoozedNotificationRetriesWithoutLosingSnoozeOrReplayingAfterRestart()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 59, 0, TimeSpan.Zero);
        var reminder = Repeat(new TimeOnly(10, 0), 127);
        var settings = Settings(reminder);
        var scheduler = NewScheduler(settings, () => now);
        var alerts = new List<ReminderAlert>();
        scheduler.Alert = Collect(alerts);
        scheduler.Tick(false);
        Assert.True(scheduler.Snooze(reminder.Id, alerts[0].Occurrence));

        int attempts = 0;
        scheduler.Alert = alert =>
        {
            Assert.True(alert.Snoozed);
            return ++attempts == 2;
        };
        now = now.AddMinutes(5);
        scheduler.Tick(false);
        Assert.Equal(1, attempts);
        Assert.Contains("Snoozed until", scheduler.NextOccurrenceText(reminder));
        now = now.AddSeconds(10);
        scheduler.Tick(false);
        Assert.Equal(2, attempts);

        var restarted = NewScheduler(settings, () => now);
        restarted.Alert = _ => { attempts++; return true; };
        restarted.Tick(false);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void FailedStartReleasesReservationAndSuccessfulStartConsumesToken()
    {
        var now = new DateTimeOffset(2026, 10, 14, 9, 59, 0, TimeSpan.Zero);
        var reminder = OneTime(new DateOnly(2026, 10, 14), new TimeOnly(10, 0));
        var settings = Settings(reminder);
        var scheduler = NewScheduler(settings, () => now);
        scheduler.Alert = _ => true;
        scheduler.Tick(false);
        var occurrence = reminder.Date!.Value;

        Assert.True(scheduler.TryStart(reminder.Id, occurrence));
        Assert.False(scheduler.TryStart(reminder.Id, occurrence)); // Another action while Start awaits.
        scheduler.CompleteStart(reminder.Id, occurrence, started: false);
        Assert.True(scheduler.TryStart(reminder.Id, occurrence));
        scheduler.CompleteStart(reminder.Id, occurrence, started: true);
        Assert.False(scheduler.TryStart(reminder.Id, occurrence));
        Assert.False(NewScheduler(settings, () => now).TryStart(reminder.Id, occurrence));
    }

    [Fact]
    public async Task ColdStartReminderActionsDrainOnlyAfterControllerInitialization()
    {
        var id = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 14);
        var pending = new List<(Guid Id, DateOnly Occurrence, string Action)>
        {
            (id, date, "start"), (id, date, "snooze"), (id, date, "dismiss")
        };
        bool initializingController = true, controllerReady = false, schedulerReady = false;
        var handled = new List<string>();
        bool Ready() => !initializingController && controllerReady && schedulerReady;
        Task Handle(Guid reminderId, DateOnly occurrence, string action)
        {
            Assert.True(Ready());
            Assert.Equal(id, reminderId);
            Assert.Equal(date, occurrence);
            handled.Add(action);
            return Task.CompletedTask;
        }

        await App.DrainPendingReminderActionsAsync(pending, Ready, Handle);
        Assert.Equal(3, pending.Count);
        Assert.Empty(handled);

        initializingController = false; // Models App.InitializeAsync finally.
        await App.DrainPendingReminderActionsAsync(pending, Ready, Handle);
        Assert.Equal(3, pending.Count);
        Assert.Empty(handled);

        controllerReady = schedulerReady = true;
        await App.DrainPendingReminderActionsAsync(pending, Ready, Handle);
        Assert.Empty(pending);
        Assert.Equal(new[] { "start", "snooze", "dismiss" }, handled);
        await App.DrainPendingReminderActionsAsync(pending, Ready, Handle);
        Assert.Equal(3, handled.Count); // Each queued action replayed exactly once.
    }

    private static ReminderSettings Settings(params RecordingReminder[] items) => new() { Items = [.. items] };

    private static Func<ReminderAlert, bool> Collect(List<ReminderAlert> items) => alert =>
    {
        items.Add(alert);
        return true;
    };

    private static RecordingReminder OneTime(DateOnly date, TimeOnly time) => new() { Date = date, Time = time };

    private static RecordingReminder Repeat(TimeOnly time, int days) => new() { Time = time, RepeatDays = days };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
