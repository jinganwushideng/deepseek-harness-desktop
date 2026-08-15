using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class PluginPreviewService : IDisposable
{
    public const string Placeholder = "pack://application:,,,/Assets/PluginPreviewPlaceholder.png";
    private const int MaxImageBytes = 6 * 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly HttpClient _official;
    private readonly HttpClient _direct;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new(StringComparer.Ordinal);

    public PluginPreviewService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
        _official = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _direct = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _official.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeek-Harness-Desktop/1.0");
        _direct.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeek-Harness-Desktop/1.0");
    }

    public async Task PrepareAsync(IEnumerable<PluginCatalogItem> items, Action<PluginCatalogItem> imageReady, CancellationToken cancellationToken = default)
    {
        var candidates = items.Select(item => (Item: item, Url: ResolvePreviewUrl(item))).Where(value => value.Url is not null).ToArray();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, token) =>
        {
            try
            {
                // ConcurrentDictionary may invoke a value factory more than once. Keeping an
                // unstarted Lazy in the dictionary guarantees only the winning download runs.
                var pending = _inFlight.GetOrAdd(item.Url!, url =>
                    new Lazy<Task<string?>>(
                        () => GetOrDownloadAsync(url, token),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                var task = pending.Value;
                string? path;
                try { path = await task; }
                finally
                {
                    if (task.IsCompleted)
                        _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(item.Url!, pending));
                }
                if (path is null) return;
                item.Item.PreviewImagePath = path;
                imageReady(item.Item);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                _log.Warn("repository", $"preview unavailable ({item.Item.Package}): {ex.Message}");
            }
        });
    }

    public static string? ResolvePreviewUrl(PluginCatalogItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.PreviewImageUrl)) return item.PreviewImageUrl;
        if (!Uri.TryCreate(item.RepositoryUrl, UriKind.Absolute, out var repository) ||
            !repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = repository.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return $"https://opengraph.githubassets.com/1/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1].Replace(".git", "", StringComparison.OrdinalIgnoreCase))}";
    }

    private async Task<string?> GetOrDownloadAsync(string value, CancellationToken cancellationToken)
    {
        if (!PluginRepositoryService.TryGetTrustedPreviewUri(value, out var uri)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        var cached = Directory.EnumerateFiles(_paths.PluginPreviewCache, key + ".*").FirstOrDefault();
        if (cached is not null) return cached;

        byte[]? content = null;
        try { content = await DownloadAsync(_official, uri, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            var mirror = ToChinaMirror(uri);
            if (mirror is null) throw;
            _log.Info("repository", $"preview switched to direct China-accessible mirror: {uri.Host}");
            content = await DownloadAsync(_direct, mirror, cancellationToken);
        }
        var extension = DetectImageExtension(content) ?? throw new InvalidDataException("预览资源不是受支持的图片。 ");
        var path = Path.Combine(_paths.PluginPreviewCache, key + extension);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, content, cancellationToken);
        File.Move(temporary, path, true);
        return path;
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxImageBytes) throw new InvalidDataException("预览图超过 6 MiB。 ");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (target.Length + read > MaxImageBytes) throw new InvalidDataException("预览图超过 6 MiB。 ");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return target.ToArray();
    }

    public static string? DetectImageExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 6 && Encoding.ASCII.GetString(bytes[..6]) is "GIF87a" or "GIF89a") return ".gif";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return ".bmp";
        return null;
    }

    private static Uri? ToChinaMirror(Uri source)
    {
        if (!source.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = source.AbsolutePath.Trim('/').Split('/', 4);
        if (parts.Length != 4) return null;
        return new Uri($"https://cdn.jsdelivr.net/gh/{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}");
    }

    public void Dispose()
    {
        _official.Dispose();
        _direct.Dispose();
    }
}
