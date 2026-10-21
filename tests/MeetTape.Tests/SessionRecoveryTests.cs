namespace MeetTape.Tests;

public sealed class SessionRecoveryTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../dist/recovery-tests/" + Guid.NewGuid().ToString("N")));
    private readonly LoadedConfiguration config;

    public SessionRecoveryTests()
    {
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        config = loaded with { Settings = loaded.Settings with { MeetingsRoot = root } };
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task FailedWithIntactTracksRecoversAndKeepsTrimAndOriginals()
    {
        string directory = Session(RecordingState.Failed, trim: true, duration: 2);
        await WriteAudioAsync(directory, 2, 2);
        string me = Path.Combine(directory, "me.mp3"), them = Path.Combine(directory, "them.mp3");
        byte[] originalMe = File.ReadAllBytes(me), originalThem = File.ReadAllBytes(them);
        byte[] checkpoint = [1, 2, 3, 4];
        File.WriteAllBytes(Path.Combine(directory, "0001-checkpoint.json"), checkpoint);
        var before = SessionStore.Read(directory);

        await SessionRecovery.RecoverAsync(directory, before.Id, [root], "ffmpeg");

        var recovered = SessionStore.Read(directory);
        Assert.Equal(RecordingState.Recorded, recovered.RecordingState);
        Assert.Equal(JobState.Paused, recovered.JobState);
        Assert.Equal(before.DurationSeconds, recovered.DurationSeconds);
        Assert.Null(recovered.Error);
        Assert.Equal(StageState.AwaitingUser, recovered.Stages["trim"].Status);
        Assert.False(ProcessingJobs.Eligible(recovered));
        Assert.True(SessionRow.From(Assert.Single(SessionStore.Discover(root))).NeedsPreparation);
        Assert.Equal(originalMe, File.ReadAllBytes(me));
        Assert.Equal(originalThem, File.ReadAllBytes(them));
        Assert.Equal(checkpoint, File.ReadAllBytes(Path.Combine(directory, "0001-checkpoint.json")));

        using var preparation = new PostRecordingWorkflow("ffmpeg");
        preparation.Begin(directory);
        await preparation.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        preparation.Choose("skip");
        Assert.Equal(StageState.Completed, SessionStore.Read(directory).Stages["trim"].Status);
        Assert.True(ProcessingJobs.Eligible(SessionStore.Read(directory)));
    }

    [Fact]
    public async Task InterruptedWithMissingDurationDerivesItFromFinalizedTracksAndCanResumeProcessing()
    {
        string directory = Session(RecordingState.Interrupted, trim: false, duration: null);
        await WriteAudioAsync(directory, 2, 2);
        var before = SessionStore.Read(directory);
        await SessionRecovery.RecoverAsync(directory, before.Id, [root], "ffmpeg");

        var recovered = SessionStore.Read(directory);
        Assert.Equal(RecordingState.Recorded, recovered.RecordingState);
        Assert.InRange(recovered.DurationSeconds!.Value, 1.999, 2.001);
        Assert.InRange(recovered.Trim.EndSeconds!.Value, 1.999, 2.001);
        Assert.Equal(StageState.Paused, recovered.Stages["transcribe"].Status);
        Assert.True(ProcessingJobs.CanResume(recovered));
        Assert.False(ProcessingJobs.Eligible(recovered));
        using (var jobs = new ProcessingJobs(config, () => false)) jobs.Resume(directory);
        Assert.True(ProcessingJobs.Eligible(SessionStore.Read(directory)));
        Assert.Equal(StageState.Pending, SessionStore.Read(directory).Stages["transcribe"].Status);
        Assert.Equal(RecordingState.Recorded, Assert.Single(SessionStore.Discover(root)).Data!.RecordingState);
    }

    [Fact]
    public async Task ControllerRecoversOnlySelectedSessionAndExposesPausedProcessingAfterRefresh()
    {
        string directory = Session(RecordingState.Failed, trim: false, duration: 2);
        string other = Session(RecordingState.Interrupted, trim: false, duration: null);
        await WriteAudioAsync(directory, 2, 2);
        var data = SessionStore.Read(directory);
        byte[] otherMetadata = File.ReadAllBytes(Path.Combine(other, "session.json"));
        var controller = new DesktopController(config);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RecoverAsync(directory, Guid.NewGuid()));
            Assert.Equal(RecordingState.Failed, SessionStore.Read(directory).RecordingState);
            await controller.RecoverAsync(directory, data.Id);
            var snapshot = await controller.ReadAsync();
            var recovered = snapshot.Sessions.Single(s => s.Directory == directory).Data!;
            Assert.Equal(RecordingState.Recorded, recovered.RecordingState);
            Assert.True(ProcessingJobs.CanResume(recovered));
            Assert.False(ProcessingJobs.Eligible(recovered));
            Assert.Equal(otherMetadata, File.ReadAllBytes(Path.Combine(other, "session.json")));
        }
        finally { await controller.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("different-duration")]
    [InlineData("wrong-metadata")]
    [InlineData("wrong-trim")]
    [InlineData("invalid-header")]
    [InlineData("corrupted-payload")]
    [InlineData("wrong-identity")]
    public async Task DamagedOrUnmatchedInputsNeverChangeMetadataOrOtherFiles(string scenario)
    {
        string directory = Session(RecordingState.Failed, trim: false, duration: 2);
        await WriteAudioAsync(directory, 2, scenario == "different-duration" ? 1 : 2);
        var old = SessionStore.Read(directory);
        if (scenario == "wrong-metadata")
            SessionStore.Save(directory, old with { DurationSeconds = 3 });
        if (scenario == "wrong-trim")
            SessionStore.Save(directory, old with { Trim = new(0, 3) });
        string path = Path.Combine(directory, "them.mp3");
        if (scenario == "missing") File.Delete(path);
        if (scenario == "empty") File.WriteAllBytes(path, []);
        if (scenario == "invalid-header") File.WriteAllBytes(path, new byte[100]);
        if (scenario == "truncated")
        {
            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..(bytes.Length - 2000)]);
        }
        if (scenario == "corrupted-payload")
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes.AsSpan(5000, 5000).Fill(0);
            File.WriteAllBytes(path, bytes);
        }
        string checkpoint = Path.Combine(directory, "checkpoint.json");
        File.WriteAllBytes(checkpoint, [4, 5, 6]);
        byte[] beforeJson = File.ReadAllBytes(Path.Combine(directory, "session.json"));
        byte[] beforeCheckpoint = File.ReadAllBytes(checkpoint);
        byte[]? beforeThem = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Guid requestedId = scenario == "wrong-identity" ? Guid.NewGuid() : old.Id;

        Exception failure = await Record.ExceptionAsync(() => SessionRecovery.RecoverAsync(directory, requestedId, [root], "ffmpeg"))
            ?? throw new Exception("Damaged recording unexpectedly recovered.");

        Assert.True(failure is InvalidDataException or InvalidOperationException);
        Assert.Equal(beforeJson, File.ReadAllBytes(Path.Combine(directory, "session.json")));
        Assert.Equal(beforeCheckpoint, File.ReadAllBytes(checkpoint));
        Assert.Equal(beforeThem, File.Exists(path) ? File.ReadAllBytes(path) : null);
        Assert.Equal(RecordingState.Failed, SessionStore.Read(directory).RecordingState);
    }

    [Fact]
    public async Task InterruptedWithoutFinalizedMp3HeaderStaysInterruptedEvenWhenAudioDecodes()
    {
        string directory = Session(RecordingState.Interrupted, trim: false, duration: null);
        await WriteAudioAsync(directory, 2, 2);
        string file = Path.Combine(directory, "them.mp3");
        byte[] bytes = File.ReadAllBytes(file);
        // Break only the finalized length field. Decoding can still succeed.
        int pos = bytes.AsSpan().IndexOf("Info"u8);
        if (pos < 0) pos = bytes.AsSpan().IndexOf("Xing"u8);
        Assert.True(pos > 0);
        bytes[pos + 15]--;
        File.WriteAllBytes(file, bytes);

        var old = SessionStore.Read(directory);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionRecovery.RecoverAsync(directory, old.Id, [root], "ffmpeg"));
        Assert.Contains("length", failure.Message);
        Assert.Equal(RecordingState.Interrupted, SessionStore.Read(directory).RecordingState);
    }

    [Fact]
    public async Task FailedWithoutSavedDurationCannotBeRecovered()
    {
        string directory = Session(RecordingState.Failed, trim: false, duration: null);
        await WriteAudioAsync(directory, 2, 2);
        var old = SessionStore.Read(directory);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionRecovery.RecoverAsync(directory, old.Id, [root], "ffmpeg"));
        Assert.Contains("no saved duration", failure.Message);
        Assert.Equal(RecordingState.Failed, SessionStore.Read(directory).RecordingState);
    }

    private string Session(RecordingState state, bool trim, double? duration)
    {
        var start = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var data = new SessionData
        {
            Id = Guid.NewGuid(), StartedAtUtc = start, EndedAtUtc = duration is { } seconds ? start.AddSeconds(seconds) : null,
            DurationSeconds = duration, RecordingState = state, JobState = JobState.Paused, Error = "Capture interrupted.",
            Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
            Processing = new(120, config.Settings.Transcription, config.Settings.Summary, config.Directory),
            Stages = new()
            {
                ["trim"] = new(trim, trim ? StageState.AwaitingUser : StageState.Disabled),
                ["prepare"] = new(true, StageState.Pending),
                ["transcribe"] = new(true, StageState.Pending),
                ["assemble"] = new(true, StageState.Pending),
                ["summary"] = new(false, StageState.Disabled)
            },
            Trim = new(0, duration)
        };
        var created = SessionStore.Create(root, data);
        created.Lease.Dispose();
        return created.Directory;
    }

    private static async Task WriteAudioAsync(string directory, int meSeconds, int themSeconds)
    {
        foreach (var track in new[] { ("me.mp3", meSeconds), ("them.mp3", themSeconds) })
        {
            using var encoder = new FfmpegEncoder("ffmpeg", Path.Combine(directory, track.Item1));
            await encoder.Input.WriteAsync(new byte[SampleRateBytes * track.Item2]);
            await encoder.CompleteAsync();
        }
    }

    private const int SampleRateBytes = 48000 * sizeof(float);

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
