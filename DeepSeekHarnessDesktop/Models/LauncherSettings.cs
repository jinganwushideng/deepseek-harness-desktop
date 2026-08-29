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
    public bool FollowSkinAppearance { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public bool AutoStartServer { get; set; } = true;
    public bool OpenExternalBrowser { get; set; }
    public bool ForceTelemetryOff { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RuntimeUpdateSource HarnessUpdateSource { get; set; } = RuntimeUpdateSource.Auto;
    public string CurrentRuntimeVersion { get; set; } = RuntimeInfo.SeedVersion;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public DateTimeOffset? LastDesktopUpdateCheck { get; set; }
    public string DismissedDesktopUpdateVersion { get; set; } = string.Empty;
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

public enum RuntimeUpdateSource
{
    Auto,
    Official,
    ChinaMirror
}

public enum PluginCategory
{
    Plugin,
    Skin,
    Skill,
    DeveloperTool,
    Other
}

public sealed record PluginItem(
    string Id,
    string Package,
    string Source,
    string Version,
    bool IsBuiltIn,
    bool Disabled,
    PluginCategory Category = PluginCategory.Plugin)
{
    public bool IsSkin => Category == PluginCategory.Skin;
    public bool Enabled => !Disabled;
    public string StateText => Disabled ? "未启用" : "已启用";
    public string CategoryText => Category switch
    {
        PluginCategory.Skin => "皮肤",
        PluginCategory.Skill => "Skill",
        PluginCategory.DeveloperTool => "开发工具",
        PluginCategory.Other => "其他",
        _ => "插件"
    };
}

public sealed class PluginCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAt { get; set; }
    public string Generator { get; set; } = string.Empty;
    public List<string> DiscoverySources { get; set; } = [];
    public List<PluginCatalogItem> Items { get; set; } = [];
}

public sealed class PluginCatalogItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DescriptionZh { get; set; } = string.Empty;
    public string DescriptionSource { get; set; } = string.Empty;
    public string InstallSpec { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string PreviewImageUrl { get; set; } = string.Empty;
    public string DiscoverySource { get; set; } = string.Empty;
    public string SourceType { get; set; } = "npm";
    public string License { get; set; } = string.Empty;
    public PluginCategory Category { get; set; } = PluginCategory.Plugin;
    public bool Verified { get; set; }
    public bool HasLifecycleScripts { get; set; }
    public bool RequiresBuildApproval { get; set; }
    public long Popularity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CategoryText => Category switch
    {
        PluginCategory.Skin => "皮肤",
        PluginCategory.Skill => "Skill",
        PluginCategory.DeveloperTool => "开发工具",
        PluginCategory.Other => "其他",
        _ => "插件"
    };
    public string TrustText => Verified ? "已校验 Harness 清单" : "社区来源，请自行确认";
    public string PopularityText => Popularity > 0 ? $"热度 {Popularity:N0}" : "新收录";
    public string DisplayDescription => string.IsNullOrWhiteSpace(DescriptionZh) ? Description : DescriptionZh;
    public string DescriptionSourceText => string.IsNullOrWhiteSpace(DescriptionZh) ? "英文项目描述" : "中文文档";
    public string DiscoverySourceText => string.IsNullOrWhiteSpace(DiscoverySource) ? SourceType : DiscoverySource;
    [JsonIgnore]
    public string PreviewImagePath { get; set; } = "pack://application:,,,/Assets/PluginPreviewPlaceholder.png";
    [JsonIgnore]
    public bool IsInstalled { get; set; }
    [JsonIgnore]
    public bool IsEnabled { get; set; }
    [JsonIgnore]
    public string InstallStateText => !IsInstalled ? "未安装" : IsEnabled ? "已启用" : "已安装 · 未启用";
    [JsonIgnore]
    public string InstallActionText => !IsInstalled ? "下载安装" : IsEnabled ? "已启用" : "启用";
    [JsonIgnore]
    public bool CanRunInstallAction => !IsInstalled || !IsEnabled;
}

public sealed record PluginInstallProgress(int Percentage, string Stage, string Detail = "", bool IsIndeterminate = false);

public enum FeaturedSkinSetupChoice
{
    KeepDisabled,
    Enable,
    Remove
}

public sealed record FeaturedSkinDefinition(
    string Id,
    string DisplayName,
    string Description,
    string PrimaryPackage,
    IReadOnlyList<string> ManagedPackages,
    string License,
    string RepositoryUrl);

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
