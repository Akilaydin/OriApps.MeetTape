using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeetTape;

public enum ApiFailureKind { Transient, Permanent, InvalidResponse, Configuration, FileSystem }
public sealed class ChatApiException(ApiFailureKind kind, string message, HttpStatusCode? status = null,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public ApiFailureKind Kind { get; } = kind;
    public HttpStatusCode? Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public sealed record TranscriptionReply(string Text, string AudioSha256);

public sealed class ChatApiClient(HttpClient client, Func<bool> captureActive,
    Func<ApiSettings, string>? readKey = null, TimeSpan? timeout = null)
{
    public const int MaxAudioBytes = 8 * 1024 * 1024;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    internal const string NoSpeechMarker = "[NO_SPEECH]";
    public const string SourceInstruction = "\nStereo: the left channel is Me, the right channel is Them. The Me microphone may contain echoes of Them; output identical speech only once. " +
        "Each utterance: [MM:SS] Me/Them/Unknown: text. Timestamps are approximate and relative to the start of this chunk. Do not invent names; use Unknown for an unclear source. " +
        "If the entire chunk contains no speech, output only " + NoSpeechMarker + ". Do not use this marker for unintelligible speech.";

    public async Task<TranscriptionReply> TranscribeAsync(ApiSettings settings, string prompt, string audioPath, CancellationToken token)
    {
        EnsureIdle(token);
        try
        {
            ConfigurationLoader.ValidateApi(settings, "transcription");
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("Transcription prompt is empty.");
        }
        catch (InvalidDataException ex)
        { throw new ChatApiException(ApiFailureKind.Configuration, ex.Message + " No request made."); }
        string key = ReadKey(settings, "transcription");
        byte[] audio;
        try
        {
            await using var file = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            if (file.Length is <= 0 or > MaxAudioBytes)
                throw new ChatApiException(ApiFailureKind.Permanent, "Audio chunk is empty or exceeds 8 MiB; prepare smaller chunks.", HttpStatusCode.RequestEntityTooLarge);
            audio = new byte[(int)file.Length];
            await file.ReadExactlyAsync(audio, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Error("api", "Read transcription audio", ex);
            throw new ChatApiException(ApiFailureKind.FileSystem, AppDiagnostics.UserMessage(ex, "Cannot read audio chunk; check selected files and permissions."));
        }
        string audioHash = Convert.ToHexString(SHA256.HashData(audio));
        byte[] body = CreateBody(settings.Model, prompt + SourceInstruction, audio);
        return new(await SendAsync(settings, key, body, token), audioHash);
    }

    public async Task<string> SummarizeAsync(ApiSettings settings, string prompt, string transcript, CancellationToken token)
    {
        EnsureIdle(token);
        try
        {
            ConfigurationLoader.ValidateApi(settings, "summary");
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("Summary prompt is empty.");
            if (string.IsNullOrWhiteSpace(transcript)) throw new InvalidDataException("Summary transcript is empty.");
        }
        catch (InvalidDataException ex)
        { throw new ChatApiException(ApiFailureKind.Configuration, ex.Message + " No request made."); }
        string key = ReadKey(settings, "summary");
        if (System.Text.Encoding.UTF8.GetByteCount(prompt) + System.Text.Encoding.UTF8.GetByteCount(transcript) > 2 * 1024 * 1024)
            throw new ChatApiException(ApiFailureKind.Permanent, "Summary input exceeds 2 MiB; no automatic truncation or partial summary.");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { model = settings.Model, stream = false,
            messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = transcript } } });
        return await SendAsync(settings, key, body, token);
    }

    private string ReadKey(ApiSettings settings, string stage)
    {
        string key;
        try { key = (readKey ?? (api => ApiKeyProvider.Get(api)))(settings); }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { throw new ChatApiException(ApiFailureKind.Configuration,
            $"Cannot read the {stage} API key from environment variable '{settings.ApiKeyEnvironmentVariable}'. Check the key in Settings; no request made."); }
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ChatApiException(ApiFailureKind.Configuration,
                $"The {stage} API key in environment variable '{settings.ApiKeyEnvironmentVariable}' is missing or invalid. Set the key in Settings; no request made.");
        AppDiagnostics.ProtectSecret(key);
        return key;
    }

    private async Task<string> SendAsync(ApiSettings settings, string key, byte[] body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        EnsureIdle(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            AppDiagnostics.Info("api", "Request started");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            AppDiagnostics.Info("api", $"Response HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var after = response.Headers.RetryAfter;
                TimeSpan? delay = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                throw new ChatApiException(Classify(response.StatusCode), $"API HTTP {(int)response.StatusCode}; transcription not accepted.",
                    response.StatusCode, delay);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new ChatApiException(ApiFailureKind.InvalidResponse, "API response exceeds 2 MiB; transcription not accepted.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var content = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (content.Length + count > MaxResponseBytes)
                    throw new ChatApiException(ApiFailureKind.InvalidResponse, "API response exceeds 2 MiB; transcription not accepted.");
                content.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            return ParseText(content.ToArray());
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            AppDiagnostics.Error("api", "Request timeout", ex);
            throw new ChatApiException(ApiFailureKind.Transient, "API request timed out; transcription not accepted.");
        }
        catch (HttpRequestException ex)
        {
            AppDiagnostics.Error("api", "Request connection", ex);
            throw new ChatApiException(ex.StatusCode is { } status ? Classify(status) : ApiFailureKind.Transient,
                AppDiagnostics.UserMessage(ex, "API connection failed; transcription not accepted."), ex.StatusCode);
        }
        catch (IOException ex)
        {
            AppDiagnostics.Error("api", "Read response connection", ex);
            throw new ChatApiException(ApiFailureKind.Transient, "API response connection interrupted; transcription not accepted.");
        }
    }

    private void EnsureIdle(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (captureActive()) throw new OperationCanceledException("Capture active; API request deferred.");
    }

    internal static byte[] CreateBody(string model, string prompt, byte[] audio) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        model, stream = false, messages = new[] { new { role = "user", content = new object[]
        {
            new { type = "text", text = prompt },
            new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(audio), format = "mp3" } }
        } } }
    });

    private static ApiFailureKind Classify(HttpStatusCode status) =>
        (int)status is 408 or 429 or >= 500 and <= 599 ? ApiFailureKind.Transient : ApiFailureKind.Permanent;

    internal static string ParseText(byte[] raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("finish_reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop"
                    && first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { } text && !string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (JsonException) { }
        throw new ChatApiException(ApiFailureKind.InvalidResponse, "API returned malformed, empty or incomplete transcription; result not accepted.");
    }
}
