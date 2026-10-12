using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MeetTape;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closed += (_, _) => (DataContext as SettingsViewModel)?.ClearSecrets();
        Closing += (_, e) => { if (DataContext is SettingsViewModel { CanSave: false, Saved: false }) e.Cancel = true; };
    }
    private async void SaveClick(object? sender, RoutedEventArgs e) => await ((SettingsViewModel)DataContext!).SaveAsync();
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
