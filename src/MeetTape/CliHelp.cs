namespace MeetTape;

internal static class CliHelp
{
    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["init"] = """
            init — Create a configuration and editable prompts without overwriting existing files.
            PowerShell: meettape init [--config <path>]
            Default file: %APPDATA%\MeetTape\config.json (path is printed by init).
            Edit that file and its prompts to change settings, then restart MeetTape.
            Set the configured API key environment variable before transcription.
            Re-running init preserves all existing files; it does not reset settings.
            """,
        ["start"] = """
            start — Begin recording your microphone (Me) and system playback (Them).
            Interactive shell: start
            Run 'meettape' in PowerShell first, then enter 'start' at the meettape> prompt.
            Recording runs in this process until you enter 'stop' or exit. Only one recording can be active.
            No transcription or API calls run while recording is active.
            """,
        ["stop"] = """
            stop — Finish the active recording and start post-recording preparation.
            Interactive shell: stop
            Run 'meettape' in PowerShell first; after 'start', enter 'stop' in that same shell.
            You will be asked for a title and, if trimming is enabled, offered a silence-trim choice.
            Transcription proceeds in the background after preparation; source MP3s remain unchanged.
            """,
        ["status"] = """
            status — Show the current process's capture state, duration, byte counts and errors.
            PowerShell: meettape status [--config <path>]
            Interactive shell: status
            Use the interactive command to inspect a live recording. The one-shot command
            starts another process, which cannot inspect an active capture in another shell.
            It normally reports Idle and displays an explanatory warning.
            The one-shot command starts no capture and makes no API requests.
            """,
        ["sessions"] = """
            sessions — List saved sessions, IDs, folders and recording/processing states.
            PowerShell: meettape sessions [--config <path>]
            Interactive shell: sessions
            Copy a session ID from this list when using recovery commands or requesting a summary.
            Listing sessions does not start transcription or make API requests.
            """,
        ["devices"] = """
            devices — List Windows microphone and playback devices and their IDs.
            PowerShell: meettape devices
            Interactive shell: devices
            Use the IDs in capture.microphoneDeviceId and capture.playbackDeviceId in config.json.
            Null device IDs select Windows defaults. No recording is started.
            """,
        ["check-config"] = """
            check-config — Validate configuration and enabled-stage API credentials locally.
            PowerShell: meettape check-config [--config <path>]
            This does not contact the API. Missing credentials do not prevent recording
            when the corresponding API stage is disabled.
            """,
        ["update"] = """
            update — Update the installed MeetTape executable and its supporting files from public GitHub Releases.
            PowerShell: meettape update
            Interactive shell: update (only when not recording; closes MeetTape after launching updater)
            Uses built-in Windows PowerShell and public GitHub access; no authentication is needed.
            The updater opens a separate PowerShell window, waits for MeetTape to exit, then updates.
            The original terminal returns to its command prompt immediately; progress appears in the new window.
            The updater window closes on success; on failure it waits for Enter after displaying the error.
            Configuration, API keys, prompts and recordings are not modified.
            """,
        ["accept"] = """
            accept — Apply the proposed start/end silence-trim interval to both tracks.
            Interactive shell: accept
            Use after the trim suggestion appears following 'stop' or 'prepare <session-id>'.
            The original Me/Them MP3 files are preserved.
            """,
        ["skip"] = """
            skip — Keep both original tracks as the transcription input without trimming.
            Interactive shell: skip
            Use after a trim suggestion appears following 'stop' or 'prepare <session-id>'.
            """,
        ["trim"] = """
            trim — Choose a shared manual time interval for both audio tracks.
            Interactive shell: trim <start> <end>
            Example: trim 00:12.5 01:30
            Times are seconds, MM:SS or HH:MM:SS; require 0 <= start < end <= duration.
            Available after the trim suggestion appears. Originals remain unchanged.
            """,
        ["prepare"] = """
            prepare — Reopen title/trim preparation for a previously recorded session.
            Interactive shell: prepare <session-id>
            Find the ID with 'sessions'. Useful if a trim choice was interrupted or deferred.
            Not available while recording; does not alter original MP3s.
            """,
        ["chunks"] = """
            chunks — Prepare stereo audio chunks for an existing session.
            Interactive shell: chunks <session-id>
            Normal processing prepares missing chunks automatically. Use this for diagnostics
            or manual recovery after finding the ID with 'sessions'. Not while recording.
            """,
        ["rechunk"] = """
            rechunk — Rebuild unfinished audio chunks using the current configured chunkSeconds.
            Interactive shell: rechunk <session-id>
            Use to recover from oversized chunks after reducing chunkSeconds and restarting.
            Verified previously paid transcript checkpoints are reused where possible.
            Not available while recording.
            """,
        ["resume"] = """
            resume — Retry a paused processing job after resolving its reported error.
            Interactive shell: resume <session-id>
            Find the ID with 'sessions'. Fix configuration, credentials or storage first;
            restart MeetTape to reload changed settings. Completed chunks are retained.
            Not available while recording.
            """,
        ["assemble"] = """
            assemble — Validate/rebuild a complete transcript from saved chunk results.
            Interactive shell: assemble <session-id>
            Uses local checkpoints without uploading audio or repeating paid API requests.
            Not available while recording. A conflicting existing transcript is not overwritten.
            """,
        ["summary"] = """
            summary — Request an optional AI summary of a completed transcript.
            Interactive shell: summary <session-id>
            Requires stages.summary enabled and a complete transcription.md.
            Uses the configured summary API/prompt. It never runs automatically.
            """,
        ["help"] = """
            help — Explain MeetTape commands.
            PowerShell: meettape --help | meettape help <command> | meettape <command> --help
            Interactive shell: help | help <command> | <command> --help
            Use 'help advanced' for recovery and less frequently used commands.
            """,
        ["exit"] = """
            exit — Leave the interactive shell (alias: quit).
            Interactive shell: exit
            If recording is active, exit finalizes and closes the capture.
            Incomplete background processing resumes at the next interactive startup.
            """
    };

    internal static bool IsInteractiveOnly(string topic) =>
        topic is "start" or "stop" or "accept" or "skip" or "trim" or "prepare" or "chunks" or "rechunk"
            or "resume" or "assemble" or "summary" or "exit" or "quit";

    internal static bool IsCommandName(string topic) => Topics.ContainsKey(topic);

    internal static int? TryWriteCommandLine(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Count == 1 && (args[0] is "--help" or "help"))
        {
            WriteOverview(output);
            return 0;
        }

        if (args.Count == 2 && args[0] == "help")
            return WriteTopic(args[1] == "--help" ? "help" : args[1], output, error);
        if (args.Count == 2 && args[1] == "--help")
            return WriteTopic(args[0], output, error);
        return null;
    }

    internal static bool TryWriteInteractive(string line, TextWriter output)
    {
        string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return false;
        if (words.Length == 1 && (words[0].Equals("help", StringComparison.OrdinalIgnoreCase) || words[0] == "--help"))
        {
            WriteOverview(output);
            return true;
        }

        if (words[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            if (words.Length == 2) WriteTopic(words[1] == "--help" ? "help" : words[1], output, output);
            else CliColors.WriteLine(output, "Usage: help [command|advanced]", ConsoleColor.Yellow);
            return true;
        }

        if (words.Length == 2 && words[1] == "--help")
        {
            WriteTopic(words[0], output, output);
            return true;
        }

        return false;
    }

    internal static void WriteOverview(TextWriter output) => CliColors.WriteHelp(output, """
        MeetTape — record meetings from an interactive terminal.
        First time: meettape init       Create config; edit it and set your API key.
        PowerShell:  meettape            Open the interactive shell.
                     meettape update     Update from the public GitHub Release

        At meettape>:
          start       Begin recording Me + Them
          stop        Finish recording; name meeting and choose optional trimming
          status      Show capture status
          sessions    List meetings and processing states
          update      Install the latest release and exit (only when not recording)
          help <cmd>  Explain a command (also: meettape <cmd> --help in PowerShell)
          exit        Quit

        More commands: meettape help advanced (or help advanced inside the shell)
        List sessions without opening the shell: meettape sessions
        """);

    private static int WriteTopic(string topic, TextWriter output, TextWriter error)
    {
        if (topic.Equals("advanced", StringComparison.OrdinalIgnoreCase))
        {
            CliColors.WriteHelp(output, """
                Advanced commands (inside meettape>, when not recording):
                  summary <id>      Request optional Summary after transcription
                  prepare <id>      Resume deferred title/trim selection
                  accept / skip     Choose suggested trimming or original tracks
                  trim <from> <to>  Set manual trimming boundaries
                  resume <id>       Restart a paused processing job
                  rechunk <id>      Recover unfinished oversized chunks

                Support-only recovery (normally automatic, inside meettape>):
                  chunks <id>       Manually prepare audio chunks
                  assemble <id>     Rebuild transcript from saved checkpoints

                Audio devices: meettape devices (PowerShell) or devices (inside shell)
                Configuration: meettape init, meettape check-config (PowerShell)
                Update: meettape update (PowerShell) or update (inside shell)
                Details: help <command> (in shell), meettape <command> --help (PowerShell)
                """);
            return 0;
        }

        if (topic.Equals("quit", StringComparison.OrdinalIgnoreCase)) topic = "exit";
        if (Topics.TryGetValue(topic, out string? description))
        {
            CliColors.WriteHelp(output, description);
            return 0;
        }

        CliColors.WriteLine(error, $"Unknown help topic '{topic}'. Use 'help' or 'help advanced' to list commands.", ConsoleColor.Red);
        return 2;
    }
}
