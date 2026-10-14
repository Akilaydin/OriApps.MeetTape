using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.Diagnostics;

namespace MeetTape;

public sealed partial class SettingsWindow : Window
{
    private bool confirmingClose;
    private bool discardConfirmed;
    internal Func<Task<bool>>? ConfirmDiscard { get; set; }
    internal Func<string> LogsDirectory { get; set; } = () => AppDiagnostics.DirectoryPath;
    internal Action<string>? OpenFolder { get; set; }

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closed += (_, _) => (DataContext as SettingsViewModel)?.ClearSecrets();
        Closing += async (_, e) =>
        {
            if (DataContext is not SettingsViewModel settings) return;
            if (!settings.CanSave) { e.Cancel = true; return; }
            if (discardConfirmed || !settings.HasUnsavedChanges) return;
            e.Cancel = true;
            if (confirmingClose) return;
            confirmingClose = true;
            try
            {
                if (await (ConfirmDiscard?.Invoke() ?? AskDiscardAsync()))
                { discardConfirmed = true; Close(); }
            }
            finally { confirmingClose = false; }
        };
    }
    private Task<bool> AskDiscardAsync()
    {
        var keep = new Button { Name = "KeepEditingSettings", Content = "Keep editing" };
        var discard = new Button { Name = "DiscardSettings", Content = "Discard" };
        var dialog = new Window
        {
            Title = "Unsaved settings", Icon = Icon, Width = 400, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "Discard unsaved settings and prompt edits?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { keep, discard } }
                }
            }
        };
        keep.Click += (_, _) => dialog.Close(false);
        discard.Click += (_, _) => dialog.Close(true);
        return dialog.ShowDialog<bool>(this);
    }
    private async void SaveClick(object? sender, RoutedEventArgs e) => await ((SettingsViewModel)DataContext!).SaveAsync();
    private void LoadTranscriptionPromptClick(object? sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext!).LoadTranscriptionPrompt();
    private void LoadSummaryPromptClick(object? sender, RoutedEventArgs e) => ((SettingsViewModel)DataContext!).LoadSummaryPrompt();
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OpenLogsFolderClick(object? sender, RoutedEventArgs e)
    {
        var error = this.FindControl<TextBlock>("LogsFolderError")!;
        error.Text = ""; error.IsVisible = false;
        try
        {
            string directory = LogsDirectory();
            Directory.CreateDirectory(directory);
            if (OpenFolder is { } open) open(directory);
            else using (Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })) { }
        }
        catch (Exception ex)
        {
            error.Text = AppDiagnostics.UserMessage(ex, "Cannot open logs folder. Check folder permissions and Windows Explorer, then retry.");
            error.IsVisible = true;
        }
    }
}
