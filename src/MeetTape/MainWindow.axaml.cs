using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MeetTape;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
    private void RecordClick(object? sender, RoutedEventArgs e) => ((MainViewModel)DataContext!).Toggle();
}
