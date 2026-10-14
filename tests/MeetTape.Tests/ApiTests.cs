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

    [Theory]
    [InlineData(200, "stop", "accepted")]
    [InlineData(200, "length", "pause")]
    public async Task ResponsesLogBoundedTechnicalMetadataWithoutPayload(int status, string finish, string decision)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetTapeApiMetadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new FileDiagnostics(directory))
            using (AppDiagnostics.Use(log))
            {
                string raw = JsonSerializer.Serialize(new
                {
                    id = "response-123", model = "upstream-model",
                    usage = new { prompt_tokens = 3, completion_tokens = 4, total_tokens = 7, hidden = "private usage" },
                    choices = new[] { new { finish_reason = finish, message = new { content = "private transcript" } } },
                    hidden = new string('z', 20000)
                });
                using var handler = new Handler((_, _) =>
                {
                    var response = Reply((HttpStatusCode)status, raw);
                    response.Headers.Add("x-request-id", "request-123");
                    response.Headers.Add("x-user-payload", "private header");
                    return Task.FromResult(response);
                });
                using var http = new HttpClient(handler);
                var api = new ChatApiClient(http, () => false, _ => "fixture-key");
                if (decision == "accepted") await api.TranscribeAsync(Settings, "private prompt", path, default);
                else await Assert.ThrowsAsync<ChatApiException>(() => api.TranscribeAsync(Settings, "private prompt", path, default));
            }
            string logged = File.ReadAllText(Path.Combine(directory, "meettape.log"));
            foreach (string expected in new[] { "stage=transcription", "requested_model=configured-model", "example.test",
                "elapsed_ms=", "bytes=", "response_id=response-123", "model=upstream-model", "x-request-id=request-123",
                "choices=1", "finish_reason=" + finish, "content=String", "prompt_tokens=3", "completion_tokens=4", "total_tokens=7", "decision=" + decision })
                Assert.Contains(expected, logged);
            foreach (string secret in new[] { "private transcript", "private prompt", "private usage", "private header", "fixture-key", "AQID", new string('z', 100) })
                Assert.DoesNotContain(secret, logged);
            Assert.True(logged.Length < 10000);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("model_not_found", "Unknown model; private prompt", "unknown-model")]
    [InlineData("invalid_request_error", "Unsupported input_audio; private transcript", "audio-input-unsupported")]
    [InlineData("content_filter", "Request blocked; private prompt", "blocked")]
    [InlineData("rate_limit_exceeded", "Quota exceeded; private transcript", "rate-limit")]
    public async Task StructuredHttpErrorsLogSafeReasonWithoutEchoedUserText(string code, string message, string reason)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetTapeHttpMetadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new FileDiagnostics(directory))
            using (AppDiagnostics.Use(log))
            {
                string raw = JsonSerializer.Serialize(new { error = new { type = "invalid_request_error", code, message }, model = "upstream-model" });
                using var handler = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.BadRequest, raw)));
                using var http = new HttpClient(handler);
                var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "fixture-key")
                    .TranscribeAsync(Settings, "private prompt", path, default));
                Assert.Equal(ApiFailureKind.Permanent, failure.Kind);
                Assert.Equal(HttpStatusCode.BadRequest, failure.Status);
                Assert.DoesNotContain(reason, failure.Message);
            }
            string logged = File.ReadAllText(Path.Combine(directory, "meettape.log"));
            Assert.Contains("error_type=invalid_request_error", logged);
            Assert.Contains("error_code=" + code, logged);
            Assert.Contains("error_reason=" + reason, logged);
            Assert.Contains("decision=pause", logged);
            Assert.DoesNotContain("private prompt", logged);
            Assert.DoesNotContain("private transcript", logged);
            Assert.DoesNotContain("fixture-key", logged);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(400, ApiFailureKind.Permanent)]
    [InlineData(503, ApiFailureKind.Transient)]
    public async Task OversizedErrorDiagnosticsRetainHttpFailureClassification(int status, ApiFailureKind expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply((HttpStatusCode)status, new string('x', 2 * 1024 * 1024 + 1))));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "fixture-key")
            .TranscribeAsync(Settings, "private prompt", path, default));
        Assert.Equal(expected, error.Kind);
        Assert.Equal((HttpStatusCode)status, error.Status);
    }

    [Fact]
    public async Task SummaryAndProviderBlockDiagnosticsMaskArbitraryMetadataWithoutChangingResults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetTapeSummaryMetadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new FileDiagnostics(directory))
            using (AppDiagnostics.Use(log))
            {
                using var handler = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK,
                    "{\"id\":\"private transcript\",\"model\":\"private summary\",\"usage\":{\"prompt_tokens\":\"private prompt\"}," +
                    "\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"private summary\"}}]}")));
                using var http = new HttpClient(handler);
                var api = new ChatApiClient(http, () => false, _ => "fixture-key");
                Assert.Equal("private summary", await api.SummarizeAsync(Settings, "private prompt", "private transcript", default));
                using var blocked = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK,
                    "{\"choices\":[],\"modelVersion\":\"provider-model\",\"responseId\":\"provider-response\",\"usageMetadata\":{\"totalTokenCount\":8}," +
                    "\"promptFeedback\":{\"blockReason\":\"SAFETY\",\"blockReasonMessage\":\"private prompt\"}}")));
                using var blockedHttp = new HttpClient(blocked);
                var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(blockedHttp, () => false, _ => "fixture-key")
                    .TranscribeAsync(Settings, "private prompt", path, default));
                Assert.False(failure.EmptyChoices);
            }
            string logged = File.ReadAllText(Path.Combine(directory, "meettape.log"));
            Assert.Contains("stage=summary", logged);
            Assert.Contains("block_reason=SAFETY", logged);
            Assert.Contains("model_version=provider-model", logged);
            Assert.Contains("response_id=provider-response", logged);
            Assert.Contains("totalTokenCount=8", logged);
            Assert.Contains("choices=0", logged);
            foreach (string secret in new[] { "private transcript", "private summary", "private prompt", "fixture-key" }) Assert.DoesNotContain(secret, logged);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

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
    [InlineData("not-json", "reason=invalid-json", "bytes=8")]
    [InlineData("{}", "reason=choices-missing", "choices=missing")]
    [InlineData("{\"choices\":{}}", "reason=choices-not-array", "choices=Object")]
    [InlineData("{\"choices\":[]}", "reason=choices-empty", "choices=Array[0]")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"private transcript\"}}]}",
        "reason=finish-reason-not-stop", "finish_reason=length; message=Object; content=String[length=18, blank=False]")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}", "reason=content-not-string", "content=Null")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":[{\"text\":\"private transcript\"}]}}]}",
        "reason=content-not-string", "content=Array[1]")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\" \"}}]}",
        "reason=content-blank", "blank=True")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"secret_finish_reason\",\"message\":{\"content\":\"private transcript\"}}]}",
        "reason=finish-reason-not-stop", "finish_reason=other-string")]
    public async Task InvalidResponseLogsStructureWithoutPayload(string raw, string expectedReason, string expectedShape)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MeetTapeApiDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new FileDiagnostics(directory))
            using (AppDiagnostics.Use(log))
            {
                using var handler = new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, raw)));
                using var http = new HttpClient(handler);
                var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "fixture-key")
                    .TranscribeAsync(Settings, "private prompt", path, default));
                Assert.Equal(ApiFailureKind.InvalidResponse, failure.Kind);
            }

            string logged = File.ReadAllText(Path.Combine(directory, "meettape.log"));
            Assert.Contains("Rejected response HTTP 200", logged);
            Assert.Contains(expectedReason, logged);
            Assert.Contains(expectedShape, logged);
            Assert.DoesNotContain("private transcript", logged);
            Assert.DoesNotContain("secret_finish_reason", logged);
            Assert.DoesNotContain("private prompt", logged);
            Assert.DoesNotContain("fixture-key", logged);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
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

    [Theory]
    [InlineData("endpoint", "https://user:fixture-secret@example.test/api", "endpoint")]
    [InlineData("model", "", "model")]
    [InlineData("prompt", "", "prompt")]
    [InlineData("key", "", "MEETTAPE_API_KEY")]
    [InlineData("key-error", "fixture-secret", "MEETTAPE_API_KEY")]
    public async Task LocalValidationNamesTheProblemWithoutLeakingValuesOrMakingRequests(string field, string value, string expected)
    {
        using var handler = new Handler((_, _) => throw new Exception("No API request expected"));
        using var http = new HttpClient(handler);
        var settings = field == "endpoint" ? Settings with { Endpoint = value } :
            field == "model" ? Settings with { Model = value } : Settings;
        var api = new ChatApiClient(http, () => false, _ => field == "key-error"
            ? throw new InvalidDataException(value) : field == "key" ? value : "fixture-key");
        foreach (bool summary in new[] { false, true })
        {
            string prompt = field == "prompt" ? value : "fixture-prompt";
            var failure = await Assert.ThrowsAsync<ChatApiException>(() => summary
                ? api.SummarizeAsync(settings, prompt, "fixture-transcript", default)
                : api.TranscribeAsync(settings, prompt, path, default));
            Assert.Equal(ApiFailureKind.Configuration, failure.Kind);
            Assert.Contains(expected, failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fixture-secret", failure.Message);
        }
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string raw) => new(status) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); }
    }
    public void Dispose() => File.Delete(path);
}
