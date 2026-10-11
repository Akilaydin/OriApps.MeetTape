using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace MeetTape;

public static class UpdateLauncher
{
    public static int Run(TextWriter output, TextWriter error) => Run(
        AppContext.BaseDirectory, output, error, info =>
        {
            using var process = Process.Start(info);
            return process is not null;
        });

    public static int Run(string executableDirectory, TextWriter output, TextWriter error,
        Func<ProcessStartInfo, bool> startProcess)
    {
        string script = Path.Combine(executableDirectory, "update.ps1");
        string installationMarker = Path.GetFullPath(Path.Combine(executableDirectory, "..", "installed-version.txt"));
        if (File.Exists(installationMarker))
        {
            try { RestoreMissingScripts(executableDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                CliColors.WriteLine(error, "Cannot restore bundled update scripts beside MeetTape.exe; check installation permissions.", ConsoleColor.Red);
                return 2;
            }
        }
        if (!File.Exists(script))
        {
            CliColors.WriteLine(error, "Installed updater not found alongside MeetTape.exe. Install a release containing update.ps1 first.", ConsoleColor.Red);
            return 2;
        }

        var info = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = executableDirectory,
            // The caller must exit before its EXE can be replaced. A child attached to
            // the same console writes progress after cmd.exe has already shown its prompt.
            // Open a separate console instead of obscuring the caller's prompt.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-WaitForProcessId",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);

        try
        {
            if (!startProcess(info)) throw new InvalidOperationException("Updater process did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            CliColors.WriteLine(error, "Cannot launch updater. Check that Windows PowerShell is available.", ConsoleColor.Red);
            return 2;
        }

        CliColors.WriteLine(output, "Updater opened in a separate PowerShell window; MeetTape is closing.", ConsoleColor.Green);
        return 0;
    }

    private static void RestoreMissingScripts(string executableDirectory)
    {
        foreach (string name in new[] { "install.ps1", "update.ps1", "InstallerPath.ps1" })
        {
            string destination = Path.Combine(executableDirectory, name);
            if (File.Exists(destination)) continue;

            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("MeetTape.Installer." + name)
                ?? throw new InvalidOperationException("Embedded installer script is missing.");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(target);
                    target.Flush(flushToDisk: true);
                }
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { } // Concurrent first-run bootstrap.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
