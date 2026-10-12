namespace MeetTape.Tests;

public sealed class DesktopInstanceTests
{
    [Fact]
    [Trait("Category", "WindowsOnly")]
    public void RepeatedStartupSignalsPrimaryAndNormalExitAllowsRestart()
    {
        string name = @"Local\MeetTape.Test." + Guid.NewGuid().ToString("N");
        using (var primary = new DesktopInstance(name))
        {
            Assert.True(primary.IsPrimary);
            using var activated = new ManualResetEventSlim();
            primary.Listen(activated.Set);
            bool secondIsPrimary = true;
            Exception? failure = null;
            var secondary = new Thread(() =>
            {
                try
                {
                    using var second = new DesktopInstance(name);
                    second.ActivateExisting();
                    secondIsPrimary = second.IsPrimary;
                }
                catch (Exception ex) { failure = ex; }
            });
            secondary.Start();
            // The mutex owner must remain on its acquiring thread until Dispose.
            Assert.True(secondary.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.False(secondIsPrimary);
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        }
        using var restarted = new DesktopInstance(name);
        Assert.True(restarted.IsPrimary);
    }

    [Fact]
    [Trait("Category", "WindowsOnly")]
    public void AbandonedOwnerDoesNotBlockStartup()
    {
        string name = @"Local\MeetTape.Test." + Guid.NewGuid().ToString("N");
        using var previous = new Mutex(false, name);
        var terminated = new Thread(() => previous.WaitOne());
        terminated.Start();
        Assert.True(terminated.Join(TimeSpan.FromSeconds(5)));
        using var replacement = new DesktopInstance(name);
        Assert.True(replacement.IsPrimary);
    }
}
