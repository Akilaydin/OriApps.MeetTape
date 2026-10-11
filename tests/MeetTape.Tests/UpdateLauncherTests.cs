using System.Diagnostics;

namespace MeetTape.Tests;

public sealed class UpdateLauncherTests
{
    [Fact]
    public void LauncherUsesAdjacentScriptWithSafeArgumentListAndParentId()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetTape updater " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "update.ps1");
            File.WriteAllText(script, "# test updater");
            using var output = new StringWriter();
            using var error = new StringWriter();
            ProcessStartInfo? started = null;

            int code = UpdateLauncher.Run(directory, output, error, info => { started = info; return true; });

            Assert.Equal(0, code);
            Assert.NotNull(started);
            Assert.Equal("powershell.exe", started.FileName);
            Assert.Equal(directory, started.WorkingDirectory);
            Assert.True(started.UseShellExecute);
            Assert.Equal(ProcessWindowStyle.Normal, started.WindowStyle);
            Assert.False(started.RedirectStandardOutput);
            Assert.False(started.RedirectStandardError);
            Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-WaitForProcessId", Environment.ProcessId.ToString()],
                started.ArgumentList.ToArray());
            Assert.Contains("separate PowerShell window", output.ToString());
            Assert.Empty(error.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingScriptDoesNotStartAnUpdater()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = UpdateLauncher.Run(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            output, error, _ => throw new InvalidOperationException("Must not start."));

        Assert.Equal(2, code);
        Assert.Contains("Installed updater not found", error.ToString());
    }

    [Fact]
    public void LegacyPrivateInstallRestoresMissingScriptsFromVerifiedExecutableResources()
    {
        string installation = Path.Combine(Path.GetTempPath(), "MeetTape-Bootstrap-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(installation, "app");
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(installation, "installed-version.txt"), "v0.1.5");
            using var output = new StringWriter();
            using var error = new StringWriter();
            int starts = 0;
            int result = UpdateLauncher.Run(app, output, error, _ => { starts++; return true; });

            Assert.Equal(0, result);
            Assert.Equal(1, starts);
            Assert.Empty(error.ToString());
            foreach (string script in new[] { "install.ps1", "update.ps1", "InstallerPath.ps1" })
            {
                string file = Path.Combine(app, script);
                Assert.True(File.Exists(file));
                Assert.NotEmpty(File.ReadAllText(file));
            }
            Assert.Empty(Directory.GetFiles(app, "*.tmp"));
            Assert.Equal(0, UpdateLauncher.Run(app, output, error, _ => { starts++; return true; }));
            Assert.Equal(2, starts);
        }
        finally { Directory.Delete(installation, recursive: true); }
    }
}
