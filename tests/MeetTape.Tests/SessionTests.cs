using System.Text.Json.Nodes;
using NAudio.Wave;

namespace MeetTape.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetTapeSessions-" + Guid.NewGuid().ToString("N"));
    private readonly LoadedConfiguration configuration;

    public SessionTests()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        configuration = loaded with { Settings = loaded.Settings with { MeetingsRoot = root } };
    }

    [Fact]
    public void EligibleLegacyMetadataIsNeverCachedEvenIfCallerStopsAfterDiscovery()
    {
        var created = SessionStore.Create(root, Data() with
            { RecordingState = RecordingState.Recorded, JobState = JobState.Completed });
        created.Lease.Dispose();
        var cache = new Dictionary<string, (DateTime LastWriteTimeUtc, long Length)>();
        Assert.Single(SessionStore.Discover(root, cache, ProcessingJobs.Eligible));
        Assert.Empty(cache);
        using var locked = new FileStream(Path.Combine(created.Directory, "session.json"), FileMode.Open,
            FileAccess.Read, FileShare.None);
        Assert.Null(Assert.Single(SessionStore.Discover(root, cache, ProcessingJobs.Eligible)).Data);
    }

    [Fact]
    public void CompletedDiscoverySkipsReadsButFindsChangesAndRetainsRestartRecovery()
    {
        var data = Data() with { RecordingState = RecordingState.Recorded, JobState = JobState.Completed };
        var created = SessionStore.Create(root, data);
        created.Lease.Dispose();
        var cache = new Dictionary<string, (DateTime LastWriteTimeUtc, long Length)>(StringComparer.OrdinalIgnoreCase);
        Assert.Single(SessionStore.Discover(root, cache));
        string metadata = Path.Combine(created.Directory, "session.json");
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(SessionStore.Discover(root, cache));
            Assert.Null(Assert.Single(SessionStore.Discover(root)).Data);
        }
        Assert.Single(SessionStore.Discover(root));
        Assert.Single(SessionStore.Discover(root, new()));

        SessionStore.Save(created.Directory, data with { Title = "Resumed recording", JobState = JobState.NotStarted });
        Assert.Equal(JobState.NotStarted, Assert.Single(SessionStore.Discover(root, cache)).Data!.JobState);
        Assert.Empty(cache);
        var interrupted = SessionStore.Create(root, Data());
        interrupted.Lease.Dispose();
        var recovered = SessionStore.Discover(root, cache).Single(s => s.Directory == interrupted.Directory);
        Assert.Equal(RecordingState.Interrupted, recovered.Data!.RecordingState);
        Assert.Equal(JobState.Paused, recovered.Data.JobState);

        SessionStore.Save(created.Directory, data);
        SessionStore.Discover(root, cache);
        Directory.Delete(created.Directory, recursive: true);
        SessionStore.Discover(root, cache);
        Assert.Empty(cache);
    }

    [Fact]
    public void MetadataExistsBeforeSourcesStartAndStopPreservesIdentityAndOriginals()
    {
        string? directory = null;
        Guid id = Guid.Empty;
        var me = new Source();
        var them = new Source();
        var recorder = new Mp3Recorder(configuration, () => new(me, them));
        using var capture = recorder.Capture;
        me.BeforeStart = () =>
        {
            directory = capture.RecordingDirectory;
            var saved = SessionStore.Read(directory!);
            Assert.Equal(RecordingState.Recording, saved.RecordingState);
            Assert.Null(saved.Title);
            id = saved.Id;
        };
        capture.Start();
        Assert.Equal(RecordingState.Recording, Assert.Single(SessionStore.Discover(root)).Data!.RecordingState);
        capture.Stop();
        var completed = SessionStore.Read(directory!);
        Assert.Equal(id, completed.Id);
        Assert.Equal(RecordingState.Recorded, completed.RecordingState);
        Assert.Equal(capture.Status.Duration.TotalSeconds, completed.DurationSeconds);
        Assert.Equal(StageState.AwaitingUser, completed.Stages["trim"].Status);
        Assert.Equal(StageState.Disabled, completed.Stages["summary"].Status);
        string mePath = Path.Combine(directory!, "me.mp3");
        byte[] original = File.ReadAllBytes(mePath);
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(mePath, "overwrite"));
        SessionStore.Save(directory!, completed with { Title = "later" });
        Assert.Equal(original, File.ReadAllBytes(mePath));
    }

    [Fact]
    public void FailedMetadataUpdateRetainsPreviousJsonAndAudioWithDiagnostic()
    {
        var recorder = new Mp3Recorder(configuration, () => new(new Source(), new Source()));
        using var capture = recorder.Capture;
        capture.Start();
        string directory = capture.RecordingDirectory!;
        string path = Path.Combine(directory, "session.json");
        byte[] initial = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        capture.Stop();
        Assert.Contains("session.json", capture.Status.Error!);
        Assert.Equal(initial, File.ReadAllBytes(path));
        Assert.True(File.Exists(Path.Combine(directory, "me.mp3")));
        Assert.True(File.Exists(Path.Combine(directory, "them.mp3")));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task DeviceFailureIsSavedAsFailedCaptureAndPausedJob()
    {
        var me = new Source();
        var recorder = new Mp3Recorder(configuration, () => new(me, new Source()));
        using var capture = recorder.Capture;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Failed += _ => finished.TrySetResult();
        capture.Start();
        me.Fail(new System.Runtime.InteropServices.COMException("private content", unchecked((int)0x88890004)));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = SessionStore.Read(capture.RecordingDirectory!);
        Assert.Equal(RecordingState.Failed, saved.RecordingState);
        Assert.Equal(JobState.Paused, saved.JobState);
        Assert.Contains("88890004", saved.Error!);
        Assert.DoesNotContain("private content", saved.Error!);
    }

    [Fact]
    public void InterruptedUnnamedRecordingIsDiscoveredWithoutChangingOriginalBytes()
    {
        var data = Data();
        var created = SessionStore.Create(root, data);
        File.WriteAllBytes(Path.Combine(created.Directory, "me.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(created.Directory, "them.mp3"), [4, 5, 6]);
        created.Lease.Dispose();
        var recovered = Assert.Single(SessionStore.Discover(root));
        Assert.Equal(data.Id, recovered.Data!.Id);
        Assert.Null(recovered.Data.Title);
        Assert.Equal(RecordingState.Interrupted, recovered.Data.RecordingState);
        Assert.Equal(JobState.Paused, recovered.Data.JobState);
        Assert.True(recovered.MePresent && recovered.ThemPresent);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(created.Directory, "me.mp3")));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(created.Directory, "them.mp3")));
        Assert.Equal(RecordingState.Interrupted, SessionStore.Read(created.Directory).RecordingState);
    }

    [Fact]
    public void DirectoryCollisionNeverChangesExistingFiles()
    {
        var data = Data();
        var created = SessionStore.Create(root, data);
        using var lease = created.Lease;
        byte[] original = File.ReadAllBytes(Path.Combine(created.Directory, "session.json"));
        Assert.Throws<RecordingException>(() => SessionStore.Create(root, data));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(created.Directory, "session.json")));
    }

    [Fact]
    public void UnknownMetadataFieldsAndOtherSchemasAreRejectedWithoutChanges()
    {
        var created = SessionStore.Create(root, Data());
        created.Lease.Dispose();
        string path = Path.Combine(created.Directory, "session.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["customNote"] = "preserve";
        File.WriteAllText(path, json.ToJsonString());
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => SessionStore.Read(created.Directory));
        Assert.Equal(before, File.ReadAllBytes(path));
        SessionStore.Save(created.Directory, Data() with { SchemaVersion = 2 });
        Assert.Throws<InvalidDataException>(() => SessionStore.Read(created.Directory));
    }

    [Fact]
    public void InvalidMetadataProducesSafeDiagnosticAndRetainsAudio()
    {
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        string directory = Path.Combine(root, "broken");
        File.WriteAllText(Path.Combine(directory, "session.json"), "private meeting content");
        File.WriteAllBytes(Path.Combine(directory, "me.mp3"), [1, 2, 3]);
        var session = Assert.Single(SessionStore.Discover(root));
        Assert.Null(session.Data);
        Assert.DoesNotContain("private meeting content", session.Diagnostic!);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
    }

    [Fact]
    public void SnapshotContainsEnvironmentVariableNameButNoKeyValue()
    {
        const string name = "MEETTAPE_UNIT_TEST_SECRET";
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "test-only-secret-value");
            var data = Data();
            data = data with { Processing = data.Processing with { Transcription = data.Processing.Transcription with { ApiKeyEnvironmentVariable = name } } };
            var created = SessionStore.Create(root, data);
            using var lease = created.Lease;
            string json = File.ReadAllText(Path.Combine(created.Directory, "session.json"));
            Assert.Contains(name, json);
            Assert.DoesNotContain("test-only-secret-value", json);
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    private SessionData Data() => new()
    {
        Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow,
        Capture = new(null, null, new(48000, 32, 2, "float"), new(48000, 32, 2, "float")),
        Processing = new(120, configuration.Settings.Transcription, configuration.Settings.Summary, configuration.Directory),
        Stages = new() { ["transcribe"] = new(true, StageState.Pending) }
    };

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private sealed class Source : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public Action? BeforeStart { get; set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Fail(Exception error) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void StartRecording() => BeforeStart?.Invoke();
        public void StopRecording() { }
        public void Dispose() { }
    }
}
