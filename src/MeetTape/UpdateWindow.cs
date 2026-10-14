using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OriApps.MeetTape;

public sealed class UpdateWindow : Window
{
    public UpdateWindow(string currentVersion, string availableVersion)
    {
        Title = "MeetTape — Update";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Name = "DeclineUpdate", Content = "Not now" };
        var accept = new Button { Name = "ConfirmUpdate", Content = "Download and restart" };
        cancel.Click += (_, _) => Close(false);
        accept.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"Current version: {currentVersion}\nAvailable version: {availableVersion}" },
                new TextBlock { Text = "Download this update and restart MeetTape? Saved recordings, settings and processing checkpoints will be retained.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
            }
        };
    }
}
