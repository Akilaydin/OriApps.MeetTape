using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MeetTape.Tests;

public sealed class OpenAiTranscriptionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MeetTapeNativeAsr-" + Guid.NewGuid().ToString("N"));
    private string AudioPath => Path.Combine(directory, "chunk.mp3");

    public OpenAiTranscriptionTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(AudioPath, [1, 2, 3]);
    }

    private static ApiSettings Settings(string model) => new()
    {
        Model = model, Endpoint = "https://api.openai.com/v1/audio/transcriptions",
        PromptFile = "prompt.md", ApiKeyEnvironmentVariable = "OPENAI_TEST_KEY"
    };

    [Fact]
    public async Task DiarizeUsesMultipartAndExactSegmentTimestampsButTrustsRecordedTrack()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);
            string body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("name=model", body);
            Assert.Contains("gpt-4o-transcribe-diarize", body);
            Assert.Contains("name=response_format", body);
            Assert.Contains("diarized_json", body);
            Assert.Contains("name=chunking_strategy", body);
            Assert.Contains("auto", body);
            Assert.Contains("filename=chunk.mp3", body);
            Assert.Contains("Content-Type: audio/mpeg", body);
            Assert.DoesNotContain("name=prompt", body);
            return Response("""
                {"duration":10,"text":"Spoken words","segments":[
                    {"id":"one","speaker":"A","start":1.25,"end":3.5,"text":"Первая фраза."},
                    {"id":"two","speaker":"B","start":4,"end":5.5,"text":"Вторая\n фраза."}]}
                """);
        });
        using var http = new HttpClient(handler);
        var result = await new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings("gpt-4o-transcribe-diarize"), "unused prompt", AudioPath, default,
                context: null, source: "Them", chunkDurationSeconds: 10);

        Assert.Equal("[00:01.25] Them: Первая фраза.\n[00:04] Them: Вторая фраза.", result.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3])), result.AudioSha256);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GptTranscribeUsesJsonTextAndOnlyChunkStartTimestamp()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType?.MediaType);
            string body = await request.Content.ReadAsStringAsync(token);
            Assert.Contains("gpt-transcribe", body);
            Assert.Contains("name=response_format", body);
            Assert.DoesNotContain("diarized_json", body);
            Assert.DoesNotContain("chunking_strategy", body);
            Assert.DoesNotContain("name=prompt", body);
            return Response("""{"text":"  Первая строка.\nВторая строка.  ","languages":[{"code":"ru"}]}""");
        });
        using var http = new HttpClient(handler);
        var reply = await new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings("gpt-transcribe"), "unused timed prompt", AudioPath, default,
                context: null, source: "Me", chunkDurationSeconds: 32, ownStartSeconds: 2);
        Assert.Equal("[00:02] Me: Первая строка. Вторая строка.", reply.Text);
    }

    [Theory]
    [InlineData("gpt-transcribe", "{\"text\":\"\"}")]
    [InlineData("gpt-4o-transcribe-diarize", "{\"duration\":8,\"text\":\"\",\"segments\":[]}")]
    public async Task ValidModelReportedSilenceBecomesNoSpeech(string model, string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(json)));
        using var http = new HttpClient(handler);
        var reply = await new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings(model), "prompt", AudioPath, default);
        Assert.Equal("[NO_SPEECH]", reply.Text);
    }

    [Fact]
    public void MalformedAndOutOfRangeDiarizationIsRejectedWithoutPublishingText()
    {
        foreach (string json in new[]
        {
            "{}", "[]", "{\"duration\":3,\"text\":\"speech\",\"segments\":[]}",
            "{\"duration\":3,\"segments\":[{\"speaker\":\"A\",\"start\":-1,\"end\":2,\"text\":\"speech\"}]}",
            "{\"duration\":3,\"segments\":[{\"speaker\":\"A\",\"start\":2,\"end\":4.5,\"text\":\"speech\"}]}",
            "{\"duration\":3,\"segments\":[{\"speaker\":\"A\",\"start\":1,\"end\":2,\"text\":\"first\"},{\"speaker\":\"B\",\"start\":0.5,\"end\":1.5,\"text\":\"second\"}]}",
            "{\"duration\":3,\"segments\":[{\"speaker\":\"A\",\"start\":1,\"end\":2,\"text\":\" \"}]}"
        })
        {
            var ex = Assert.Throws<ChatApiException>(() => OpenAiTranscriptionFormatter.Parse(
                Encoding.UTF8.GetBytes(json), "Them", true, 3, 0));
            Assert.Equal(ApiFailureKind.InvalidResponse, ex.Kind);
            Assert.DoesNotContain("first", ex.Message);
        }
    }

    [Fact]
    public async Task InvalidTextObjectAndHttpErrorCannotBeSavedAsSpeech()
    {
        foreach (string json in new[] { "{}", "null", "{\"text\":null}", "{\"text\":123}", "not-json" })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Response(json)));
            using var http = new HttpClient(handler);
            var error = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(http, () => false, _ => "test-key")
                .TranscribeAsync(Settings("gpt-transcribe"), "prompt", AudioPath, default));
            Assert.Equal(ApiFailureKind.InvalidResponse, error.Kind);
        }
        using var rejected = new Handler((_, _) => Task.FromResult(Response("{\"error\":{\"message\":\"private\"}}", HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(rejected);
        var failure = await Assert.ThrowsAsync<ChatApiException>(() => new ChatApiClient(client, () => false, _ => "test-key")
            .TranscribeAsync(Settings("gpt-transcribe"), "prompt", AudioPath, default));
        Assert.Equal(ApiFailureKind.Permanent, failure.Kind);
        Assert.DoesNotContain("private", failure.Message);
    }

    [Fact]
    public async Task CancellationOfMultipartRequestPropagatesWithoutAcceptingAReply()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler(async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Response("{}");
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatApiClient(http, () => false, _ => "test-key")
            .TranscribeAsync(Settings("gpt-transcribe"), "prompt", AudioPath, cancel.Token));
    }

    [Theory]
    [InlineData("gpt-transcribe")]
    [InlineData("gpt-4o-transcribe-diarize")]
    public async Task NativeAsrPipelinePublishesTwoSourceTranscriptAndDurableCheckpoints(string model)
    {
        using var fixture = new JobTests();
        string session = fixture.Session(120);
        var saved = SessionStore.Read(session);
        var originalMe = File.ReadAllBytes(Path.Combine(session, "me.mp3"));
        var originalThem = File.ReadAllBytes(Path.Combine(session, "them.mp3"));
        var audio = saved.Audio!;
        var chunks = AudioChunker.PlanMono(0, 3L * AudioChunker.SampleRate, 120, audio.RelativeDirectory);
        foreach (var chunk in chunks)
            File.WriteAllBytes(Path.Combine(session, chunk.RelativePath), [1, 2, (byte)chunk.Index]);
        string prompt = Path.Combine(directory, "prompt.md");
        File.WriteAllText(prompt, "Mock transcription context");
        var settings = Settings(model) with { PromptFile = prompt };
        saved = saved with { Audio = audio with { Chunks = chunks }, Processing = saved.Processing with { Transcription = settings } };
        SessionStore.Save(session, saved);
        var config = new LoadedConfiguration(new Configuration
        {
            MeetingsRoot = Path.GetDirectoryName(session)!, Transcription = settings, Summary = saved.Processing.Summary,
            ChunkSeconds = 120
        }, saved.Processing.ConfigurationDirectory);
        using var handler = new Handler((_, _) => Task.FromResult(Response(model == "gpt-transcribe"
            ? "{\"text\":\"Речь из записи.\"}"
            : "{\"duration\":3,\"text\":\"Речь из записи.\",\"segments\":[{\"id\":\"s\",\"speaker\":\"A\",\"start\":1,\"end\":2,\"text\":\"Речь из записи.\"}]}")));
        using var http = new HttpClient(handler);
        using var jobs = new ProcessingJobs(config, () => false, http, readKey: _ => "test-key");
        await jobs.ProcessSessionAsync(session, default);

        var done = SessionStore.Read(session);
        Assert.True(done.JobState == JobState.Completed, $"Unexpected state {done.JobState}: {done.Error}");
        Assert.Equal(2, handler.Calls);
        Assert.Equal(chunks.Count, done.Stages["transcribe"].CompletedChunks);
        Assert.Contains("**Me:** Речь из записи.", File.ReadAllText(Path.Combine(session, "transcription.md")));
        Assert.Contains("**Them:** Речь из записи.", File.ReadAllText(Path.Combine(session, "transcription.md")));
        Assert.Equal(originalMe, File.ReadAllBytes(Path.Combine(session, "me.mp3")));
        Assert.Equal(originalThem, File.ReadAllBytes(Path.Combine(session, "them.mp3")));
        await jobs.ProcessSessionAsync(session, default);
        Assert.Equal(2, handler.Calls);
    }

    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
