using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

[assembly: AvaloniaTestApplication(typeof(MeetTape.UiTests.TestApp))]

namespace MeetTape.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WindowTests
{
    [AvaloniaFact]
    public void FakeCaptureUpdatesCompiledBindings()
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
        var button = window.FindControl<Button>("RecordButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Recording (fake)", window.FindControl<TextBlock>("CaptureStatus")!.Text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Ready", window.FindControl<TextBlock>("CaptureStatus")!.Text);
        window.Close();
    }
}
