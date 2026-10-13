using System.Diagnostics;

namespace MeetTape.UiTests;

public sealed class RecycleBinTests
{
    [WindowsCiFact]
    [Trait("Category", "NativeWindows")]
    public void WholeTemporaryRecordingReachesNativeRecycleBinWithItsFilesIntact()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../dist/recycle-tests/" + Guid.NewGuid().ToString("N")));
        string recording = Path.Combine(root, "Meetings", "Disposable meeting");
        ConfigurationBootstrap.Initialize(Path.Combine(root, "config.json"), Path.Combine(root, "Meetings"));
        var config = ConfigurationLoader.Load(Path.Combine(root, "config.json"));
        var data = new SessionData
        {
            Id = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, DurationSeconds = 2,
            RecordingState = RecordingState.Recorded, JobState = JobState.Canceled,
            Capture = new(null, null, new(48000, 32, 1, "float"), new(48000, 32, 1, "float")),
            Processing = new(120, config.Settings.Transcription, config.Settings.Summary, config.Directory),
            Stages = new() { ["transcribe"] = new(true, StageState.Pending) }
        };
        Directory.CreateDirectory(recording);
        File.WriteAllBytes(Path.Combine(recording, "me.mp3"), [1, 2, 3]);
        File.SetAttributes(Path.Combine(recording, "me.mp3"), FileAttributes.ReadOnly);
        SessionStore.Save(recording, data);
        byte[] metadata = File.ReadAllBytes(Path.Combine(recording, "session.json"));
        Directory.CreateDirectory(Path.Combine(recording, "chunks"));
        File.WriteAllText(Path.Combine(recording, "chunks", "checkpoint.json"), "Disposable checkpoint");
        try
        {
            Assert.Throws<InvalidOperationException>(() => WindowsRecycleBin.Recycle(recording, Guid.NewGuid()));
            Assert.True(Directory.Exists(recording));
            string recycled = "";
            SessionRemoval.Recycle(recording, data.Id, [config.MeetingsRoot], config.Directory, (source, id) =>
                recycled = WindowsRecycleBin.Recycle(source, id));
            Assert.False(Directory.Exists(recording));
            Assert.Contains("$Recycle.Bin", recycled, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(recycled, "me.mp3")));
            Assert.Equal(metadata, File.ReadAllBytes(Path.Combine(recycled, "session.json")));
            Assert.Equal("Disposable checkpoint", File.ReadAllText(Path.Combine(recycled, "chunks", "checkpoint.json")));
            Assert.Empty(SessionStore.Discover(config.MeetingsRoot));
        }
        finally
        {
            // Leave the recycled fixture to disposal of this runner; never empty a user's bin.
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [WindowsCiFact]
    [Trait("Category", "NativeWindows")]
    public void JunctionAndLinkedChildAreRefusedBeforeRecycle()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../dist/recycle-link-tests/" + Guid.NewGuid().ToString("N")));
        string meetings = Path.Combine(root, "Meetings"), foreign = Path.Combine(root, "foreign");
        Directory.CreateDirectory(meetings); Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "Unrelated fixture");
        string junction = Path.Combine(meetings, "linked-meeting");
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", junction, foreign }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit(); Assert.Equal(0, process.ExitCode);
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(junction, Guid.NewGuid(), [meetings], root, (_, _) => throw new Exception("Must not recycle link")));
            string recording = Path.Combine(meetings, "real-meeting"); Directory.CreateDirectory(recording);
            Directory.Move(junction, Path.Combine(recording, "linked-child"));
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(recording, Guid.NewGuid(), [meetings], root, (_, _) => throw new Exception("Must not recycle child link")));
            Assert.Equal("Unrelated fixture", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
        }
        finally
        {
            string nested = Path.Combine(meetings, "real-meeting", "linked-child");
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(nested)) Directory.Delete(nested);
            Directory.Delete(root, true);
        }
    }
}
