using System.Diagnostics;
using System.Net;

namespace MeetTape.Tests;

public sealed class StartupReleaseNoticeTests
{
    [Theory]
    [InlineData("0.0.1+buildhash", "v0.0.2", "Update available. To update, run: update")]
    [InlineData("0.0.2", "v0.0.2", "Up to date.")]
    [InlineData("0.0.3", "v0.0.2", "Current version is newer than the latest published release.")]
    public void ComparesPublishedReleaseVersions(string current, string upstream, string expected)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/repos/Akilaydin/OriApps.MeetTape/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Contains("MeetTape", request.Headers.UserAgent.ToString());
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Reply(HttpStatusCode.OK, $"{{\"tag_name\":\"{upstream}\"}}"));
        });
        using var client = new HttpClient(handler);
        using var output = new StringWriter();

        StartupReleaseNotice.Write(output, current, client, TimeSpan.FromSeconds(1));

        Assert.Equal(1, handler.Calls);
        Assert.Contains($"Current version: v{current.Split('+')[0]}", output.ToString());
        Assert.Contains($"Upstream version: {upstream}", output.ToString());
        Assert.Contains(expected, output.ToString());
        if (!expected.StartsWith("Update available", StringComparison.Ordinal))
            Assert.DoesNotContain("To update, run: update", output.ToString());
    }

    [Theory]
    [InlineData("0.0.0+buildhash")]
    [InlineData("0.1.0-preview.1")]
    [InlineData("unknown")]
    public void DevelopmentBuildNeverClaimsThatAnUpdateIsAvailable(string current)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not contact GitHub for a dev build."));
        using var client = new HttpClient(handler);
        using var output = new StringWriter();

        StartupReleaseNotice.Write(output, current, client, TimeSpan.FromSeconds(1));

        Assert.Contains("Current version:", output.ToString());
        Assert.Contains("development build", output.ToString());
        Assert.Contains("Upstream version: not checked", output.ToString());
        Assert.DoesNotContain("Up to date", output.ToString());
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"tag_name\":\"v0.0.2\"}")]
    [InlineData(HttpStatusCode.InternalServerError, "private server error")]
    [InlineData(HttpStatusCode.OK, "{")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"v0.0.2-beta\"}")]
    public void GithubErrorsAndUnexpectedResponsesDoNotClaimAnUpdate(HttpStatusCode code, string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(code, body)));
        using var client = new HttpClient(handler);
        using var output = new StringWriter();

        StartupReleaseNotice.Write(output, "0.0.1", client, TimeSpan.FromSeconds(1));

        Assert.Contains("Current version: v0.0.1", output.ToString());
        Assert.Contains("Upstream version: unavailable", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
        Assert.DoesNotContain("Update available", output.ToString());
    }

    [Fact]
    public void NetworkFailuresDoNotThrowOrExposeDiagnostics()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private network failure"));
        using var client = new HttpClient(handler);
        using var output = new StringWriter();

        StartupReleaseNotice.Write(output, "0.0.1", client, TimeSpan.FromSeconds(1));

        Assert.Contains("Upstream version: unavailable", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupHasAHardDeadlineEvenIfHandlerIgnoresCancellation(bool honorCancellation)
    {
        using var handler = new Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, honorCancellation ? cancellationToken : CancellationToken.None);
            return Reply(HttpStatusCode.OK, "{}");
        });
        using var client = new HttpClient(handler);
        using var output = new StringWriter();
        var elapsed = Stopwatch.StartNew();

        StartupReleaseNotice.Write(output, "0.0.1", client, TimeSpan.FromMilliseconds(30));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Startup took {elapsed.Elapsed}.");
        Assert.Contains("Upstream version: unavailable", output.ToString());
    }

    [Fact]
    public void ExistingSevenArgumentCliRunOverloadRemainsAvailable()
    {
        Assert.Contains(typeof(Cli).GetMethods(), method => method.IsPublic && method.Name == nameof(Cli.Run)
            && method.GetParameters().Length == 7);
    }

    [Fact]
    public void OneShotCliNeverChecksGithub()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTape-Startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = Path.Combine(root, "config.json");
            ConfigurationBootstrap.Initialize(config, Path.Combine(root, "Meetings"));
            int notices = 0;
            void WriteNotice(TextWriter writer) { notices++; writer.WriteLine("version check marker"); }
            CaptureCoordinator Capture(CaptureSettings _) => new(() => throw new InvalidOperationException("Unexpected hardware use."));

            foreach (string[] command in new[]
            {
                new[] { "--help" }, new[] { "devices" }, new[] { "update" },
                new[] { "--config", config, "sessions" }, new[] { "--config", config, "status" }
            })
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                Assert.Equal(0, Cli.Run(command, new StringReader("exit\n"), output, error, Capture,
                    (_, _) => 0, () => [], WriteNotice));
                Assert.DoesNotContain("version check marker", output.ToString());
            }

            Assert.Equal(0, notices);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body)
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return handle(request, cancellationToken);
        }
    }
}
