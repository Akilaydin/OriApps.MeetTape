namespace MeetTape.Tests;

public sealed class CliHelpTests
{
    [Fact]
    public void OverviewShowsRecordingWorkflowWithoutAdvancedCommandDump()
    {
        var result = Run("--help");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains("meettape configure", result.Output);
        Assert.Contains("meettape>", result.Output);
        Assert.Contains("start", result.Output);
        Assert.Contains("stop", result.Output);
        Assert.Contains("help advanced", result.Output);
        Assert.DoesNotContain("rechunk <id>", result.Output);
        Assert.DoesNotContain("assemble <id>", result.Output);
        Assert.DoesNotContain("devices", result.Output);
    }

    [Theory]
    [InlineData("start", "Interactive shell: start")]
    [InlineData("stop", "Interactive shell: stop")]
    [InlineData("sessions", "PowerShell: meettape sessions")]
    [InlineData("status", "PowerShell: meettape status")]
    [InlineData("configure", "PowerShell: meettape configure")]
    [InlineData("devices", "PowerShell: meettape devices")]
    [InlineData("check-config", "PowerShell: meettape check-config")]
    [InlineData("update", "PowerShell: meettape update")]
    [InlineData("prepare", "Interactive shell: prepare <session-id>")]
    [InlineData("chunks", "Interactive shell: chunks <session-id>")]
    [InlineData("rechunk", "Interactive shell: rechunk <session-id>")]
    [InlineData("resume", "Interactive shell: resume <session-id>")]
    [InlineData("assemble", "Interactive shell: assemble <session-id>")]
    [InlineData("summary", "Interactive shell: summary <session-id>")]
    [InlineData("accept", "Interactive shell: accept")]
    [InlineData("skip", "Interactive shell: skip")]
    [InlineData("trim", "Interactive shell: trim <start> <end>")]
    [InlineData("help", "PowerShell: meettape --help")]
    [InlineData("exit", "Interactive shell: exit")]
    [InlineData("quit", "Interactive shell: exit")]
    public void EveryCommandHasContextHelpWithoutConfigurationOrDevices(string command, string expected)
    {
        var result = Run(command, "--help");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains(expected, result.Output);
        Assert.Equal(result.Output, Run("help", command).Output);
    }

    [Fact]
    public void ContextHelpIgnoresMissingConfigAndAcceptsGlobalOptionBeforeOrAfterCommand()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"MeetTape-missing-{Guid.NewGuid():N}.json");
        var before = Run("--config", missing, "start", "--help");
        var after = Run("sessions", "--help", "--config", missing);

        Assert.Equal(0, before.Code);
        Assert.Equal(0, after.Code);
        Assert.Contains("Interactive shell: start", before.Output);
        Assert.Contains("PowerShell: meettape sessions", after.Output);
        Assert.Empty(before.Error);
        Assert.Empty(after.Error);
    }

    [Fact]
    public void StartAndStopAreNotMisrepresentedAsOneShotCommands()
    {
        foreach (string command in new[] { "start", "stop" })
        {
            var result = Run(command);
            Assert.Equal(2, result.Code);
            Assert.Contains("interactive command", result.Error);
            Assert.Contains($"meettape {command} --help", result.Error);
        }
    }

    [Fact]
    public void UpdateDoesNotRequireConfigurationOrAudioDevices()
    {
        bool launched = false;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = Cli.Run(["update"], new StringReader("exit"), output, error,
            _ => throw new InvalidOperationException("Unexpected audio device initialization."),
            (_, _) => { launched = true; return 0; });

        Assert.Equal(0, result);
        Assert.True(launched);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void DevicesListsTheSameEndpointsInPowerShellAndInteractiveShell()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-Devices-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = Path.Combine(root, "config.json");
            ConfigurationBootstrap.Initialize(config, Path.Combine(root, "Meetings"));
            var devices = new[]
            {
                new AudioDevice("microphone-id", "Test microphone", CaptureChannel.Me, true),
                new AudioDevice("playback-id", "Test speakers", CaptureChannel.Them, false)
            };
            int calls = 0;
            IReadOnlyList<AudioDevice> List() { calls++; return devices; }
            CaptureCoordinator Capture(CaptureSettings _) => new(() => throw new InvalidOperationException("Unexpected hardware use."));

            using var powerShellOutput = new StringWriter();
            using var powerShellError = new StringWriter();
            int powerShellResult = Cli.Run(["devices"], new StringReader(""), powerShellOutput, powerShellError,
                Capture, null, List);

            using var shellOutput = new StringWriter();
            using var shellError = new StringWriter();
            int shellResult = Cli.Run(["--config", config], new StringReader("devices\nstatus\nexit\n"),
                shellOutput, shellError, Capture, null, List);

            Assert.Equal(0, powerShellResult);
            Assert.Equal(0, shellResult);
            Assert.Equal(2, calls);
            Assert.Contains("Me (default): Test microphone\n  microphone-id", powerShellOutput.ToString());
            Assert.Contains("Them: Test speakers\n  playback-id", powerShellOutput.ToString());
            Assert.Contains("Me (default): Test microphone\n  microphone-id", shellOutput.ToString());
            Assert.Contains("Them: Test speakers\n  playback-id", shellOutput.ToString());
            Assert.Contains("Idle.", shellOutput.ToString());
            Assert.DoesNotContain("Unknown command", shellOutput.ToString());
            Assert.Empty(powerShellError.ToString());
            Assert.Empty(shellError.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedDeviceEnumerationDoesNotExitInteractiveShell()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-DeviceFailure-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = Path.Combine(root, "config.json");
            ConfigurationBootstrap.Initialize(config, Path.Combine(root, "Meetings"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            int result = Cli.Run(["--config", config], new StringReader("devices\nstatus\nexit\n"),
                output, error,
                _ => new CaptureCoordinator(() => throw new InvalidOperationException("Unexpected hardware use.")),
                null, () => throw new System.Runtime.InteropServices.COMException("private device diagnostic"));

            Assert.Equal(0, result);
            Assert.Contains("Cannot list audio devices", output.ToString());
            Assert.Contains("Idle.", output.ToString());
            Assert.DoesNotContain("private device diagnostic", output.ToString());
            Assert.Empty(error.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OneShotStatusWarnsItCannotInspectAnotherProcess()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-Status-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = Path.Combine(root, "config.json");
            ConfigurationBootstrap.Initialize(config, Path.Combine(root, "Meetings"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            int result = Cli.Run(["--config", config, "status"], new StringReader(""),
                output, error, _ => new CaptureCoordinator(() => throw new InvalidOperationException("Unexpected hardware use.")));

            Assert.Equal(0, result);
            Assert.Contains("cannot inspect a recording in another terminal", output.ToString());
            Assert.Contains("Idle.", output.ToString());
            Assert.Empty(error.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RepeatedConfigurePreservesFilesWhenEnterKeepsEveryValue()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-Configure-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "config.json");
            using var output = new StringWriter();
            using var error = new StringWriter();
            int first = Cli.Run(["configure", "--config", path], new StringReader(new string('\n', 17)), output, error,
                _ => throw new InvalidOperationException("Unexpected hardware use."));
            string original = File.ReadAllText(path);
            string promptsDirectory = Path.Combine(root, "prompts");
            string transcriptionPrompt = File.ReadAllText(Path.Combine(promptsDirectory, "transcription.md"));

            output.GetStringBuilder().Clear();
            int second = Cli.Run(["configure", "--config", path], new StringReader(new string('\n', 17)), output, error,
                _ => throw new InvalidOperationException("Unexpected hardware use."));

            Assert.Equal(0, first);
            Assert.Equal(0, second);
            Assert.Contains(Path.GetFullPath(path), output.ToString());
            Assert.Contains("Configuration unchanged", output.ToString());
            Assert.Contains("Restart MeetTape", output.ToString());
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(transcriptionPrompt, File.ReadAllText(Path.Combine(promptsDirectory, "transcription.md")));
            Assert.Empty(error.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InitIsNoLongerAPublicCommand()
    {
        Assert.Equal(2, Run("init").Code);
        Assert.Equal(2, Run("init", "--help").Code);
    }

    [Fact]
    public void InteractiveUpdateExitsAfterLaunchingUpdater()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-Update-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = Path.Combine(root, "config.json");
            ConfigurationBootstrap.Initialize(config, Path.Combine(root, "Meetings"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            int launches = 0;
            int code = Cli.Run(["--config", config], new StringReader("update\nstart\n"), output, error,
                _ => new CaptureCoordinator(() => throw new InvalidOperationException("Unexpected recording.")),
                (_, _) => { launches++; return 0; });

            Assert.Equal(0, code);
            Assert.Equal(1, launches);
            Assert.DoesNotContain("Capture started", output.ToString());
            Assert.Empty(error.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AdvancedAndUnknownHelpTopicsProduceUsefulResults()
    {
        var advanced = Run("help", "advanced");
        Assert.Equal(0, advanced.Code);
        Assert.Contains("rechunk <id>", advanced.Output);
        Assert.Contains("summary <id>", advanced.Output);
        Assert.Contains("Support-only recovery", advanced.Output);
        Assert.Contains("devices (inside shell)", advanced.Output);

        var unknown = Run("help", "not-a-command");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("Unknown help topic", unknown.Error);

        var missingPath = Run("--config", "--help");
        Assert.Equal(2, missingPath.Code);
        Assert.Contains("Missing --config path", missingPath.Error);
    }

    [Fact]
    public void ShellProvidesPerCommandHelpWithoutExecutingRecordingCommands()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-CliHelp-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = Path.Combine(root, "config.json");
            ConfigurationBootstrap.Initialize(config, Path.Combine(root, "Meetings"));
            using var output = new StringWriter();
            using var error = new StringWriter();

            int result = Cli.Run(["--config", config],
                new StringReader("help start\nstop --help\nhelp --help\nhelp advanced\nsessions --help\ndevices --help\nhelp missing\nexit\n"),
                output, error, _ => new CaptureCoordinator(() => throw new InvalidOperationException("Unexpected hardware use.")));

            Assert.Equal(0, result);
            Assert.Empty(error.ToString());
            Assert.Contains("Interactive shell: start", output.ToString());
            Assert.Contains("Interactive shell: stop", output.ToString());
            Assert.Contains("PowerShell: meettape sessions", output.ToString());
            Assert.Contains("Interactive shell: devices", output.ToString());
            Assert.Contains("PowerShell: meettape --help", output.ToString());
            Assert.Contains("Advanced commands", output.ToString());
            Assert.Contains("Unknown help topic", output.ToString());
            Assert.DoesNotContain("Capture started", output.ToString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = Cli.Run(args, new StringReader("exit\n"), output, error,
            _ => throw new InvalidOperationException("Help should not access hardware or configuration."));
        return (result, output.ToString(), error.ToString());
    }
}
