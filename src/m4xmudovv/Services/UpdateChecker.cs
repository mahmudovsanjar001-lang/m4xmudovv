using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace m4xmudovv.Services;

/// <summary>
/// Real (not simulated) update check against an optional JSON feed: {"version":"1.2.0","url":"https://..."}.
/// With no feed configured it says so honestly instead of pretending to check.
/// </summary>
public static class UpdateChecker
{
    public static async Task<string> CheckAsync(string? feedUrl, Version current, CancellationToken cancellationToken = default)
    {
        var currentText = $"{current.Major}.{current.Minor}.{current.Build}";
        if (string.IsNullOrWhiteSpace(feedUrl))
            return $"No update source is configured for this build, so nothing was checked. You are running version {currentText}.";

        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "The update feed address in settings.json is not a valid https URL.";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await client.GetStringAsync(uri, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var versionText = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            if (!Version.TryParse(versionText, out var latest))
                return "The update feed did not contain a valid version number.";

            if (latest > current)
            {
                var link = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                return $"Version {versionText} is available (you have {currentText})." + (string.IsNullOrWhiteSpace(link) ? string.Empty : $" Download: {link}");
            }
            return $"You are up to date (version {currentText}).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }   // cancelled by the caller
        catch (HttpRequestException ex) { return $"The update server could not be reached: {ex.Message}"; }
        catch (OperationCanceledException) { return "The update check timed out."; }
        catch (JsonException) { return "The update feed is not valid JSON."; }
    }
}
