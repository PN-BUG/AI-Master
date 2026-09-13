using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SharedUpdates;

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string ReleasePageUrl,
    bool UpdateAvailable,
    bool ReleaseFound = true,
    IReadOnlyList<GitHubReleaseAsset>? Assets = null)
{
    public IReadOnlyList<GitHubReleaseAsset> ReleaseAssets => Assets ?? Array.Empty<GitHubReleaseAsset>();
}

public sealed record GitHubReleaseAsset(
    string Name,
    string DownloadUrl,
    long SizeBytes,
    string? Digest);

public static class GitHubUpdateChecker
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public static async Task<UpdateCheckResult> CheckAsync(
        string owner,
        string repository,
        Assembly assembly,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentNullException.ThrowIfNull(assembly);

        var currentVersion = GetCurrentVersion(assembly);
        var apiUrl = BuildLatestReleaseApiUrl(owner, repository);
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd($"{repository}-update-check/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(
                currentVersion,
                currentVersion,
                $"https://github.com/{owner}/{repository}/releases",
                UpdateAvailable: false,
                ReleaseFound: false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseReleaseJson(json, currentVersion, owner, repository);
    }

    public static UpdateCheckResult ParseReleaseJson(
        string json,
        string currentVersion,
        string owner,
        string repository)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var latestVersion = root.GetProperty("tag_name").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(latestVersion))
            throw new InvalidOperationException("The latest GitHub release has no version tag.");

        var releasePageUrl = root.TryGetProperty("html_url", out var urlNode)
            ? urlNode.GetString()
            : null;
        releasePageUrl ??= $"https://github.com/{owner}/{repository}/releases/latest";
        var assets = new List<GitHubReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsNode) && assetsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetNode in assetsNode.EnumerateArray())
            {
                var name = assetNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                var downloadUrl = assetNode.TryGetProperty("browser_download_url", out var downloadNode)
                    ? downloadNode.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl)) continue;
                var size = assetNode.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var value)
                    ? value
                    : 0;
                var digest = assetNode.TryGetProperty("digest", out var digestNode)
                    ? digestNode.GetString()
                    : null;
                assets.Add(new GitHubReleaseAsset(name, downloadUrl, size, digest));
            }
        }

        return new UpdateCheckResult(
            currentVersion,
            latestVersion,
            releasePageUrl,
            IsNewerVersion(latestVersion, currentVersion),
            ReleaseFound: true,
            Assets: assets);
    }

    public static string BuildLatestReleaseApiUrl(string owner, string repository) =>
        $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest";

    public static string GetCurrentVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    public static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        if (!TryParseVersion(latestVersion, out var latest, out var latestIsPrerelease)) return false;
        if (!TryParseVersion(currentVersion, out var current, out var currentIsPrerelease)) return true;

        var comparison = latest.CompareTo(current);
        if (comparison != 0) return comparison > 0;
        return currentIsPrerelease && !latestIsPrerelease;
    }

    private static bool TryParseVersion(string value, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0, 0);
        isPrerelease = false;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        var metadataAt = normalized.IndexOf('+');
        if (metadataAt >= 0) normalized = normalized[..metadataAt];
        var prereleaseAt = normalized.IndexOf('-');
        if (prereleaseAt >= 0)
        {
            isPrerelease = true;
            normalized = normalized[..prereleaseAt];
        }

        var segments = normalized.Split('.');
        if (segments.Length is < 1 or > 4) return false;
        var parts = new int[4];
        for (var index = 0; index < segments.Length; index++)
            if (!int.TryParse(segments[index], out parts[index]) || parts[index] < 0) return false;

        version = new Version(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }
}

