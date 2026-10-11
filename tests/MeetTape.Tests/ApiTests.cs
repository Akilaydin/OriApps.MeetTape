using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeetTape.Tests;

public sealed class ApiTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "MeetTapeApi-" + Guid.NewGuid().ToString("N") + ".mp3");
    private static readonly ApiSettings Settings = new() { Endpoint = "https://example.test/v1/chat/completions",
        Model = "configured-model", PromptFile = "configured-prompt.md" };
    private const string Valid = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"[00:01] Me: test\"}}]}";

    public ApiTests() => File.WriteAllBytes(path, [1, 2, 3]);

    [Fact]
    public async Task RequestUsesConfiguredModelPromptBearerAndMp3AndReturnsValidatedText()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Settings.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            var root = body.RootElement;
            Assert.Equal("configured-model", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            var content = root.GetProperty("messages")[0].GetProperty("content");
            Assert.StartsWith("user-prompt", content[0].GetProperty("text").GetString());
            Assert.Equal("input_audio", content[1].GetProperty("type").GetString());
            Assert.Equal("AQID", content[1].GetProperty("input_audio").GetProperty("data").GetString());
            Assert.Equal("mp3", content[1].GetProperty("input_audio").GetProperty("format").GetString());
            return Reply(HttpStatusCode.OK, Valid);
        });
        using var http = new HttpClient(handler);
        var reply = await new ChatApiClient(http, () => false, _ => "test-key").TranscribeAsync(Settings, "user-prompt", path, default);
        Assert.Equal("[00:01] Me: test", reply.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), reply.AudioSha256);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"[NO_SPEECH]\"}}]}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\" \"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":[{}]}}]}")]
    public async Task InvalidOrIncompleteResponsesNeverSucceed(string raw)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, raw)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(ApiFailureKind.InvalidResponse, failure.Kind);
        Assert.DoesNotContain(raw, failure.Message);
    }

    [Theory]
    [InlineData(400, ApiFailureKind.Permanent)]
    [InlineData(401, ApiFailureKind.Permanent)]
    [InlineData(403, ApiFailureKind.Permanent)]
    [InlineData(413, ApiFailureKind.Permanent)]
    [InlineData(408, ApiFailureKind.Transient)]
    [InlineData(429, ApiFailureKind.Transient)]
    [InlineData(500, ApiFailureKind.Transient)]
    [InlineData(503, ApiFailureKind.Transient)]
    public async Task HttpFailuresExposeOnlySafeStructuredDiagnostics(int status, ApiFailureKind kind)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Reply((HttpStatusCode)status, "private provider body test-key");
            response.Headers.RetryAfter = new(System.TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings, "private prompt", path, default));
        Assert.Equal(kind, failure.Kind);
        Assert.Equal((HttpStatusCode)status, failure.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        Assert.DoesNotContain("private", failure.Message);
        Assert.DoesNotContain("test-key", failure.Message);
    }

    [Fact]
    public async Task CaptureMissingKeyUnsafeEndpointAndOversizedAudioMakeNoRequests()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, Valid)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ChatApiClient(http, () => true, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, default));
        int checks = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ChatApiClient(http, () => ++checks > 1, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, default));
        var missing = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "")
            .TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(ApiFailureKind.Configuration, missing.Kind);
        await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings with { Endpoint = "http://example.test/v1/chat/completions" }, "prompt", path, default));
        using (var file = File.OpenWrite(path)) file.SetLength(ChatApiClient.MaxAudioBytes + 1);
        var large = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkTimeoutAndCallerCancellationRemainDistinct()
    {
        using var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply(HttpStatusCode.OK, Valid); });
        using var http = new HttpClient(handler);
        var timeout = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key", TimeSpan.FromMilliseconds(20))
            .TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(ApiFailureKind.Transient, timeout.Kind);
        using var canceled = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, canceled.Token));
        using var broken = new Handler((_, _) => throw new HttpRequestException("private network details test-key"));
        using var brokenHttp = new HttpClient(broken);
        var network = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(brokenHttp, () => false, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(ApiFailureKind.Transient, network.Kind);
        Assert.DoesNotContain("private", network.Message);
    }

    [Fact]
    public async Task MissingFileAndOversizedResponseFailWithoutLeakingContents()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, new string('x', 2 * 1024 * 1024 + 1))));
        using var http = new HttpClient(handler);
        var api = new ChatApiClient(http, () => false, _ => "test-key");
        var missing = await Assert.ThrowsAsync<ChatApiException>(() => api.TranscribeAsync(Settings, "prompt", path + ".missing", default));
        Assert.Equal(ApiFailureKind.FileSystem, missing.Kind);
        var large = await Assert.ThrowsAsync<ChatApiException>(() => api.TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(ApiFailureKind.InvalidResponse, large.Kind);
    }

    [Fact]
    public async Task UnknownLengthResponseIsStillBounded()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new UnknownLengthContent() }));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings, "prompt", path, default));
        Assert.Equal(ApiFailureKind.InvalidResponse, failure.Kind);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[2 * 1024 * 1024 + 1]).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(new byte[2 * 1024 * 1024 + 1]));
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string raw) => new(status) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); }
    }
    public void Dispose() => File.Delete(path);
}
