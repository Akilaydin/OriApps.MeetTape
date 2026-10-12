using System.ComponentModel;

namespace MeetTape;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private bool active;
    public string CaptureText => active ? "Recording (fake)" : "Ready";
    public string RecordLabel => active ? "Stop" : "Start";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Toggle()
    {
        active = !active;
        PropertyChanged?.Invoke(this, new(nameof(CaptureText)));
        PropertyChanged?.Invoke(this, new(nameof(RecordLabel)));
    }
}
