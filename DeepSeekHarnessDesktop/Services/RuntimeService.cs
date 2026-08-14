using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class RuntimeService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

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

    public async Task<string?> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetStringAsync("https://registry.npmjs.org/@deepseek-ai%2Fdsh/latest", cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    public async Task InstallVersionAsync(string version, string currentVersion, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9A-Za-z.+-]+$")) throw new ArgumentException("版本号格式无效。", nameof(version));
        if (IsInstalled(version)) return;
        var stage = Path.Combine(_paths.Staging, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        progress?.Report("正在准备 Node.js 运行时…");
        await CopyDirectoryAsync(Path.Combine(_paths.VersionRoot(currentVersion), "node"), Path.Combine(stage, "node"), cancellationToken);
        Directory.CreateDirectory(Path.Combine(stage, "app"));
        progress?.Report($"正在安装 DeepSeek Harness {version}…");
        var npm = Path.Combine(stage, "node", "npm.cmd");
        var exit = await RunProcessAsync(npm, $"install --prefix \"{Path.Combine(stage, "app")}\" --no-audit --no-fund --save-exact \"@deepseek-ai/dsh@{version}\" \"pnpm@11.19.0\"", stage, cancellationToken,
            line => { _log.Info("npm", line); progress?.Report(line); });
        if (exit != 0 || !File.Exists(Path.Combine(stage, "app", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")))
            throw new InvalidOperationException($"Harness {version} 安装失败，npm 退出代码 {exit}。");
        await MoveDirectoryWithRetryAsync(stage, _paths.VersionRoot(version), cancellationToken);
        await WriteCurrentAsync(version, cancellationToken);
    }

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
        var start = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (environment is not null) foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output?.Invoke(e.Data); };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
