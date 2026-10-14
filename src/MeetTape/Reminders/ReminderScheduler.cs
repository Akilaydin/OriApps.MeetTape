using OriApps.MeetTape.Settings;
using OriApps.MeetTape.Diagnostics;
using System.Text.Json;

namespace OriApps.MeetTape.Reminders;

internal sealed record ReminderAlert(Guid Id, DateOnly Occurrence, string MeetingName, bool Snoozed);

internal sealed record ReminderProgress
{
    public DateOnly? LastAlertDate { get; init; }
    public TimeOnly? LastAlertTime { get; init; }
    public DateTimeOffset? SnoozeAt { get; init; }
    public DateTimeOffset? LastDeliveredAt { get; init; }
    public bool Consumed { get; init; }
}

// Only transient delivery state lives here. The editable reminder definitions live in config.json.
// The delivery marker is durable before calling the native notification transport.
internal sealed class ReminderScheduler
{
    private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string path;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeZoneInfo timeZone;
    private readonly Dictionary<Guid, ReminderProgress> progress;
    private readonly Dictionary<Guid, DateTimeOffset> nextDeliveryAttempt = [];
    private readonly HashSet<(Guid Id, DateOnly Occurrence)> activeStarts = [];
    private ReminderSettings settings;

    internal Func<ReminderAlert, bool>? Alert { get; set; }

    internal ReminderScheduler(string path, ReminderSettings settings,
        Func<DateTimeOffset>? clock = null, TimeZoneInfo? timeZone = null)
    {
        this.path = path;
        this.settings = settings;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.timeZone = timeZone ?? TimeZoneInfo.Local;
        try
        {
            progress = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<Guid, ReminderProgress>>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Reminder delivery state is invalid.")
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppDiagnostics.Error("reminders", "Load delivery progress", error);
            progress = [];
        }
    }

    internal void Configure(ReminderSettings updated)
    {
        foreach (var previous in settings.Items)
        {
            var next = updated.Items.FirstOrDefault(item => item.Id == previous.Id);
            if (next is not null && next.Enabled && previous.Time == next.Time
                && previous.Date == next.Date && previous.RepeatDays == next.RepeatDays) continue;
            nextDeliveryAttempt.Remove(previous.Id);
            if (progress.TryGetValue(previous.Id, out var state) && state.SnoozeAt is not null)
                SaveProgress(previous.Id, state with { SnoozeAt = null, Consumed = true });
        }
        settings = updated;
    }

    internal void Tick(bool recording)
    {
        var now = clock();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).Date);
        foreach (var reminder in settings.Items.Where(item => item.Enabled))
        {
            progress.TryGetValue(reminder.Id, out var state);
            state ??= new ReminderProgress();
            if (state.SnoozeAt is { } snoozed && snoozed <= now)
            {
                bool expired = now - snoozed >= TimeSpan.FromMinutes(2);
                if (recording || expired)
                {
                    state = state with { SnoozeAt = null, Consumed = true };
                    SaveProgress(reminder.Id, state);
                }
                else if (state.LastAlertDate is { } occurrence &&
                    DeliverWithAck(reminder.Id, state,
                        state with { SnoozeAt = null, Consumed = false, LastDeliveredAt = now },
                        new(reminder.Id, occurrence, reminder.MeetingName, true), now))
                {
                    state = state with { SnoozeAt = null, Consumed = false, LastDeliveredAt = now };
                }
            }

            foreach (var date in new[] { today, today.AddDays(1) })
            {
                if ((state.LastAlertDate == date && state.LastAlertTime == reminder.Time) || !OccursOn(reminder, date)) continue;
                var meeting = AtLocal(date, reminder.Time);
                var due = meeting.AddMinutes(-1);
                if (now < due || now >= meeting) continue;
                var delivered = state with { LastAlertDate = date, LastAlertTime = reminder.Time,
                    SnoozeAt = null, LastDeliveredAt = now, Consumed = recording };
                if (recording || DeliverWithAck(reminder.Id, state, delivered,
                    new(reminder.Id, date, reminder.MeetingName, false), now))
                {
                    if (recording) SaveProgress(reminder.Id, delivered);
                }
                break;
            }
        }
    }

    internal bool Snooze(Guid id, DateOnly occurrence)
    {
        if (!settings.Items.Any(item => item.Id == id && item.Enabled)
            || !progress.TryGetValue(id, out var state)
            || state.LastAlertDate != occurrence || state.SnoozeAt is not null
            || state.Consumed || !IsRecent(state)) return false;
        SaveProgress(id, state with { SnoozeAt = clock().AddMinutes(5), Consumed = true });
        return true;
    }

    internal bool TryStart(Guid id, DateOnly occurrence)
    {
        var key = (id, occurrence);
        return !activeStarts.Contains(key) && CanStart(id, occurrence) && activeStarts.Add(key);
    }

    internal void CompleteStart(Guid id, DateOnly occurrence, bool started)
    {
        if (!activeStarts.Remove((id, occurrence)) || !started || !CanStart(id, occurrence)) return;
        SaveProgress(id, progress[id] with { Consumed = true, SnoozeAt = null });
    }

    private bool CanStart(Guid id, DateOnly occurrence) =>
        settings.Items.Any(item => item.Id == id && item.Enabled)
        && progress.TryGetValue(id, out var state)
        && state.LastAlertDate == occurrence && !state.Consumed && IsRecent(state);

    private bool DeliverWithAck(Guid id, ReminderProgress previous, ReminderProgress candidate,
        ReminderAlert alert, DateTimeOffset now)
    {
        if (nextDeliveryAttempt.TryGetValue(alert.Id, out var retryAfter) && now < retryAfter) return false;
        nextDeliveryAttempt[alert.Id] = now.Add(DeliveryRetryDelay);
        SaveProgress(id, candidate);
        bool accepted = false;
        try
        {
            accepted = Alert?.Invoke(alert) == true;
        }
        catch (Exception error)
        {
            AppDiagnostics.Error("reminders", "Show notification", error);
        }
        if (accepted) nextDeliveryAttempt.Remove(alert.Id);
        else SaveProgress(id, previous);
        return accepted;
    }

    internal void Dismiss(Guid id, DateOnly occurrence)
    {
        if (progress.TryGetValue(id, out var state) && state.LastAlertDate == occurrence)
            SaveProgress(id, state with { SnoozeAt = null, Consumed = true });
    }

    internal string NextOccurrenceText(RecordingReminder reminder)
    {
        var now = clock();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).Date);
        progress.TryGetValue(reminder.Id, out var state);
        if (!reminder.Enabled) return "Disabled";
        if (state?.SnoozeAt is { } snooze) return "Snoozed until " + TimeZoneInfo.ConvertTime(snooze, timeZone).ToString("g");
        for (int n = 0; n <= 7; n++)
        {
            var date = today.AddDays(n);
            if (!OccursOn(reminder, date) || (state?.LastAlertDate == date && state.LastAlertTime == reminder.Time)) continue;
            var meeting = AtLocal(date, reminder.Time);
            if (meeting > now) return meeting.AddMinutes(-1).ToString("ddd, MMM d, HH:mm");
        }
        return "Completed";
    }

    private void SaveProgress(Guid id, ReminderProgress state)
    {
        var updated = new Dictionary<Guid, ReminderProgress>(progress) { [id] = state };
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, updated, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            progress[id] = state;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    private static bool OccursOn(RecordingReminder reminder, DateOnly date) =>
        reminder.RepeatDays == 0 ? reminder.Date == date : reminder.RepeatsOn(date.DayOfWeek);

    private bool IsRecent(ReminderProgress state) =>
        state.LastDeliveredAt is { } delivered && clock() - delivered <= TimeSpan.FromHours(2);
}
