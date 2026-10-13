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
        Directory.CreateDirectory(recording);
        File.WriteAllBytes(Path.Combine(recording, "me.mp3"), [1, 2, 3]);
        File.SetAttributes(Path.Combine(recording, "me.mp3"), FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(recording, "session.json"), "Disposable fixture metadata");
        Directory.CreateDirectory(Path.Combine(recording, "chunks"));
        File.WriteAllText(Path.Combine(recording, "chunks", "checkpoint.json"), "Disposable checkpoint");
        try
        {
            string recycled = WindowsRecycleBin.Recycle(recording);
            Assert.False(Directory.Exists(recording));
            Assert.Contains("$Recycle.Bin", recycled, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(recycled, "me.mp3")));
            Assert.Equal("Disposable fixture metadata", File.ReadAllText(Path.Combine(recycled, "session.json")));
            Assert.Equal("Disposable checkpoint", File.ReadAllText(Path.Combine(recycled, "chunks", "checkpoint.json")));
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
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(junction, Guid.NewGuid(), [meetings], root, _ => throw new Exception("Must not recycle link")));
            string recording = Path.Combine(meetings, "real-meeting"); Directory.CreateDirectory(recording);
            Directory.Move(junction, Path.Combine(recording, "linked-child"));
            Assert.Throws<InvalidOperationException>(() => SessionRemoval.Recycle(recording, Guid.NewGuid(), [meetings], root, _ => throw new Exception("Must not recycle child link")));
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
