using System.Diagnostics;
using System.Text.Json;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class NodeHelperService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    public NodeHelperService(AppPaths paths, LogService log) { _paths = paths; _log = log; }

    public async Task<JsonDocument> CallAsync(LauncherSettings settings, object request, CancellationToken cancellationToken = default)
    {
        var node = _paths.NodeExe(settings.CurrentRuntimeVersion);
        var runtimeApp = Path.Combine(_paths.VersionRoot(settings.CurrentRuntimeVersion), "app");
        var start = new ProcessStartInfo(node, $"\"{_paths.Helper}\" \"{runtimeApp}\" \"{settings.DshHome}\"")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = _paths.Root, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Node 辅助程序。");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request)); process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) { _log.Error("helper", error); throw new InvalidOperationException(LogService.Redact(error)); }
        return JsonDocument.Parse(output);
    }
}
