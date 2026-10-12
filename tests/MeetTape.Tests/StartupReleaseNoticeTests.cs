using System.Diagnostics;
using System.Net;

namespace MeetTape.Tests;

public sealed class StartupReleaseNoticeTests
{
    [Theory]
    [InlineData("0.0.1+buildhash", "v0.0.2", "Update available. Use Update in MeetTape or run meettape-cli update.")]
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

        string output = StartupReleaseNotice.Get(current, client, TimeSpan.FromSeconds(1));

        Assert.Equal(1, handler.Calls);
        Assert.Contains($"Current version: v{current.Split('+')[0]}", output);
        Assert.Contains($"Upstream version: {upstream}", output);
        Assert.Contains(expected, output);
        if (!expected.StartsWith("Update available", StringComparison.Ordinal))
            Assert.DoesNotContain("Use Update in MeetTape", output);
    }

    [Theory]
    [InlineData("0.0.0+buildhash")]
    [InlineData("0.1.0-preview.1")]
    [InlineData("unknown")]
    public void DevelopmentBuildNeverClaimsThatAnUpdateIsAvailable(string current)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not contact GitHub for a dev build."));
        using var client = new HttpClient(handler);

        string output = StartupReleaseNotice.Get(current, client, TimeSpan.FromSeconds(1));

        Assert.Contains("Current version:", output);
        Assert.Contains("development build", output);
        Assert.Contains("Upstream version: not checked", output);
        Assert.DoesNotContain("Up to date", output);
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

        string output = StartupReleaseNotice.Get("0.0.1", client, TimeSpan.FromSeconds(1));

        Assert.Contains("Current version: v0.0.1", output);
        Assert.Contains("Upstream version: unavailable", output);
        Assert.DoesNotContain("private", output);
        Assert.DoesNotContain("Update available", output);
    }

    [Fact]
    public void NetworkFailuresDoNotThrowOrExposeDiagnostics()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private network failure"));
        using var client = new HttpClient(handler);

        string output = StartupReleaseNotice.Get("0.0.1", client, TimeSpan.FromSeconds(1));

        Assert.Contains("Upstream version: unavailable", output);
        Assert.DoesNotContain("private", output);
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
        var elapsed = Stopwatch.StartNew();

        string output = StartupReleaseNotice.Get("0.0.1", client, TimeSpan.FromMilliseconds(30));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Startup took {elapsed.Elapsed}.");
        Assert.Contains("Upstream version: unavailable", output);
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
