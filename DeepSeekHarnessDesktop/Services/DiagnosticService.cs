using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeepSeekHarnessDesktop.Models;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessDesktop.Services;

public sealed partial class DiagnosticService
{
    private readonly AppPaths _paths;
    private readonly RuntimeService _runtime;
    private readonly HarnessProcessService _server;
    private readonly LogService _log;

    public DiagnosticService(AppPaths paths, RuntimeService runtime, HarnessProcessService server, LogService log)
    {
        _paths = paths;
        _runtime = runtime;
        _server = server;
        _log = log;
    }

    public async Task<string> RunAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var result = new StringBuilder();
        void Check(string name, bool ok, string detail) => result.AppendLine($"{(ok ? "✓" : "✗")} {name}：{detail}");
        var node = _paths.NodeExe(settings.CurrentRuntimeVersion);
        var profilePackage = Path.Combine(settings.DshHome, "profiles", "web", "package.json");

        result.AppendLine("DeepSeek Harness Desktop 诊断");
        result.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        result.AppendLine($"桌面壳：{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown"}");
        result.AppendLine($"系统：{Environment.OSVersion.VersionString} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        result.AppendLine();

        Check("内置 Node.js", File.Exists(node), File.Exists(node) ? FileVersionInfo.GetVersionInfo(node).FileVersion ?? RuntimeInfo.NodeVersion : "缺失");
        Check("Harness 运行时", _runtime.IsInstalled(settings.CurrentRuntimeVersion), settings.CurrentRuntimeVersion);
        Check("pnpm", File.Exists(_paths.PnpmScript(settings.CurrentRuntimeVersion)), _paths.PnpmScript(settings.CurrentRuntimeVersion));
        Check("工作目录", Directory.Exists(settings.Workspace), settings.Workspace);
        Check("DSH_HOME", Directory.Exists(settings.DshHome), settings.DshHome);
        Check("工作目录写入", CanWrite(settings.Workspace), Directory.Exists(settings.Workspace) ? "可写" : "目录不存在");
        Check("DSH_HOME 写入", CanWrite(settings.DshHome), Directory.Exists(settings.DshHome) ? "可写" : "目录不存在");
        Check("web profile", File.Exists(profilePackage), profilePackage);
        Check("端口", !HarnessProcessService.IsPortInUse(settings.Port) || _server.State == ServerState.Running, settings.Port.ToString());
        Check("Web 首页", await _server.IsHealthyAsync(settings.Port, cancellationToken), $"http://127.0.0.1:{settings.Port}");
        Check("Harness API", await _server.IsApiReadyAsync(settings.Port, cancellationToken), "host.describe");

        string webViewVersion;
        try { webViewVersion = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "未发现"; }
        catch (Exception ex) { webViewVersion = "检测失败：" + ex.Message; }
        Check("WebView2 Runtime", !string.IsNullOrWhiteSpace(webViewVersion) && webViewVersion != "未发现", webViewVersion);

        var officialRegistry = new Uri(ChinaMirrorService.OfficialNpmRegistry);
        var proxy = WebRequest.DefaultWebProxy?.GetProxy(officialRegistry);
        Check("官方源系统代理", true, proxy is not null && proxy != officialRegistry ? "已检测到系统代理" : "直连");
        Check("国内镜像策略", true, "强制直连");

        var configLines = new List<string>();
        var exit = await _server.RunCliAsync(settings, $"--profile web --patch \"{_paths.LauncherPatch}\" --dump-config", configLines.Add, cancellationToken);
        Check("Profile 配置", exit == 0, exit == 0 ? "验证通过" : $"CLI 退出代码 {exit}");

        if (_server.Process is { HasExited: false } process)
        {
            process.Refresh();
            Check("服务器进程", true, $"PID {process.Id} · {process.WorkingSet64 / 1024 / 1024:N0} MiB");
        }
        else Check("服务器进程", false, "未运行");

        if (_server.LastFailure is { } failure)
        {
            result.AppendLine();
            result.AppendLine("最近启动故障：");
            result.AppendLine($"- 阶段：{failure.Stage}");
            result.AppendLine($"- 原因：{failure.Detail}");
            result.AppendLine($"- 建议：{failure.Suggestion}");
        }

        return LogService.Redact(result.ToString());
    }

    public async Task<string> ExportBundleAsync(LauncherSettings settings, string? existingReport = null, CancellationToken cancellationToken = default)
    {
        var report = string.IsNullOrWhiteSpace(existingReport) ? await RunAsync(settings, cancellationToken) : existingReport;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stage = Path.Combine(_paths.Staging, "diagnostics-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(_paths.Logs, $"DeepSeek-Harness-Diagnostics-{stamp}.zip");
        Directory.CreateDirectory(stage);
        try
        {
            var knownSecrets = ReadKnownSecrets(settings.DshHome);
            string Sanitize(string value) => SanitizeForExport(value, settings, knownSecrets);

            await File.WriteAllTextAsync(Path.Combine(stage, "report.txt"), Sanitize(report), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                createdAt = DateTimeOffset.Now,
                desktopVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
                runtimeVersion = settings.CurrentRuntimeVersion,
                serverState = _server.State.ToString(),
                startupStage = _server.LastFailure?.Stage.ToString(),
                secretsIncluded = false
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            AddSanitizedFile(_paths.Config, Path.Combine(stage, "launcher.json"), Sanitize);
            AddSanitizedFile(_paths.CurrentRuntime, Path.Combine(stage, "runtime-current.json"), Sanitize);
            AddSanitizedFile(_paths.LauncherPatch, Path.Combine(stage, "launcher.patch.yml"), Sanitize);
            AddSanitizedFile(Path.Combine(settings.DshHome, "settings.yaml"), Path.Combine(stage, "harness-settings.redacted.yaml"), Sanitize);
            AddSanitizedFile(Path.Combine(settings.DshHome, "profiles", "web", "package.json"), Path.Combine(stage, "profile-package.json"), Sanitize);

            var logsDirectory = Path.Combine(stage, "logs");
            Directory.CreateDirectory(logsDirectory);
            foreach (var file in new DirectoryInfo(_paths.Logs).GetFiles("*.log").OrderByDescending(file => file.LastWriteTimeUtc).Take(4))
                AddSanitizedFile(file.FullName, Path.Combine(logsDirectory, file.Name), Sanitize);

            foreach (var root in new[] { Path.Combine(settings.DshHome, "logs"), Path.Combine(settings.DshHome, "dsh-chat-qq") }.Where(Directory.Exists))
            {
                foreach (var file in new DirectoryInfo(root).GetFiles("*.log").OrderByDescending(file => file.LastWriteTimeUtc).Take(2))
                    AddSanitizedFile(file.FullName, Path.Combine(logsDirectory, "harness-" + file.Name), Sanitize);
            }

            if (File.Exists(output)) File.Delete(output);
            ZipFile.CreateFromDirectory(stage, output, CompressionLevel.Optimal, false);
            _log.Info("diagnostics", $"diagnostic bundle created: {output}");
            return output;
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (Exception ex) { _log.Warn("diagnostics", "failed to clean staging: " + ex.Message); }
        }
    }

    private static bool CanWrite(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        var probe = Path.Combine(directory, ".dsh-desktop-write-probe-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); return true; }
        catch { try { if (File.Exists(probe)) File.Delete(probe); } catch { } return false; }
    }

    private static void AddSanitizedFile(string source, string destination, Func<string, string> sanitize)
    {
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            File.WriteAllText(destination, sanitize(reader.ReadToEnd()), new UTF8Encoding(false));
        }
        catch { }
    }

    private static IReadOnlyList<string> ReadKnownSecrets(string dshHome)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { Path.Combine(dshHome, ".credentials.yaml"), Path.Combine(dshHome, ".env") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var match = SecretValueLine().Match(line);
                    var value = match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : string.Empty;
                    if (value.Length >= 8) values.Add(value);
                }
            }
            catch { }
        }
        return values.ToArray();
    }

    internal static string SanitizeForExport(string value, LauncherSettings settings, IReadOnlyList<string>? knownSecrets = null)
    {
        var clean = LogService.Redact(value);
        clean = SecretAssignment().Replace(clean, match => match.Groups[1].Value + "***");
        foreach (var secret in knownSecrets ?? []) clean = clean.Replace(secret, "***", StringComparison.Ordinal);
        var replacements = new[]
        {
            (settings.Workspace, "%WORKSPACE%"),
            (settings.DshHome, "%DSH_HOME%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
        };
        foreach (var (path, token) in replacements.Where(item => !string.IsNullOrWhiteSpace(item.Item1)).OrderByDescending(item => item.Item1.Length))
        {
            clean = clean.Replace(path, token, StringComparison.OrdinalIgnoreCase);
            clean = clean.Replace(path.Replace("\\", "\\\\"), token.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
        }
        return clean;
    }

    [GeneratedRegex("(?im)^(\\s*(?:appSecret|api[_-]?key|token|secret|password)\\s*[:=]\\s*)[^\\r\\n,}]+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("^\\s*[^#:=\\r\\n]+\\s*[:=]\\s*['\"]?([^'\"\\r\\n]+)", RegexOptions.Compiled)]
    private static partial Regex SecretValueLine();
}
