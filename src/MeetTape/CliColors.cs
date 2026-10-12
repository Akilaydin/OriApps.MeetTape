using System.Text;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeetTape.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeetTape.UiTests")]

namespace MeetTape;

internal static class CliColors
{
    internal static void Write(TextWriter output, string highlighted, ConsoleColor color, string rest = "")
    {
        if (output is CliColorWriter console)
            console.WriteHighlighted(highlighted, rest, color, appendNewline: false);
        else
            output.Write(highlighted + rest);
    }

    internal static void WriteLine(TextWriter output, string highlighted, ConsoleColor color, string rest = "")
    {
        if (output is CliColorWriter console)
            console.WriteHighlighted(highlighted, rest, color, appendNewline: true);
        else
            output.WriteLine(highlighted + rest);
    }

    internal static void WriteHelp(TextWriter output, string text)
    {
        if (output is not CliColorWriter console || !console.CanColor)
        {
            output.WriteLine(text);
            return;
        }

        console.WriteBlock(() =>
        {
            using var lines = new StringReader(text);
            string? line;
            while ((line = lines.ReadLine()) is not null)
            {
                string trimmed = line.TrimStart();
                int indent = line.Length - trimmed.Length;
                int headingEnd = trimmed.IndexOf(" — ", StringComparison.Ordinal);
                string command = trimmed.Split(' ', 2)[0];
                if (headingEnd > 0 && indent == 0)
                    WriteLine(output, line[..headingEnd], ConsoleColor.Cyan, line[headingEnd..]);
                else if (trimmed.StartsWith("At meettape>:", StringComparison.Ordinal) ||
                         trimmed.StartsWith("Advanced commands", StringComparison.Ordinal))
                    WriteLine(output, line, ConsoleColor.Cyan);
                else if (indent >= 2 && CliHelp.IsCommandName(command))
                {
                    int end = indent + command.Length;
                    WriteLine(output, line[..end], ConsoleColor.Cyan, line[end..]);
                }
                else
                    output.WriteLine(line);
            }
        });
    }
}

// Only real, non-redirected console streams can change the terminal palette.
// All writes use one lock so background job output cannot inherit a temporary color.
internal sealed class CliColorWriter : TextWriter
{
    private static readonly object ConsoleGate = new();
    private readonly TextWriter inner;
    private readonly Func<bool> isTerminal;
    private readonly Func<string?> readNoColor;
    private readonly Func<ConsoleColor> readForeground;
    private readonly Action<ConsoleColor> setForeground;
    private bool awaitingCommand;

    internal CliColorWriter(TextWriter inner, Func<bool> isTerminal, Func<string?> readNoColor,
        Func<ConsoleColor> readForeground, Action<ConsoleColor> setForeground)
    {
        this.inner = inner;
        this.isTerminal = isTerminal;
        this.readNoColor = readNoColor;
        this.readForeground = readForeground;
        this.setForeground = setForeground;
    }

    internal static CliColorWriter Wrap(TextWriter writer, bool stderr = false)
    {
        bool isConsoleStream = ReferenceEquals(writer, stderr ? Console.Error : Console.Out);
        return new(writer,
            () => isConsoleStream && !Console.IsOutputRedirected && !Console.IsErrorRedirected,
            () => Environment.GetEnvironmentVariable("NO_COLOR"),
            () => Console.ForegroundColor,
            color => Console.ForegroundColor = color);
    }

    public override Encoding Encoding => inner.Encoding;

    internal bool CanColor
    {
        get
        {
            try { return isTerminal() && string.IsNullOrEmpty(readNoColor()); }
            catch (Exception) { return false; } // No console: plain text instead.
        }
    }

    internal void WriteBlock(Action write)
    {
        lock (ConsoleGate) write();
    }

    internal void BeginInteractivePrompt()
    {
        lock (ConsoleGate)
        {
            awaitingCommand = true;
            WriteHighlighted("meettape>", " ", ConsoleColor.Cyan, appendNewline: false);
        }
    }

    internal void EndInteractivePrompt()
    {
        lock (ConsoleGate) awaitingCommand = false;
    }

    internal void WriteHighlighted(string highlighted, string rest, ConsoleColor color, bool appendNewline)
    {
        lock (ConsoleGate)
        {
            if (appendNewline && awaitingCommand) inner.WriteLine();
            try
            {
                if (!CanColor)
                {
                    WritePlain(highlighted + rest);
                    return;
                }

                ConsoleColor previous;
                try { previous = readForeground(); }
                catch (Exception)
                {
                    WritePlain(highlighted + rest);
                    return;
                }

                try { setForeground(color); }
                catch (Exception)
                {
                    TryRestore(previous);
                    WritePlain(highlighted + rest);
                    return;
                }

                try { inner.Write(highlighted); }
                finally { TryRestore(previous); }
                WritePlain(rest);
            }
            finally
            {
                if (appendNewline && awaitingCommand)
                    WriteHighlighted("meettape>", " ", ConsoleColor.Cyan, appendNewline: false);
            }
        }

        void WritePlain(string value)
        {
            if (appendNewline) inner.WriteLine(value);
            else inner.Write(value);
        }
    }

    private void TryRestore(ConsoleColor previous)
    {
        // A failing console setter must not mask a writer exception.
        try { setForeground(previous); }
        catch (Exception) { }
    }

    private void WriteLineAndRestorePrompt(Action writeLine)
    {
        lock (ConsoleGate)
        {
            // ReadLine stays active while background jobs write progress.
            if (awaitingCommand) inner.WriteLine();
            writeLine();
            if (awaitingCommand) WriteHighlighted("meettape>", " ", ConsoleColor.Cyan, appendNewline: false);
        }
    }

    public override void Write(char value) { lock (ConsoleGate) inner.Write(value); }
    public override void Write(string? value) { lock (ConsoleGate) inner.Write(value); }
    public override void WriteLine(string? value) => WriteLineAndRestorePrompt(() => inner.WriteLine(value));
    public override void WriteLine() => WriteLineAndRestorePrompt(inner.WriteLine);
    public override void Flush() { lock (ConsoleGate) inner.Flush(); }
}
