using System.IO.Compression;
using System.Reflection;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class FeaturedSkinService
{
    public const string DeepWhaleId = "deep-whale-day-night";
    public const string ThemeCollectionId = "dshthemes-collection";

    private const string DeepWhaleResource = "DeepSeekHarnessDesktop.featured.deep-whale.zip";
    private const string ThemesUiResource = "DeepSeekHarnessDesktop.featured.dshthemes-ui.tgz";
    private const string ThemesCoreResource = "DeepSeekHarnessDesktop.featured.dshthemes-core.tgz";
    private const string ClsxResource = "DeepSeekHarnessDesktop.featured.clsx.tgz";

    private readonly AppPaths _paths;
    private readonly PluginService _plugins;
    private readonly LogService _log;

    public FeaturedSkinService(AppPaths paths, PluginService plugins, LogService log)
    {
        _paths = paths;
        _plugins = plugins;
        _log = log;
    }

    public static IReadOnlyList<FeaturedSkinDefinition> Definitions { get; } =
    [
        new(
            DeepWhaleId,
            "鲸鱼娘昼夜工坊",
            "带白昼水晶工坊与夜晚月潮观测室的完整角色主题。",
            "@dsh-external/dsh-client-ui-skin-maid-atelier",
            ["@dsh-external/dsh-client-ui-skin-maid-atelier"],
            "CC BY-NC-SA 4.0（禁止商用）",
            "https://github.com/GGBond2424648901/deep-whale-day-night-theme"),
        new(
            ThemeCollectionId,
            "DeepSeek Harness Themes",
            "包含 DeepSeek、OLED、Dracula、Catppuccin、Tokyo Night 等多套社区主题。",
            "@dshthemes/ui",
            ["@dshthemes/ui", "@dshthemes/core"],
            "MIT",
            "https://github.com/orxz/deepseek-harness-themes")
    ];

    public bool HasEmbeddedPayloads()
    {
        var names = Assembly.GetExecutingAssembly().GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        return names.Contains(DeepWhaleResource) && names.Contains(ThemesUiResource) && names.Contains(ThemesCoreResource) && names.Contains(ClsxResource);
    }

    public async Task ApplyFirstRunChoicesAsync(
        LauncherSettings settings,
        IReadOnlyDictionary<string, FeaturedSkinSetupChoice> choices,
        IProgress<PluginInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (choices.Count(pair => pair.Value == FeaturedSkinSetupChoice.Enable) > 1)
            throw new InvalidOperationException("首次只能启用一套精选皮肤，避免多个皮肤同时接管页面。 ");

        var selected = Definitions.Where(definition => choices.GetValueOrDefault(definition.Id) != FeaturedSkinSetupChoice.Remove).ToArray();
        if (selected.Length > 0 && !HasEmbeddedPayloads())
            throw new InvalidOperationException("程序内缺少精选皮肤离线包，请重新构建或重新安装桌面壳。 ");

        var step = 0;
        foreach (var definition in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            step++;
            var basePercent = 12 + (step - 1) * 31;
            progress?.Report(new PluginInstallProgress(basePercent, $"正在准备 {definition.DisplayName}", "释放内置离线包"));
            var specs = await PreparePayloadAsync(settings, definition.Id, cancellationToken);
            if (!ProfileHasDependencies(settings, definition.ManagedPackages))
            {
                var mapped = new Progress<PluginInstallProgress>(value =>
                {
                    var percentage = basePercent + Math.Clamp(value.Percentage, 0, 88) * 28 / 88;
                    progress?.Report(value with { Percentage = percentage, Stage = $"{definition.DisplayName} · {value.Stage}" });
                });
                var exit = await _plugins.InstallManyAsync(settings, specs, mapped, cancellationToken);
                if (exit != 0) throw new InvalidOperationException($"{definition.DisplayName} 离线安装失败，退出代码 {exit}。 ");
            }
        }

        await _plugins.RepairKnownBundleConflictsAsync(settings, cancellationToken);
        progress?.Report(new PluginInstallProgress(79, "正在应用首次皮肤选择", "写入启用状态"));
        var rows = await _plugins.InspectAsync(settings, cancellationToken);
        foreach (var definition in Definitions)
        {
            var choice = choices.GetValueOrDefault(definition.Id, FeaturedSkinSetupChoice.KeepDisabled);
            var managed = rows.Where(item => item.Package.Equals(definition.PrimaryPackage, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (choice == FeaturedSkinSetupChoice.Remove)
            {
                foreach (var item in managed)
                {
                    var exit = await _plugins.RemoveAsync(settings, item, cancellationToken);
                    if (exit != 0) throw new InvalidOperationException($"删除 {definition.DisplayName} 失败，退出代码 {exit}。 ");
                }
                if (definition.Id == ThemeCollectionId)
                {
                    var core = new PluginItem("dsh-themes-core", "@dshthemes/core", "精选皮肤离线依赖", "0.2.0", false, true, PluginCategory.Skin);
                    var coreExit = await _plugins.RemoveAsync(settings, core, cancellationToken);
                    if (coreExit != 0) throw new InvalidOperationException($"删除 {definition.DisplayName} 核心依赖失败，退出代码 {coreExit}。 ");
                    var clsx = new PluginItem("clsx", "clsx", "精选皮肤离线依赖", "2.1.1", false, false);
                    var clsxExit = await _plugins.RemoveAsync(settings, clsx, cancellationToken);
                    if (clsxExit != 0) throw new InvalidOperationException($"删除 {definition.DisplayName} 界面依赖失败，退出代码 {clsxExit}。 ");
                }
                continue;
            }

            var disabled = choice != FeaturedSkinSetupChoice.Enable;
            foreach (var item in managed)
                if (item.Disabled != disabled) await _plugins.SetDisabledAsync(settings, item, disabled, cancellationToken);
        }
        progress?.Report(new PluginInstallProgress(94, "精选皮肤已准备完成", "即将启动 Harness"));
    }

    private static bool ProfileHasDependencies(LauncherSettings settings, IReadOnlyList<string> packageNames)
    {
        var manifestPath = Path.Combine(settings.DshHome, "profiles", "web", "package.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)) return false;
            var installed = dependencies.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return packageNames.All(installed.Contains);
        }
        catch { return false; }
    }

    private async Task<IReadOnlyList<string>> PreparePayloadAsync(LauncherSettings settings, string id, CancellationToken cancellationToken)
    {
        var root = Path.Combine(settings.DshHome, "desktop-featured-skins", id);
        Directory.CreateDirectory(root);
        if (id == DeepWhaleId)
        {
            var package = Path.Combine(root, "package");
            var manifest = Path.Combine(package, "package.json");
            if (!File.Exists(manifest))
            {
                if (Directory.Exists(package)) Directory.Delete(package, true);
                Directory.CreateDirectory(package);
                await using var source = OpenResource(DeepWhaleResource);
                using var archive = new ZipArchive(source, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(package, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(package) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("精选皮肤压缩包包含越界路径。 ");
                    if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using var input = entry.Open();
                        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                        await input.CopyToAsync(output, cancellationToken);
                    }
                }
            }
            if (!File.Exists(manifest)) throw new InvalidDataException("鲸鱼娘昼夜工坊离线包不完整。 ");
            return [package];
        }

        var core = await CopyResourceAsync(ThemesCoreResource, Path.Combine(root, "dshthemes-core-0.2.0.tgz"), cancellationToken);
        var clsx = await CopyResourceAsync(ClsxResource, Path.Combine(root, "clsx-2.1.1.tgz"), cancellationToken);
        var ui = await CopyResourceAsync(ThemesUiResource, Path.Combine(root, "dshthemes-ui-0.2.0.tgz"), cancellationToken);
        return [core, clsx, ui];
    }

    private static Stream OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"程序内缺少精选皮肤资源：{name}");

    private static async Task<string> CopyResourceAsync(string resource, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;
        await using var input = OpenResource(resource);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
        return destination;
    }
}
