using System.Text;

namespace MeetTape.Tests;

public sealed class CliColorsTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "1")]
    [InlineData(true, "0")]
    public void RedirectedOrNoColorOutputRemainsPlainAndNeverTouchesConsole(bool terminal, string? noColor)
    {
        using var text = new StringWriter();
        using var writer = new CliColorWriter(text, () => terminal, () => noColor,
            () => throw new Exception("Foreground must not be read"), _ => throw new Exception("Foreground must not be changed"));

        CliColors.WriteLine(writer, "Warning", ConsoleColor.Yellow, ": details");
        CliColors.WriteHelp(writer, "MeetTape — help");

        Assert.Equal("Warning: details" + Environment.NewLine + "MeetTape — help" + Environment.NewLine, text.ToString());
    }

    [Fact]
    public void HighlightedSegmentUsesRequestedColorAndRestoresOriginalBeforePlainText()
    {
        ConsoleColor current = ConsoleColor.DarkGray;
        using var text = new ObservingWriter(() => current);
        using var writer = new CliColorWriter(text, () => true, () => null, () => current, color => current = color);

        CliColors.WriteLine(writer, "Created", ConsoleColor.Green, " C:\\meeting\\config.json");

        Assert.Equal(ConsoleColor.DarkGray, current);
        Assert.Equal("Created C:\\meeting\\config.json" + Environment.NewLine, text.ToString());
        Assert.Equal(ConsoleColor.Green, text.ObservedColors[0]);
        Assert.All(text.ObservedColors.Skip(1), color => Assert.Equal(ConsoleColor.DarkGray, color));
    }

    [Fact]
    public void InteractivePromptRestoresForegroundBeforeUserInput()
    {
        ConsoleColor current = ConsoleColor.Gray;
        using var text = new ObservingWriter(() => current);
        using var writer = new CliColorWriter(text, () => true, () => null, () => current, color => current = color);

        CliColors.Write(writer, "meettape>", ConsoleColor.Cyan, " ");

        Assert.Equal("meettape> ", text.ToString());
        Assert.Equal([ConsoleColor.Cyan, ConsoleColor.Gray], text.ObservedColors);
        Assert.Equal(ConsoleColor.Gray, current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackgroundMessagesRestoreInteractivePromptUntilInputIsReceived(bool terminal)
    {
        ConsoleColor current = ConsoleColor.Gray;
        using var text = new StringWriter();
        using var writer = new CliColorWriter(text, () => terminal, () => null, () => current, color => current = color);

        writer.BeginInteractivePrompt();
        await Task.Run(() =>
        {
            writer.WriteLine("Job: chunk 6/6 saved/validated.");
            CliColors.WriteLine(writer, "Job: complete transcription.md saved.", ConsoleColor.Green);
        });

        string newline = Environment.NewLine;
        Assert.Equal($"meettape> {newline}Job: chunk 6/6 saved/validated.{newline}meettape> {newline}" +
                     $"Job: complete transcription.md saved.{newline}meettape> ", text.ToString());
        Assert.Equal(ConsoleColor.Gray, current);

        writer.EndInteractivePrompt();
        writer.WriteLine("Idle.");
        Assert.EndsWith($"meettape> Idle.{newline}", text.ToString());
    }

    [Fact]
    public void ColorSetterFailureFallsBackToPlainText()
    {
        using var text = new StringWriter();
        using var writer = new CliColorWriter(text, () => true, () => null,
            () => ConsoleColor.White, _ => throw new IOException("No console"));

        CliColors.WriteLine(writer, "Error", ConsoleColor.Red, ": details");

        Assert.Equal("Error: details" + Environment.NewLine, text.ToString());
    }

    [Fact]
    public void ColorGetterFailureFallsBackToPlainText()
    {
        using var text = new StringWriter();
        using var writer = new CliColorWriter(text, () => true, () => null,
            () => throw new IOException("No console"), _ => throw new Exception("Setter must not run"));

        CliColors.WriteLine(writer, "Error", ConsoleColor.Red);

        Assert.Equal("Error" + Environment.NewLine, text.ToString());
    }

    [Fact]
    public void WriteFailurePropagatesButForegroundIsRestored()
    {
        ConsoleColor current = ConsoleColor.Gray;
        using var writer = new CliColorWriter(new FailingWriter(), () => true, () => null,
            () => current, color => current = color);

        var error = Assert.Throws<IOException>(() => CliColors.WriteLine(writer, "error", ConsoleColor.Red));

        Assert.Equal("Disk is full", error.Message);
        Assert.Equal(ConsoleColor.Gray, current);
    }

    [Fact]
    public void HelpHighlightsHeadersAndCommandsWithoutChangingWords()
    {
        ConsoleColor current = ConsoleColor.White;
        using var text = new ObservingWriter(() => current);
        using var writer = new CliColorWriter(text, () => true, () => null, () => current, color => current = color);

        CliColors.WriteHelp(writer, "MeetTape — recorder\nAt meettape>:\n  start       Begin recording\n  exit        Quit");

        Assert.Equal("MeetTape — recorder" + Environment.NewLine + "At meettape>:" + Environment.NewLine +
                     "  start       Begin recording" + Environment.NewLine + "  exit        Quit" + Environment.NewLine, text.ToString());
        Assert.Contains(ConsoleColor.Cyan, text.ObservedColors);
        Assert.Equal(ConsoleColor.White, current);
    }

    private sealed class ObservingWriter(Func<ConsoleColor> foreground) : StringWriter
    {
        internal List<ConsoleColor> ObservedColors { get; } = [];

        public override void Write(string? value)
        {
            ObservedColors.Add(foreground());
            base.Write(value);
        }

        public override void WriteLine(string? value)
        {
            ObservedColors.Add(foreground());
            base.WriteLine(value);
        }
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) => throw new IOException("Disk is full");
    }
}
