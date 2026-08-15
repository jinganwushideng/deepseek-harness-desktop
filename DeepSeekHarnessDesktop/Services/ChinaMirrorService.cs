namespace DeepSeekHarnessDesktop.Services;

public static class ChinaMirrorService
{
    public const string OfficialNpmRegistry = "https://registry.npmjs.org";
    public const string ChinaNpmRegistry = "https://registry.npmmirror.com";
    public const string CatalogMirrorUrl = "https://cdn.jsdelivr.net/gh/jinganwushideng/deepseek-harness-desktop@main/catalog/plugin-index.json";

    private static readonly string[] NetworkFailureSignals =
    [
        "etimedout", "econnreset", "econnrefused", "enotfound", "fetch failed",
        "network timeout", "socket hang up", "certificate has expired", "502 bad gateway",
        "503 service unavailable", "504 gateway timeout", "ERR_PNPM_META_FETCH_FAIL"
    ];

    public static bool LooksLikeNetworkFailure(IEnumerable<string> output) =>
        output.Any(line => NetworkFailureSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)));

    public static void ForceDirectConnection(IDictionary<string, string> environment)
    {
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NPM_CONFIG_PROXY", "NPM_CONFIG_HTTPS_PROXY" })
            environment[name] = string.Empty;
        environment["NO_PROXY"] = "*";
        environment["no_proxy"] = "*";
    }

    public static void ApplySystemProxyForOfficial(IDictionary<string, string> environment, Uri destination, System.Net.IWebProxy? proxy = null, bool considerProcessEnvironment = true)
    {
        if (new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" }.Any(name =>
                environment.TryGetValue(name, out var configured) ? !string.IsNullOrWhiteSpace(configured) : considerProcessEnvironment && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))) return;
        try
        {
            proxy ??= System.Net.Http.HttpClient.DefaultProxy;
            if (proxy.IsBypassed(destination)) return;
            var address = proxy.GetProxy(destination);
            if (address is null || address == destination || address.Scheme is not ("http" or "https")) return;
            environment["HTTP_PROXY"] = address.AbsoluteUri;
            environment["HTTPS_PROXY"] = address.AbsoluteUri;
        }
        catch { /* The official request can still use the process' inherited proxy configuration. */ }
    }
}
