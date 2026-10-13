using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeetTape;

internal static class SessionRemoval
{
    internal static bool CanDelete(SessionData data) => data.RecordingState == RecordingState.Recorded
        && !ProcessingJobs.Eligible(data) && !ProcessingJobs.CanCancel(data);

    internal static void Recycle(string directory, Guid id, IReadOnlyList<string> roots, string configurationDirectory,
        Func<string, Guid, string> recycle, IReadOnlyList<string>? protectedFiles = null)
    {
        if (!Path.IsPathFullyQualified(directory) || id == Guid.Empty)
            throw new InvalidOperationException("Refresh and select a saved recording before deleting.");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string parent = Path.GetDirectoryName(directory) ?? "";
        if (!roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), parent, StringComparison.OrdinalIgnoreCase))
            || Within(configurationDirectory, directory) || protectedFiles?.Any(path => Within(path, directory)) == true)
            throw new InvalidOperationException("Only a meeting folder directly inside a known meetings root can be deleted.");

        var pins = new List<SafeFileHandle>();
        try
        {
            // Pin ancestors against replacement while validating and recycling their child.
            for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent)
            {
                RejectLink(ancestor.FullName);
                var pin = CreateFile(ancestor.FullName, 0x80000000, 3, 0, 3, 0x02200000, 0);
                if (pin.IsInvalid) { pin.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                pins.Add(pin);
                RejectLink(ancestor.FullName);
            }
            ValidateTree(directory);
            using (var lease = new FileStream(Path.Combine(directory, ".session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                ValidateRecording(directory, id);
            }
            // Windows directory moves require closed child handles; the controller/scheduler still exclude this session.
            ValidateRecording(directory, id);
            _ = recycle(directory, id);
            if (Directory.Exists(directory)) throw new IOException("Recycle did not remove the recording folder.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or COMException)
        { throw new InvalidOperationException("Cannot move this recording to Windows Recycle Bin. Check folder permissions and Recycle Bin availability; no permanent deletion is attempted.", error); }
        finally { foreach (var pin in pins) pin.Dispose(); }
    }

    private static bool Within(string path, string directory) => string.Equals(Path.GetFullPath(path), directory, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRecording(string directory, Guid id)
    {
        ValidateTree(directory);
        var data = SessionStore.Read(directory);
        if (data.Id != id) throw new InvalidOperationException("The selected recording changed. Refresh and confirm it again.");
        if (!CanDelete(data)) throw new InvalidOperationException("Cancel this meeting's unfinished processing before deleting.");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked meeting folders or files cannot be deleted. Refresh and check the original folder.");
    }

    private static void ValidateTree(string directory)
    {
        RejectLink(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
