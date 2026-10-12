namespace MeetTape.Tests;

public sealed class CliHelpTests
{
    [Fact]
    public void OverviewDescribesGuiAndDiagnosticsWithoutShell()
    {
        var result = Run("--help");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains("Avalonia UI", result.Output);
        Assert.Contains("Start / Stop", result.Output);
        Assert.Contains("meettape configure", result.Output);
        Assert.Contains("help advanced", result.Output);
        Assert.DoesNotContain("meettape>", result.Output);
    }

    [Theory]
    [InlineData("start", "GUI action")]
    [InlineData("stop", "GUI action")]
    [InlineData("sessions", "PowerShell: meettape sessions")]
    [InlineData("status", "PowerShell: meettape status")]
    [InlineData("configure", "PowerShell: meettape configure")]
    [InlineData("devices", "PowerShell: meettape devices")]
    [InlineData("check-config", "PowerShell: meettape check-config")]
    [InlineData("update", "PowerShell: meettape update")]
    [InlineData("prepare", "GUI action")]
    [InlineData("chunks", "PowerShell: meettape chunks")]
    [InlineData("rechunk", "PowerShell: meettape rechunk")]
    [InlineData("resume", "GUI action")]
    [InlineData("assemble", "PowerShell: meettape assemble")]
    [InlineData("summary", "GUI action")]
    [InlineData("accept", "GUI action")]
    [InlineData("skip", "GUI action")]
    [InlineData("trim", "GUI action")]
    [InlineData("help", "PowerShell: meettape --help")]
    [InlineData("exit", "GUI action")]
    [InlineData("quit", "GUI action")]
    public void ContextHelpDoesNotAccessConfigurationOrDevices(string command, string expected)
    {
        var result = Run(command, "--help");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains(expected, result.Output);
        Assert.Equal(result.Output, Run("help", command).Output);
    }

    [Fact]
    public void ContextHelpWorksWithMissingExplicitConfigBeforeOrAfterCommand()
    {
        Assert.Equal(0, Run("--config", "missing.json", "start", "--help").Code);
        Assert.Equal(0, Run("sessions", "--help", "--config", "missing.json").Code);
        Assert.Contains("Missing --config path", Run("--config", "--help").Error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public void CaptureActionsCannotStartAnotherProcessRecording(string command)
    {
        var result = Run(command);
        Assert.Equal(2, result.Code);
        Assert.Contains("GUI action", result.Error);
    }

    [Fact]
    public void UpdateDoesNotLoadSettingsOrAudioDevices()
    {
        bool launched = false;
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, Cli.Run(["update"], TextReader.Null, output, error,
            (_, _) => { launched = true; return 0; }, () => throw new Exception("No hardware")));
        Assert.True(launched); Assert.Empty(error.ToString());
    }

    [Fact]
    public void DevicesListsEndpointsWithoutCreatingCapture()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, Cli.Run(["devices"], TextReader.Null, output, error,
            null, () =>
            [new("mic-id", "Microphone", CaptureChannel.Me, true), new("render-id", "Speakers", CaptureChannel.Them, false)]));
        Assert.Contains("Me (default): Microphone", output.ToString());
        Assert.Contains("render-id", output.ToString()); Assert.Empty(error.ToString());
    }

    [Fact]
    public void DeviceErrorsAreSanitized()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, Cli.Run(["devices"], TextReader.Null, output, error,
            null,
            () => throw new System.Runtime.InteropServices.COMException("private device diagnostic")));
        Assert.Contains("Windows Audio", error.ToString()); Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public void RepeatedConsoleConfigurePreservesSettingsAndPrompts()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-Configure-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "config.json");
            using var output = new StringWriter(); using var error = new StringWriter();
            int Configure() => Cli.Run(["configure", "--config", path], new StringReader(new string('\n', 17)), output, error);
            Assert.Equal(0, Configure());
            string original = File.ReadAllText(path);
            string prompt = File.ReadAllText(Path.Combine(root, "prompts", "transcription.md"));
            Assert.Equal(0, Configure());
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(prompt, File.ReadAllText(Path.Combine(root, "prompts", "transcription.md")));
            Assert.Empty(error.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AdvancedHelpSeparatesLocalRecoveryAndGuiOperations()
    {
        var result = Run("help", "advanced");
        Assert.Equal(0, result.Code); Assert.Contains("rechunk <id>", result.Output);
        Assert.Contains("Request Summary in the GUI", result.Output);
        Assert.Contains("No API calls", result.Output);
        Assert.Equal(2, Run("help", "unknown").Code);
        Assert.Equal(2, Run("init").Code);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        int result = Cli.Run(args, TextReader.Null, output, error,
            null, () => throw new Exception("Help must not access hardware"));
        return (result, output.ToString(), error.ToString());
    }
}
