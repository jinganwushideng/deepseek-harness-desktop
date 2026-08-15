using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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

    public ServerState State { get; private set; } = ServerState.Stopped;
    public Process? Process => _process is { HasExited: false } ? _process : null;
    public event Action<ServerState, string?>? StateChanged;
    public event Func<Task>? RestartRequested;

    public HarnessProcessService(AppPaths paths, LogService log) { _paths = paths; _log = log; }

    public async Task StartAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        if (Process is not null) return;
        if (IsPortInUse(settings.Port)) throw new InvalidOperationException($"端口 {settings.Port} 已被其他程序占用。");
        var node = _paths.NodeExe(settings.CurrentRuntimeVersion);
        var bin = _paths.DshBin(settings.CurrentRuntimeVersion);
        if (!File.Exists(node) || !File.Exists(bin)) throw new FileNotFoundException("当前 Harness 运行时不完整。");
        Directory.CreateDirectory(settings.Workspace); Directory.CreateDirectory(settings.DshHome);
        SetState(ServerState.Starting, null); _intentionalStop = false;
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
        if (!await WaitHealthyAsync(settings.Port, TimeSpan.FromSeconds(90), cancellationToken))
        {
            await StopAsync(); throw new TimeoutException("Harness 服务器未在 90 秒内就绪。");
        }
        SetState(ServerState.Running, null);
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
        while (DateTimeOffset.UtcNow < until && !cancellationToken.IsCancellationRequested)
        {
            if (Process is null) return false;
            if (await IsHealthyAsync(port, cancellationToken)) return true;
            await Task.Delay(350, cancellationToken);
        }
        return false;
    }

    private async void ProcessExited(object? sender, EventArgs e)
    {
        var code = -1; try { code = _process?.ExitCode ?? -1; } catch { }
        _log.Warn("server", $"process exited code={code}");
        if (_intentionalStop) { SetState(ServerState.Stopped, null); return; }
        _job?.Dispose(); _job = null; _process?.Dispose(); _process = null;
        var now = DateTimeOffset.UtcNow; _crashes.Enqueue(now);
        while (_crashes.Count > 0 && now - _crashes.Peek() > TimeSpan.FromSeconds(60)) _crashes.Dequeue();
        if (_crashes.Count <= 3)
        {
            SetState(ServerState.Starting, $"服务器异常退出，正在自动恢复（{_crashes.Count}/3）");
            if (RestartRequested is not null) await RestartRequested.Invoke();
        }
        else SetState(ServerState.Faulted, "服务器在 60 秒内连续退出 3 次，已停止自动恢复。");
    }

    private void SetState(ServerState state, string? message) { State = state; StateChanged?.Invoke(state, message); }
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
