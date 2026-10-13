using Avalonia.Headless.XUnit;
using System.Reflection;

namespace MeetTape.UiTests;

public sealed class TaskbarTests
{
    [AvaloniaFact]
    public void CaptureTransitionsReachTheTaskbarEvenWhenWindowIconsAlreadyMatch()
    {
        var app = (App)Avalonia.Application.Current!;
        var calls = new List<bool>();
        var field = typeof(App).GetField("taskbar", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? original = field.GetValue(app);
        var model = (MainViewModel)typeof(App).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        var changed = typeof(App).GetMethod("ModelChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var indicator = new RecordingTaskbar(calls.Add);
        try
        {
            field.SetValue(app, indicator);
            foreach (bool active in new[] { false, true, true, false, true, false })
            {
                model.RefreshCapture(new(active, TimeSpan.Zero, 0, 0, active ? null : "Fixture interruption"));
                changed.Invoke(app, [model, new System.ComponentModel.PropertyChangedEventArgs(null)]);
            }
            Assert.Equal(new[] { false, true, false, true, false }, calls);
        }
        finally { field.SetValue(app, original); model.RefreshCapture(new(false, TimeSpan.Zero, 0, 0, null)); }
    }

    [Fact]
    public void RecreatedTaskbarReappliesCaptureStateAndFailuresStayOutsideCapture()
    {
        var calls = new List<bool>();
        bool fail = true;
        using var indicator = new RecordingTaskbar(active =>
        {
            calls.Add(active);
            if (fail) throw new System.Runtime.InteropServices.COMException("Fixture shell unavailable");
        });
        indicator.Update(true); indicator.Update(true);
        Assert.False(indicator.Applied);
        fail = false; indicator.Restore(); indicator.Update(false); indicator.Restore();
        Assert.True(indicator.Applied);
        Assert.Equal(new[] { true, true, false, false }, calls);
    }

    [Fact]
    public void ExitClearsAnActiveOverlayAndRejectsLateCaptureUpdates()
    {
        var calls = new List<bool>();
        var indicator = new RecordingTaskbar(calls.Add);
        indicator.Update(true); indicator.Dispose(); indicator.Dispose(); indicator.Update(true); indicator.Restore();
        Assert.Equal(new[] { true, false }, calls);
    }
}
