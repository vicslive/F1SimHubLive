using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Compares the running picker's assembly version against the latest
/// GitHub Release tag for <c>vicslive/F1SimHubLive</c>. The result is cached
/// in <c>%LOCALAPPDATA%\F1SimHubLive-Picker\update-check.json</c> for 24 h
/// so launching the picker repeatedly doesn't hammer the GitHub API
/// (unauthenticated rate limit is 60 req/h per IP).
///
/// The check is best-effort: any network/parse failure returns
/// <see cref="UpdateCheckResult.Unknown"/> and the picker silently degrades
/// to "show version, no update hint".
/// </summary>
internal sealed class UpdateChecker
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/vicslive/F1SimHubLive/releases/latest";
    private const string RepoReleasesUrl =
        "https://github.com/vicslive/F1SimHubLive/releases";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly Version _currentVersion;
    private readonly string _cachePath;

    public UpdateChecker(Version currentVersion)
    {
        _currentVersion = currentVersion;
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cachePath = Path.Combine(root, "F1SimHubLive-Picker", "update-check.json");
    }

    public string RepoReleasesPage => RepoReleasesUrl;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var cached = TryReadCache();
        if (cached is not null
            && DateTime.UtcNow - cached.CheckedAtUtc < CacheTtl
            && !string.IsNullOrEmpty(cached.LatestTag))
        {
            return Build(cached.LatestTag!, cached.HtmlUrl ?? RepoReleasesUrl);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            // GitHub requires a User-Agent on every request; using the product
            // name makes the request easy to identify in their logs if needed.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"F1SimHubLive-Picker/{_currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await http.GetStringAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? (t.GetString() ?? "")
                : "";
            string url = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String
                ? (h.GetString() ?? RepoReleasesUrl)
                : RepoReleasesUrl;

            if (string.IsNullOrWhiteSpace(tag))
                return UpdateCheckResult.Unknown(_currentVersion);

            WriteCache(new CacheEntry
            {
                CheckedAtUtc = DateTime.UtcNow,
                LatestTag = tag,
                HtmlUrl = url,
            });

            return Build(tag, url);
        }
        catch
        {
            // Network blocked, GitHub down, rate-limited, JSON drift, etc.
            // Fall back to whatever the cache still holds (even if stale) so
            // the user sees *some* signal rather than nothing.
            if (cached is not null && !string.IsNullOrEmpty(cached.LatestTag))
                return Build(cached.LatestTag!, cached.HtmlUrl ?? RepoReleasesUrl);
            return UpdateCheckResult.Unknown(_currentVersion);
        }
    }

    private UpdateCheckResult Build(string latestTag, string htmlUrl)
    {
        Version? latest = ParseTag(latestTag);
        if (latest is null)
            return UpdateCheckResult.Unknown(_currentVersion);

        bool isNewer = NormalizedCompare(latest, _currentVersion) > 0;
        return new UpdateCheckResult(
            Current: _currentVersion,
            Latest: latest,
            LatestTag: latestTag,
            HtmlUrl: htmlUrl,
            IsUpdateAvailable: isNewer);
    }

    private static Version? ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string trimmed = tag.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(1);
        // Drop any pre-release/build suffix ("1.2.0-rc1" -> "1.2.0").
        int dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed.Substring(0, dash);
        return Version.TryParse(trimmed, out var v) ? v : null;
    }

    /// <summary>
    /// Compares two versions ignoring trailing zero components so
    /// <c>1.1.4</c> and <c>1.1.4.0</c> compare equal.
    /// </summary>
    private static int NormalizedCompare(Version a, Version b)
    {
        int aMajor = a.Major, aMinor = a.Minor;
        int aBuild = Math.Max(a.Build, 0), aRev = Math.Max(a.Revision, 0);
        int bMajor = b.Major, bMinor = b.Minor;
        int bBuild = Math.Max(b.Build, 0), bRev = Math.Max(b.Revision, 0);

        int c = aMajor.CompareTo(bMajor); if (c != 0) return c;
        c = aMinor.CompareTo(bMinor); if (c != 0) return c;
        c = aBuild.CompareTo(bBuild); if (c != 0) return c;
        return aRev.CompareTo(bRev);
    }

    private CacheEntry? TryReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            string json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<CacheEntry>(json);
        }
        catch
        {
            return null;
        }
    }

    private void WriteCache(CacheEntry entry)
    {
        try
        {
            string dir = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(entry,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // Cache write is best-effort; not worth interrupting the launch.
        }
    }

    private sealed class CacheEntry
    {
        public DateTime CheckedAtUtc { get; set; }
        public string? LatestTag { get; set; }
        public string? HtmlUrl { get; set; }
    }
}

internal sealed record UpdateCheckResult(
    Version Current,
    Version? Latest,
    string? LatestTag,
    string? HtmlUrl,
    bool IsUpdateAvailable)
{
    public static UpdateCheckResult Unknown(Version current) =>
        new(current, Latest: null, LatestTag: null, HtmlUrl: null, IsUpdateAvailable: false);
}
