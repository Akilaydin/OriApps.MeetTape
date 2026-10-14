using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace MeetTape.Tests;

public sealed class DiagnosticTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MeetTapeDiagnostics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AsyncDisposalJoinsAnInFlightWriterBeforeExclusiveReadAndCleanup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var log = new FileDiagnostics(root);
        log.Write("Error", "fixture", "Slow diagnostic", new BlockingException(entered, release));
        Task completion = Task.CompletedTask;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            completion = log.DisposeAsync().AsTask();
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            release.Set();
            await log.DisposeAsync();
        }
        await completion;
        using (var file = new FileStream(Path.Combine(root, "meettape.log"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(file.Length > 0);
        Directory.Delete(root, recursive: true);
    }

    private sealed class BlockingException(ManualResetEventSlim entered, ManualResetEventSlim release) : Exception
    {
        public override string ToString()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture writer was not released.");
            return "Fixture slow diagnostic";
        }
    }

    [Fact]
    public async Task LogsSurviveRestartRotateAndRetainExceptionContextWithoutCredentials()
    {
        for (int restart = 0; restart < 2; restart++)
        {
            await using var log = new FileDiagnostics(root, maxBytes: 2048, retainedFiles: 3);
            using var scope = AppDiagnostics.Use(log);
            AppDiagnostics.ProtectSecret("fixture-key-secret");
            for (int i = 0; i < 30; i++)
                AppDiagnostics.Error("api", "Request " + i, new HttpRequestException(
                    "Authorization: Bearer hidden-auth fixture-key-secret https://user:pass@example.test/private?token=hidden-query",
                    new SocketException((int)SocketError.HostNotFound), HttpStatusCode.ServiceUnavailable));
        }
        var files = Directory.GetFiles(root, "meettape.log*");
        Assert.InRange(files.Length, 2, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 2048));
        string text = string.Concat(files.Select(File.ReadAllText));
        Assert.Contains("HttpRequestException", text);
        Assert.Contains("SocketException", text);
        Assert.Contains("503", text);
        Assert.Contains("hresult", text);
        foreach (string secret in new[] { "fixture-key-secret", "hidden-auth", "hidden-query", "user:pass", "/private" })
            Assert.DoesNotContain(secret, text);
        foreach (string line in files.SelectMany(File.ReadAllLines)) using (JsonDocument.Parse(line)) { }
    }

    [Fact]
    public async Task ExistingLogIsAppendedAfterRestart()
    {
        await using (var first = new FileDiagnostics(root)) first.Write("Information", "startup", "First run");
        await using (var second = new FileDiagnostics(root)) second.Write("Information", "startup", "Second run");
        string text = File.ReadAllText(Path.Combine(root, "meettape.log"));
        Assert.Contains("First run", text); Assert.Contains("Second run", text);
    }

    [Fact]
    public async Task UnwritableLogsDoNotDelayOrReplaceCaptureFailure()
    {
        Directory.CreateDirectory(root);
        string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "fixture");
        await using var log = new FileDiagnostics(blocked);
        using var scope = AppDiagnostics.Use(log);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var capture = new CaptureCoordinator(() => throw new UnauthorizedAccessException("private-device-detail"));
        var error = Assert.Throws<InvalidOperationException>(capture.Start);
        Assert.Contains("Access denied", error.Message);
        Assert.DoesNotContain("private-device-detail", error.Message);
        Assert.False(capture.Status.IsActive);
        for (int i = 0; i < 1000; i++) AppDiagnostics.Info("capture", "Fixture activity");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal("fixture", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task CaptureFfmpegAndApiFailuresHavePersistentTechnicalContext()
    {
        Directory.CreateDirectory(root);
        string logs = Path.Combine(root, "logs");
        await using (var log = new FileDiagnostics(logs))
        using (AppDiagnostics.Use(log))
        {
            using var capture = new CaptureCoordinator(() => throw new UnauthorizedAccessException("fixture-device-denied"));
            Assert.Throws<InvalidOperationException>(capture.Start);
            await Assert.ThrowsAsync<RecordingException>(() => AudioTrimmer.RunAsync(
                Path.Combine(root, "missing-ffmpeg.exe"), [], null, default));
            using var handler = new NetworkFailure(); using var http = new HttpClient(handler);
            var config = ConfigurationLoader.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json")));
            string audio = Path.Combine(root, "fixture.mp3"); File.WriteAllBytes(audio, [1, 2, 3]);
            var client = new ChatApiClient(http, () => false, readKey: _ => "fixture-api-key");
            var error = await Assert.ThrowsAsync<ChatApiException>(() => client.TranscribeAsync(config.Settings.Transcription, "fixture prompt", audio, default));
            Assert.Contains("DNS", error.Message);
        }
        string text = File.ReadAllText(Path.Combine(logs, "meettape.log"));
        foreach (string component in new[] { "capture", "ffmpeg", "api" }) Assert.Contains(component, text);
        Assert.Contains("UnauthorizedAccessException", text);
        Assert.Contains("Win32Exception", text);
        Assert.Contains("SocketException", text);
        Assert.DoesNotContain("fixture-api-key", text);
        Assert.DoesNotContain("fixture prompt", text);
    }

    [Fact]
    public void UserReasonsDistinguishDnsTlsTimeoutAndFullStorageWithoutPrivateMessages()
    {
        Assert.Contains("DNS", AppDiagnostics.UserMessage(new HttpRequestException("private", new SocketException((int)SocketError.HostNotFound)), "Failed."));
        Assert.Contains("certificate", AppDiagnostics.UserMessage(new HttpRequestException("private", new AuthenticationException("private")), "Failed."));
        Assert.Contains("timed out", AppDiagnostics.UserMessage(new TimeoutException("private"), "Failed."));
        Assert.Contains("full", AppDiagnostics.UserMessage(new IOException("private", unchecked((int)0x80070070)), "Failed."));
        Assert.DoesNotContain("private", AppDiagnostics.UserMessage(new HttpRequestException("private"), "Failed."));
    }

    private sealed class NetworkFailure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("fixture-api-key https://example.test/private?token=hidden", new SocketException((int)SocketError.HostNotFound));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
