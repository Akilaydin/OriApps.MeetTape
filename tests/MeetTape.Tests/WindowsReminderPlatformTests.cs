using Microsoft.Toolkit.Uwp.Notifications;

namespace OriApps.MeetTape.Tests;

public sealed class WindowsReminderPlatformTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("snooze")]
    [InlineData("dismiss")]
    public void ToastActionCarriesOccurrenceIdentity(string action)
    {
        var id = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 14);
        string arguments = new ToastArguments().Add("id", id.ToString("N"))
            .Add("occurrence", "2026-10-14").Add("action", action).ToString();

        Assert.True(WindowsReminderNotifications.TryParseActivation(arguments,
            out var actualId, out var actualOccurrence, out var actualAction));
        Assert.Equal(id, actualId);
        Assert.Equal(date, actualOccurrence);
        Assert.Equal(action, actualAction);
    }

    [Theory]
    [InlineData("action=start")]
    [InlineData("id=not-a-guid;occurrence=2026-10-14;action=start")]
    [InlineData("id=12345678123412341234123456781234;occurrence=wrong;action=start")]
    [InlineData("id=12345678123412341234123456781234;occurrence=2026-10-14;action=stop")]
    public void InvalidToastActionsAreIgnored(string arguments)
    {
        Assert.False(WindowsReminderNotifications.TryParseActivation(arguments, out _, out _, out _));
    }

    [Fact]
    public void LoginStartupUsesStableVelopackStubAndCanBeDisabled()
    {
        var store = new FakeStartupStore();
        string current = @"C:\Users\test\AppData\Local\OriApps.MeetTape\current\MeetTape.exe";
        string stub = @"C:\Users\test\AppData\Local\OriApps.MeetTape\MeetTape.exe";
        var startup = new LoginStartupRegistrar(store, () => current, path => path == stub);

        Assert.False(startup.IsEnabled());
        startup.SetEnabled(true);
        Assert.Equal($"\"{stub}\" --tray", store.Command);
        Assert.True(startup.IsEnabled());

        startup.SetEnabled(false);
        Assert.Null(store.Command);
        Assert.False(startup.IsEnabled());
    }

    [Fact]
    public void LoginStartupRejectsUninstalledExecutableWithoutChangingRegistry()
    {
        var store = new FakeStartupStore();
        var startup = new LoginStartupRegistrar(store, () => @"C:\source\MeetTape.exe", _ => true);

        Assert.False(startup.IsEnabled());
        Assert.Throws<InvalidOperationException>(() => startup.SetEnabled(true));
        Assert.Null(store.Command);
    }

    private sealed class FakeStartupStore : ILoginStartupStore
    {
        public string? Command { get; private set; }
        public string? Read() => Command;
        public void Write(string command) => Command = command;
        public void Delete() => Command = null;
    }
}
