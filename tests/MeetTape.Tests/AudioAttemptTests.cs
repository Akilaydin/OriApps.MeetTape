namespace MeetTape.Tests;

public sealed class AudioAttemptTests : IDisposable
{
    private readonly ChunkingTests fixture = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialFailureDiscardsOnlyTheNewAttemptAndRetryKeepsSelectedFiles(bool chunking)
    {
        string directory = await fixture.SessionAsync();
        var data = SessionStore.Read(directory);
        string processed = Path.Combine(directory, "processed");
        string them = Path.Combine(directory, "them.mp3");
        byte[] originalThem = File.ReadAllBytes(them);
        if (chunking)
        {
            var selected = await new AudioChunker("ffmpeg").PrepareAsync(directory, data, default);
            SessionStore.Save(directory, data = data with { Audio = selected });
        }
        else
        {
            var selected = await new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1, 4, 5, "manual", default);
            SessionStore.Save(directory, data = data with { Trim = selected });
            File.SetAttributes(them, FileAttributes.Normal); File.WriteAllText(them, "invalid MP3 fixture");
            File.SetAttributes(them, FileAttributes.ReadOnly);
        }
        string checkpoint = Path.Combine(directory, "chunks", "fixture.json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!); File.WriteAllText(checkpoint, "paid fixture");
        var retained = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
        string[] selectedDirectories = Directory.GetDirectories(processed);
        if (chunking)
            await Assert.ThrowsAsync<InvalidDataException>(() => new AudioChunker("ffmpeg").PrepareAsync(directory, data with { DurationSeconds = 6 }, default));
        else
            await Assert.ThrowsAsync<RecordingException>(() => new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1.5, 4, 5, "manual", default));
        Assert.Equal(selectedDirectories, Directory.GetDirectories(processed));
        foreach (var file in retained) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(directory, file.Key)));

        if (chunking)
        {
            var retry = await new AudioChunker("ffmpeg").PrepareAsync(directory, data, default);
            Assert.All(retry.Chunks, chunk => Assert.True(File.Exists(Path.Combine(directory, chunk.RelativePath))));
        }
        else
        {
            File.SetAttributes(them, FileAttributes.Normal); File.WriteAllBytes(them, originalThem); File.SetAttributes(them, FileAttributes.ReadOnly);
            var retry = await new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1.5, 4, 5, "manual", default);
            Assert.True(File.Exists(Path.Combine(directory, retry.RelativeDirectory!, "me.mp3")));
        }
        Assert.Equal(selectedDirectories.Length + 1, Directory.GetDirectories(processed).Length);
        Assert.Equal("paid fixture", File.ReadAllText(checkpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterAnMp3WriteLeavesNoAttemptAndAllowsRetry(bool chunking)
    {
        string directory = await fixture.SessionAsync();
        var data = SessionStore.Read(directory);
        var originals = new[] { "me.mp3", "them.mp3", "session.json" }.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(directory, name)));
        string processed = Path.Combine(directory, "processed"); Directory.CreateDirectory(processed);
        using var canceled = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(processed, "*.mp3")
        { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        int written = 0;
        void CancelAfterWrite(object sender, FileSystemEventArgs args)
        {
            try
            {
                if (new FileInfo(args.FullPath).Length > 0 && Interlocked.CompareExchange(ref written, 1, 0) == 0) canceled.Cancel();
            }
            catch (IOException) { }
        }
        watcher.Created += CancelAfterWrite; watcher.Changed += CancelAfterWrite; watcher.EnableRaisingEvents = true;
        if (chunking)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AudioChunker("ffmpeg").PrepareAsync(directory, data, canceled.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1, 4, 5, "manual", canceled.Token));
        watcher.EnableRaisingEvents = false;
        Assert.Equal(1, written);
        Assert.Empty(Directory.GetDirectories(processed));
        foreach (var file in originals) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(directory, file.Key)));
        if (chunking) _ = await new AudioChunker("ffmpeg").PrepareAsync(directory, data, default);
        else _ = await new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1, 4, 5, "manual", default);
        Assert.Single(Directory.GetDirectories(processed));
    }

    [Fact]
    public async Task SavedAttemptsAreNeverDiscardedIncludingEquivalentSelectedPaths()
    {
        string directory = await fixture.SessionAsync();
        var data = SessionStore.Read(directory);
        var trim = await new AudioTrimmer("ffmpeg").ApplyAsync(directory, 1, 4, 5, "manual", default);
        var audio = await new AudioChunker("ffmpeg").PrepareAsync(directory, data with { Trim = trim }, default);
        SessionStore.Save(directory, data with
        {
            Trim = trim with { RelativeDirectory = "processed/../" + trim.RelativeDirectory },
            Audio = audio with { RelativeDirectory = audio.RelativeDirectory + "/" }
        });
        var retained = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        AudioTrimmer.DiscardAttempt(directory, trim.RelativeDirectory!);
        AudioTrimmer.DiscardAttempt(directory, audio.RelativeDirectory);
        foreach (var file in retained) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSelectionPersistenceDiscardsCompletedAttemptAndCanRetry(bool chunking)
    {
        string directory = await fixture.SessionAsync();
        var data = SessionStore.Read(directory);
        if (!chunking) SessionStore.Save(directory, data with { Stages = new(data.Stages) { ["trim"] = new(true, StageState.Pending) } });
        var originals = new[] { "me.mp3", "them.mp3" }.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(directory, name)));
        string processed = Path.Combine(directory, "processed"); Directory.CreateDirectory(processed);
        string metadata = Path.Combine(directory, "session.json");
        using var watcher = new FileSystemWatcher(processed, "*.mp3") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        int written = 0;
        void RejectSelection(object sender, FileSystemEventArgs args)
        {
            try
            {
                if (new FileInfo(args.FullPath).Length > 0 && Interlocked.CompareExchange(ref written, 1, 0) == 0)
                    File.SetAttributes(metadata, FileAttributes.ReadOnly);
            }
            catch (IOException) { }
        }
        watcher.Created += RejectSelection; watcher.Changed += RejectSelection;
        using var handler = new FixtureApi(); using var http = new HttpClient(handler);
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var config = ConfigurationLoader.Load(example);
        config = config with { Settings = config.Settings with { MeetingsRoot = Path.GetDirectoryName(directory)! } };
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "fixture-key");
        using var workflow = new PostRecordingWorkflow("ffmpeg");
        try
        {
            if (!chunking) { workflow.Begin(directory); await workflow.Completion; }
            watcher.EnableRaisingEvents = true;
            if (chunking) await jobs.ProcessSessionAsync(directory, default);
            else { workflow.Choose("manual", 1, 4); await workflow.Completion; }
            watcher.EnableRaisingEvents = false;
            Assert.Equal(1, written); Assert.Equal(0, handler.Calls);
            Assert.Empty(Directory.GetDirectories(processed));
            var failed = SessionStore.Read(directory);
            if (chunking) Assert.Null(failed.Audio); else Assert.Null(failed.Trim.RelativeDirectory);
            foreach (var file in originals) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(directory, file.Key)));
            File.SetAttributes(metadata, FileAttributes.Normal);
            if (chunking) { jobs.Resume(directory); await jobs.ProcessSessionAsync(directory, default); Assert.NotNull(SessionStore.Read(directory).Audio); }
            else { workflow.Begin(directory); await workflow.Completion; workflow.Choose("manual", 1, 4); await workflow.Completion; Assert.NotNull(SessionStore.Read(directory).Trim.RelativeDirectory); }
            Assert.Single(Directory.GetDirectories(processed));
        }
        finally { watcher.EnableRaisingEvents = false; File.SetAttributes(metadata, FileAttributes.Normal); }
    }

    private sealed class FixtureApi : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:00] Me: fixture\"}}]}") });
        }
    }

    public void Dispose() => fixture.Dispose();
}
