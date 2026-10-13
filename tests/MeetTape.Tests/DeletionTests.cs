using NAudio.Wave;

namespace MeetTape.Tests;

public sealed class DeletionTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../dist/delete-tests/" + Guid.NewGuid().ToString("N")));
    private readonly LoadedConfiguration config;
    private string Meetings => Path.Combine(root, "Meetings");
    public DeletionTests()
    {
        ConfigurationBootstrap.Initialize(Path.Combine(root, "config.json"), Meetings);
        config = ConfigurationLoader.Load(Path.Combine(root, "config.json"));
    }

    private string Session(JobState state = JobState.Canceled)
    {
        var data = new SessionData
        {
            Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 2,
            RecordingState = RecordingState.Recorded, JobState = state,
            Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
            Processing = new(120, config.Settings.Transcription, config.Settings.Summary, config.Directory),
            Stages = new() { ["transcribe"] = new(true, StageState.Pending) }
        };
        var created = SessionStore.Create(Meetings, data); created.Lease.Dispose();
        foreach (string name in new[] { "me.mp3", "them.mp3", "transcription.md", "summary.md" })
            File.WriteAllBytes(Path.Combine(created.Directory, name), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(created.Directory, "chunks"));
        File.WriteAllText(Path.Combine(created.Directory, "chunks", "checkpoint.json"), "Fixture checkpoint");
        return created.Directory;
    }

    [Fact]
    public async Task ControllerMovesWholeCanceledFolderKeepsOtherMeetingAndRejectsStaleIdentity()
    {
        string directory = Session(), other = Session();
        var originals = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
        byte[] otherMetadata = File.ReadAllBytes(Path.Combine(other, "session.json"));
        var controller = new DesktopController(config) { RecycleRecording = (source, _) =>
        {
            string bin = Path.Combine(root, "Fixture bin"); Directory.CreateDirectory(bin);
            string moved = Path.Combine(bin, "Recording"); Directory.Move(source, moved); return moved;
        } };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DeleteRecordingAsync(directory, Guid.NewGuid()));
            await controller.DeleteRecordingAsync(directory, SessionStore.Read(directory).Id);
            Assert.False(Directory.Exists(directory));
            foreach (var file in originals) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(root, "Fixture bin", "Recording", file.Key)));
            Assert.Equal(otherMetadata, File.ReadAllBytes(Path.Combine(other, "session.json")));
            Assert.Equal(other, Assert.Single((await controller.ReadAsync()).Sessions).Directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DeleteRecordingAsync(directory, Guid.NewGuid()));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(JobState.NotStarted)]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Paused)]
    public void UncanceledWorkIsNeverRecycled(JobState state)
    {
        string directory = Session(state);
        var data = SessionStore.Read(directory);
        byte[] metadata = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(directory, data.Id, [Meetings], root, (_, _) => throw new Exception("Must not recycle")));
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(directory, "session.json")));
        Assert.True(File.Exists(Path.Combine(directory, "me.mp3")));
    }

    [Theory]
    [InlineData(RecordingState.Failed)]
    [InlineData(RecordingState.Interrupted)]
    public async Task FailedAndInterruptedRecordingsUseExistingSafeRecyclePath(RecordingState state)
    {
        string directory = Session(JobState.Paused), other = Session();
        var data = SessionStore.Read(directory) with { RecordingState = state };
        SessionStore.Save(directory, data);
        Assert.True(SessionRemoval.CanDelete(data));
        Assert.False(SessionRemoval.CanDelete(data with { JobState = JobState.Running }));
        Assert.False(SessionRemoval.CanDelete(data with { Stages = new(data.Stages) { ["prepare"] = new(true, StageState.Running) } }));
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
        var controller = new DesktopController(config) { RecycleRecording = (source, _) =>
        {
            string destination = Path.Combine(root, "Fixture bin");
            Directory.Move(source, destination); return destination;
        } };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DeleteRecordingAsync(directory, Guid.NewGuid()));
            await controller.DeleteRecordingAsync(directory, data.Id);
            Assert.False(Directory.Exists(directory));
            Assert.True(Directory.Exists(other));
            foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(root, "Fixture bin", file.Key)));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Fact]
    public void LeaseAndRecycleFailuresRetainAllAudioAndMetadataAndAllowRetry()
    {
        string directory = Session(); var id = SessionStore.Read(directory).Id;
        byte[] metadata = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        using (var lease = new FileStream(Path.Combine(directory, ".session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(directory, id, [Meetings], root, (_, _) => throw new Exception("Must not recycle")));
        foreach (Exception failure in new Exception[] { new IOException("Fixture bin unavailable"), new UnauthorizedAccessException() })
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(directory, id, [Meetings], root, (_, _) => throw failure));
        Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(directory, "session.json")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
    }

    [Fact]
    public void RootForeignNestedAndConfigurationFoldersAreRejectedBeforeRecycle()
    {
        string directory = Session(); Guid id = SessionStore.Read(directory).Id;
        foreach (string path in new[] { Meetings, root, Path.Combine(directory, "chunks"), Path.Combine(root, "foreign") })
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(path, id, [Meetings], root, (_, _) => throw new Exception("Must not recycle")));
        Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(directory, id, [Meetings], directory, (_, _) => throw new Exception("Must not recycle config")));
        Assert.True(File.Exists(Path.Combine(root, "config.json")));
        Assert.True(File.Exists(Path.Combine(directory, "me.mp3")));
    }

    [Fact]
    public async Task LiveCaptureRejectsDeletionEvenWhenTheSelectedRowWasPreviouslyIdle()
    {
        string directory = Session(); Guid id = SessionStore.Read(directory).Id;
        using var capture = new CaptureCoordinator(() => new(new Source(), new Source()));
        var controller = new DesktopController(config, capture) { RecycleRecording = (_, _) => throw new Exception("Must not recycle during capture") };
        try
        {
            await controller.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DeleteRecordingAsync(directory, id));
            Assert.True(capture.Status.IsActive);
            Assert.True(File.Exists(Path.Combine(directory, "me.mp3")));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Fact]
    public async Task FolderContainingACurrentConfiguredPromptCannotBeRecycled()
    {
        string directory = Session(); Guid id = SessionStore.Read(directory).Id;
        var updated = config with { Settings = config.Settings with { Transcription = config.Settings.Transcription with { PromptFile = Path.Combine(directory, "transcription.md") } } };
        bool called = false;
        var controller = new DesktopController(updated) { RecycleRecording = (source, _) =>
        {
            called = true;
            string moved = Path.Combine(root, "Fixture bin"); Directory.Move(source, moved); return moved;
        } };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DeleteRecordingAsync(directory, id));
            Assert.False(called);
            Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Fact]
    public void CompletedMeetingCanBeRecycledAndAncestorReplacementIsBlockedDuringTheMove()
    {
        string directory = Session(JobState.Completed);
        var data = SessionStore.Read(directory);
        SessionStore.Save(directory, data with { Stages = new() { ["transcribe"] = new(true, StageState.Completed), ["assemble"] = new(true, StageState.Completed) } });
        string bin = Path.Combine(root, "Fixture bin"); Directory.CreateDirectory(bin);
        SessionRemoval.Recycle(directory, data.Id, [Meetings], root, (source, _) =>
        {
            Assert.Throws<IOException>(() => Directory.Move(Meetings, Meetings + "-swapped"));
            string moved = Path.Combine(bin, "completed"); Directory.Move(source, moved); return moved;
        });
        Assert.True(Directory.Exists(Meetings));
        Assert.Empty(SessionStore.Discover(Meetings));
        Assert.Equal(data.Id, SessionStore.Read(Path.Combine(bin, "completed")).Id);
    }

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private sealed class Source : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
    }
}
