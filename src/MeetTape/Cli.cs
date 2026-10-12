namespace MeetTape;

public static class Cli
{
    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error) =>
        RunCore(args, input, output, error, UpdateLauncher.Run, AudioDevices.List);

    internal static int Run(string[] args, TextReader input, TextWriter output, TextWriter error,
        Func<TextWriter, TextWriter, int>? launchUpdate,
        Func<IReadOnlyList<AudioDevice>> listDevices) =>
        RunCore(args, input, output, error, launchUpdate ?? UpdateLauncher.Run, listDevices);

    private static int RunCore(string[] args, TextReader input, TextWriter output, TextWriter error,
        Func<TextWriter, TextWriter, int> launchUpdate, Func<IReadOnlyList<AudioDevice>> listDevices)
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
                    error.WriteLine("Missing --config path. Use 'meettape --help'.");
                    return 2;
                }
                explicitConfigPath = args[++i];
            }
            else positional.Add(args[i]);
        }
        if (CliHelp.TryWriteCommandLine(positional, output, error) is { } helpResult) return helpResult;
        string? command = positional.FirstOrDefault();
        bool recovery = positional.Count == 2 && command is "chunks" or "rechunk" or "assemble"
            && Guid.TryParse(positional[1], out _);
        if (!recovery && (positional.Count != 1 || command is not ("configure" or "check-config" or "status" or "devices" or "sessions" or "update")))
        {
            error.WriteLine(command is "start" or "stop" ?
                $"'{command}' is a GUI action. Open MeetTape. For details: meettape {command} --help." :
                "Unknown or incomplete argument. Use 'meettape --help'.");
            return 2;
        }
        try
        {
            if (command == "update") return launchUpdate(output, error);
            if (command == "configure") return ConfigurationWizard.Run(
                explicitConfigPath ?? ConfigurationBootstrap.UserConfigurationPath, input, output, error);
            if (command == "devices")
            {
                var devices = listDevices();
                if (devices.Count == 0) output.WriteLine("No active microphone or playback devices found.");
                foreach (var device in devices)
                    output.WriteLine($"{device.Channel}{(device.IsDefault ? " (default)" : "")}: {device.Name}\n  {device.Id}");
                return 0;
            }
            string path = explicitConfigPath ?? ConfigurationBootstrap.DefaultConfigurationPath(
                Environment.CurrentDirectory, ConfigurationBootstrap.UserConfigurationPath, AppContext.BaseDirectory);
            if (!File.Exists(path)) { error.WriteLine("Configuration not found. Open MeetTape Settings or pass --config <path>."); return 2; }
            var config = ConfigurationLoader.Load(path, validatePrompts: command == "check-config");
            if (command == "check-config")
            {
                if (config.Settings.Stages.Transcribe) _ = ApiKeyProvider.Get(config.Settings.Transcription);
                if (config.Settings.Stages.Summary) _ = ApiKeyProvider.Get(config.Settings.Summary);
                output.WriteLine("Configuration and enabled-stage credentials are valid. No API request made.");
                return 0;
            }
            if (command == "status")
            {
                output.WriteLine("This process cannot inspect a recording in another terminal or GUI process. Open the running MeetTape window.");
                output.WriteLine("Idle. No capture started in this process.");
                return 0;
            }
            var sessions = SessionStore.Discover(config.MeetingsRoot);
            if (command == "sessions")
            {
                if (sessions.Count == 0) output.WriteLine("No sessions yet.");
                foreach (var session in sessions)
                {
                    output.WriteLine($"{session.Data?.Id.ToString() ?? "unknown"}: {session.Data?.RecordingState.ToString() ?? "invalid"}; job={session.Data?.JobState.ToString() ?? "invalid"}; title={session.Data?.Title ?? "unnamed"}\n  {session.Directory}");
                    if (session.Diagnostic is not null) output.WriteLine(session.Diagnostic);
                }
                return 0;
            }
            var selected = sessions.SingleOrDefault(s => s.Data?.Id == Guid.Parse(positional[1]))
                ?? throw new InvalidOperationException("Session ID not found; use sessions.");
            var controller = new DesktopController(config);
            controller.Notice += notice => output.WriteLine(notice.Message);
            try { controller.RecoverAsync(selected.Directory, command!).GetAwaiter().GetResult(); }
            finally { controller.ShutdownAsync().GetAwaiter().GetResult(); }
            var recovered = SessionStore.Read(selected.Directory);
            if (recovered.Error is { } diagnostic) { error.WriteLine(diagnostic); return 2; }
            output.WriteLine("Recovery completed. Originals and successful checkpoints retained; no API request made.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            error.WriteLine(ex is InvalidDataException or InvalidOperationException ? ex.Message :
                "Cannot access configuration, prompts or audio devices. Check paths, permissions and Windows Audio service.");
            return 2;
        }
    }
}
