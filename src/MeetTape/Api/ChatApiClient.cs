using OriApps.MeetTape.Diagnostics;
using OriApps.MeetTape.Settings;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OriApps.MeetTape.Api;

public enum ApiFailureKind { Transient, Permanent, InvalidResponse, Configuration, FileSystem }
public sealed class ChatApiException(ApiFailureKind kind, string message, HttpStatusCode? status = null,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public ApiFailureKind Kind { get; } = kind;
    public HttpStatusCode? Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    internal bool EmptyChoices { get; init; }
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

    internal static string InstructionFor(string? source) => source switch
    {
        null => SourceInstruction, // Existing stereo checkpoints and unfinished sessions.
        "Me" or "Them" => $"\nThis is MONO audio from the {source} recording source. " +
            $"Label every utterance as [MM:SS] {source}: text, regardless of voices or names in the user's prompt. " +
            "Timestamps are approximate and relative to the start of this chunk. Do not infer speakers or invent names. " +
            "If the entire chunk contains no speech, output only " + NoSpeechMarker + ". Do not use this marker for unintelligible speech.",
        _ => throw new InvalidDataException("Unsupported audio source in chunk manifest.")
    };

    public Task<TranscriptionReply> TranscribeAsync(ApiSettings settings, string prompt, string audioPath, CancellationToken token) =>
        TranscribeAsync(settings, prompt, audioPath, token, null);

    internal async Task<TranscriptionReply> TranscribeAsync(ApiSettings settings, string prompt, string audioPath, CancellationToken token,
        string? context, string? source = null)
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
        byte[] body = CreateBody(settings.Model, prompt + InstructionFor(source), audio);
        return new(await SendAsync(settings, key, body, "transcription", context, token), audioHash);
    }

    public Task<string> SummarizeAsync(ApiSettings settings, string prompt, string transcript, CancellationToken token) =>
        SummarizeAsync(settings, prompt, transcript, token, null);

    internal async Task<string> SummarizeAsync(ApiSettings settings, string prompt, string transcript, CancellationToken token, string? context)
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
        return await SendAsync(settings, key, body, "summary", context, token);
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

    private async Task<string> SendAsync(ApiSettings settings, string key, byte[] body, string stage, string? context, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        EnsureIdle(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        var timer = Stopwatch.StartNew();
        string requestContext = $"stage={stage}; {context ?? "session=none; attempt=1"}; requested_model={MetadataToken(settings.Model)}; " +
            $"endpoint={new Uri(settings.Endpoint).GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)}/";
        try
        {
            AppDiagnostics.Info("api", $"Request started; {requestContext}; request_bytes={body.Length}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            AppDiagnostics.Info("api", $"Response HTTP {(int)response.StatusCode}");
            ChatApiException? failure = null;
            if (!response.IsSuccessStatusCode)
            {
                var after = response.Headers.RetryAfter;
                TimeSpan? delay = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                failure = new ChatApiException(Classify(response.StatusCode), $"API HTTP {(int)response.StatusCode}; transcription not accepted.",
                    response.StatusCode, delay);
            }
            byte[] raw;
            try { raw = await ReadResponseAsync(response, deadline.Token); }
            catch (Exception ex) when (!token.IsCancellationRequested
                && (ex is ChatApiException || failure is not null && ex is IOException or OperationCanceledException))
            {
                LogResponse(response, null, requestContext, timer.ElapsedMilliseconds,
                    failure?.Kind == ApiFailureKind.Transient ? "retry; body=unavailable" : "pause; body=unavailable");
                if (failure is not null) throw failure;
                throw;
            }
            token.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                LogResponse(response, raw, requestContext, timer.ElapsedMilliseconds, failure.Kind == ApiFailureKind.Transient ? "retry" : "pause");
                throw failure;
            }
            try
            {
                string text = ParseText(raw, response.StatusCode);
                LogResponse(response, raw, requestContext, timer.ElapsedMilliseconds, "accepted");
                return text;
            }
            catch (ChatApiException ex)
            {
                LogResponse(response, raw, requestContext, timer.ElapsedMilliseconds,
                    stage == "transcription" && ex.EmptyChoices ? "bounded-retry" : "pause");
                throw;
            }
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            AppDiagnostics.Error("api", $"Request timeout; {requestContext}; elapsed_ms={timer.ElapsedMilliseconds}; decision=retry", ex);
            throw new ChatApiException(ApiFailureKind.Transient, "API request timed out; transcription not accepted.");
        }
        catch (HttpRequestException ex)
        {
            string decision = ex.StatusCode is { } failedStatus && Classify(failedStatus) == ApiFailureKind.Permanent ? "pause" : "retry";
            AppDiagnostics.Error("api", $"Request connection; {requestContext}; elapsed_ms={timer.ElapsedMilliseconds}; decision={decision}", ex);
            throw new ChatApiException(ex.StatusCode is { } status ? Classify(status) : ApiFailureKind.Transient,
                AppDiagnostics.UserMessage(ex, "API connection failed; transcription not accepted."), ex.StatusCode);
        }
        catch (IOException ex)
        {
            AppDiagnostics.Error("api", $"Read response connection; {requestContext}; elapsed_ms={timer.ElapsedMilliseconds}; decision=retry", ex);
            throw new ChatApiException(ApiFailureKind.Transient, "API response connection interrupted; transcription not accepted.");
        }
    }

    private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new ChatApiException(ApiFailureKind.InvalidResponse, "API response exceeds 2 MiB; transcription not accepted.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (content.Length + count > MaxResponseBytes)
                throw new ChatApiException(ApiFailureKind.InvalidResponse, "API response exceeds 2 MiB; transcription not accepted.");
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }

    private static void LogResponse(HttpResponseMessage response, byte[]? raw, string context, long elapsed, string decision)
    {
        var fields = new List<string> { $"Response HTTP {(int)response.StatusCode}; {context}; elapsed_ms={elapsed}; " +
            $"bytes={(raw is null ? response.Content.Headers.ContentLength?.ToString() ?? "unknown" : raw.Length.ToString())}; decision={decision}" };
        foreach (string name in new[] { "x-request-id", "request-id", "x-correlation-id", "x-openai-request-id", "x-goog-request-id", "traceparent", "cf-ray" })
            if (response.Headers.TryGetValues(name, out var values)) fields.Add($"{name}={MetadataToken(values.FirstOrDefault())}");
        if (raw is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var id)) fields.Add("response_id=" + MetadataToken(id));
                    if (root.TryGetProperty("request_id", out var requestId)) fields.Add("request_id=" + MetadataToken(requestId));
                    if (root.TryGetProperty("requestId", out var providerRequestId)) fields.Add("request_id=" + MetadataToken(providerRequestId));
                    if (root.TryGetProperty("responseId", out var responseId)) fields.Add("response_id=" + MetadataToken(responseId));
                    if (root.TryGetProperty("model", out var model)) fields.Add("model=" + MetadataToken(model));
                    if (root.TryGetProperty("modelVersion", out var modelVersion)) fields.Add("model_version=" + MetadataToken(modelVersion));
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                    {
                        fields.Add("choices=" + choices.GetArrayLength());
                        if (choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object)
                        {
                            if (choices[0].TryGetProperty("finish_reason", out var finish)) fields.Add("finish_reason=" + KnownReason(finish, false));
                            if (choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                            {
                                fields.Add("content=" + (message.TryGetProperty("content", out var value) ? value.ValueKind.ToString() : "missing"));
                                if (message.TryGetProperty("refusal", out var refusal)) fields.Add("refusal_present=" + (refusal.ValueKind != JsonValueKind.Null));
                            }
                        }
                    }
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        foreach (string name in new[] { "prompt_tokens", "completion_tokens", "total_tokens", "input_tokens", "output_tokens" })
                            if (usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                && value.TryGetInt64(out long count)) fields.Add($"{name}={count}");
                    if (root.TryGetProperty("usageMetadata", out var providerUsage) && providerUsage.ValueKind == JsonValueKind.Object)
                        foreach (string name in new[] { "promptTokenCount", "candidatesTokenCount", "totalTokenCount", "thoughtsTokenCount", "cachedContentTokenCount" })
                            if (providerUsage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                && value.TryGetInt64(out long count)) fields.Add($"{name}={count}");
                    foreach (string name in new[] { "promptFeedback", "prompt_feedback" })
                        if (root.TryGetProperty(name, out var feedback) && feedback.ValueKind == JsonValueKind.Object)
                            foreach (string reason in new[] { "blockReason", "block_reason" })
                                if (feedback.TryGetProperty(reason, out var block)) fields.Add("block_reason=" + KnownReason(block, true));
                    if (root.TryGetProperty("error", out var error))
                    {
                        fields.Add("error_shape=" + error.ValueKind);
                        if (error.ValueKind == JsonValueKind.Object)
                        {
                            if (error.TryGetProperty("type", out var type)) fields.Add("error_type=" + ErrorToken(type));
                            if (error.TryGetProperty("code", out var code)) fields.Add("error_code=" + ErrorToken(code));
                            if (error.TryGetProperty("status", out var status)) fields.Add("error_status=" + ErrorToken(status));
                            fields.Add("error_reason=" + ErrorReason(error));
                        }
                    }
                }
            }
            catch (JsonException) { fields.Add("json=invalid"); }
        }
        AppDiagnostics.Info("api", string.Join("; ", fields));
    }

    private static string MetadataToken(JsonElement value) => value.ValueKind == JsonValueKind.String ? MetadataToken(value.GetString()) : value.ValueKind.ToString();
    private static string MetadataToken(string? value) => string.IsNullOrEmpty(value) ? "empty" : value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/') ? value : "[redacted value]";

    private static string KnownReason(JsonElement value, bool block)
    {
        if (value.ValueKind != JsonValueKind.String) return value.ValueKind.ToString();
        string? text = value.GetString();
        return block ? text is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "OTHER" or "IMAGE_SAFETY" or "MODEL_ARMOR" or "BLOCK_REASON_UNSPECIFIED" ? text : "other-string"
            : text is "stop" or "length" or "content_filter" or "tool_calls" or "function_call" ? text : "other-string";
    }

    private static string ErrorToken(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number.ToString();
        if (value.ValueKind != JsonValueKind.String) return value.ValueKind.ToString();
        string text = value.GetString()!;
        return text.ToLowerInvariant() is "invalid_request_error" or "model_not_found" or "unsupported_input" or "unsupported_audio"
            or "content_filter" or "safety" or "rate_limit_exceeded" or "quota_exceeded" or "invalid_api_key" or "authentication_error"
            or "permission_denied" or "server_error" or "overloaded_error" or "invalid_argument" or "not_found" or "resource_exhausted"
            ? text : "[redacted value]";
    }

    private static string ErrorReason(JsonElement error)
    {
        string hint = string.Join(" ", new[] { "code", "type", "message" }.Select(name => error.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()![..Math.Min(value.GetString()!.Length, 2048)] : "")).ToLowerInvariant();
        if (hint.Contains("model") && (hint.Contains("unknown") || hint.Contains("not_found") || hint.Contains("not found") || hint.Contains("does not exist"))) return "unknown-model";
        if (hint.Contains("audio") && (hint.Contains("unsupported") || hint.Contains("not support") || hint.Contains("invalid"))) return "audio-input-unsupported";
        if (hint.Contains("blocked") || hint.Contains("content_filter") || hint.Contains("safety")) return "blocked";
        if (hint.Contains("rate_limit") || hint.Contains("quota") || hint.Contains("rate limit")) return "rate-limit";
        return "provider-error; message=[redacted]";
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

    internal static string ParseText(byte[] raw, HttpStatusCode status)
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
            AppDiagnostics.Info("api", $"Rejected response HTTP {(int)status}; bytes={raw.Length}; {DescribeRejectedResponse(root)}");
            if (status == HttpStatusCode.OK && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var emptyChoices) && emptyChoices.ValueKind == JsonValueKind.Array
                && emptyChoices.GetArrayLength() == 0 && !HasProviderBlock(root))
                throw new ChatApiException(ApiFailureKind.InvalidResponse, "API HTTP 200 returned empty choices; result not accepted.", status)
                    { EmptyChoices = true };
        }
        catch (JsonException)
        {
            AppDiagnostics.Info("api", $"Rejected response HTTP {(int)status}; bytes={raw.Length}; reason=invalid-json");
        }
        throw new ChatApiException(ApiFailureKind.InvalidResponse, "API returned malformed, empty or incomplete transcription; result not accepted.");
    }

    private static bool HasProviderBlock(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) return true;
        foreach (string name in new[] { "promptFeedback", "prompt_feedback" })
            if (root.TryGetProperty(name, out var feedback) && feedback.ValueKind == JsonValueKind.Object)
                foreach (string reason in new[] { "blockReason", "block_reason" })
                    if (feedback.TryGetProperty(reason, out var block) && block.ValueKind != JsonValueKind.Null
                        && (block.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(block.GetString()))) return true;
        return false;
    }

    private static string DescribeRejectedResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return $"reason=root-not-object; root={root.ValueKind}";
        if (!root.TryGetProperty("choices", out var choices)) return "reason=choices-missing; root=Object; choices=missing";
        if (choices.ValueKind != JsonValueKind.Array) return $"reason=choices-not-array; root=Object; choices={choices.ValueKind}";
        if (choices.GetArrayLength() == 0) return "reason=choices-empty; root=Object; choices=Array[0]";

        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object)
            return $"reason=choice-not-object; choices=Array[{choices.GetArrayLength()}]; choice0={first.ValueKind}";

        bool hasFinish = first.TryGetProperty("finish_reason", out var finish);
        string? finishValue = hasFinish && finish.ValueKind == JsonValueKind.String ? finish.GetString() : null;
        string finishShape = finishValue switch
        {
            "stop" or "length" or "content_filter" or "tool_calls" or "function_call" => finishValue,
            null => hasFinish ? finish.ValueKind.ToString() : "missing",
            _ => "other-string"
        };
        bool hasMessage = first.TryGetProperty("message", out var message);
        var content = default(JsonElement);
        bool hasContent = hasMessage && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out content);
        string? contentText = hasContent && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        string contentShape = !hasContent ? "missing" : content.ValueKind switch
        {
            JsonValueKind.String => $"String[length={contentText!.Length}, blank={string.IsNullOrWhiteSpace(contentText)}]",
            JsonValueKind.Array => $"Array[{content.GetArrayLength()}]",
            _ => content.ValueKind.ToString()
        };
        string reason = !hasFinish ? "finish-reason-missing"
            : finish.ValueKind != JsonValueKind.String ? "finish-reason-not-string"
            : finishValue != "stop" ? "finish-reason-not-stop"
            : !hasMessage ? "message-missing"
            : message.ValueKind != JsonValueKind.Object ? "message-not-object"
            : !hasContent ? "content-missing"
            : content.ValueKind != JsonValueKind.String ? "content-not-string"
            : "content-blank";
        return $"reason={reason}; choices=Array[{choices.GetArrayLength()}]; finish_reason={finishShape}; " +
            $"message={(hasMessage ? message.ValueKind.ToString() : "missing")}; content={contentShape}";
    }
}
