using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class PluginRepositoryService : IDisposable
{
    public const string CatalogUrl = "https://raw.githubusercontent.com/jinganwushideng/deepseek-harness-desktop/main/catalog/plugin-index.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly HttpClient _directHttp;
    private readonly bool _ownsClient;

    public PluginRepositoryService(AppPaths paths, LogService log, HttpClient? http = null)
    {
        _paths = paths;
        _log = log;
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeek-Harness-Desktop/1.0");
        _directHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
        _directHttp.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeek-Harness-Desktop/1.0");
    }

    public async Task<(PluginCatalog Catalog, string Source)> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var cached = ReadFile(_paths.PluginCatalogCache);
        if (!forceRefresh && cached is not null && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_paths.PluginCatalogCache) < RefreshInterval)
            return (cached, "本地缓存");

        try
        {
            using var response = await GetOnlineCatalogAsync(cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var catalog = await JsonSerializer.DeserializeAsync<PluginCatalog>(stream, JsonOptions, cancellationToken)
                          ?? throw new InvalidDataException("目录内容为空。");
            Validate(catalog);
            SaveCache(catalog);
            return (catalog, "在线自动目录");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
        {
            _log.Warn("repository", "catalog refresh failed, using fallback: " + ex.Message);
            if (cached is not null) return (cached, "离线缓存");
            var embedded = ReadEmbedded();
            return (embedded, "内置离线目录");
        }
    }

    private async Task<HttpResponseMessage> GetOnlineCatalogAsync(CancellationToken cancellationToken)
    {
        Exception? firstError = null;
        foreach (var (url, client) in new[] { (CatalogUrl, _http), (ChinaMirrorService.CatalogMirrorUrl, _directHttp) })
        {
            try
            {
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (!url.Equals(CatalogUrl, StringComparison.Ordinal)) _log.Info("repository", "catalog switched to China-accessible mirror");
                    return response;
                }
                response.Dispose();
                firstError ??= new HttpRequestException($"目录服务返回 {(int)response.StatusCode}。");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                firstError ??= ex;
            }
        }
        throw new HttpRequestException("插件目录与国内可用镜像均无法访问。", firstError);
    }

    public static void Validate(PluginCatalog catalog)
    {
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("不支持的插件目录版本。");
        if (catalog.Items.Count is < 1 or > 1000) throw new InvalidDataException("插件目录数量异常。");
        catalog.DiscoverySources = catalog.DiscoverySources.Take(10).Select(value => Clean(value, 160, "目录来源")).ToList();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog.Items)
        {
            item.Id = Clean(item.Id, 120, "ID");
            item.Name = Clean(item.Name, 160, "名称");
            item.Description = Clean(item.Description, 600, "描述", allowEmpty: true);
            item.DescriptionZh = Clean(item.DescriptionZh, 600, "中文描述", allowEmpty: true);
            item.DescriptionSource = Clean(item.DescriptionSource, 200, "描述来源", allowEmpty: true);
            item.Package = Clean(item.Package, 220, "包名");
            item.InstallSpec = Clean(item.InstallSpec, 500, "安装来源");
            item.Version = Clean(item.Version, 80, "版本", allowEmpty: true);
            item.License = Clean(item.License, 80, "许可证", allowEmpty: true);
            item.DiscoverySource = Clean(item.DiscoverySource, 200, "发现来源", allowEmpty: true);
            if (!ids.Add(item.Id)) throw new InvalidDataException($"目录包含重复 ID：{item.Id}");
            _ = PluginService.NormalizeInstallSpec(item.InstallSpec);
            if (!string.IsNullOrWhiteSpace(item.RepositoryUrl))
            {
                if (!Uri.TryCreate(item.RepositoryUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    uri.Host is not ("github.com" or "gitlab.com" or "gitee.com" or "bitbucket.org" or "codeberg.org"))
                    throw new InvalidDataException($"不受信任的仓库链接：{item.RepositoryUrl}");
            }
            if (!string.IsNullOrWhiteSpace(item.PreviewImageUrl) && !TryGetTrustedPreviewUri(item.PreviewImageUrl, out _))
                item.PreviewImageUrl = string.Empty;
        }
    }

    public static bool TryGetTrustedPreviewUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        var host = parsed.Host.ToLowerInvariant();
        if (host is not ("raw.githubusercontent.com" or "user-images.githubusercontent.com" or "repository-images.githubusercontent.com" or "opengraph.githubassets.com" or
            "github.com" or "gitlab.com" or "gitee.com" or "bitbucket.org" or "codeberg.org" or "unpkg.com" or
            "cdn.jsdelivr.net" or "raw.gitmirror.com")) return false;
        if (parsed.AbsoluteUri.Length > 1200) return false;
        uri = parsed;
        return true;
    }

    public static IReadOnlyList<PluginCatalogItem> Filter(PluginCatalog catalog, string? query, PluginCategory? category)
    {
        var text = query?.Trim();
        return catalog.Items
            .Where(item => category is null || item.Category == category)
            .Where(item => string.IsNullOrWhiteSpace(text) ||
                           item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                           item.Package.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                           item.DisplayDescription.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Verified)
            .ThenByDescending(item => item.Popularity)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private PluginCatalog ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeepSeekHarnessDesktop.plugin-index.json")
                           ?? throw new InvalidOperationException("内置插件目录缺失。");
        var catalog = JsonSerializer.Deserialize<PluginCatalog>(stream, JsonOptions)
                      ?? throw new InvalidDataException("内置插件目录为空。");
        Validate(catalog);
        return catalog;
    }

    private static PluginCatalog? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var catalog = JsonSerializer.Deserialize<PluginCatalog>(File.ReadAllText(path), JsonOptions);
            if (catalog is null) return null;
            Validate(catalog);
            return catalog;
        }
        catch { return null; }
    }

    private void SaveCache(PluginCatalog catalog)
    {
        var temporary = _paths.PluginCatalogCache + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(catalog, JsonOptions));
        File.Move(temporary, _paths.PluginCatalogCache, true);
    }

    private static string Clean(string value, int max, string field, bool allowEmpty = false)
    {
        value = value?.Trim() ?? string.Empty;
        if ((!allowEmpty && value.Length == 0) || value.Length > max || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidDataException($"插件目录字段无效：{field}");
        return value;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
        _directHttp.Dispose();
    }
}
