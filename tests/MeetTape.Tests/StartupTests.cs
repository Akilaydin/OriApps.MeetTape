namespace MeetTape.Tests;

public sealed class StartupTests
{
    [Theory]
    [InlineData("configure")]
    [InlineData("--help")]
    [InlineData("--config")]
    [InlineData("chunks")]
    [InlineData("update")]
    public void RemovedArgumentsSelectGuiErrorAndNeverSmoke(string argument)
    {
        try
        {
            Program.ConfigureStartup([argument]);
            Assert.False(App.Smoke);
            Assert.Contains("launch MeetTape without arguments", App.StartupError);
        }
        finally { Program.ConfigureStartup([]); }
    }

    [Fact]
    public void OnlyExactSmokeArgumentBypassesNormalInitialization()
    {
        try
        {
            Program.ConfigureStartup(["--ui-smoke"]);
            Assert.True(App.Smoke);
            Assert.Null(App.StartupError);
            Program.ConfigureStartup(["--ui-smoke", "--config", "private-path"]);
            Assert.False(App.Smoke);
            Assert.NotNull(App.StartupError);
            Assert.DoesNotContain("private-path", App.StartupError);
            Program.ConfigureStartup([]);
            Assert.False(App.Smoke);
            Assert.Null(App.StartupError);
        }
        finally { Program.ConfigureStartup([]); }
    }
}
