using System.Text;
using System.Text.Json;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class PluginService
{
    private readonly AppPaths _paths;
    private readonly HarnessProcessService _server;
    private readonly NodeHelperService _helper;
    private readonly LogService _log;
    private readonly SkillService _skills;
    public PluginService(AppPaths paths, HarnessProcessService server, NodeHelperService helper, LogService log, SkillService? skills = null) { _paths = paths; _server = server; _helper = helper; _log = log; _skills = skills ?? new SkillService(); }

    public async Task<IReadOnlyList<PluginItem>> InspectAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path.Combine(settings.DshHome, "profiles", "web", "package.json")))
            await _server.RunCliAsync(settings, "--profile web --dump-default-config", line => _log.Info("profile", line), cancellationToken);
        using var response = await _helper.CallAsync(settings, new { op = "profile.inspect", patchPath = _paths.LauncherPatch }, cancellationToken);
        var list = new List<PluginItem>();
        if (response.RootElement.TryGetProperty("rows", out var rows))
        {
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.GetProperty("id").GetString() ?? "unknown";
                var package = row.TryGetProperty("package", out var p) ? p.GetString() ?? "" : "";
                var source = row.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                var builtIn = row.TryGetProperty("builtIn", out var b) && b.GetBoolean();
                var disabled = row.TryGetProperty("disabled", out var d) && d.GetBoolean();
                list.Add(new PluginItem(id, package, source, "", builtIn, disabled));
            }
        }
        if (response.RootElement.TryGetProperty("dependencies", out var dependencies))
        {
            foreach (var item in dependencies.EnumerateObject())
                if (!list.Any(x => x.Source == item.Name))
                    list.Add(new PluginItem(item.Name, item.Name, "用户依赖", item.Value.GetString() ?? "", false, false));
        }
        var storeManifest = Path.Combine(UserPackageRoot(settings), "package.json");
        if (File.Exists(storeManifest))
        {
            foreach (var item in ReadDependencyMap(storeManifest))
                if (!list.Any(x => x.Package.Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new PluginItem(item.Key, item.Key, "用户 Skill 包", item.Value, false, false));
        }
        return list;
    }

    public string UserPluginDirectory(LauncherSettings settings) => Path.Combine(settings.DshHome, "profiles", "web", "node_modules");
    public string UserSkillPackageDirectory(LauncherSettings settings) => Path.Combine(UserPackageRoot(settings), "node_modules");
    public string OfficialPluginDirectory(LauncherSettings settings) => Path.Combine(_paths.VersionRoot(settings.CurrentRuntimeVersion), "app", "node_modules", "@deepseek-ai");

    public void SyncStoredSkills(LauncherSettings settings)
    {
        var store = UserPackageRoot(settings);
        var manifest = Path.Combine(store, "package.json");
        if (!File.Exists(manifest)) return;
        LogSkillSync(_skills.SyncPackagedSkills(settings, store, ReadDependencyNames(manifest)));
    }

    public async Task SetDisabledAsync(LauncherSettings settings, PluginItem plugin, bool disabled, CancellationToken cancellationToken = default)
    {
        var snapshot = _paths.LauncherPatch + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        File.Copy(_paths.LauncherPatch, snapshot, true);
        try
        {
            using var _ = await _helper.CallAsync(settings, new { op = "patch.setDisabled", path = _paths.LauncherPatch, id = plugin.Id, disabled }, cancellationToken);
            var exit = await _server.RunCliAsync(settings, $"--profile web --patch \"{_paths.LauncherPatch}\" --dump-config", line => _log.Info("config", line), cancellationToken);
            if (exit != 0) throw new InvalidOperationException("插件配置验证失败。");
        }
        catch { File.Copy(snapshot, _paths.LauncherPatch, true); throw; }
    }

    public async Task<int> InstallAsync(LauncherSettings settings, string spec, bool linkLocal, CancellationToken cancellationToken = default)
    {
        spec = NormalizeInstallSpec(spec);
        if (Directory.Exists(spec))
        {
            var localPath = Path.GetFullPath(spec).TrimEnd(Path.DirectorySeparatorChar);
            var officialPath = Path.GetFullPath(OfficialPluginDirectory(settings)).TrimEnd(Path.DirectorySeparatorChar);
            if (localPath.Equals(officialPath, StringComparison.OrdinalIgnoreCase) || localPath.StartsWith(officialPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能把当前运行时的官方组件目录当作用户插件来源。 ");
            spec = (linkLocal ? "link:" : "file:") + localPath;
        }
        return await RunManagedPluginCommandAsync(settings, $"add \"{ValidatePnpmArgument(spec)}\"", cancellationToken);
    }

    public static string NormalizeInstallSpec(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("插件来源不能为空。", nameof(input));
        var trimmed = input.Trim();
        var tokens = TokenizeCommand(trimmed);
        if (tokens.Count == 0) throw new ArgumentException("插件来源不能为空。", nameof(input));

        var executable = Path.GetFileNameWithoutExtension(tokens[0]).ToLowerInvariant();
        if (executable == "dsh") return NormalizeRepositoryLink(ParseDshCommand(tokens, 1, input));

        if (executable == "npx")
        {
            var index = SkipRunnerFlags(tokens, 1);
            RequireDshPackage(tokens, index, input);
            return NormalizeRepositoryLink(ParseDshCommand(tokens, index + 1, input));
        }

        if (executable is "pnpm" or "yarn" && tokens.Count > 1 && tokens[1].Equals("dlx", StringComparison.OrdinalIgnoreCase))
        {
            var index = SkipRunnerFlags(tokens, 2);
            RequireDshPackage(tokens, index, input);
            return NormalizeRepositoryLink(ParseDshCommand(tokens, index + 1, input));
        }

        if (executable == "bunx")
        {
            var index = SkipRunnerFlags(tokens, 1);
            RequireDshPackage(tokens, index, input);
            return NormalizeRepositoryLink(ParseDshCommand(tokens, index + 1, input));
        }

        if (executable == "npm" && tokens.Count > 1 && tokens[1].Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            var index = SkipRunnerFlags(tokens, 2);
            if (index < tokens.Count && tokens[index] == "--") index++;
            RequireDshPackage(tokens, index, input);
            return NormalizeRepositoryLink(ParseDshCommand(tokens, index + 1, input));
        }

        if (executable is "npm" or "pnpm" or "yarn" or "bun")
            return NormalizeRepositoryLink(ParsePackageManagerCommand(executable, tokens, input));

        return NormalizeRepositoryLink(trimmed);
    }

    public static string DescribeInstallSource(string spec)
    {
        if (spec.StartsWith("git", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("gitlab:", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("bitbucket:", StringComparison.OrdinalIgnoreCase)) return "Git 仓库";
        if (Uri.TryCreate(spec, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "远程链接";
        return "npm 包";
    }

    private static int SkipRunnerFlags(IReadOnlyList<string> tokens, int index)
    {
        while (index < tokens.Count && (tokens[index].Equals("-y", StringComparison.OrdinalIgnoreCase) || tokens[index].Equals("--yes", StringComparison.OrdinalIgnoreCase))) index++;
        return index;
    }

    private static void RequireDshPackage(IReadOnlyList<string> tokens, int index, string input)
    {
        if (index >= tokens.Count || !(tokens[index].Equals("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase) || tokens[index].StartsWith("@deepseek-ai/dsh@", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("完整 Harness 命令必须使用 @deepseek-ai/dsh。", nameof(input));
    }

    private static string ParseDshCommand(IReadOnlyList<string> tokens, int index, string input)
    {
        if (index >= tokens.Count || !tokens[index].Equals("plugin", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("无法识别这条 Harness 命令：缺少 plugin 子命令。", nameof(input));
        index++;
        if (index < tokens.Count && tokens[index].StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
        {
            if (!tokens[index].Equals("--profile=web", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("桌面壳只管理 web profile。", nameof(input));
            index++;
        }
        else if (index < tokens.Count && tokens[index].Equals("--profile", StringComparison.OrdinalIgnoreCase))
        {
            if (++index >= tokens.Count || !tokens[index].Equals("web", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("桌面壳只管理 web profile。", nameof(input));
            index++;
        }
        if (index >= tokens.Count || !tokens[index].Equals("add", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("无法识别这条 Harness 命令：缺少 add 操作。", nameof(input));
        index++;
        if (index != tokens.Count - 1 || string.IsNullOrWhiteSpace(tokens[index])) throw new ArgumentException("完整命令中必须且只能包含一个插件来源。", nameof(input));
        return tokens[index];
    }

    private static string ParsePackageManagerCommand(string manager, IReadOnlyList<string> tokens, string input)
    {
        var operations = manager switch
        {
            "yarn" => new[] { "add" },
            _ => new[] { "add", "install", "i" }
        };
        var operation = -1;
        for (var index = 1; index < tokens.Count; index++)
            if (operations.Contains(tokens[index], StringComparer.OrdinalIgnoreCase)) { operation = index; break; }
        if (operation < 0) throw new ArgumentException($"无法识别 {manager} 安装操作。", nameof(input));

        var candidates = tokens.Skip(operation + 1).Where(token => token != "--" && !token.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 1) throw new ArgumentException("安装指令必须且只能包含一个插件来源。", nameof(input));
        return candidates[0];
    }

    private static string NormalizeRepositoryLink(string source)
    {
        if (source.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var separator = source.IndexOf(':');
            if (separator > 4) return "git+ssh://" + source[..separator] + "/" + source[(separator + 1)..];
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return source;
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (host is "github.com" or "www.github.com")
        {
            if (segments.Length < 2) throw new ArgumentException("GitHub 链接必须指向具体仓库。", nameof(source));
            var reference = ExtractRepositoryReference(segments, 2, source, "GitHub");
            return BuildGitUrl(uri, segments.Take(2), reference);
        }
        if (host is "bitbucket.org" or "www.bitbucket.org")
        {
            if (segments.Length < 2) throw new ArgumentException("Bitbucket 链接必须指向具体仓库。", nameof(source));
            string? reference = null;
            if (segments.Length > 2)
            {
                if (!segments[2].Equals("src", StringComparison.OrdinalIgnoreCase) || segments.Length < 4) throw new ArgumentException("请使用 Bitbucket 仓库首页或 src/<分支> 链接。", nameof(source));
                reference = string.Join('/', segments.Skip(3));
            }
            return BuildGitUrl(uri, segments.Take(2), reference);
        }
        if (host is "gitlab.com" or "www.gitlab.com")
        {
            var marker = Array.IndexOf(segments, "-");
            var projectSegments = marker < 0 ? segments : segments.Take(marker).ToArray();
            if (projectSegments.Length < 2) throw new ArgumentException("GitLab 链接必须指向具体仓库。", nameof(source));
            string? reference = null;
            if (marker >= 0)
            {
                var tail = segments.Skip(marker + 1).ToArray();
                if (tail.Length < 2 || tail[0] is not ("tree" or "tags" or "commit")) throw new ArgumentException("请使用 GitLab 仓库首页、分支、标签或提交链接。", nameof(source));
                reference = string.Join('/', tail.Skip(1));
            }
            return BuildGitUrl(uri, projectSegments, reference);
        }
        if (host is "gitee.com" or "www.gitee.com")
        {
            if (segments.Length < 2) throw new ArgumentException("Gitee 链接必须指向具体仓库。", nameof(source));
            var reference = ExtractRepositoryReference(segments, 2, source, "Gitee");
            return BuildGitUrl(uri, segments.Take(2), reference);
        }
        if (uri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) return "git+" + source;
        return source;
    }

    private static string? ExtractRepositoryReference(IReadOnlyList<string> segments, int index, string source, string provider)
    {
        if (segments.Count == index) return null;
        if (segments.Count <= index + 1 || segments[index] is not ("tree" or "commit")) throw new ArgumentException($"请使用 {provider} 仓库首页、分支或提交链接。", nameof(source));
        return string.Join('/', segments.Skip(index + 1));
    }

    private static string BuildGitUrl(Uri original, IEnumerable<string> projectSegments, string? reference)
    {
        var project = string.Join('/', projectSegments).TrimEnd('/');
        if (!project.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) project += ".git";
        var authority = original.GetLeftPart(UriPartial.Authority);
        var fragment = string.IsNullOrWhiteSpace(reference) ? original.Fragment : "#" + reference;
        return $"git+{authority}/{project}{fragment}";
    }

    private static IReadOnlyList<string> TokenizeCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        for (var index = 0; index < command.Length; index++)
        {
            var value = command[index];
            if (quote != '\0')
            {
                if (value == quote) quote = '\0';
                else current.Append(value);
                continue;
            }
            if (value is '\'' or '"') { quote = value; continue; }
            if (char.IsWhiteSpace(value))
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(value);
        }
        if (quote != '\0') throw new ArgumentException("安装命令中的引号未闭合。", nameof(command));
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
    public async Task<int> RemoveAsync(LauncherSettings settings, PluginItem plugin, CancellationToken cancellationToken = default)
    {
        if (plugin.IsBuiltIn) throw new InvalidOperationException("官方插件只能禁用，不能删除。");
        return plugin.Source == "用户 Skill 包"
            ? await RunManagedStoreCommandAsync(settings, $"remove \"{ValidatePnpmArgument(plugin.Package)}\"", cancellationToken)
            : await RunManagedPluginCommandAsync(settings, $"remove \"{ValidatePnpmArgument(plugin.Package)}\"", cancellationToken);
    }
    public async Task<int> UpdateAllAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var exit = await RunManagedPluginCommandAsync(settings, "update", cancellationToken);
        return exit == 0 ? await RunManagedStoreCommandAsync(settings, "update", cancellationToken, false) : exit;
    }
    public async Task<int> ReinstallAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var exit = await RunManagedPluginCommandAsync(settings, "install", cancellationToken);
        return exit == 0 ? await RunManagedStoreCommandAsync(settings, "install", cancellationToken, false) : exit;
    }

    private async Task<int> RunManagedPluginCommandAsync(LauncherSettings settings, string pnpmArguments, CancellationToken cancellationToken)
    {
        var profileDir = Path.Combine(settings.DshHome, "profiles", "web");
        var manifestPath = Path.Combine(profileDir, "package.json");
        if (!File.Exists(manifestPath))
        {
            var initExit = await _server.RunCliAsync(settings, "--profile web --dump-default-config", line => _log.Info("profile", line), cancellationToken);
            if (initExit != 0 || !File.Exists(manifestPath)) throw new InvalidOperationException("web profile 初始化失败。");
        }
        var beforeDependencies = ReadDependencyNames(manifestPath);
        _log.Info("plugin", $"running managed pnpm {pnpmArguments}");
        var exit = await _server.RunPnpmAsync(settings, pnpmArguments, profileDir, line => _log.Info("plugin", line), cancellationToken);
        if (exit != 0) return exit;
        using var response = await _helper.CallAsync(settings, new { op = "plugin.reconcile", beforeDependencies }, cancellationToken);
        var plainPackages = response.RootElement.TryGetProperty("plain", out var plainResponse)
            ? plainResponse.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];
        var packagedSkills = plainPackages.Where(package => _skills.PackageProvidesSkills(profileDir, package)).ToArray();
        if (packagedSkills.Length > 0)
        {
            exit = await MigrateSkillPackagesAsync(settings, profileDir, manifestPath, packagedSkills, cancellationToken);
            if (exit != 0) return exit;
            _log.Info("plugin", "moved CLI + Skill packages to isolated user store: " + string.Join(", ", packagedSkills));
        }
        SyncStoredSkills(settings);
        if (response.RootElement.TryGetProperty("plain", out var plainElement) && plainElement.GetArrayLength() > 0)
        {
            var unrecognized = plainElement.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item) && !packagedSkills.Contains(item!, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (unrecognized.Length > 0) _log.Warn("plugin", "installed dependencies without dsh.bundle or packaged Skill: " + string.Join(", ", unrecognized));
        }
        return 0;
    }

    private async Task<int> MigrateSkillPackagesAsync(LauncherSettings settings, string profileDir, string manifestPath, IReadOnlyCollection<string> packageNames, CancellationToken cancellationToken)
    {
        var dependencies = ReadDependencyMap(manifestPath);
        foreach (var packageName in packageNames)
        {
            if (!dependencies.TryGetValue(packageName, out var version)) continue;
            var installSpec = FormatDependencySpec(packageName, version);
            var exit = await RunManagedStoreCommandAsync(settings, $"add \"{ValidatePnpmArgument(installSpec)}\"", cancellationToken);
            if (exit != 0) return exit;
            exit = await _server.RunPnpmAsync(settings, $"remove \"{ValidatePnpmArgument(packageName)}\"", profileDir, line => _log.Info("plugin", line), cancellationToken);
            if (exit != 0) return exit;
        }
        return 0;
    }

    private async Task<int> RunManagedStoreCommandAsync(LauncherSettings settings, string pnpmArguments, CancellationToken cancellationToken, bool createIfMissing = true)
    {
        var store = UserPackageRoot(settings);
        var manifest = Path.Combine(store, "package.json");
        if (!File.Exists(manifest))
        {
            if (!createIfMissing) return 0;
            Directory.CreateDirectory(store);
            await File.WriteAllTextAsync(manifest, "{\n  \"name\": \"dsh-desktop-user-packages\",\n  \"private\": true,\n  \"dependencies\": {}\n}\n", cancellationToken);
        }
        _log.Info("plugin", $"running isolated user-package pnpm {pnpmArguments}");
        var exit = await _server.RunPnpmAsync(settings, pnpmArguments, store, line => _log.Info("plugin", line), cancellationToken);
        if (exit == 0) SyncStoredSkills(settings);
        return exit;
    }

    private void LogSkillSync(PackagedSkillSyncResult result)
    {
        foreach (var warning in result.Warnings) _log.Warn("skill", warning);
        if (result.Imported + result.Updated + result.Removed > 0)
            _log.Info("skill", $"package skills synced: imported={result.Imported} updated={result.Updated} removed={result.Removed}");
    }

    private static string UserPackageRoot(LauncherSettings settings) => Path.Combine(settings.DshHome, "launcher-packages");

    private static string[] ReadDependencyNames(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.TryGetProperty("dependencies", out var dependencies)
            ? dependencies.EnumerateObject().Select(item => item.Name).ToArray()
            : [];
    }

    private static Dictionary<string, string> ReadDependencyMap(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.TryGetProperty("dependencies", out var dependencies)
            ? dependencies.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static string FormatDependencySpec(string packageName, string version) => string.IsNullOrWhiteSpace(version) ? packageName : $"{packageName}@{version}";

    private static string ValidatePnpmArgument(string argument)
    {
        if (argument.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) throw new ArgumentException("插件来源包含不安全的命令字符。", nameof(argument));
        return argument;
    }
}
