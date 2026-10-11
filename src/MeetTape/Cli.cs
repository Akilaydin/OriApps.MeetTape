namespace MeetTape;

public static class Cli
{
    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error) =>
        RunCore(args, input, output, error, configuration => new Mp3Recorder(configuration).Capture, UpdateLauncher.Run, AudioDevices.List);

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error,
        Func<CaptureSettings, CaptureCoordinator> createCapture,
        Func<TextWriter, TextWriter, int>? launchUpdate = null)
        => RunCore(args, input, output, error, configuration => createCapture(configuration.Settings.Capture),
            launchUpdate ?? UpdateLauncher.Run, AudioDevices.List);

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error,
        Func<CaptureSettings, CaptureCoordinator> createCapture, Func<TextWriter, TextWriter, int>? launchUpdate,
        Func<IReadOnlyList<AudioDevice>> listDevices)
        => RunCore(args, input, output, error, configuration => createCapture(configuration.Settings.Capture),
            launchUpdate ?? UpdateLauncher.Run, listDevices);

    private static int RunCore(string[] args, TextReader input, TextWriter output, TextWriter error,
        Func<LoadedConfiguration, CaptureCoordinator> createCapture, Func<TextWriter, TextWriter, int> launchUpdate,
        Func<IReadOnlyList<AudioDevice>> listDevices)
    {
        output = CliColorWriter.Wrap(output);
        error = CliColorWriter.Wrap(error, stderr: true);
        string? explicitConfigPath = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config")
            {
                if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    CliColors.WriteLine(error, "Missing --config path. Use 'meettape --help'.", ConsoleColor.Red);
                    return 2;
                }
                explicitConfigPath = args[++i];
            }
            else
                positional.Add(args[i]);
        }

        if (CliHelp.TryWriteCommandLine(positional, output, error) is { } helpResult)
            return helpResult;

        string? command = positional.Count == 1 && (positional[0] is "init" or "check-config" or "status" or "devices" or "sessions" or "update")
            ? positional[0] : null;
        if (positional.Count > 0 && command is null)
        {
            string? interactiveOnly = positional.Count == 1 && CliHelp.IsInteractiveOnly(positional[0])
                ? positional[0] : null;
            CliColors.WriteLine(error, interactiveOnly is null
                ? "Unknown or incomplete argument. Use 'meettape --help' or 'meettape <command> --help'."
                : $"'{interactiveOnly}' is an interactive command. Run 'meettape' and type '{interactiveOnly}'. For details: meettape {interactiveOnly} --help.", ConsoleColor.Red);
            return 2;
        }

        try
        {
            if (command == "update") return launchUpdate(output, error);
            if (command == "init")
            {
                string path = explicitConfigPath ?? ConfigurationBootstrap.UserConfigurationPath;
                bool created = ConfigurationBootstrap.Initialize(path, ConfigurationBootstrap.UserMeetingsRoot);
                if (created)
                    CliColors.WriteLine(output, "Created", ConsoleColor.Green,
                        $" {Path.GetFullPath(path)}. Edit this file and its prompts; set your API key environment variable before transcription.");
                else
                    output.WriteLine($"Configuration already exists: {Path.GetFullPath(path)}. Nothing overwritten; edit this file to change settings.");
                output.WriteLine("Restart MeetTape after editing configuration or prompts.");
                return 0;
            }
            if (command == "devices")
            {
                WriteDevices(output, listDevices());
                return 0;
            }
            string configPath = explicitConfigPath ?? ConfigurationBootstrap.DefaultConfigurationPath(
                Environment.CurrentDirectory, ConfigurationBootstrap.UserConfigurationPath, AppContext.BaseDirectory);
            if (!File.Exists(configPath))
            {
                CliColors.WriteLine(error, "Configuration not found. Run 'meettape init', then edit your user config, or pass --config <path>.", ConsoleColor.Red);
                return 2;
            }
            var config = ConfigurationLoader.Load(configPath, validatePrompts: command is not ("sessions" or "status"));
            using var capture = createCapture(config);
            capture.Failed += message => CliColors.WriteLine(output, message, ConsoleColor.Red);
            if (command == "check-config")
            {
                if (config.Settings.Stages.Transcribe)
                    _ = ApiKeyProvider.Get(config.Settings.Transcription);
                if (config.Settings.Stages.Summary)
                    _ = ApiKeyProvider.Get(config.Settings.Summary);
                CliColors.WriteLine(output, "Configuration and enabled-stage credentials are valid. No API request made.", ConsoleColor.Green);
                return 0;
            }
            var sessions = SessionStore.Discover(config.MeetingsRoot);
            if (command == "sessions")
            {
                WriteSessions(output, sessions);
                return 0;
            }
            foreach (var session in sessions.Where(s => s.Data?.RecordingState == RecordingState.Interrupted || s.Data is null))
                CliColors.WriteLine(output, "Session needs attention:", ConsoleColor.Yellow, $" {session.Directory}\n{session.Diagnostic}");
            if (command == "status")
            {
                CliColors.WriteLine(output, "This process cannot inspect a recording in another terminal. Use 'status' in the running meettape> shell.", ConsoleColor.Yellow);
                WriteStatus(output, capture.Status);
                return 0;
            }

            using var preparation = new PostRecordingWorkflow(config.Settings.Capture.FfmpegPath, output);
            using var jobs = new ProcessingJobs(config, output, () => capture.Status.IsActive);
            jobs.Start();
            CliHelp.WriteOverview(output);
            while (true)
            {
                CliColors.Write(output, "meettape>", ConsoleColor.Cyan, " ");
                string? line = input.ReadLine();
                if (line is null)
                    return 0;
                if (CliHelp.TryWriteInteractive(line, output))
                    continue;
                switch (line.Trim().ToLowerInvariant())
                {
                    case "exit":
                    case "quit":
                        return 0;
                    case "update":
                        if (capture.Status.IsActive)
                            CliColors.WriteLine(output, "Stop the recording before updating MeetTape.", ConsoleColor.Yellow);
                        else if (launchUpdate(output, error) == 0)
                            return 0;
                        break;
                    case "start":
                        try
                        {
                            jobs.PauseAndWait();
                            preparation.CancelAndWait();
                            capture.Start();
                            CliColors.WriteLine(output, "Capture started (Me + Them).", ConsoleColor.Green,
                                $" {capture.RecordingDirectory ?? "No encoder attached."}");
                        }
                        catch (InvalidOperationException ex) { CliColors.WriteLine(output, ex.Message, ConsoleColor.Yellow); }
                        finally { if (!capture.Status.IsActive) jobs.Start(); }
                        break;
                    case "stop":
                        try
                        {
                            capture.Stop();
                            WriteStatus(output, capture.Status, stopped: true);
                            if (capture.Status.Error is null && capture.RecordingDirectory is { } directory)
                                capture.RecordingDirectory = PromptAndPrepare(directory, preparation, input, output);
                        }
                        catch (InvalidOperationException ex) { CliColors.WriteLine(output, ex.Message, ConsoleColor.Yellow); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { CliColors.WriteLine(output, "Cannot save title/start preparation; originals retained. Check session.json, paths and permissions.", ConsoleColor.Red); }
                        finally { if (!capture.Status.IsActive) jobs.Start(); }
                        break;
                    case "status":
                        WriteStatus(output, capture.Status);
                        break;
                    case "sessions":
                        WriteSessions(output, SessionStore.Discover(config.MeetingsRoot));
                        break;
                    case "devices":
                        try { WriteDevices(output, listDevices()); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                        { CliColors.WriteLine(output, "Cannot list audio devices. Check Windows Audio service and device permissions.", ConsoleColor.Yellow); }
                        break;
                    case "":
                        break;
                    default:
                        try
                        {
                            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            parts[0] = parts[0].ToLowerInvariant();
                            if (capture.Status.IsActive && parts[0] is "prepare" or "chunks" or "rechunk" or "resume" or "assemble" or "summary" or "accept" or "skip" or "trim")
                                throw new InvalidOperationException("Audio preparation is paused during capture.");
                            switch (parts)
                            {
                                case ["prepare", var id] when Guid.TryParse(id, out var sessionId):
                                {
                                    jobs.PauseAndWait();
                                    var selected = SessionStore.Discover(config.MeetingsRoot).SingleOrDefault(s => s.Data?.Id == sessionId)
                                        ?? throw new InvalidOperationException("Session ID not found; use sessions.");
                                    PromptAndPrepare(selected.Directory, preparation, input, output);
                                    break;
                                }
                                case ["chunks", var chunkId] when Guid.TryParse(chunkId, out var chunkSessionId):
                                {
                                    jobs.PauseAndWait();
                                    var selected = SessionStore.Discover(config.MeetingsRoot).SingleOrDefault(s => s.Data?.Id == chunkSessionId)
                                        ?? throw new InvalidOperationException("Session ID not found; use sessions.");
                                    preparation.PrepareChunks(selected.Directory);
                                    break;
                                }
                                case ["rechunk", var rechunkId] when Guid.TryParse(rechunkId, out var rechunkSessionId):
                                {
                                    jobs.PauseAndWait();
                                    var selected = SessionStore.Discover(config.MeetingsRoot).SingleOrDefault(s => s.Data?.Id == rechunkSessionId)
                                        ?? throw new InvalidOperationException("Session ID not found; use sessions.");
                                    preparation.Rechunk(selected.Directory, config.Settings.ChunkSeconds, jobs.UnblockRecovered);
                                    break;
                                }
                                case ["resume", var resumeId] when Guid.TryParse(resumeId, out var resumeSessionId):
                                {
                                    preparation.CancelAndWait();
                                    var selected = SessionStore.Discover(config.MeetingsRoot).SingleOrDefault(s => s.Data?.Id == resumeSessionId)
                                        ?? throw new InvalidOperationException("Session ID not found; use sessions.");
                                    jobs.Resume(selected.Directory);
                                    break;
                                }
                                case ["assemble", var assembleId] when Guid.TryParse(assembleId, out var assembleSessionId):
                                {
                                    preparation.CancelAndWait();
                                    var selected = SessionStore.Discover(config.MeetingsRoot).SingleOrDefault(s => s.Data?.Id == assembleSessionId)
                                        ?? throw new InvalidOperationException("Session ID not found; use sessions.");
                                    jobs.Assemble(selected.Directory);
                                    break;
                                }
                                case ["summary", var summaryId] when Guid.TryParse(summaryId, out var summarySessionId):
                                {
                                    preparation.CancelAndWait();
                                    var selected = SessionStore.Discover(config.MeetingsRoot).SingleOrDefault(s => s.Data?.Id == summarySessionId)
                                        ?? throw new InvalidOperationException("Session ID not found; use sessions.");
                                    jobs.RequestSummary(selected.Directory);
                                    break;
                                }
                                case ["accept"]: preparation.Choose("accept"); break;
                                case ["skip"]: preparation.Choose("skip"); break;
                                case ["trim", var a, var b] when AudioTrimmer.TryParseTime(a, out double start)
                                    && AudioTrimmer.TryParseTime(b, out double end):
                                    preparation.Choose("manual", start, end);
                                    break;
                                default: CliColors.WriteLine(output, "Unknown command or invalid arguments; use 'help <command>'.", ConsoleColor.Yellow); break;
                            }
                        }
                        catch (InvalidOperationException ex) { CliColors.WriteLine(output, ex.Message, ConsoleColor.Yellow); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { CliColors.WriteLine(output, "Cannot update preparation; originals retained. Check session.json and permissions.", ConsoleColor.Red); }
                        finally { if (!capture.Status.IsActive) jobs.Start(); }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.COMException or AudioPreparationShutdownException)
        {
            // Do not echo file contents, JSON values, prompts, credentials or raw exceptions.
            CliColors.WriteLine(error, ex is InvalidDataException or AudioPreparationShutdownException ? ex.Message :
                "Cannot access configuration, prompts or audio devices. Check paths, permissions and Windows Audio service.", ConsoleColor.Red);
            return 2;
        }
    }

    private static void WriteStatus(TextWriter output, CaptureStatus status, bool stopped = false)
    {
        string state = status.IsActive ? "Capturing" : "Idle";
        string details = $" Duration {status.Duration:c}; Me {status.MeBytes} bytes; Them {status.ThemBytes} bytes. {status.RecordingDirectory ?? "No recording created."}";
        if (status.Error is null && (status.IsActive || stopped))
            CliColors.WriteLine(output, state + ".", ConsoleColor.Green, details);
        else
            output.WriteLine(state + "." + details);
        if (status.Error is not null)
            CliColors.WriteLine(output, status.Error, ConsoleColor.Red);
        if (status.Discontinuities > 0)
            CliColors.WriteLine(output, $"WASAPI marked {status.Discontinuities} discontinuities (including possible startup flags); timeline gaps were preserved as silence.", ConsoleColor.Yellow);
    }

    private static void WriteDevices(TextWriter output, IReadOnlyList<AudioDevice> devices)
    {
        if (devices.Count == 0)
            output.WriteLine("No active microphone or playback devices found.");
        foreach (var device in devices)
            output.WriteLine($"{device.Channel}{(device.IsDefault ? " (default)" : "")}: {device.Name}\n  {device.Id}");
    }

    private static string PromptAndPrepare(string directory, PostRecordingWorkflow preparation, TextReader input, TextWriter output)
    {
        var session = SessionStore.Read(directory);
        if (session.RecordingState != RecordingState.Recorded)
            throw new InvalidOperationException("Verify incomplete original audio before preparation.");
        output.Write("Meeting title (empty keeps current title or unnamed): ");
        string? title = input.ReadLine();
        if (title is null) return directory;
        preparation.Begin(directory, string.IsNullOrWhiteSpace(title) ? session.Title ?? "" : title);
        return preparation.SelectedDirectory!;
    }

    private static void WriteSessions(TextWriter output, IReadOnlyList<StoredSession> sessions)
    {
        if (sessions.Count == 0) output.WriteLine("No sessions yet.");
        foreach (var session in sessions)
        {
            output.WriteLine($"{session.Data?.Id.ToString() ?? "unknown"}: {session.Data?.RecordingState.ToString() ?? "invalid"}; job={session.Data?.JobState.ToString() ?? "invalid"}; Me={session.MePresent}; Them={session.ThemPresent}; title={session.Data?.Title ?? "unnamed"}\n  {session.Directory}");
            if (session.Diagnostic is not null) CliColors.WriteLine(output, session.Diagnostic, ConsoleColor.Yellow);
        }
    }
}
