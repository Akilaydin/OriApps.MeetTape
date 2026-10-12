using System.Diagnostics;
using Velopack;

namespace MeetTape.Tests;

public sealed class StartupReleaseNoticeTests
{
    [Theory]
    [InlineData(false, false, "installed MeetTape build")]
    [InlineData(true, false, "Up to date")]
    [InlineData(true, true, "Available version: 1.0.1")]
    public async Task StartupOnlyChecksAndDisplaysVersions(bool installed, bool available, string expected)
    {
        var fake = new Updates(installed, Task.FromResult<UpdateInfo?>(available ?
            new(new VelopackAsset { Version = SemanticVersion.Parse("1.0.1") }, false) : null));
        string notice = await StartupReleaseNotice.GetAsync(fake);
        Assert.Contains("Current version: 1.0.0", notice); Assert.Contains(expected, notice);
        Assert.Equal(installed ? 1 : 0, fake.Checks);
    }

    [Fact]
    public async Task FailureAndHardDeadlineDoNotPreventStartupOrExposeDiagnostics()
    {
        var fake = new Updates(true, Task.FromException<UpdateInfo?>(new HttpRequestException("private-secret")));
        string notice = await StartupReleaseNotice.GetAsync(fake);
        Assert.Contains("unavailable", notice); Assert.DoesNotContain("private-secret", notice);
        fake = new Updates(true, new TaskCompletionSource<UpdateInfo?>().Task);
        var elapsed = Stopwatch.StartNew();
        notice = await StartupReleaseNotice.GetAsync(fake, TimeSpan.FromMilliseconds(30));
        Assert.Contains("unavailable", notice); Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class Updates(bool installed, Task<UpdateInfo?> result) : IAppUpdates
    {
        public bool IsInstalled => installed;
        public string CurrentVersion => "1.0.0";
        public int Checks;
        public Task<UpdateInfo?> CheckAsync() { Checks++; return result; }
        public Task DownloadAsync(UpdateInfo update, Action<int> progress) => throw new Exception("Startup must not download");
        public void ApplyAndRestart(UpdateInfo update) => throw new Exception("Startup must not apply");
    }
}
