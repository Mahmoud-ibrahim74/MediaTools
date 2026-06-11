using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace MediaTools.Presentation.Helpers;

public class GitHubUpdateResult
{
    public bool IsUpdateAvailable { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
}

public static class GitHubUpdateHelper
{
    private const string RepoUrl = "https://api.github.com/repos/Mahmoud-ibrahim74/MediaTools/releases/latest";

    public static async Task<GitHubUpdateResult> CheckForUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaToolsApp", "1.0"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "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");

            var response = await client.GetAsync(RepoUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubUpdateResult { IsUpdateAvailable = false };
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement) || 
                !root.TryGetProperty("html_url", out var urlElement))
            {
                return new GitHubUpdateResult { IsUpdateAvailable = false };
            }

            var tagName = tagElement.GetString() ?? "";
            var htmlUrl = urlElement.GetString() ?? "";

            // Parse versions (strip 'v' if present)
            var cleanTag = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) 
                ? tagName.Substring(1) 
                : tagName;

            if (Version.TryParse(cleanTag, out var latestVersion))
            {
                var localVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (localVersion != null && latestVersion > localVersion)
                {
                    return new GitHubUpdateResult
                    {
                        IsUpdateAvailable = true,
                        LatestVersion = tagName,
                        ReleaseUrl = htmlUrl
                    };
                }
            }

            return new GitHubUpdateResult { IsUpdateAvailable = false };
        }
        catch
        {
            // Fail silently on network errors
            return new GitHubUpdateResult { IsUpdateAvailable = false };
        }
    }
}
