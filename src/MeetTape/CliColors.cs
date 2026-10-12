using System.Text;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeetTape.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeetTape.UiTests")]

namespace MeetTape;

internal static class CliColors
{
    internal static void WriteLine(TextWriter output, string highlighted, ConsoleColor color, string rest = "")
    {
        if (output is CliColorWriter console)
            console.WriteHighlighted(highlighted, rest, color);
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
// All writes use one lock so concurrent diagnostic output cannot inherit a temporary color.
internal sealed class CliColorWriter : TextWriter
{
    private static readonly object ConsoleGate = new();
    private readonly TextWriter inner;
    private readonly Func<bool> isTerminal;
    private readonly Func<string?> readNoColor;
    private readonly Func<ConsoleColor> readForeground;
    private readonly Action<ConsoleColor> setForeground;

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

    internal void WriteHighlighted(string highlighted, string rest, ConsoleColor color)
    {
        lock (ConsoleGate)
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

        void WritePlain(string value)
        {
            inner.WriteLine(value);
        }
    }

    private void TryRestore(ConsoleColor previous)
    {
        // A failing console setter must not mask a writer exception.
        try { setForeground(previous); }
        catch (Exception) { }
    }

    public override void Write(char value) { lock (ConsoleGate) inner.Write(value); }
    public override void Write(string? value) { lock (ConsoleGate) inner.Write(value); }
    public override void WriteLine(string? value) { lock (ConsoleGate) inner.WriteLine(value); }
    public override void WriteLine() { lock (ConsoleGate) inner.WriteLine(); }
    public override void Flush() { lock (ConsoleGate) inner.Flush(); }
}
