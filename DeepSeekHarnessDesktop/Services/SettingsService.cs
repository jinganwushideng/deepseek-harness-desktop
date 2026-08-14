using System.Text.Json;
using DeepSeekHarnessDesktop.Models;
using Microsoft.Win32;

namespace DeepSeekHarnessDesktop.Services;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsService(AppPaths paths) => _paths = paths;

    public LauncherSettings Load()
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.Config)) return new LauncherSettings();
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_paths.Config), JsonOptions) ?? new LauncherSettings();
        }
        catch
        {
            var broken = _paths.Config + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(_paths.Config, broken, true);
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Validate(settings);
        var temp = _paths.Config + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _paths.Config, true);
        SetAutoRun(settings.LaunchAtLogin);
    }

    private static void Validate(LauncherSettings settings)
    {
        var normalized = ValidateConnectionInput(settings.Workspace, settings.DshHome, settings.Port.ToString());
        settings.Workspace = normalized.Workspace;
        settings.DshHome = normalized.DshHome;
        settings.Port = normalized.Port;
    }

    public static (string Workspace, string DshHome, int Port) ValidateConnectionInput(string workspaceText, string dshHomeText, string portText)
    {
        if (string.IsNullOrWhiteSpace(workspaceText)) throw new ArgumentException("工作目录不能为空。");
        if (string.IsNullOrWhiteSpace(dshHomeText)) throw new ArgumentException("DSH_HOME 不能为空。");
        if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535) throw new ArgumentException("端口必须是 1024–65535 之间的数字。");
        return (Path.GetFullPath(workspaceText.Trim()), Path.GetFullPath(dshHomeText.Trim()), port);
    }

    private static void SetAutoRun(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("DeepSeekHarnessDesktop", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("DeepSeekHarnessDesktop", false);
    }
}
