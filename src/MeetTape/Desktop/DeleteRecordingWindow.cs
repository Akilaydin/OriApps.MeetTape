using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OriApps.MeetTape.Desktop;

public sealed class DeleteRecordingWindow : Window
{
    public DeleteRecordingWindow(SessionRow recording)
    {
        Title = "MeetTape — Delete recording";
        Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Name = "CancelDeleteRecording", Content = "Cancel" };
        var confirm = new Button { Name = "ConfirmDeleteRecording", Content = "Move to Recycle Bin" };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Name = "DeleteRecordingIdentity", Text = $"{recording.Title}\n{recording.Date}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Move this recording's entire folder to Windows Recycle Bin? This includes both original audio tracks, session.json, prepared audio, chunks, transcript, Summary and all other files in this recording. You can restore it from Recycle Bin.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, confirm } }
            }
        };
    }
}
