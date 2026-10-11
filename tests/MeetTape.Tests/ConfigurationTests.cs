using System.Text.Json.Nodes;

namespace MeetTape.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MeetTapeTests-" + Guid.NewGuid().ToString("N"));
    private readonly JsonObject json;

    public ConfigurationTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "prompt.md"), "Transcribe, preserving technical terms.");
        json = JsonNode.Parse("""
            {
              "meetingsRoot": "Meetings",
              "chunkSeconds": 120,
              "transcription": {
                "endpoint": "https://example.invalid/v1/chat/completions",
                "model": "audio-model",
                "apiKeyEnvironmentVariable": "MEETTAPE_TEST_MISSING_KEY",
                "promptFile": "prompt.md"
              },
              "summary": {
                "endpoint": "https://example.invalid/v1/chat/completions",
                "model": "summary-model",
                "promptFile": "prompt.md"
              }
            }
            """)!.AsObject();
        json["transcription"]!["apiKeyEnvironmentVariable"] = "MEETTAPE_TEST_" + Guid.NewGuid().ToString("N");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SummaryRequiresEnabledTranscription(bool transcribe, bool summary)
    {
        json["stages"] = new JsonObject { ["transcribe"] = transcribe, ["summary"] = summary };
        if (summary && !transcribe)
        {
            File.Delete(Path.Combine(directory, "prompt.md"));
            var error = Assert.Throws<InvalidDataException>(() => Load());
            Assert.Equal("stages.summary requires stages.transcribe to be enabled.", error.Message);
            Assert.DoesNotContain(json["summary"]!["endpoint"]!.GetValue<string>(), error.Message);
        }
        else
        {
            var loaded = Load();
            Assert.Equal(transcribe, loaded.Settings.Stages.Transcribe);
            Assert.Equal(summary, loaded.Settings.Stages.Summary);
        }
    }

    [Theory]
    [InlineData("sessions", "missing")]
    [InlineData("status", "missing")]
    [InlineData("sessions", "empty")]
    [InlineData("status", "empty")]
    [InlineData("sessions", "directory")]
    [InlineData("status", "directory")]
    public void InspectionRetainsRecordedSessionsWithoutUsablePrompts(string command, string promptState)
    {
        using var fixture = new JobTests();
        string session = fixture.Session();
        byte[] metadata = File.ReadAllBytes(Path.Combine(session, "session.json"));
        json["meetingsRoot"] = Path.GetDirectoryName(session);
        json["stages"] = new JsonObject { ["transcribe"] = true, ["summary"] = true };
        string prompt = Path.Combine(directory, "prompt.md");
        File.Delete(prompt);
        if (promptState == "empty") File.WriteAllText(prompt, " ");
        if (promptState == "directory") Directory.CreateDirectory(prompt);
        string configPath = WriteConfig();

        var result = Run(["--config", configPath, command]);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        if (command == "sessions") Assert.Contains(SessionStore.Read(session).Id.ToString(), result.Output);
        else Assert.Contains("Idle", result.Output);
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(session, "session.json")));
        Assert.False(File.Exists(ChunkStore.PathFor(session, 1)));
        Assert.Equal(2, Run(["--config", configPath, "check-config"]).Code);
        Assert.Equal(2, Run(["--config", configPath], "exit\n").Code);
    }

    [Fact]
    public void ResolvesPathsRelativeToConfigAndReadsEditedPrompts()
    {
        var config = Load();
        Assert.Equal(Path.Combine(directory, "Meetings"), config.MeetingsRoot);
        Assert.Equal(120, config.Settings.ChunkSeconds);
        Assert.Null(config.Settings.Capture.MicrophoneDeviceId);
        Assert.False(config.Settings.Stages.Summary);
        File.WriteAllText(Path.Combine(directory, "prompt.md"), "Edited prompt");
        Assert.Equal("Edited prompt", config.ReadPrompt(config.Settings.Transcription));
    }

    [Fact]
    public void DefaultFfmpegPrefersAdjacentExecutableWithoutChangingExplicitSettings()
    {
        string app = Path.Combine(AppContext.BaseDirectory, "ffmpeg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(app);
        try
        {
            string bundled = Path.Combine(app, "ffmpeg.exe");
            Assert.Equal("ffmpeg", ConfigurationLoader.ResolveFfmpegPath("ffmpeg", app));
            File.WriteAllText(bundled, "test binary; never executed");
            Assert.Equal(bundled, ConfigurationLoader.ResolveFfmpegPath("ffmpeg", app));
            Assert.Equal(bundled, ConfigurationLoader.ResolveFfmpegPath("ffmpeg.exe", app));
            Assert.Equal("C:\\custom\\ffmpeg.exe", ConfigurationLoader.ResolveFfmpegPath("C:\\custom\\ffmpeg.exe", app));
        }
        finally { Directory.Delete(app, recursive: true); }
    }

    [Fact]
    public void InitCreatesUserFilesWithoutOverwritingChanges()
    {
        string path = Path.Combine(directory, "user", "config.json");
        string meetingsRoot = Path.Combine(directory, "recordings");
        Assert.True(ConfigurationBootstrap.Initialize(path, meetingsRoot));

        var settings = ConfigurationLoader.Load(path);
        Assert.Equal(meetingsRoot, settings.MeetingsRoot);
        Assert.False(string.IsNullOrWhiteSpace(settings.ReadPrompt(settings.Settings.Transcription)));
        string summaryPath = Path.Combine(directory, "user", "prompts", "summary.md");
        Assert.True(File.Exists(summaryPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp", SearchOption.AllDirectories));

        string promptPath = Path.Combine(directory, "user", "prompts", "transcription.md");
        File.WriteAllText(promptPath, "Personal edited prompt");
        string editedJson = File.ReadAllText(path).Replace("your-audio-model", "my-model", StringComparison.Ordinal);
        File.WriteAllText(path, editedJson);

        Assert.False(ConfigurationBootstrap.Initialize(path, Path.Combine(directory, "other")));
        Assert.Equal("Personal edited prompt", File.ReadAllText(promptPath));
        Assert.Equal(editedJson, File.ReadAllText(path));
        Assert.Equal("my-model", ConfigurationLoader.Load(path).Settings.Transcription.Model);
    }

    [Fact]
    public void InitRetainsPromptsPresentBeforeFirstRun()
    {
        string root = Path.Combine(directory, "user");
        string promptPath = Path.Combine(root, "prompts", "summary.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        File.WriteAllText(promptPath, "Existing private summary prompt");

        Assert.True(ConfigurationBootstrap.Initialize(Path.Combine(root, "config.json"), Path.Combine(directory, "records")));
        Assert.Equal("Existing private summary prompt", File.ReadAllText(promptPath));
        Assert.True(File.Exists(Path.Combine(root, "prompts", "transcription.md")));
    }

    [Fact]
    public void ConcurrentInitKeepsCompleteConfigurationAndPrompts()
    {
        string root = Path.Combine(directory, "concurrent-user");
        string path = Path.Combine(root, "config.json");
        Parallel.For(0, 8, _ => ConfigurationBootstrap.Initialize(path, Path.Combine(directory, "meetings")));

        var config = ConfigurationLoader.Load(path);
        Assert.False(string.IsNullOrWhiteSpace(config.ReadPrompt(config.Settings.Transcription)));
        Assert.False(string.IsNullOrWhiteSpace(config.ReadPrompt(config.Settings.Summary)));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void DefaultConfigurationPrefersWorkingDirectoryForLegacyInvocations()
    {
        string userPath = Path.Combine(directory, "user", "config.json");
        Assert.Equal(userPath, ConfigurationBootstrap.DefaultConfigurationPath(directory, userPath));
        string legacy = Path.Combine(directory, "config.json");
        File.WriteAllText(legacy, "{}");
        Assert.Equal(legacy, ConfigurationBootstrap.DefaultConfigurationPath(directory, userPath));
    }

    [Fact]
    public void InstalledExecutablesPreferUserConfigAndSourceBuildsRetainLegacyLookup()
    {
        string userPath = Path.Combine(directory, "user", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
        File.WriteAllText(userPath, "{}");
        string legacy = Path.Combine(directory, "config.json");
        File.WriteAllText(legacy, "{}");
        string app = Path.Combine(directory, "installation", "app");
        Directory.CreateDirectory(app);
        string anotherInstall = Path.Combine(directory, "other-installation", "app");
        Directory.CreateDirectory(anotherInstall);
        string projectDirectory = Path.Combine(directory, "source", "MeetTape");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "MeetTape.csproj"), "<Project />");
        string buildOutput = Path.Combine(projectDirectory, "bin", "Release", "net10.0-windows", "win-x64");
        Directory.CreateDirectory(buildOutput);

        Assert.Equal(userPath, ConfigurationBootstrap.DefaultConfigurationPath(directory, userPath, app));
        Assert.Equal(userPath, ConfigurationBootstrap.DefaultConfigurationPath(directory, userPath, anotherInstall));
        Assert.Equal(legacy, ConfigurationBootstrap.DefaultConfigurationPath(directory, userPath, buildOutput));
        File.Delete(userPath);
        Assert.Equal(legacy, ConfigurationBootstrap.DefaultConfigurationPath(directory, userPath, app));
    }

    [Fact]
    public void CliConfigureWithExplicitPathRetainsExistingSettingsOnEnter()
    {
        string path = Path.Combine(directory, "initialized", "config.json");
        var first = Run(["--config", path, "configure"], new string('\n', 17));
        Assert.Equal(0, first.Code);
        Assert.Contains("Created configuration", first.Output);
        var second = Run(["--config", path, "configure"], new string('\n', 17));
        Assert.Equal(0, second.Code);
        Assert.Contains("Configuration unchanged", second.Output);
        Assert.Empty(second.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(341)]
    [InlineData(10801)]
    public void RejectsInvalidChunkDurations(int seconds)
    {
        json["chunkSeconds"] = seconds;
        Assert.Throws<InvalidDataException>(() => Load());
    }

    [Fact]
    public void AcceptsLargestSafeStereoChunkDuration()
    {
        json["chunkSeconds"] = AudioChunker.MaxChunkSeconds;
        Assert.Equal(340, Load().Settings.ChunkSeconds);
    }

    [Theory]
    [InlineData("meetingsRoot")]
    [InlineData("transcription")]
    [InlineData("summary")]
    public void RejectsMissingRequiredFields(string name)
    {
        json.Remove(name);
        Assert.Throws<InvalidDataException>(() => Load());
    }

    [Theory]
    [InlineData("meetingsRoot")]
    [InlineData("capture")]
    [InlineData("stages")]
    [InlineData("transcription")]
    [InlineData("summary")]
    public void RejectsNullFields(string name)
    {
        json[name] = null;
        Assert.Throws<InvalidDataException>(() => Load());
    }

    [Theory]
    [InlineData("http://remote.example/v1/chat/completions")]
    [InlineData("https://user:secret@example.invalid/api")]
    [InlineData("https://example.invalid/api?key=secret")]
    [InlineData("https://example.invalid/api#secret")]
    [InlineData("/v1/chat/completions")]
    [InlineData("file:///meeting.mp3")]
    public void RejectsUnsafeEndpointsWithoutEchoingValues(string endpoint)
    {
        json["transcription"]!["endpoint"] = endpoint;
        var error = Assert.Throws<InvalidDataException>(() => Load());
        Assert.DoesNotContain(endpoint, error.Message);
    }

    [Theory]
    [InlineData("http://localhost:8317/v1/chat/completions")]
    [InlineData("http://127.0.0.1:8317/v1/chat/completions")]
    public void AllowsLocalCompatibleGateway(string endpoint)
    {
        json["transcription"]!["endpoint"] = endpoint;
        Assert.Equal(endpoint, Load().Settings.Transcription.Endpoint);
    }

    [Fact]
    public void RejectsUnknownPropertiesAndNeverEchoesTheirContents()
    {
        json["private-secret-property"] = "private-secret-value";
        var error = Assert.Throws<InvalidDataException>(() => Load());
        Assert.DoesNotContain("private-secret", error.Message);
    }

    [Fact]
    public void RejectsWrongTypesWithoutEchoingContent()
    {
        json["chunkSeconds"] = "private-secret-value";
        var error = Assert.Throws<InvalidDataException>(() => Load());
        Assert.DoesNotContain("private-secret-value", error.Message);
    }

    [Fact]
    public void RejectsEmptyOrMissingEnabledPrompt()
    {
        File.WriteAllText(Path.Combine(directory, "prompt.md"), " ");
        Assert.Throws<InvalidDataException>(() => Load());
        File.Delete(Path.Combine(directory, "prompt.md"));
        Assert.Throws<FileNotFoundException>(() => Load());
    }

    [Fact]
    public void DisabledStagesDoNotRequirePromptFilesOrCredentials()
    {
        json["stages"] = new JsonObject { ["transcribe"] = false, ["summary"] = false };
        File.Delete(Path.Combine(directory, "prompt.md"));
        var result = Run(["--config", WriteConfig(), "check-config"]);
        Assert.Equal(0, result.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("secret\nvalue")]
    public void MissingOrInvalidApiKeyFailsWithoutEchoingIt(string? key)
    {
        var config = Load();
        var error = Assert.Throws<InvalidDataException>(() =>
            ApiKeyProvider.Get(config.Settings.Transcription, _ => key));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void ApiKeyUsesConfiguredEnvironmentVariable()
    {
        var config = Load();
        string key = ApiKeyProvider.Get(config.Settings.Transcription, name =>
            name == config.Settings.Transcription.ApiKeyEnvironmentVariable ? "test-key" : null);
        Assert.Equal("test-key", key);
    }

    [Fact]
    public void InteractiveStatusDoesNotCreateAudioFiles()
    {
        string path = WriteConfig();
        var before = Directory.GetFiles(directory);
        var result = Run(["--config", path], "status\nexit\n");
        Assert.Equal(0, result.Code);
        Assert.Contains("No recording created", result.Output);
        Assert.Contains("Idle", result.Output);
        Assert.Equal(before, Directory.GetFiles(directory));
        Assert.Empty(result.Error);
    }

    [Fact]
    public void StatusWorksWithoutApiKeyButCredentialCheckFails()
    {
        string path = WriteConfig();
        Assert.Equal(0, Run(["--config", path, "status"]).Code);
        Assert.Equal(2, Run(["--config", path, "check-config"]).Code);
    }

    [Fact]
    public void InvalidConfigReturnsDiagnosticWithoutPrivateValues()
    {
        json["private-secret-property"] = "private-secret-value";
        var result = Run(["--config", WriteConfig()]);
        Assert.Equal(2, result.Code);
        Assert.Contains("Invalid configuration JSON", result.Error);
        Assert.DoesNotContain("private-secret", result.Error);
    }

    [Fact]
    public void HelpDoesNotRequireConfigAndBadArgumentsFail()
    {
        Assert.Equal(0, Run(["--help"]).Code);
        Assert.Equal(2, Run(["--config"]).Code);
        Assert.Equal(2, Run(["unknown"]).Code);
    }

    private LoadedConfiguration Load() => ConfigurationLoader.Load(WriteConfig());

    private string WriteConfig()
    {
        string path = Path.Combine(directory, "config.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static (int Code, string Output, string Error) Run(string[] args, string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = Cli.Run(args, new StringReader(input), output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
