using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Data;
using System.Diagnostics;

namespace OriApps.MeetTape.Desktop;

public sealed partial class MainWindow : Window
{
    public Func<Task>? OpenSettings { get; set; }
    public Func<Task>? ExitApplication { get; set; }
    public Func<Task>? UpdateApplication { get; set; }
    internal Func<string, Task>? WriteClipboard { get; set; }
    internal Action<string>? OpenPath { get; set; }
    public bool AllowClose { get; set; }
    private MainViewModel Model => (MainViewModel)DataContext!;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => this.FindControl<ListBox>("SessionList")!.ContextMenu!.DataContext = DataContext;
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
    private async void CancelProcessingClick(object? sender, RoutedEventArgs e) => await Model.CancelSelectedProcessingAsync();
    private async void CancelNamedClick(object? sender, RoutedEventArgs e) => await Model.CancelNamedRecordingAsync();
    private async void SummaryClick(object? sender, RoutedEventArgs e) => await Model.SummaryAsync();
    private async void DeleteRecordingClick(object? sender, RoutedEventArgs e) => await Model.DeleteSelectedRecordingAsync(recording =>
        new DeleteRecordingWindow(recording) { Icon = Icon }.ShowDialog<bool>(this));
    private async void SettingsClick(object? sender, RoutedEventArgs e) { if (OpenSettings is not null) await OpenSettings(); }
    private async void UpdateClick(object? sender, RoutedEventArgs e) { if (UpdateApplication is not null) await UpdateApplication(); }
    private Task WriteClipboardAsync(string text) => WriteClipboard is { } write ? write(text) :
        (Clipboard ?? throw new InvalidOperationException()).SetTextAsync(text);
    private async void CopyTranscriptClick(object? sender, RoutedEventArgs e) => await CopyDocumentAsync("transcription.md");
    private async void CopySummaryClick(object? sender, RoutedEventArgs e) => await CopyDocumentAsync("summary.md");
    internal async Task CopyDocumentAsync(string name)
    {
        var session = Model.SelectedSession;
        if (session is null || (name == "transcription.md" ? !Model.CanOpenTranscript : !Model.CanOpenSummary)) return;
        try
        {
            string markdown = await File.ReadAllTextAsync(Path.Combine(session.Directory, name));
            await WriteClipboardAsync(markdown);
            Model.SetInfo(name == "transcription.md" ? "Transcript copied." : "Summary copied.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Model.SetError("The selected Markdown file no longer exists. Refresh the meeting list.");
            await Model.RefreshAsync();
        }
        catch (Exception) { Model.SetError("Cannot copy Markdown. Check file permissions and clipboard availability; the document was retained."); }
    }
    private void FolderClick(object? sender, RoutedEventArgs e) => OpenSelected(null);
    private void TranscriptClick(object? sender, RoutedEventArgs e) => OpenSelected("transcription.md");
    private void SummaryFileClick(object? sender, RoutedEventArgs e) => OpenSelected("summary.md");
    private SessionRow? RowFrom(Control source) => source.DataContext as SessionRow ?? source.GetVisualAncestors()
        .OfType<Control>().Select(control => control.DataContext).OfType<SessionRow>().FirstOrDefault();
    internal void OpenSessionMenu(Control source, bool pointer = false)
    {
        var row = RowFrom(source);
        var current = row is null ? null : Model.Sessions.FirstOrDefault(item => item.Id == row.Id && item.Directory == row.Directory);
        if (current is null) return;
        Model.SelectedSession = current;
        var list = this.FindControl<ListBox>("SessionList")!;
        var menu = list.ContextMenu!;
        menu.DataContext = Model;
        menu.PlacementTarget = source;
        menu.Placement = pointer ? PlacementMode.Pointer : PlacementMode.Bottom;
        menu.Open(list);
    }
    private void SessionActionsClick(object? sender, RoutedEventArgs e) => OpenSessionMenu((Control)sender!);
    private void SessionContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Source is Control source && RowFrom(source) is not null) OpenSessionMenu(source, e.TryGetPosition(null, out _));
        else if (!e.TryGetPosition(null, out _) && Model.SelectedSession is { } selected)
        {
            var list = this.FindControl<ListBox>("SessionList")!;
            if (list.ContainerFromItem(selected) is { } row) OpenSessionMenu(row);
        }
    }
    private void SessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source || source is Button || source.GetVisualAncestors().OfType<Button>().Any()) return;
        if (RowFrom(source) is not { } row) return;
        Model.SelectedSession = row;
        OpenSelected(null);
        e.Handled = true;
    }
    private void MeetingDetailsClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedSession is null) return;
        var text = new TextBox { Name = "MeetingDetailsText", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Bind(TextBox.TextProperty, new Binding(nameof(MainViewModel.SelectedMeetingDetails)) { Source = Model });
        var copy = new Button { Name = "CopyMeetingDetails", Content = "Copy details" };
        var close = new Button { Content = "Close" };
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        panel.Children.Add(copy); panel.Children.Add(close);
        var content = new Grid { Margin = new Avalonia.Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        content.Children.Add(text); Grid.SetRow(panel, 1); content.Children.Add(panel);
        var dialog = new Window { Title = "Meeting details", Icon = Icon, Width = 620, Height = 460,
            MinWidth = 440, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ThemeVariant.Light, Content = content };
        close.Click += (_, _) => dialog.Close();
        copy.Click += async (_, _) =>
        {
            try { await WriteClipboardAsync(Model.SelectedMeetingDetails); Model.SetInfo("Meeting details copied."); }
            catch (Exception) { Model.SetError("Cannot copy details. Select the text and press Ctrl+C."); }
        };
        _ = dialog.ShowDialog(this);
    }
    private void OpenSelected(string? name)
    {
        if (Model.SelectedSession is not { } session) return;
        try
        {
            string path = name is null ? session.Directory : Path.Combine(session.Directory, name);
            if (OpenPath is { } open) open(path);
            else using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
        }
        catch (Exception) { Model.SetError("Cannot open the selected folder or document. Check its existence and Windows file associations."); }
    }
}
