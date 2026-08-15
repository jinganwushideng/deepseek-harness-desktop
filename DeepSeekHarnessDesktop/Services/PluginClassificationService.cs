using System.Text.Json;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public static class PluginClassificationService
{
    private static readonly string[] SkinSignals =
    [
        "theme", "themes", "skin", "skins", "appearance", "wallpaper",
        "ui-theme", "ui-skins", "皮肤", "主题", "壁纸"
    ];

    public static PluginCategory Classify(string packageName, string? manifestPath = null)
    {
        if (TryReadManifest(manifestPath, out var root))
        {
            using (root)
            {
                var text = BuildSignalText(root.RootElement);
                if (ContainsSkinSignal(text)) return PluginCategory.Skin;
                if (text.Contains("SKILL.md", StringComparison.OrdinalIgnoreCase) || text.Contains("\"skills\"", StringComparison.OrdinalIgnoreCase))
                    return PluginCategory.Skill;
            }
        }
        return ContainsSkinSignal(packageName) ? PluginCategory.Skin : PluginCategory.Plugin;
    }

    public static PluginCategory ParseCategory(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "skin" or "theme" => PluginCategory.Skin,
        "skill" => PluginCategory.Skill,
        "developertool" or "developer-tool" or "devtool" => PluginCategory.DeveloperTool,
        "other" => PluginCategory.Other,
        _ => PluginCategory.Plugin
    };

    public static string ManifestPath(string nodeModulesRoot, string packageName) =>
        Path.Combine(nodeModulesRoot, packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");

    private static bool ContainsSkinSignal(string value) =>
        SkinSignals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static string BuildSignalText(JsonElement root)
    {
        var parts = new List<string>();
        foreach (var property in new[] { "name" })
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String) parts.Add(value.GetString() ?? string.Empty);
        if (root.TryGetProperty("keywords", out var keywords)) parts.Add(keywords.GetRawText());
        return string.Join(' ', parts);
    }

    private static bool TryReadManifest(string? path, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { document = JsonDocument.Parse(File.ReadAllText(path)); return true; }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
