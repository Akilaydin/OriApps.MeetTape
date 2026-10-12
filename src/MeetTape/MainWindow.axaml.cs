using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using System.Diagnostics;

namespace MeetTape;

public sealed partial class MainWindow : Window
{
    public Func<Task>? OpenSettings { get; set; }
    public Func<Task>? ExitApplication { get; set; }
    public Func<Task>? UpdateApplication { get; set; }
    public bool AllowClose { get; set; }
    private MainViewModel Model => (MainViewModel)DataContext!;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
    }
    private async void RecordClick(object? sender, RoutedEventArgs e) => await Model.ToggleAsync();
    private async void ContinueClick(object? sender, RoutedEventArgs e) => await Model.ContinuePreparationAsync();
    private async void StartNextClick(object? sender, RoutedEventArgs e) => await Model.StartNextRecordingAsync();
    private void PrepareClick(object? sender, RoutedEventArgs e) => Model.PrepareSelected();
    private async void AcceptClick(object? sender, RoutedEventArgs e) => await Model.ChooseAsync(TrimDecision.Accept);
    private async void SkipClick(object? sender, RoutedEventArgs e) => await Model.ChooseAsync(TrimDecision.Skip);
    private async void ManualClick(object? sender, RoutedEventArgs e) => await Model.ChooseAsync(TrimDecision.Manual);
    private async void ResumeClick(object? sender, RoutedEventArgs e) => await Model.ResumeAsync();
    private async void SummaryClick(object? sender, RoutedEventArgs e) => await Model.SummaryAsync();
    private async void SettingsClick(object? sender, RoutedEventArgs e) { if (OpenSettings is not null) await OpenSettings(); }
    private async void ExitClick(object? sender, RoutedEventArgs e) { if (ExitApplication is not null) await ExitApplication(); }
    private async void UpdateClick(object? sender, RoutedEventArgs e) { if (UpdateApplication is not null) await UpdateApplication(); }
    private async void CopyDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException();
            await clipboard.SetTextAsync(Model.DiagnosticText);
            Model.SetInfo("Details copied.");
        }
        catch (Exception) { Model.SetError("Cannot copy details. Select the text in Details and press Ctrl+C."); }
    }
    private void FolderClick(object? sender, RoutedEventArgs e) => OpenSelected(null);
    private void TranscriptClick(object? sender, RoutedEventArgs e) => OpenSelected("transcription.md");
    private void SummaryFileClick(object? sender, RoutedEventArgs e) => OpenSelected("summary.md");
    private void OpenSelected(string? name)
    {
        if (Model.SelectedSession is not { } session) return;
        try { Process.Start(new ProcessStartInfo(name is null ? session.Directory : Path.Combine(session.Directory, name)) { UseShellExecute = true }); }
        catch (Exception) { Model.SetError("Cannot open the selected folder or document. Check its existence and Windows file associations."); }
    }
}
