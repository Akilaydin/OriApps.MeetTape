using OriApps.MeetTape.Api;
using OriApps.MeetTape.Audio;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace OriApps.MeetTape.Diagnostics;

internal static class AppDiagnostics
{
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetTape", "Logs");
    internal static FileDiagnostics? ProcessLog;
    private static readonly AsyncLocal<FileDiagnostics?> scopedLog = new();
    private static FileDiagnostics? Log => scopedLog.Value ?? ProcessLog;

    internal static IDisposable Use(FileDiagnostics log)
    {
        var previous = scopedLog.Value;
        scopedLog.Value = log;
        return new Scope(() => scopedLog.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }

    internal static void Info(string component, string stage) => Log?.Write("Information", component, stage);
    internal static void Error(string component, string stage, Exception error) => Log?.Write("Error", component, stage, error);
    internal static void ProtectSecret(string secret) => Log?.ProtectSecret(secret);

    internal static string UserMessage(Exception error, string fallback)
    {
        var chain = new List<Exception>();
        for (Exception? current = error; current is not null && chain.Count < 8; current = current.InnerException) chain.Add(current);
        string? cause = chain.Any(e => e.HResult == unchecked((int)0x88890004))
            ? "Audio device is disconnected or unavailable. Reconnect it or select another device, then retry."
            : chain.Any(e => e is AuthenticationException || e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
            ? "Secure connection failed. Check the server certificate, system clock and network."
            : chain.Any(e => e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }
                || e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
            ? "Server name could not be resolved. Check the endpoint, DNS and network, then retry."
            : chain.Any(e => e is TimeoutException or TaskCanceledException)
            ? "Operation timed out. Check network/device availability, then retry."
            : chain.OfType<HttpRequestException>().FirstOrDefault() is { } http
            ? http.StatusCode is { } status
                ? $"Server returned HTTP {(int)status}. Check service availability and API settings, then retry."
                : "Network connection failed. Check the endpoint, network and proxy, then retry."
            : chain.Any(e => e is UnauthorizedAccessException)
            ? "Access denied. Check file/device permissions and whether another app is using them, then retry."
            : chain.Any(e => e is FileNotFoundException or DirectoryNotFoundException)
            ? "Required file or folder is missing. Check the selected paths, then retry."
            : chain.OfType<IOException>().FirstOrDefault(e => e is not RecordingException) is { } io
            ? (io.HResult & 0xffff) switch
            {
                112 or 39 => "Storage is full. Free disk space, then retry.",
                32 or 33 => "Storage file is in use. Close the other operation, then retry.",
                _ => "Storage operation failed. Check disk availability, free space and permissions, then retry."
            }
            : chain.OfType<Win32Exception>().FirstOrDefault() is { } native
            ? native.NativeErrorCode switch
            {
                2 or 3 => "Required program or device is missing. Check FFmpeg and selected devices, then retry.",
                5 => "Access denied. Check program/device permissions, then retry.",
                _ => "Windows operation failed. Check selected devices and program availability, then retry."
            }
            : null;
        return cause is null ? error is InvalidDataException or InvalidOperationException or RecordingException or ChatApiException
            ? error.Message : fallback : fallback + " " + cause;
    }
}

internal sealed class FileDiagnostics : IDisposable, IAsyncDisposable
{
    private readonly string directory;
    private readonly int maxBytes;
    private readonly int retainedFiles;
    private readonly Channel<(DateTimeOffset Time, string Level, string Component, string Stage, Exception? Error)> queue =
        Channel.CreateBounded<(DateTimeOffset, string, string, string, Exception?)>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private readonly Task worker;
    private readonly HashSet<string> secrets = new(StringComparer.Ordinal);
    private string[] orderedSecrets = [];
    private readonly object secretGate = new();
    private static readonly Regex Credentials = new(@"(?i)(bearer\s+\S+|(?:api[_-]?key|access[_-]?token|authorization|password|secret)\s*[:=]\s*(?:bearer\s+)?[^\s,;]+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Url = new(@"https?://[^\s<>""']+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal FileDiagnostics(string directory, int maxBytes = 1024 * 1024, int retainedFiles = 5)
    {
        this.directory = directory;
        this.maxBytes = Math.Max(1024, maxBytes);
        this.retainedFiles = Math.Clamp(retainedFiles, 1, 10);
        worker = Task.Run(WriteAsync);
    }

    internal void Write(string level, string component, string stage, Exception? error = null) =>
        queue.Writer.TryWrite((DateTimeOffset.Now, level, component, stage, error));

    internal void ProtectSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (secretGate)
            if (secrets.Add(secret)) orderedSecrets = secrets.OrderByDescending(s => s.Length).ToArray();
    }

    private string Redact(string text)
    {
        lock (secretGate)
            foreach (string secret in orderedSecrets) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        text = Credentials.Replace(text, "[redacted credential]");
        return Url.Replace(text, match => Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + "/[redacted path]" : "[redacted URL]");
    }

    private async Task WriteAsync()
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            try
            {
                string detail = item.Error is null ? "" : Redact(item.Error.ToString());
                if (detail.Length > 12000) detail = detail[..12000] + " [truncated]";
                string entry = JsonSerializer.Serialize(new
                {
                    time = item.Time, level = item.Level, component = item.Component, stage = Redact(item.Stage),
                    errorType = item.Error?.GetType().FullName, hresult = item.Error?.HResult,
                    httpStatus = item.Error is HttpRequestException http ? (int?)http.StatusCode : item.Error is ChatApiException api ? (int?)api.Status : null,
                    requestError = (item.Error as HttpRequestException)?.HttpRequestError.ToString(),
                    details = detail
                }) + "\n";
                if (Encoding.UTF8.GetByteCount(entry) > maxBytes) entry = JsonSerializer.Serialize(new
                { time = item.Time, level = item.Level, component = item.Component, stage = "Oversized diagnostic truncated", errorType = item.Error?.GetType().FullName }) + "\n";
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "meettape.log");
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(entry) > maxBytes)
                {
                    for (int index = retainedFiles - 1; index >= 1; index--)
                    {
                        string source = index == 1 ? path : path + "." + (index - 1);
                        string target = path + "." + index;
                        if (File.Exists(source)) File.Move(source, target, overwrite: true);
                    }
                    if (retainedFiles == 1) File.Delete(path);
                }
                await File.AppendAllTextAsync(path, entry, new UTF8Encoding(false));
            }
            catch (Exception) { /* Logging failures must never affect recording or saved work. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        try { worker.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* Exit remains available if the log storage fails. */ }
    }
}
