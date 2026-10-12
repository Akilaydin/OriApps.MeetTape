namespace MeetTape;

internal static class CliHelp
{
    internal static bool IsCommandName(string topic) => Topics.ContainsKey(topic);
    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["configure"] = "configure — Open graphical settings. PowerShell: meettape configure [--config <path>]. Use configure --console for the diagnostic console wizard. Restart MeetTape after saving. Keys use Windows user environment variables, never config.json; existing prompts are retained.",
        ["start"] = "start — GUI action: Start recording Me + Them in the MeetTape window or tray. One active capture per process. Processing pauses before capture starts.",
        ["stop"] = "stop — GUI action: finalize recording, enter an optional title and choose Accept, Skip or manual trim. Originals retained. No blocking audio/API work on the UI thread.",
        ["status"] = "status — PowerShell: meettape status [--config <path>]. Describes this process only; cannot inspect another GUI process. Open the running window for live recording status.",
        ["sessions"] = "sessions — PowerShell: meettape sessions [--config <path>]. List saved session IDs, folders and states without API calls.",
        ["devices"] = "devices — PowerShell: meettape devices. List device IDs. Null configured IDs select Windows defaults. Settings also provides device selection.",
        ["check-config"] = "check-config — PowerShell: meettape check-config [--config <path>]. Validate enabled prompts and API credentials locally, without API requests.",
        ["update"] = "update — PowerShell: meettape update, or Update in the idle GUI. Stop recording first. A separate PowerShell updater waits for this process to exit; running EXEs cannot be replaced. Configuration, prompts, API keys and recordings retained.",
        ["prepare"] = "prepare — GUI action: select a finalized session and choose Name / Trim to resume unfinished title/trim selection, including after restart.",
        ["accept"] = "accept — GUI action: accept the proposed shared trim interval. Originals retained.",
        ["skip"] = "skip — GUI action: select original tracks, without trimming.",
        ["trim"] = "trim — GUI action: enter a shared manual interval in seconds, MM:SS or HH:MM:SS. Requires 0 <= start < end <= duration.",
        ["resume"] = "resume — GUI action: select a paused session and click Resume after fixing its cause. Restart to apply new settings; successful chunks retained.",
        ["summary"] = "summary — GUI action: Request Summary for a complete published transcription.md. Requires stages.summary enabled. No automatic Summary request.",
        ["chunks"] = "chunks — PowerShell: meettape chunks <session-id> [--config <path>]. Prepare missing local stereo chunks. Close the GUI first. No API request.",
        ["rechunk"] = "rechunk — PowerShell: meettape rechunk <session-id> [--config <path>]. Recover oversized unfinished chunks using current chunkSeconds; verified checkpoints retained. Close the GUI first. No API request.",
        ["assemble"] = "assemble — PowerShell: meettape assemble <session-id> [--config <path>]. Rebuild transcription.md from complete local checkpoints; differing documents retained. Close the GUI first. No API request.",
        ["exit"] = "exit — GUI action: Exit in the window or tray finalizes capture and joins processing. Window close hides to tray. Saved work resumes on next launch.",
        ["help"] = "help — PowerShell: meettape --help | meettape help <command> | meettape <command> --help. Help does not load settings or access hardware."
    };

    internal static int? TryWriteCommandLine(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Count == 1 && args[0] is "--help" or "help") { WriteOverview(output); return 0; }
        if (args.Count == 2 && args[0] == "help") return WriteTopic(args[1] == "--help" ? "help" : args[1], output, error);
        if (args.Count == 2 && args[1] == "--help") return WriteTopic(args[0], output, error);
        return null;
    }

    internal static void WriteOverview(TextWriter output) => CliColors.WriteHelp(output, """
        MeetTape — Windows meeting recorder with Avalonia UI.
        meettape                    Open window and tray (Start / Stop, sessions, trim, Summary)
        meettape configure          Open Settings
        meettape --config <path>     Open GUI with explicit configuration
        meettape update             Update from the public GitHub Release after safe exit
        meettape devices            List audio devices
        meettape sessions           List stored sessions
        meettape check-config       Validate enabled-stage credentials locally
        meettape help <command>     Explain a command or GUI action
        meettape help advanced      Support-only recovery commands
        Close hides the window. Exit finalizes recording and saves background work.
        """);

    private static int WriteTopic(string topic, TextWriter output, TextWriter error)
    {
        if (topic.Equals("advanced", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Support-only recovery (close GUI first): chunks <id>, rechunk <id>, assemble <id>. No API calls. Use Name / Trim, Resume and Request Summary in the GUI. Details: meettape <command> --help.");
            return 0;
        }
        if (topic.Equals("quit", StringComparison.OrdinalIgnoreCase)) topic = "exit";
        if (Topics.TryGetValue(topic, out string? description)) { CliColors.WriteHelp(output, description); return 0; }
        error.WriteLine($"Unknown help topic '{topic}'. Use 'help' or 'help advanced'.");
        return 2;
    }
}
