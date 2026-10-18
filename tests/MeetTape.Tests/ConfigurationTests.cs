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
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("directory")]
    public void SessionDiscoveryDoesNotReadPromptsOrAlterFinalizedRecordings(string promptState)
    {
        using var fixture = new JobTests();
        string session = fixture.Session();
        byte[] metadata = File.ReadAllBytes(Path.Combine(session, "session.json"));
        string prompt = Path.Combine(directory, "prompt.md");
        File.Delete(prompt);
        if (promptState == "empty") File.WriteAllText(prompt, " ");
        if (promptState == "directory") Directory.CreateDirectory(prompt);
        var found = Assert.Single(SessionStore.Discover(Path.GetDirectoryName(session)!));
        Assert.Equal(SessionStore.Read(session).Id, found.Data!.Id);
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(session, "session.json")));
        Assert.False(File.Exists(ChunkStore.PathFor(session, 1)));
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

    [Theory]
    [InlineData("Артём О", true)]
    [InlineData("", false)]
    [InlineData("bad\nname", false)]
    public void MeDisplayNameCanBeCustomizedButRejectsInvalidNames(string displayName, bool valid)
    {
        json["meDisplayName"] = displayName;
        if (valid) Assert.Equal(displayName, Load().Settings.MeDisplayName);
        else Assert.Throws<InvalidDataException>(() => Load());
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
        string transcription = settings.ReadPrompt(settings.Settings.Transcription);
        string summary = settings.ReadPrompt(settings.Settings.Summary);
        Assert.Equal("""
            Transcribe all audible speech in this mono audio chunk in its original language. The main language is Russian; preserve English technical terms.
            Preserve spoken wording, short acknowledgements, repetitions, self-corrections, numbers, questions, and negations. Do not summarize, translate, rewrite, or complete a phrase cut off by a clip boundary.
            Use [inaudible] only for an audible word or passage you cannot understand. Do not replace quiet or unintelligible speech with silence.
            For each short spoken phrase, output one physical line in this exact format:
            [MM:SS] SOURCE: spoken text
            Replace SOURCE with the recording source specified below. Use the approximate start time of that phrase relative to this audio chunk. Times must stay within the supplied audio and increase chronologically. Put every timestamp at the start of a new line; never place a timestamp inside the spoken text.
            If the entire audio chunk has no audible speech, output exactly [NO_SPEECH] on its own, with no timestamp or source label.
            Return only the transcription.
            """.ReplaceLineEndings("\n"), transcription.ReplaceLineEndings("\n").TrimEnd());
        Assert.Contains("summary in Russian", summary);
        Assert.Contains("[inaudible]", transcription);
        Assert.Contains("[MM:SS] Me/Them/Unknown: text", ChatApiClient.SourceInstruction);
        Assert.Contains(ChatApiClient.NoSpeechMarker, ChatApiClient.SourceInstruction);
        foreach (string source in new[] { "Me", "Them" })
        {
            string instruction = transcription + ChatApiClient.InstructionFor(source);
            Assert.Contains("[MM:SS] SOURCE: spoken text", instruction);
            Assert.Contains($"This is MONO audio from the {source} recording source.", instruction);
            Assert.Contains($"Label every utterance as [MM:SS] {source}: text", instruction);
            Assert.Contains("Do not use this marker for unintelligible speech.", instruction);
        }
        Assert.DoesNotMatch(@"[\p{IsCyrillic}\p{IsCJKUnifiedIdeographs}]", transcription + summary + ChatApiClient.SourceInstruction);
        string summaryPath = Path.Combine(directory, "user", "prompts", "summary.md");
        Assert.True(File.Exists(summaryPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp", SearchOption.AllDirectories));

        string promptPath = Path.Combine(directory, "user", "prompts", "transcription.md");
        File.WriteAllText(promptPath, "Personal edited prompt");
        File.WriteAllText(summaryPath, "Personal summary instructions — сохранить мой текст.");
        byte[] summaryBytes = File.ReadAllBytes(summaryPath);
        string editedJson = File.ReadAllText(path).Replace("your-audio-model", "my-model", StringComparison.Ordinal);
        File.WriteAllText(path, editedJson);

        Assert.False(ConfigurationBootstrap.Initialize(path, Path.Combine(directory, "other")));
        Assert.Equal("Personal edited prompt", File.ReadAllText(promptPath));
        Assert.Equal(summaryBytes, File.ReadAllBytes(summaryPath));
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
    public void UserConfigurationAlwaysLivesOutsideReplaceableApplicationBinaries()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MeetTape", "config.json"), ConfigurationBootstrap.UserConfigurationPath);
        Assert.False(ConfigurationBootstrap.UserConfigurationPath.StartsWith(AppContext.BaseDirectory,
            StringComparison.OrdinalIgnoreCase));
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
        Assert.Throws<InvalidDataException>(() => Load());
    }

    [Fact]
    public void DisabledStagesDoNotRequirePromptFilesOrCredentials()
    {
        json["stages"] = new JsonObject { ["transcribe"] = false, ["summary"] = false };
        File.Delete(Path.Combine(directory, "prompt.md"));
        var loaded = Load();
        Assert.False(loaded.Settings.Stages.Transcribe);
        Assert.False(loaded.Settings.Stages.Summary);
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
            ApiKeyProvider.Get(config.Settings.Transcription, (_, _) => key));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void ApiKeyUsesConfiguredEnvironmentVariable()
    {
        var config = Load();
        string key = ApiKeyProvider.Get(config.Settings.Transcription, (name, _) =>
            name == config.Settings.Transcription.ApiKeyEnvironmentVariable ? "test-key" : null);
        Assert.Equal("test-key", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingProcessKeyUsesUserEnvironmentWithoutReadingRealKeys(string? processKey)
    {
        var config = Load();
        var targets = new List<EnvironmentVariableTarget>();
        string key = ApiKeyProvider.Get(config.Settings.Transcription, (name, target) =>
        {
            Assert.Equal(config.Settings.Transcription.ApiKeyEnvironmentVariable, name);
            targets.Add(target);
            return target == EnvironmentVariableTarget.Process ? processKey : "fixture-user-key";
        });
        Assert.Equal("fixture-user-key", key);
        Assert.Equal(new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User }, targets);
    }

    [Fact]
    public void ExplicitProcessKeyRetainsPriorityAndInvalidKeyNeverLeaks()
    {
        var api = Load().Settings.Transcription;
        Assert.Equal("fixture-process-key", ApiKeyProvider.Get(api, (_, target) =>
            target == EnvironmentVariableTarget.Process ? "fixture-process-key" : throw new Exception("No User lookup expected")));
        var error = Assert.Throws<InvalidDataException>(() => ApiKeyProvider.Get(api, (_, target) =>
            target == EnvironmentVariableTarget.Process ? "fixture-secret\ninvalid" : throw new Exception("No User lookup expected")));
        Assert.Contains(api.ApiKeyEnvironmentVariable, error.Message);
        Assert.DoesNotContain("fixture-secret", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureConfigurationRetainsValidationWithoutRequiringPromptFiles(bool empty)
    {
        string prompt = Path.Combine(directory, "prompt.md");
        if (empty) File.WriteAllText(prompt, " "); else File.Delete(prompt);
        string path = WriteConfig();
        Assert.Throws<InvalidDataException>(() => ConfigurationLoader.Load(path));
        Assert.True(ConfigurationLoader.LoadForCapture(path).Settings.Stages.Transcribe);
        json["capture"] = new JsonObject { ["ffmpegPath"] = "" };
        Assert.Throws<InvalidDataException>(() => ConfigurationLoader.LoadForCapture(WriteConfig()));
    }

    private LoadedConfiguration Load() => ConfigurationLoader.Load(WriteConfig());

    private string WriteConfig()
    {
        string path = Path.Combine(directory, "config.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
