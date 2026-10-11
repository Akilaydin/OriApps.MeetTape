namespace MeetTape.Tests;

public sealed class ConfigurationWizardTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetTape-Wizard-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "config.json");

    [Fact]
    public void FirstRunCreatesEditableFilesAndStoresKeyOnlyInEnvironment()
    {
        const string secret = "never-print-this-key-4821";
        var saved = new Dictionary<EnvironmentVariableTarget, string>();
        var result = Run(Answers(secret: secret),
            getKey: _ => null,
            setKey: (name, value, target) =>
            {
                Assert.Equal("MEETTAPE_API_KEY", name);
                saved[target] = value;
            });

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.True(File.Exists(ConfigPath));
        Assert.True(File.Exists(Path.Combine(root, "prompts", "transcription.md")));
        Assert.True(File.Exists(Path.Combine(root, "prompts", "summary.md")));
        Assert.Equal(secret, saved[EnvironmentVariableTarget.User]);
        Assert.Equal(secret, saved[EnvironmentVariableTarget.Process]);
        Assert.Equal(2, saved.Count);
        Assert.DoesNotContain(secret, File.ReadAllText(ConfigPath));
        Assert.DoesNotContain(secret, result.Output);
        Assert.DoesNotContain(secret, result.Error);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SecondRunChangesSelectedSettingsButPreservesCustomPromptsAndUnchangedFields()
    {
        ConfigureDefaults();
        string originalPrompt = Path.Combine(root, "prompts", "transcription.md");
        File.WriteAllText(originalPrompt, "My custom transcription instructions");
        string summaryPrompt = Path.Combine(root, "prompts", "summary.md");
        File.WriteAllText(summaryPrompt, "My custom summary instructions");

        var result = Run(Answers(new Dictionary<int, string>
        {
            [0] = "OtherMeetings",
            [3] = @"C:\Custom\ffmpeg.exe",
            [6] = "y",
            [7] = "180",
            [9] = "my-audio-model",
            [13] = "my-summary-model"
        }), getKey: _ => "existing-private-key", setKey: (_, _, _) => Assert.Fail("No key was entered."));

        Assert.Equal(0, result.Code);
        var settings = ConfigurationLoader.Load(ConfigPath).Settings;
        Assert.Equal("OtherMeetings", settings.MeetingsRoot);
        Assert.Equal(@"C:\Custom\ffmpeg.exe", settings.Capture.FfmpegPath);
        Assert.True(settings.Stages.Trim);
        Assert.True(settings.Stages.Transcribe);
        Assert.True(settings.Stages.Summary);
        Assert.Equal(180, settings.ChunkSeconds);
        Assert.Equal("my-audio-model", settings.Transcription.Model);
        Assert.Equal("my-summary-model", settings.Summary.Model);
        Assert.Equal("prompts/transcription.md", settings.Transcription.PromptFile);
        Assert.Equal("My custom transcription instructions", File.ReadAllText(originalPrompt));
        Assert.Equal("My custom summary instructions", File.ReadAllText(summaryPrompt));
        Assert.DoesNotContain("existing-private-key", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void RepeatedRunWithoutEditsPreservesExactConfigurationAndKeys()
    {
        ConfigureDefaults();
        string initial = File.ReadAllText(ConfigPath);
        string prompt = Path.Combine(root, "prompts", "transcription.md");
        File.WriteAllText(prompt, "My prompt");

        var result = Run(Answers(), _ => "private-key-not-to-be-shown",
            (_, _, _) => Assert.Fail("Blank key must not replace stored key."));

        Assert.Equal(0, result.Code);
        Assert.Contains("configured; Enter to keep", result.Output);
        Assert.Equal(initial, File.ReadAllText(ConfigPath));
        Assert.Equal("My prompt", File.ReadAllText(prompt));
        Assert.DoesNotContain("private-key-not-to-be-shown", result.Output);
    }

    [Fact]
    public void DifferentApiVariableNamesAllowSeparateKeys()
    {
        ConfigureDefaults();
        var targets = new List<(string Variable, string Value, EnvironmentVariableTarget Target)>();
        var result = Run(Answers(new Dictionary<int, string>
        {
            [10] = "MY_AUDIO_API_KEY",
            [14] = "MY_SUMMARY_API_KEY"
        }, secret: "audio-secret", secondSecret: "summary-secret"),
            _ => null, (variable, value, target) => targets.Add((variable, value, target)));

        Assert.Equal(0, result.Code);
        Assert.Equal(4, targets.Count);
        Assert.Contains(("MY_AUDIO_API_KEY", "audio-secret", EnvironmentVariableTarget.User), targets);
        Assert.Contains(("MY_AUDIO_API_KEY", "audio-secret", EnvironmentVariableTarget.Process), targets);
        Assert.Contains(("MY_SUMMARY_API_KEY", "summary-secret", EnvironmentVariableTarget.User), targets);
        Assert.Contains(("MY_SUMMARY_API_KEY", "summary-secret", EnvironmentVariableTarget.Process), targets);
        Assert.DoesNotContain("audio-secret", result.Output);
        Assert.DoesNotContain("summary-secret", result.Output);
        Assert.DoesNotContain("audio-secret", File.ReadAllText(ConfigPath));
        Assert.DoesNotContain("summary-secret", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void EndOfInputCancelsWithoutOverwritingExistingConfiguration()
    {
        ConfigureDefaults();
        string before = File.ReadAllText(ConfigPath);
        var result = Run("different-location\n");
        Assert.Equal(1, result.Code);
        Assert.Contains("canceled", result.Output);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void InvalidStageCombinationOrEndpointDoesNotReplaceExistingConfiguration()
    {
        ConfigureDefaults();
        string before = File.ReadAllText(ConfigPath);

        var invalidStages = Run(Answers(new Dictionary<int, string> { [5] = "n", [6] = "y" }));
        Assert.Equal(2, invalidStages.Code);
        Assert.Contains("Summary requires transcription", invalidStages.Error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));

        var invalidEndpoint = Run(Answers(new Dictionary<int, string> { [8] = "http://other-server.com" }));
        Assert.Equal(2, invalidEndpoint.Code);
        Assert.Contains("endpoint must use HTTPS", invalidEndpoint.Error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void InvalidEnvironmentVariableNameDoesNotWriteKeysOrConfiguration()
    {
        ConfigureDefaults();
        string before = File.ReadAllText(ConfigPath);
        var result = Run(Answers(new Dictionary<int, string> { [10] = "NOT=VALID" }, "not-a-key-to-print"),
            _ => null, (_, _, _) => Assert.Fail("Invalid variable must not be set."));

        Assert.Equal(2, result.Code);
        Assert.Contains("Invalid API key environment variable name", result.Error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.DoesNotContain("not-a-key-to-print", result.Output);
    }

    [Fact]
    public void InvalidKeyNeverLeaksOrChangesConfiguration()
    {
        ConfigureDefaults();
        string before = File.ReadAllText(ConfigPath);
        var result = Run(Answers(new Dictionary<int, string> { [7] = "123" }, "secret-with\ncontrol"),
            _ => null, (_, _, _) => Assert.Fail("Invalid key must not be saved."),
            readSecret: _ => "secret-with\ncontrol");

        Assert.Equal(2, result.Code);
        Assert.Contains("Invalid API key", result.Error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.DoesNotContain("secret-with", result.Output);
        Assert.DoesNotContain("secret-with", result.Error);
    }

    [Fact]
    public void ApiEnvironmentWriteFailureHasSafeDiagnosticWithoutExposingKey()
    {
        ConfigureDefaults();
        var result = Run(Answers(secret: "private-value-not-for-logs"),
            _ => null, (_, _, _) => throw new UnauthorizedAccessException("private-value-not-for-logs"));

        Assert.Equal(2, result.Code);
        Assert.Contains("Cannot save API key", result.Error);
        Assert.DoesNotContain("private-value-not-for-logs", result.Error);
        Assert.DoesNotContain("private-value-not-for-logs", result.Output);
        Assert.DoesNotContain("private-value-not-for-logs", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void CancelAtKeyPromptDoesNotPersistEditedSettings()
    {
        ConfigureDefaults();
        string before = File.ReadAllText(ConfigPath);
        var result = Run(Answers(new Dictionary<int, string> { [7] = "180" }),
            readSecret: _ => null);

        Assert.Equal(1, result.Code);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task ConcurrentConfigureProcessesDoNotLoseSettings()
    {
        ConfigureDefaults();
        using var barrier = new Barrier(2);
        (int Code, string Output, string Error) Configure(string folder) =>
            Run(Answers(new Dictionary<int, string> { [0] = folder }),
                readSecret: _ =>
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                    return "";
                });

        var first = Task.Run(() => Configure("FirstFolder"));
        var second = Task.Run(() => Configure("SecondFolder"));
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(new[] { 0, 2 }, outcomes.Select(result => result.Code).Order());
        Assert.Contains(outcomes, result => result.Error.Contains("changed during editing", StringComparison.Ordinal));
        string savedFolder = ConfigurationLoader.Load(ConfigPath).Settings.MeetingsRoot;
        Assert.Contains(savedFolder, new[] { "FirstFolder", "SecondFolder" });
    }

    [Fact]
    public void InvalidNumericAndBooleanInputsAreRePrompted()
    {
        ConfigureDefaults();
        string answers = Answers(new Dictionary<int, string> { [4] = "n", [7] = "200" });
        answers = answers.Replace("\n200\n", "\n500\n200\n", StringComparison.Ordinal)
            .Replace("\nn\n", "\nmaybe\nn\n", StringComparison.Ordinal);
        var result = Run(answers);

        Assert.Equal(0, result.Code);
        Assert.Contains("Enter y or n", result.Output);
        Assert.Contains("Enter a whole number", result.Output);
        Assert.False(ConfigurationLoader.Load(ConfigPath).Settings.Stages.Trim);
        Assert.Equal(200, ConfigurationLoader.Load(ConfigPath).Settings.ChunkSeconds);
    }

    private void ConfigureDefaults()
    {
        Assert.True(ConfigurationBootstrap.Initialize(ConfigPath, Path.Combine(root, "Meetings")));
    }

    private (int Code, string Output, string Error) Run(string answers, Func<string, string?>? getKey = null,
        Action<string, string, EnvironmentVariableTarget>? setKey = null, Func<TextReader, string?>? readSecret = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = ConfigurationWizard.Run(ConfigPath, new StringReader(answers), output, error,
            getKey ?? (_ => null),
            setKey ?? ((_, _, _) => Assert.Fail("Unexpected environment write.")),
            readSecret ?? (reader => reader.ReadLine()));
        return (result, output.ToString(), error.ToString());
    }

    private static string Answers(Dictionary<int, string>? edits = null, string secret = "", string? secondSecret = null)
    {
        var values = Enumerable.Repeat("", 16).ToArray();
        foreach (var (index, value) in edits ?? [])
            values[index] = value;
        return string.Join('\n', values.Append(secret).Concat(secondSecret is null ? [] : [secondSecret])) + "\n";
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
