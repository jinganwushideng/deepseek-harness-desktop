using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class RuntimeService
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly TimeSpan InstallAttemptTimeout = TimeSpan.FromMinutes(6);
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _directHttp = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
    public RuntimeUpdateSource LastResolvedSource { get; private set; } = RuntimeUpdateSource.ChinaMirror;
    public string LastResolvedSourceText => SourceDisplayName(LastResolvedSource);

    public RuntimeService(AppPaths paths, LogService log) { _paths = paths; _log = log; }
    public bool IsInstalled(string version) => File.Exists(_paths.NodeExe(version)) && File.Exists(_paths.DshBin(version));

    public async Task EnsureSeedAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (IsInstalled(RuntimeInfo.SeedVersion))
        {
            await EnsureHelperAsync(cancellationToken);
            if (!File.Exists(_paths.CurrentRuntime)) await WriteCurrentAsync(RuntimeInfo.SeedVersion, cancellationToken);
            return;
        }

        progress?.Report("正在释放离线 Node.js 与 DeepSeek Harness…");
        _log.Info("runtime", $"extracting embedded runtime {RuntimeInfo.SeedVersion}");
        var stage = Path.Combine(_paths.Staging, "seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeepSeekHarnessDesktop.runtime.seed.zip")
            ?? throw new InvalidOperationException("程序内缺少 runtime.seed.zip。");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("运行时压缩包包含越界路径。");
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        if (!File.Exists(Path.Combine(stage, "node", "node.exe")) || !File.Exists(Path.Combine(stage, "app", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")))
            throw new InvalidDataException("内置运行时不完整。");
        var target = _paths.VersionRoot(RuntimeInfo.SeedVersion);
        if (Directory.Exists(target)) Directory.Move(target, target + ".incomplete-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        await MoveDirectoryWithRetryAsync(stage, target, cancellationToken);
        await EnsureHelperAsync(cancellationToken);
        if (!File.Exists(_paths.CurrentRuntime)) await WriteCurrentAsync(RuntimeInfo.SeedVersion, cancellationToken);
        _log.Info("runtime", "embedded runtime ready");
    }

    public Task<string?> CheckLatestAsync(CancellationToken cancellationToken = default) =>
        CheckLatestAsync(RuntimeUpdateSource.Auto, cancellationToken);

    public async Task<string?> CheckLatestAsync(RuntimeUpdateSource source, CancellationToken cancellationToken = default)
    {
        if (source != RuntimeUpdateSource.Auto)
        {
            var version = await FetchLatestVersionAsync(source, cancellationToken);
            LastResolvedSource = source;
            return version;
        }

        // Auto means an actual availability/latency race. The official request uses
        // Windows' proxy configuration; the China mirror always uses a direct client.
        var pending = new List<Task<(RuntimeUpdateSource Source, string? Version)>>
        {
            FetchLatestResultAsync(RuntimeUpdateSource.Official, cancellationToken),
            FetchLatestResultAsync(RuntimeUpdateSource.ChinaMirror, cancellationToken)
        };
        var errors = new List<Exception>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            try
            {
                var result = await completed;
                if (!string.IsNullOrWhiteSpace(result.Version))
                {
                    LastResolvedSource = result.Source;
                    _log.Info("network", $"automatic npm source selected: {SourceDisplayName(result.Source)}");
                    ObserveBackgroundFailures(pending);
                    return result.Version;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add(ex);
            }
        }
        throw new HttpRequestException("官方 npm 源和国内镜像均无法获取 Harness 版本。", errors.LastOrDefault());
    }

    public Task InstallVersionAsync(string version, string currentVersion, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        InstallVersionAsync(version, currentVersion, RuntimeUpdateSource.Auto,
            progress is null ? null : new Progress<PluginInstallProgress>(value => progress.Report(string.IsNullOrWhiteSpace(value.Detail) ? value.Stage : value.Detail)),
            cancellationToken);

    public async Task InstallVersionAsync(
        string version,
        string currentVersion,
        RuntimeUpdateSource source,
        IProgress<PluginInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9A-Za-z.+-]+$")) throw new ArgumentException("版本号格式无效。", nameof(version));
        if (IsInstalled(version))
        {
            await WriteCurrentAsync(version, cancellationToken);
            progress?.Report(new PluginInstallProgress(100, "Harness 已安装", $"已切换到 {version}"));
            return;
        }
        if (!await InstallGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有 Harness 更新正在进行，请等待或取消当前任务。");

        string? stage = null;
        try
        {
            stage = Path.Combine(_paths.Staging, "update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            progress?.Report(new PluginInstallProgress(4, "正在准备更新", "创建隔离的 staging 目录"));
            await CopyDirectoryAsync(Path.Combine(_paths.VersionRoot(currentVersion), "node"), Path.Combine(stage, "node"), cancellationToken);
            progress?.Report(new PluginInstallProgress(12, "Node.js 运行时已就绪", $"准备安装 Harness {version}"));

            var sources = GetInstallSourcePlan(source, LastResolvedSource);
            Exception? lastError = null;
            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selected = sources[index];
                try
                {
                    if (index > 0)
                    {
                        progress?.Report(new PluginInstallProgress(18, "正在切换下载源", $"改用{SourceDisplayName(selected)}重试"));
                        var oldApp = Path.Combine(stage, "app");
                        if (Directory.Exists(oldApp)) Directory.Delete(oldApp, true);
                    }
                    await InstallFromSourceAsync(stage, version, selected, progress, cancellationToken);
                    LastResolvedSource = selected;
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (source == RuntimeUpdateSource.Auto && index + 1 < sources.Count && ex is not OperationCanceledException)
                {
                    lastError = ex;
                    _log.Warn("network", $"runtime install through {SourceDisplayName(selected)} failed; trying fallback: {ex.Message}");
                }
            }
            if (lastError is not null) throw lastError;

            var dshBin = Path.Combine(stage, "app", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (!File.Exists(dshBin)) throw new InvalidOperationException($"Harness {version} 安装后缺少启动文件。");
            progress?.Report(new PluginInstallProgress(94, "正在切换运行时", "安装结果完整，准备原子切换版本"));
            var target = _paths.VersionRoot(version);
            if (Directory.Exists(target)) Directory.Move(target, target + ".incomplete-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            await MoveDirectoryWithRetryAsync(stage, target, cancellationToken);
            stage = null;
            await WriteCurrentAsync(version, cancellationToken);
            progress?.Report(new PluginInstallProgress(100, "Harness 更新完成", $"已安装 {version} · {LastResolvedSourceText}"));
            _log.Info("runtime", $"runtime {version} installed via {LastResolvedSourceText}");
        }
        finally
        {
            InstallGate.Release();
            if (stage is not null && Directory.Exists(stage))
            {
                try { Directory.Delete(stage, true); }
                catch (Exception ex) { _log.Warn("runtime", "failed to clean update staging: " + ex.Message); }
            }
        }
    }

    public static IReadOnlyList<RuntimeUpdateSource> GetInstallSourcePlan(RuntimeUpdateSource requested, RuntimeUpdateSource automaticWinner)
    {
        if (requested == RuntimeUpdateSource.Official) return [RuntimeUpdateSource.Official];
        if (requested == RuntimeUpdateSource.ChinaMirror) return [RuntimeUpdateSource.ChinaMirror];
        var first = automaticWinner is RuntimeUpdateSource.Official or RuntimeUpdateSource.ChinaMirror ? automaticWinner : RuntimeUpdateSource.ChinaMirror;
        return first == RuntimeUpdateSource.Official
            ? [RuntimeUpdateSource.Official, RuntimeUpdateSource.ChinaMirror]
            : [RuntimeUpdateSource.ChinaMirror, RuntimeUpdateSource.Official];
    }

    public static string SourceDisplayName(RuntimeUpdateSource source) => source switch
    {
        RuntimeUpdateSource.Official => "官方仓库（系统代理）",
        RuntimeUpdateSource.ChinaMirror => "国内镜像（直连）",
        _ => "自动选择"
    };

    private async Task<string?> FetchLatestVersionAsync(RuntimeUpdateSource source, CancellationToken cancellationToken)
    {
        var url = source == RuntimeUpdateSource.ChinaMirror
            ? "https://registry.npmmirror.com/@deepseek-ai%2Fdsh/latest"
            : "https://registry.npmjs.org/@deepseek-ai%2Fdsh/latest";
        var client = source == RuntimeUpdateSource.ChinaMirror ? _directHttp : _http;
        var json = await client.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    private async Task<(RuntimeUpdateSource Source, string? Version)> FetchLatestResultAsync(RuntimeUpdateSource source, CancellationToken cancellationToken) =>
        (source, await FetchLatestVersionAsync(source, cancellationToken));

    private static void ObserveBackgroundFailures(IEnumerable<Task<(RuntimeUpdateSource Source, string? Version)>> tasks)
    {
        foreach (var task in tasks)
            _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task InstallFromSourceAsync(
        string stage,
        string version,
        RuntimeUpdateSource source,
        IProgress<PluginInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var app = Path.Combine(stage, "app");
        Directory.CreateDirectory(app);
        var node = Path.Combine(stage, "node", "node.exe");
        var npmCli = Path.Combine(stage, "node", "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli)) throw new FileNotFoundException("便携 Node.js 中缺少 npm-cli.js。", npmCli);

        var environment = CreateSourceEnvironment(source);
        var lines = new List<string>();
        var detailPercent = 26;
        void Capture(string line)
        {
            lines.Add(line);
            _log.Info("npm", line);
            if (!string.IsNullOrWhiteSpace(line))
                progress?.Report(new PluginInstallProgress(detailPercent, $"正在从{SourceDisplayName(source)}下载", line, true));
        }

        progress?.Report(new PluginInstallProgress(22, "正在解析 Harness 依赖", $"{SourceDisplayName(source)} · 每个源最多等待 {InstallAttemptTimeout.TotalMinutes:0} 分钟", true));
        var baseArguments = new List<string>
        {
            npmCli, "install", "--prefix", app, "--no-audit", "--no-fund", "--save-exact", "--legacy-peer-deps",
            $"@deepseek-ai/dsh@{version}", "pnpm@11.19.0"
        };
        var exit = await RunNpmWithTimeoutAsync(node, baseArguments, stage, environment, cancellationToken, Capture);
        if (exit != 0) throw CreateNpmFailure(version, source, exit, lines);

        for (var round = 0; round < 6; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var missingPeers = FindMissingPeerDependencies(app);
            if (missingPeers.Count == 0) break;
            detailPercent = Math.Min(72, 48 + round * 7);
            progress?.Report(new PluginInstallProgress(detailPercent, "正在补齐官方 peer 依赖", $"第 {round + 1} 轮 · {missingPeers.Count} 个包", true));
            var peerArguments = new List<string>
            {
                npmCli, "install", "--prefix", app, "--no-audit", "--no-fund", "--save-exact", "--legacy-peer-deps"
            };
            peerArguments.AddRange(missingPeers.Select(item => $"{item.Key}@{item.Value}"));
            lines.Clear();
            exit = await RunNpmWithTimeoutAsync(node, peerArguments, stage, environment, cancellationToken, Capture);
            if (exit != 0) throw CreateNpmFailure(version, source, exit, lines);
        }

        var stillMissing = FindMissingPeerDependencies(app);
        if (stillMissing.Count > 0)
            throw new InvalidOperationException($"Harness {version} 仍缺少 {stillMissing.Count} 个必要 peer 依赖，未切换当前版本。");

        progress?.Report(new PluginInstallProgress(82, "正在完成原生依赖安装", "执行新版 Harness 依赖声明的安装脚本", true));
        lines.Clear();
        exit = await RunNpmWithTimeoutAsync(node,
            [npmCli, "approve-scripts", "--prefix", app, "--all"], stage, environment, cancellationToken, Capture);
        if (exit != 0) throw CreateNpmFailure(version, source, exit, lines);
        progress?.Report(new PluginInstallProgress(90, "正在验证运行时完整性", "依赖闭包与启动文件检查通过"));
    }

    private async Task<int> RunNpmWithTimeoutAsync(
        string node,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken,
        Action<string> output)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallAttemptTimeout);
        try
        {
            return await RunProcessAsync(node, arguments, workingDirectory, timeout.Token, output, environment);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"下载或依赖安装超过 {InstallAttemptTimeout.TotalMinutes:0} 分钟，已终止进程树。可切换更新源后重试。");
        }
    }

    private static InvalidOperationException CreateNpmFailure(string version, RuntimeUpdateSource source, int exitCode, IReadOnlyList<string> lines)
    {
        var detail = lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return new InvalidOperationException($"Harness {version} 通过{SourceDisplayName(source)}安装失败，npm 退出代码 {exitCode}{(string.IsNullOrWhiteSpace(detail) ? "。" : "：" + detail)}");
    }

    private static Dictionary<string, string> CreateSourceEnvironment(RuntimeUpdateSource source)
    {
        var registry = source == RuntimeUpdateSource.ChinaMirror ? ChinaMirrorService.ChinaNpmRegistry : ChinaMirrorService.OfficialNpmRegistry;
        var environment = new Dictionary<string, string> { ["NPM_CONFIG_REGISTRY"] = registry };
        if (source == RuntimeUpdateSource.ChinaMirror) ChinaMirrorService.ForceDirectConnection(environment);
        else ChinaMirrorService.ApplySystemProxyForOfficial(environment, new Uri(registry));
        return environment;
    }

    internal static IReadOnlyDictionary<string, string> FindMissingPeerDependencies(string appRoot)
    {
        var nodeModules = Path.Combine(appRoot, "node_modules");
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(nodeModules)) return missing;
        foreach (var packageDirectory in EnumeratePackageDirectories(nodeModules))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageDirectory, "package.json")));
                if (!document.RootElement.TryGetProperty("peerDependencies", out var peers) || peers.ValueKind != JsonValueKind.Object) continue;
                document.RootElement.TryGetProperty("peerDependenciesMeta", out var metadata);
                foreach (var peer in peers.EnumerateObject())
                {
                    if (IsOptionalPeer(metadata, peer.Name) || IsPackageResolvable(appRoot, packageDirectory, peer.Name)) continue;
                    var range = peer.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(range) && IsValidPackageName(peer.Name)) missing.TryAdd(peer.Name, range);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return missing;
    }

    private static IEnumerable<string> EnumeratePackageDirectories(string nodeModules)
    {
        foreach (var directory in Directory.EnumerateDirectories(nodeModules))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (name.StartsWith("@", StringComparison.Ordinal))
            {
                foreach (var scopedPackage in Directory.EnumerateDirectories(directory))
                {
                    if (!File.Exists(Path.Combine(scopedPackage, "package.json"))) continue;
                    yield return scopedPackage;
                    var nested = Path.Combine(scopedPackage, "node_modules");
                    if (Directory.Exists(nested)) foreach (var child in EnumeratePackageDirectories(nested)) yield return child;
                }
            }
            else if (File.Exists(Path.Combine(directory, "package.json")))
            {
                yield return directory;
                var nested = Path.Combine(directory, "node_modules");
                if (Directory.Exists(nested)) foreach (var child in EnumeratePackageDirectories(nested)) yield return child;
            }
        }
    }

    private static bool IsOptionalPeer(JsonElement metadata, string peerName) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(peerName, out var item) &&
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True;

    private static bool IsPackageResolvable(string appRoot, string packageDirectory, string packageName)
    {
        var current = packageDirectory;
        var root = Path.GetFullPath(appRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        while (Path.GetFullPath(current).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(current, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
            if (File.Exists(candidate)) return true;
            var parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }
        return File.Exists(Path.Combine(appRoot, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar), "package.json"));
    }

    private static bool IsValidPackageName(string packageName) =>
        System.Text.RegularExpressions.Regex.IsMatch(packageName, @"^(?:@[a-z0-9._-]+/)?[a-z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public Task SwitchAsync(string version, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled(version)) throw new DirectoryNotFoundException($"运行时 {version} 不完整。");
        return WriteCurrentAsync(version, cancellationToken);
    }

    public IReadOnlyList<string> InstalledVersions() => Directory.Exists(_paths.Versions)
        ? Directory.EnumerateDirectories(_paths.Versions).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().OrderByDescending(x => x).ToArray() : [];

    private async Task EnsureHelperAsync(CancellationToken cancellationToken)
    {
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeepSeekHarnessDesktop.helper.mjs")
            ?? throw new InvalidOperationException("程序内缺少 helper.mjs。");
        await using var output = new FileStream(_paths.Helper, FileMode.Create, FileAccess.Write, FileShare.Read);
        await resource.CopyToAsync(output, cancellationToken);
    }

    private async Task WriteCurrentAsync(string version, CancellationToken cancellationToken)
    {
        var temp = _paths.CurrentRuntime + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { version }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(temp, _paths.CurrentRuntime, true);
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = file.Replace(source, target, StringComparison.OrdinalIgnoreCase);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(string source, string target, CancellationToken cancellationToken)
    {
        IOException? last = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Move(source, target); return; }
            catch (IOException ex) { last = ex; }
            catch (UnauthorizedAccessException ex) { last = new IOException(ex.Message, ex); }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
        }
        throw new IOException($"无法将运行时从 staging 切换到版本目录：{last?.Message}", last);
    }

    internal static async Task<int> RunProcessAsync(string file, string arguments, string cwd, CancellationToken cancellationToken, Action<string>? output = null, IDictionary<string, string>? environment = null)
    {
        var start = CreateProcessStartInfo(file, cwd, environment);
        start.Arguments = arguments;
        return await RunProcessCoreAsync(start, cancellationToken, output);
    }

    internal static async Task<int> RunProcessAsync(string file, IReadOnlyList<string> arguments, string cwd, CancellationToken cancellationToken, Action<string>? output = null, IDictionary<string, string>? environment = null)
    {
        var start = CreateProcessStartInfo(file, cwd, environment);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return await RunProcessCoreAsync(start, cancellationToken, output);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string file, string cwd, IDictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (environment is not null) foreach (var item in environment) start.Environment[item.Key] = item.Value;
        return start;
    }

    private static async Task<int> RunProcessCoreAsync(ProcessStartInfo start, CancellationToken cancellationToken, Action<string>? output)
    {
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output?.Invoke(e.Data); };
        using var job = new JobObject();
        try
        {
            process.Start();
            job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { if (process.HasExited is false) await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }
    }
}
