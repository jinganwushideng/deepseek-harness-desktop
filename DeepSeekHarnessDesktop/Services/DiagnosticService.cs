using System.Text;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class DiagnosticService
{
    private readonly AppPaths _paths;
    private readonly RuntimeService _runtime;
    private readonly HarnessProcessService _server;
    public DiagnosticService(AppPaths paths, RuntimeService runtime, HarnessProcessService server) { _paths = paths; _runtime = runtime; _server = server; }

    public async Task<string> RunAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var result = new StringBuilder();
        void Check(string name, bool ok, string detail) => result.AppendLine($"{(ok ? "✓" : "✗")} {name}：{detail}");
        Check("内置 Node.js", File.Exists(_paths.NodeExe(settings.CurrentRuntimeVersion)), File.Exists(_paths.NodeExe(settings.CurrentRuntimeVersion)) ? RuntimeInfo.NodeVersion : "缺失");
        Check("Harness 运行时", _runtime.IsInstalled(settings.CurrentRuntimeVersion), settings.CurrentRuntimeVersion);
        Check("pnpm", Directory.Exists(_paths.PnpmBinDir(settings.CurrentRuntimeVersion)), _paths.PnpmBinDir(settings.CurrentRuntimeVersion));
        Check("工作目录", Directory.Exists(settings.Workspace), settings.Workspace);
        Check("DSH_HOME", Directory.Exists(settings.DshHome), settings.DshHome);
        Check("端口", !HarnessProcessService.IsPortInUse(settings.Port) || _server.State == ServerState.Running, settings.Port.ToString());
        Check("Web 服务", await _server.IsHealthyAsync(settings.Port, cancellationToken), $"http://127.0.0.1:{settings.Port}");
        Check("WebView2 Runtime", Directory.Exists(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application"), "Evergreen Runtime");
        var configLines = new List<string>();
        var exit = await _server.RunCliAsync(settings, $"--profile web --patch \"{_paths.LauncherPatch}\" --dump-config", configLines.Add, cancellationToken);
        Check("Profile 配置", exit == 0, exit == 0 ? "验证通过" : $"CLI 退出代码 {exit}");
        return result.ToString();
    }

    public string Export(string content)
    {
        var path = Path.Combine(_paths.Logs, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, content); return path;
    }
}
