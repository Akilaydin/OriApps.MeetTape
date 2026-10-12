using System.Text.Json.Nodes;

namespace MeetTape.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../dist/settings-tests/" + Guid.NewGuid().ToString("N")));
    private string ConfigPath => Path.Combine(root, "config.json");

    public ConfigurationStoreTests() => ConfigurationBootstrap.Initialize(ConfigPath, Path.Combine(root, "Meetings"));

    [Fact]
    public void UnchangedSettingsAndCustomPromptsRemainExactAndKeysStayOutOfFiles()
    {
        string before = File.ReadAllText(ConfigPath);
        string prompt = Path.Combine(root, "prompts", "transcription.md");
        File.WriteAllText(prompt, "My custom prompt");
        Assert.Null(ConfigurationStore.Save(ConfigPath, before, before, new Dictionary<string, string>(),
            (_, _, _) => Assert.Fail("No key entered")));
        var targets = new Dictionary<EnvironmentVariableTarget, string>();
        Assert.Null(ConfigurationStore.Save(ConfigPath, before, before,
            new Dictionary<string, string> { ["MEETTAPE_API_KEY"] = "fixture-secret" },
            (name, key, target) => { Assert.Equal("MEETTAPE_API_KEY", name); targets[target] = key; }));
        Assert.Equal("fixture-secret", targets[EnvironmentVariableTarget.User]);
        Assert.Equal("fixture-secret", targets[EnvironmentVariableTarget.Process]);
        Assert.Equal(2, targets.Count);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Equal("My custom prompt", File.ReadAllText(prompt));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("stages", "summary")]
    [InlineData("transcription", "endpoint")]
    [InlineData("transcription", "apiKeyEnvironmentVariable")]
    public void InvalidSettingsDoNotChangeConfigurationOrKeys(string section, string property)
    {
        string before = File.ReadAllText(ConfigPath);
        var json = JsonNode.Parse(before)!;
        if (section == "stages")
        {
            json[section]!["transcribe"] = false;
            json[section]![property] = true;
        }
        else json[section]![property] = property == "endpoint" ? "http://remote.example" : "NOT=VALID";
        string? error = ConfigurationStore.Save(ConfigPath, before, json.ToJsonString(),
            new Dictionary<string, string> { ["MEETTAPE_API_KEY"] = "fixture-secret" },
            (_, _, _) => Assert.Fail("Invalid settings must not write keys"));
        Assert.NotNull(error);
        Assert.DoesNotContain("fixture-secret", error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("NOT=VALID", "fixture-secret")]
    [InlineData("MEETTAPE_API_KEY", "fixture-secret\ncontrol")]
    [InlineData("MEETTAPE_API_KEY", " ")]
    public void InvalidKeysAreRejectedWithoutExposingTheirValues(string variable, string key)
    {
        string before = File.ReadAllText(ConfigPath);
        string? error = ConfigurationStore.Save(ConfigPath, before, before.Replace("120", "90"),
            new Dictionary<string, string> { [variable] = key }, (_, _, _) => Assert.Fail("Invalid key"));
        Assert.Contains("Invalid API key", error);
        Assert.DoesNotContain("fixture-secret", error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void EnvironmentFailureReturnsSafeDiagnosticAndRetainsPrompts()
    {
        string before = File.ReadAllText(ConfigPath);
        string? error = ConfigurationStore.Save(ConfigPath, before, before,
            new Dictionary<string, string> { ["MEETTAPE_API_KEY"] = "fixture-secret" },
            (_, _, _) => throw new UnauthorizedAccessException("fixture-secret"));
        Assert.Contains("Cannot save API key", error);
        Assert.DoesNotContain("fixture-secret", error);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task ConcurrentSavesNeverLoseSettings()
    {
        string before = File.ReadAllText(ConfigPath);
        using var barrier = new Barrier(2);
        string? Save(string folder)
        {
            var json = JsonNode.Parse(before)!;
            json["meetingsRoot"] = folder;
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return ConfigurationStore.Save(ConfigPath, before, json.ToJsonString(), new Dictionary<string, string>(),
                (_, _, _) => Assert.Fail("No keys entered"));
        }
        var outcomes = await Task.WhenAll(Task.Run(() => Save("FirstFolder")), Task.Run(() => Save("SecondFolder")))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(outcomes, error => error is null);
        Assert.Single(outcomes, error => error?.Contains("changed during editing") == true);
        Assert.Contains(ConfigurationLoader.Load(ConfigPath).Settings.MeetingsRoot, new[] { "FirstFolder", "SecondFolder" });
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(root, true);
}
