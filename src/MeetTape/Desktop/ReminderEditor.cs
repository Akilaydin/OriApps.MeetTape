using OriApps.MeetTape.Reminders;
using OriApps.MeetTape.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace OriApps.MeetTape.Desktop;

public sealed class ReminderRow(RecordingReminder reminder) : INotifyPropertyChanged
{
    public RecordingReminder Reminder { get; set; } = reminder;
    public string Title => string.IsNullOrWhiteSpace(Reminder.MeetingName) ? "Meeting" : Reminder.MeetingName;
    public string RepeatText => Reminder.RepeatDays == 0 ? "Doesn't repeat" :
        string.Join(", ", Enumerable.Range(0, 7).Where(day => (Reminder.RepeatDays & (1 << day)) != 0)
            .Select(day => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(day + 1) % 7]));
    public string EnabledText => Reminder.Enabled ? "Disable" : "Enable";
    public string When => Reminder.RepeatDays == 0
        ? $"{Reminder.Date:ddd, MMM d} · {Reminder.Time:HH:mm}"
        : $"{Reminder.Time:HH:mm} · {RepeatText}";
    private string next = "";
    public string Next { get => next; set { next = value; PropertyChanged?.Invoke(this, new(null)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Editing is staged in Settings; the disk and scheduler change only on Save settings.
public sealed class ReminderEditor : INotifyPropertyChanged
{
    private readonly Func<RecordingReminder, string> nextOccurrence;
    private Guid? editing;
    private bool runAtWindowsStartup;
    private int recurrence;
    public ObservableCollection<ReminderRow> Items { get; } = [];
    public ReminderRow? Selected { get; set; }
    public bool RunAtWindowsStartup { get => runAtWindowsStartup; set { runAtWindowsStartup = value; Changed(); } }
    public string MeetingName { get; set; } = "";
    public DateTimeOffset? Date { get; set; } = DateTimeOffset.Now.Date;
    public TimeSpan? Time { get; set; } = new TimeSpan(9, 0, 0);
    public int Recurrence { get => recurrence; set { recurrence = value; Changed(); } }
    public bool IsOneTime => recurrence == 0;
    public bool IsRepeating => recurrence == 1;
    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
    public bool Sunday { get; set; }
    public bool IsEditing { get; private set; }
    public string Error { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Edited;

    internal ReminderEditor(ReminderSettings settings, Func<RecordingReminder, string>? nextOccurrence = null)
    {
        this.nextOccurrence = nextOccurrence ?? (_ => "Scheduled");
        runAtWindowsStartup = settings.RunAtWindowsStartup;
        foreach (var reminder in settings.Items) Items.Add(Row(reminder));
    }

    public void Add()
    {
        editing = null;
        MeetingName = "";
        Date = DateTimeOffset.Now.Date;
        Time = new TimeSpan(9, 0, 0);
        Recurrence = 0;
        Monday = Tuesday = Wednesday = Thursday = Friday = Saturday = Sunday = false;
        IsEditing = true; Error = ""; Changed();
    }

    public void Edit()
    {
        if (Selected is null) return;
        var reminder = Selected.Reminder;
        editing = reminder.Id;
        MeetingName = reminder.MeetingName;
        Date = reminder.Date is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)) : DateTimeOffset.Now.Date;
        Time = reminder.Time.ToTimeSpan();
        Recurrence = reminder.RepeatDays == 0 ? 0 : 1;
        Monday = reminder.RepeatsOn(DayOfWeek.Monday);
        Tuesday = reminder.RepeatsOn(DayOfWeek.Tuesday);
        Wednesday = reminder.RepeatsOn(DayOfWeek.Wednesday);
        Thursday = reminder.RepeatsOn(DayOfWeek.Thursday);
        Friday = reminder.RepeatsOn(DayOfWeek.Friday);
        Saturday = reminder.RepeatsOn(DayOfWeek.Saturday);
        Sunday = reminder.RepeatsOn(DayOfWeek.Sunday);
        IsEditing = true; Error = ""; Changed();
    }

    public void Cancel() { IsEditing = false; Error = ""; Changed(); }

    public bool Commit()
    {
        if (!IsEditing) return true;
        int days = (Monday ? 1 : 0) | (Tuesday ? 2 : 0) | (Wednesday ? 4 : 0)
            | (Thursday ? 8 : 0) | (Friday ? 16 : 0) | (Saturday ? 32 : 0) | (Sunday ? 64 : 0);
        if (Time is null || (Recurrence == 0 && Date is null) || (Recurrence == 1 && days == 0))
        {
            Error = Recurrence == 1 ? "Select at least one day." : "Select a date and time.";
            Changed(); return false;
        }
        if (MeetingName.Length > 120) { Error = "Meeting name must be 120 characters or fewer."; Changed(); return false; }
        var existing = Items.FirstOrDefault(row => row.Reminder.Id == editing);
        var reminder = new RecordingReminder
        {
            Id = editing ?? Guid.NewGuid(), MeetingName = MeetingName.Trim(), Enabled = existing?.Reminder.Enabled ?? true,
            Date = Recurrence == 0 ? DateOnly.FromDateTime(Date!.Value.Date) : null,
            Time = TimeOnly.FromTimeSpan(Time.Value), RepeatDays = Recurrence == 1 ? days : 0
        };
        if (existing is not null)
        {
            int index = Items.IndexOf(existing);
            Items[index] = Row(reminder);
            Selected = Items[index];
        }
        else Items.Add(Row(reminder));
        IsEditing = false; Error = ""; Changed(); return true;
    }

    public void Toggle()
    {
        if (Selected is null) return;
        int index = Items.IndexOf(Selected);
        Items[index] = Row(Selected.Reminder with { Enabled = !Selected.Reminder.Enabled });
        Selected = Items[index]; Changed();
    }

    public void Delete()
    {
        if (Selected is null) return;
        Items.Remove(Selected); Selected = null; IsEditing = false; Changed();
    }

    internal ReminderSettings Snapshot() => new() { RunAtWindowsStartup = RunAtWindowsStartup,
        Items = Items.Select(row => row.Reminder).ToList() };
    internal string Signature => JsonSerializer.Serialize(Snapshot());
    internal void RefreshNext() { foreach (var row in Items) row.Next = nextOccurrence(row.Reminder); Changed(); }
    private ReminderRow Row(RecordingReminder reminder) => new(reminder) { Next = nextOccurrence(reminder) };
    private void Changed() { PropertyChanged?.Invoke(this, new(null)); Edited?.Invoke(); }
}
