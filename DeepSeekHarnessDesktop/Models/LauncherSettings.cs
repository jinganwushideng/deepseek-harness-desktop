using System.Text.Json.Serialization;

namespace DeepSeekHarnessDesktop.Models;

public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool Initialized { get; set; }
    public string Workspace { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string DshHome { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    public int Port { get; set; } = 3080;
    public bool CloseToTray { get; set; } = true;
    public bool NotifyOnResponseComplete { get; set; } = true;
    public string ShellThemeMode { get; set; } = "FollowWeb";
    public bool LaunchAtLogin { get; set; }
    public bool AutoStartServer { get; set; } = true;
    public bool OpenExternalBrowser { get; set; }
    public bool ForceTelemetryOff { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public string CurrentRuntimeVersion { get; set; } = RuntimeInfo.SeedVersion;
    public DateTimeOffset? LastUpdateCheck { get; set; }
}

public static class RuntimeInfo
{
    public const string SeedVersion = "0.1.0-rc.6";
    public const string NodeVersion = "24.18.0";
}

public enum ServerState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Maintenance,
    Faulted
}

public sealed record PluginItem(string Id, string Package, string Source, string Version, bool IsBuiltIn, bool Disabled);

public sealed record SkillItem(
    string Name,
    string Description,
    string Scope,
    string EntryPath,
    string ManifestPath,
    bool Enabled,
    bool ModelInvocable,
    bool UserInvocable,
    bool IsValid,
    string Status)
{
    public string Invocation => !IsValid ? "不可用" : ModelInvocable && UserInvocable ? "模型 / 用户" : ModelInvocable ? "仅模型" : UserInvocable ? "仅用户" : "均禁用";
    public string StateText => !IsValid ? "格式错误" : Enabled ? "启用" : "禁用";
}
