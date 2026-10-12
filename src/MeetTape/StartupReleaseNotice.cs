using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeetTape;

internal static class StartupReleaseNotice
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Akilaydin/OriApps.MeetTape/releases/latest";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    internal static string Get()
    {
        string current = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        using var client = new HttpClient();
        return Get(current, client, CheckTimeout);
    }

    internal static string Get(string current, HttpClient client, TimeSpan timeout)
    {
        string localTag = current.Split('+', 2)[0];
        if (!TryParseVersion(localTag, out var local) || local == new Version(0, 0, 0))
        {
            return $"Current version: {localTag} (development build){Environment.NewLine}Upstream version: not checked for development builds";
        }

        string notice = $"Current version: v{local}{Environment.NewLine}";
        Version? upstream;
        try
        {
            using var deadline = new CancellationTokenSource(timeout);
            // WaitAsync bounds the version check even if a network handler ignores cancellation.
            upstream = FetchLatestAsync(client, deadline.Token).WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException
                                   or IOException or JsonException)
        {
            upstream = null;
        }

        if (upstream is null)
        {
            return notice + "Upstream version: unavailable (could not check GitHub)";
        }

        notice += $"Upstream version: v{upstream}{Environment.NewLine}";
        int comparison = upstream.CompareTo(local);
        if (comparison > 0)
            return notice + "Update available. Use Update in MeetTape or run meettape-cli update.";
        return notice + (comparison == 0 ? "Up to date." : "Current version is newer than the latest published release.");
    }

    private static async Task<Version?> FetchLatestAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd("MeetTape");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("tag_name", out var tag)
            || tag.ValueKind != JsonValueKind.String)
            return null;

        string? value = tag.GetString();
        return value is not null && value.StartsWith('v') && TryParseVersion(value[1..], out var version)
            ? version : null;
    }

    private static bool TryParseVersion(string text, out Version? version)
    {
        version = null;
        return Regex.IsMatch(text, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)
            && Version.TryParse(text, out version);
    }
}
