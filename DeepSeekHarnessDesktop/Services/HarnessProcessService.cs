using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class HarnessProcessService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Queue<DateTimeOffset> _crashes = new();
    private Process? _process;
    private JobObject? _job;
    private bool _intentionalStop;
    private bool _startupInProgress;
    private StartupStage _startupStage;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public Process? Process => _process is { HasExited: false } ? _process : null;
    public StartupFailure? LastFailure { get; private set; }
    public event Action<ServerState, string?>? StateChanged;
    public event Action<StartupProgress>? StartupProgressChanged;
    public event Func<Task>? RestartRequested;

    public HarnessProcessService(AppPaths paths, LogService log) { _paths = paths; _log = log; }

    public async Task StartAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        if (Process is not null) return;
        LastFailure = null;
        _startupInProgress = true;
        _intentionalStop = false;
        SetState(ServerState.Starting, null);
        try
        {
            Report(StartupStage.ValidatingSettings, 4, "正在验证启动设置", $"127.0.0.1:{settings.Port}");
            if (settings.Port is < 1 or > 65535) throw new InvalidOperationException("本机端口必须在 1 到 65535 之间。");
            if (string.IsNullOrWhiteSpace(settings.Workspace) || string.IsNullOrWhiteSpace(settings.DshHome))
                throw new InvalidOperationException("工作目录和 DSH_HOME 不能为空。");

            Report(StartupStage.CheckingRuntime, 10, "正在检查 Harness 运行时", settings.CurrentRuntimeVersion);
            var node = _paths.NodeExe(settings.CurrentRuntimeVersion);
            var bin = _paths.DshBin(settings.CurrentRuntimeVersion);
            if (!File.Exists(node) || !File.Exists(bin)) throw new FileNotFoundException("当前 Harness 运行时不完整，请重装或切换运行时版本。");

            Report(StartupStage.CheckingPort, 16, "正在检查本机端口", settings.Port.ToString());
            if (IsPortInUse(settings.Port)) throw new InvalidOperationException($"端口 {settings.Port} 已被其他程序占用。启动器不会结束该程序，请修改端口或先关闭占用者。");

            Report(StartupStage.PreparingDirectories, 22, "正在准备工作目录", settings.Workspace);
            Directory.CreateDirectory(settings.Workspace);
            Directory.CreateDirectory(settings.DshHome);

            Report(StartupStage.StartingProcess, 30, "正在启动 Harness 进程", Path.GetFileName(node));
            var arguments = $"\"{bin}\" --profile web --patch \"{_paths.LauncherPatch}\" --host 127.0.0.1 --port {settings.Port}";
            var start = new ProcessStartInfo(node, arguments)
            {
                WorkingDirectory = settings.Workspace, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            start.Environment["DSH_HOME"] = settings.DshHome;
            if (settings.ForceTelemetryOff) start.Environment["DSH_TELEMETRY_DISABLED"] = "1";
            start.Environment["PATH"] = BuildManagedPath(settings, node);
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Info("harness", e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Warn("harness", e.Data); };
            _process.Exited += ProcessExited;
            _process.Start(); _job = new JobObject(); _job.Add(_process);
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            _log.Info("server", $"started pid={_process.Id} port={settings.Port}");

            if (!await WaitHealthyAsync(settings.Port, TimeSpan.FromSeconds(120), cancellationToken))
                throw new TimeoutException("Harness 已启动进程，但未在 120 秒内通过端口、HTTP 与 API 健康检查。");

            Report(StartupStage.Ready, 100, "Harness 已就绪", $"http://127.0.0.1:{settings.Port}");
            _startupInProgress = false;
            SetState(ServerState.Running, null);
        }
        catch (Exception ex)
        {
            var stage = _startupStage;
            if (Process is not null) await StopAsync();
            _startupInProgress = false;
            LastFailure = BuildFailure(stage, ex, settings);
            SetState(ServerState.Faulted, LastFailure.Title);
            throw;
        }
    }

    public async Task StopAsync()
    {
        var process = Process;
        if (process is null) { SetState(ServerState.Stopped, null); return; }
        _intentionalStop = true; SetState(ServerState.Stopping, null); _log.Info("server", $"stopping pid={process.Id}");
        TrySendCtrlC(process.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            _log.Warn("server", "graceful stop timed out; killing process tree");
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
        }
        _job?.Dispose(); _job = null; _process?.Dispose(); _process = null; SetState(ServerState.Stopped, null);
    }

    public async Task RestartAsync(LauncherSettings settings, CancellationToken cancellationToken = default) { await StopAsync(); await StartAsync(settings, cancellationToken); }

    public async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken = default)
    {
        try { using var response = await _http.GetAsync($"http://127.0.0.1:{port}/", cancellationToken); return response.IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<int> RunCliAsync(LauncherSettings settings, string arguments, Action<string>? output = null, CancellationToken cancellationToken = default)
    {
        var node = _paths.NodeExe(settings.CurrentRuntimeVersion); var bin = _paths.DshBin(settings.CurrentRuntimeVersion);
        var environment = new Dictionary<string, string>
        {
            ["DSH_HOME"] = settings.DshHome,
            ["PATH"] = BuildManagedPath(settings, node)
        };
        return await RuntimeService.RunProcessAsync(node, $"\"{bin}\" {arguments}", settings.Workspace, cancellationToken, output, environment);
    }

    public async Task<int> RunPnpmAsync(LauncherSettings settings, string arguments, string workingDirectory, Action<string>? output = null, CancellationToken cancellationToken = default)
    {
        var node = _paths.NodeExe(settings.CurrentRuntimeVersion);
        var pnpm = _paths.PnpmScript(settings.CurrentRuntimeVersion);
        if (!File.Exists(pnpm)) throw new FileNotFoundException("当前运行时缺少 pnpm 脚本。", pnpm);
        Directory.CreateDirectory(workingDirectory);
        var environment = new Dictionary<string, string>
        {
            ["DSH_HOME"] = settings.DshHome,
            ["PATH"] = BuildManagedPath(settings, node),
            ["NPM_CONFIG_REGISTRY"] = ChinaMirrorService.OfficialNpmRegistry
        };
        var lines = new List<string>();
        ChinaMirrorService.ApplySystemProxyForOfficial(environment, new Uri(ChinaMirrorService.OfficialNpmRegistry));
        void Capture(string line) { lines.Add(line); output?.Invoke(line); }
        var exit = await RuntimeService.RunProcessAsync(node, $"\"{pnpm}\" {arguments}", workingDirectory, cancellationToken, Capture, environment);
        if (exit == 0 || !ChinaMirrorService.LooksLikeNetworkFailure(lines)) return exit;

        _log.Warn("network", $"npm registry network failure; retrying through {ChinaMirrorService.ChinaNpmRegistry}");
        output?.Invoke("官方 npm 源连接失败，正在自动切换国内镜像重试…");
        environment["NPM_CONFIG_REGISTRY"] = ChinaMirrorService.ChinaNpmRegistry;
        ChinaMirrorService.ForceDirectConnection(environment);
        return await RuntimeService.RunProcessAsync(node, $"\"{pnpm}\" {arguments}", workingDirectory, cancellationToken, output, environment);
    }

    private string BuildManagedPath(LauncherSettings settings, string node) => string.Join(Path.PathSeparator,
        Path.Combine(settings.DshHome, "launcher-packages", "node_modules", ".bin"),
        Path.Combine(settings.DshHome, "profiles", "web", "node_modules", ".bin"),
        _paths.PnpmBinDir(settings.CurrentRuntimeVersion),
        Path.GetDirectoryName(node),
        Environment.GetEnvironmentVariable("PATH"));

    private async Task<bool> WaitHealthyAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        var sawPort = false;
        var sawHttp = false;
        while (DateTimeOffset.UtcNow < until && !cancellationToken.IsCancellationRequested)
        {
            if (Process is null) return false;
            if (!sawPort)
            {
                Report(StartupStage.WaitingForPort, 42, "正在等待端口监听", $"127.0.0.1:{port}", true);
                sawPort = IsPortInUse(port);
            }
            if (sawPort && !sawHttp)
            {
                Report(StartupStage.WaitingForHttp, 63, "端口已监听，正在检查 Web 首页", $"http://127.0.0.1:{port}", true);
                sawHttp = await IsHealthyAsync(port, cancellationToken);
            }
            if (sawHttp)
            {
                Report(StartupStage.WaitingForApi, 82, "Web 服务已响应，正在验证 Harness API", "host.describe", true);
                if (await IsApiReadyAsync(port, cancellationToken)) return true;
            }
            await Task.Delay(350, cancellationToken);
        }
        return false;
    }

    public async Task<bool> IsApiReadyAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new { type = "client-request", rpcId = "desktop-health", method = "host.describe", payload = new { } };
            using var response = await _http.PostAsJsonAsync($"http://127.0.0.1:{port}/api/host.describe", body, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "server-response" &&
                   json.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch { return false; }
    }

    private async void ProcessExited(object? sender, EventArgs e)
    {
        var code = -1; try { code = _process?.ExitCode ?? -1; } catch { }
        _log.Warn("server", $"process exited code={code}");
        if (_intentionalStop) { SetState(ServerState.Stopped, null); return; }
        _job?.Dispose(); _job = null; _process?.Dispose(); _process = null;
        var now = DateTimeOffset.UtcNow; _crashes.Enqueue(now);
        while (_crashes.Count > 0 && now - _crashes.Peek() > TimeSpan.FromSeconds(60)) _crashes.Dequeue();
        if (_startupInProgress)
        {
            LastFailure ??= BuildFailure(_startupStage, new InvalidOperationException($"Harness 进程在启动阶段退出，代码 {code}。"), null);
            SetState(ServerState.Faulted, LastFailure.Title);
        }
        else if (_crashes.Count <= 3)
        {
            SetState(ServerState.Starting, $"服务器异常退出，正在自动恢复（{_crashes.Count}/3）");
            if (RestartRequested is not null) await RestartRequested.Invoke();
        }
        else SetState(ServerState.Faulted, "服务器在 60 秒内连续退出 3 次，已停止自动恢复。");
    }

    private void SetState(ServerState state, string? message) { State = state; StateChanged?.Invoke(state, message); }
    private void Report(StartupStage stage, int percentage, string title, string detail = "", bool indeterminate = false)
    {
        var changed = _startupStage != stage;
        _startupStage = stage;
        StartupProgressChanged?.Invoke(new StartupProgress(stage, percentage, title, detail, indeterminate));
        if (changed) _log.Info("startup", $"{stage}: {title}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : " - " + detail)}");
    }

    private StartupFailure BuildFailure(StartupStage stage, Exception error, LauncherSettings? settings)
    {
        var title = stage switch
        {
            StartupStage.CheckingRuntime => "Harness 运行时不完整",
            StartupStage.CheckingPort => $"端口 {settings?.Port} 无法使用",
            StartupStage.StartingProcess => "Harness 进程无法启动",
            StartupStage.WaitingForPort => "Harness 进程未开始监听端口",
            StartupStage.WaitingForHttp => "端口已监听，但 Web 首页没有响应",
            StartupStage.WaitingForApi => "Web 首页可访问，但 Harness API 未就绪",
            _ => "DeepSeek Harness 启动失败"
        };
        var suggestion = stage switch
        {
            StartupStage.CheckingRuntime => "打开“服务器与更新”切换旧版本，或在“诊断与修复”重装当前运行时。",
            StartupStage.CheckingPort => "在常规设置中更换端口；启动器不会结束占用端口的其他程序。",
            StartupStage.WaitingForPort => "优先查看最近 Harness 日志，并检查插件或 profile 初始化错误。",
            StartupStage.WaitingForHttp or StartupStage.WaitingForApi => "尝试重建插件依赖；如果刚更新过 Harness，可切换回上一版本。",
            _ => "打开诊断与修复生成诊断包，或查看最近日志后重新启动。"
        };
        var recent = _log.Recent.Where(line => line.Contains("[WARN]", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal) || line.Contains("[harness]", StringComparison.Ordinal))
            .TakeLast(18).Select(LogService.Redact).ToArray();
        return new StartupFailure(stage, title, LogService.Redact(error.Message), suggestion, DateTimeOffset.Now, recent);
    }
    public static bool IsPortInUse(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
    private static void TrySendCtrlC(int processId)
    {
        try { FreeConsole(); if (!AttachConsole((uint)processId)) return; SetConsoleCtrlHandler(IntPtr.Zero, true); GenerateConsoleCtrlEvent(0, 0); Thread.Sleep(200); FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
        catch { }
    }
    public void Dispose() { try { _job?.Dispose(); } catch { } _process?.Dispose(); _http.Dispose(); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
