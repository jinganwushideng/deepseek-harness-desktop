using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace DeepSeekHarnessDesktop.Services;

public sealed record DesktopUpdateInfo(
    string Version,
    string Title,
    string ReleaseUrl,
    string InstallerUrl,
    bool IsPrerelease,
    DateTimeOffset PublishedAt);

public sealed class DesktopUpdateService
{
    private const string ReleasesEndpoint = "https://api.github.com/repos/jinganwushideng/deepseek-harness-desktop/releases?per_page=20";
    private const string ReleasesFeed = "https://github.com/jinganwushideng/deepseek-harness-desktop/releases.atom";
    private readonly LogService _log;

    public DesktopUpdateService(LogService log) => _log = log;

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<DesktopUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler { UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeepSeek-Harness-Desktop/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        DesktopUpdateInfo? newest;
        try
        {
            using var response = await client.GetAsync(ReleasesEndpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            newest = ReadJsonReleases(document.RootElement, CurrentVersion);
        }
        catch (HttpRequestException ex)
        {
            // GitHub's unauthenticated API quota is shared by public IP and may
            // be exhausted. The official Atom release feed is not subject to
            // that API quota and still follows the system proxy.
            _log.Warn("desktop-update", $"GitHub API unavailable, using releases feed: {ex.Message}");
            using var response = await client.GetAsync(ReleasesFeed, cancellationToken);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            newest = ReadAtomReleases(xml, CurrentVersion);
        }

        _log.Info("desktop-update", newest is null
            ? $"desktop is current ({CurrentVersion})"
            : $"desktop update available: {newest.Version}");
        return newest;
    }

    public static DesktopUpdateInfo? ReadJsonReleases(JsonElement root, string currentVersion)
    {
        DesktopUpdateInfo? newest = null;
        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
            var version = NormalizeVersion(tagElement.GetString());
            if (string.IsNullOrWhiteSpace(version) || !IsNewerVersion(version, currentVersion)) continue;

            var installerUrl = string.Empty;
            if (release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;
                    installerUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                    break;
                }
            }

            var candidate = new DesktopUpdateInfo(
                version,
                release.TryGetProperty("name", out var title) ? title.GetString() ?? $"v{version}" : $"v{version}",
                release.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() ?? string.Empty : string.Empty,
                installerUrl,
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean(),
                release.TryGetProperty("published_at", out var published) && DateTimeOffset.TryParse(published.GetString(), out var publishedAt) ? publishedAt : DateTimeOffset.MinValue);

            if (newest is null || IsNewerVersion(candidate.Version, newest.Version)) newest = candidate;
        }
        return newest;
    }

    public static DesktopUpdateInfo? ReadAtomReleases(string xml, string currentVersion)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        DesktopUpdateInfo? newest = null;
        foreach (var entry in document.Root?.Elements(atom + "entry") ?? [])
        {
            var releaseUrl = entry.Elements(atom + "link")
                .FirstOrDefault(link => string.Equals((string?)link.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))?
                .Attribute("href")?.Value ?? string.Empty;
            var marker = "/releases/tag/";
            var markerIndex = releaseUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            var version = NormalizeVersion(Uri.UnescapeDataString(releaseUrl[(markerIndex + marker.Length)..]));
            if (!IsNewerVersion(version, currentVersion)) continue;
            _ = DateTimeOffset.TryParse(entry.Element(atom + "updated")?.Value, out var publishedAt);
            var candidate = new DesktopUpdateInfo(
                version,
                entry.Element(atom + "title")?.Value ?? $"v{version}",
                releaseUrl,
                string.Empty,
                version.Contains('-', StringComparison.Ordinal),
                publishedAt);
            if (newest is null || IsNewerVersion(candidate.Version, newest.Version)) newest = candidate;
        }
        return newest;
    }

    public static bool IsNewerVersion(string? candidate, string? current) =>
        CompareVersions(candidate, current) > 0;

    public static int CompareVersions(string? left, string? right)
    {
        var a = ParseVersion(left);
        var b = ParseVersion(right);
        for (var index = 0; index < 3; index++)
        {
            var comparison = a.Numbers[index].CompareTo(b.Numbers[index]);
            if (comparison != 0) return comparison;
        }

        if (a.PreRelease.Count == 0 && b.PreRelease.Count == 0) return 0;
        if (a.PreRelease.Count == 0) return 1;
        if (b.PreRelease.Count == 0) return -1;
        for (var index = 0; index < Math.Max(a.PreRelease.Count, b.PreRelease.Count); index++)
        {
            if (index >= a.PreRelease.Count) return -1;
            if (index >= b.PreRelease.Count) return 1;
            var leftPart = a.PreRelease[index];
            var rightPart = b.PreRelease[index];
            var leftNumeric = int.TryParse(leftPart, out var leftNumber);
            var rightNumeric = int.TryParse(rightPart, out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric) comparison = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) comparison = -1;
            else if (rightNumeric) comparison = 1;
            else comparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string NormalizeVersion(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.StartsWith('v') ? normalized[1..] : normalized;
    }

    private static (int[] Numbers, List<string> PreRelease) ParseVersion(string? value)
    {
        var normalized = NormalizeVersion(value);
        var withoutBuild = normalized.Split('+', 2)[0];
        var split = withoutBuild.Split('-', 2);
        var numbers = split[0].Split('.');
        var parsed = new int[3];
        for (var index = 0; index < parsed.Length && index < numbers.Length; index++)
            _ = int.TryParse(numbers[index], out parsed[index]);
        var prerelease = split.Length == 2
            ? split[1].Split('.', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
        return (parsed, prerelease);
    }
}
