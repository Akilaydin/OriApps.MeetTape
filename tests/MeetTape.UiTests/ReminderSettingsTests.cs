using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace MeetTape.UiTests;

public sealed class ReminderSettingsTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../dist/reminder-ui-tests/" + Guid.NewGuid().ToString("N")));
    private string ConfigPath => Path.Combine(root, "config.json");

    public ReminderSettingsTests() => ConfigurationBootstrap.Initialize(ConfigPath, Path.Combine(root, "Meetings"));

    [AvaloniaFact]
    public async Task RemindersTabStagesWeeklyAndOneTimeEditsUntilSettingsSave()
    {
        var model = new SettingsViewModel(ConfigPath, []);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        Assert.NotNull(window.FindControl<ListBox>("RemindersList"));
        Assert.NotNull(window.FindControl<DatePicker>("ReminderDate"));
        Assert.NotNull(window.FindControl<TimePicker>("ReminderTime"));

        model.ReminderEditor.Add();
        model.ReminderEditor.MeetingName = "Engineering sync";
        model.ReminderEditor.Recurrence = 1;
        model.ReminderEditor.Time = new TimeSpan(14, 15, 0);
        model.ReminderEditor.Monday = model.ReminderEditor.Tuesday = model.ReminderEditor.Wednesday =
            model.ReminderEditor.Thursday = model.ReminderEditor.Friday =
                model.ReminderEditor.Saturday = model.ReminderEditor.Sunday = true;
        Assert.True(model.ReminderEditor.Commit());
        Assert.Single(model.ReminderEditor.Items);
        Assert.True(model.HasUnsavedChanges);
        Assert.Empty(ConfigurationLoader.LoadForCapture(ConfigPath).Settings.Reminders.Items);

        await model.SaveAsync();
        Assert.True(model.Saved, model.Message);
        Assert.False(model.HasUnsavedChanges);
        var saved = ConfigurationLoader.LoadForCapture(ConfigPath).Settings.Reminders;
        Assert.False(saved.RunAtWindowsStartup);
        var reminder = Assert.Single(saved.Items);
        Assert.Equal("Engineering sync", reminder.MeetingName);
        Assert.Equal(127, reminder.RepeatDays);
        Assert.Equal(new TimeOnly(14, 15), reminder.Time);
        Assert.Null(reminder.Date);

        model.ReminderEditor.Selected = model.ReminderEditor.Items[0];
        model.ReminderEditor.Toggle();
        Assert.False(model.ReminderEditor.Items[0].Reminder.Enabled);
        model.ReminderEditor.Delete();
        model.ReminderEditor.Add();
        model.ReminderEditor.Date = new DateTimeOffset(2027, 1, 20, 0, 0, 0, TimeSpan.Zero);
        model.ReminderEditor.Time = new TimeSpan(10, 0, 0);
        Assert.True(model.ReminderEditor.Commit());
        await model.SaveAsync();
        Assert.True(model.Saved, model.Message);
        var once = Assert.Single(ConfigurationLoader.LoadForCapture(ConfigPath).Settings.Reminders.Items);
        Assert.Equal(new DateOnly(2027, 1, 20), once.Date);
        Assert.Equal(0, once.RepeatDays);
        window.Close();
    }

    [AvaloniaFact]
    public void ReminderDraftAndStartupCheckboxDoNotTouchDiskBeforeSave()
    {
        string original = File.ReadAllText(ConfigPath);
        var model = new SettingsViewModel(ConfigPath, []);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.ReminderEditor.Add();
        model.ReminderEditor.Recurrence = 1;
        Assert.False(model.ReminderEditor.Commit());
        Assert.Equal("Select at least one day.", model.ReminderEditor.Error);
        model.ReminderEditor.Cancel();
        model.ReminderEditor.RunAtWindowsStartup = true;
        Assert.True(model.HasUnsavedChanges);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        model.ReminderEditor.RunAtWindowsStartup = false;
        Assert.False(model.HasUnsavedChanges);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupRegistrationFailureReportsPartialSaveWithoutLosingReminders()
    {
        var model = new SettingsViewModel(ConfigPath, [], applySettings: _ =>
            throw new InvalidOperationException("Settings saved, but Windows startup registration failed. Reopen Settings and retry."));
        model.ReminderEditor.RunAtWindowsStartup = true;
        model.ReminderEditor.Add();
        model.ReminderEditor.MeetingName = "Reminder retained";
        model.ReminderEditor.Date = new DateTimeOffset(2027, 4, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.True(model.ReminderEditor.Commit());

        await model.SaveAsync();

        Assert.True(model.Saved);
        Assert.Contains("startup registration failed", model.Message);
        Assert.Single(ConfigurationLoader.LoadForCapture(ConfigPath).Settings.Reminders.Items);
        // The actual Windows registration remains test-controlled, never touched by this fixture.
        var reopened = new SettingsViewModel(ConfigPath, [], actualRunAtStartup: false);
        Assert.False(reopened.ReminderEditor.RunAtWindowsStartup);
        Assert.Single(reopened.ReminderEditor.Items);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
