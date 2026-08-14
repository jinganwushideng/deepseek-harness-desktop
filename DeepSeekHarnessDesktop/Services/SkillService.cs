using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed record PackagedSkillSyncResult(
    IReadOnlySet<string> SkillPackages,
    int Imported,
    int Updated,
    int Removed,
    IReadOnlyList<string> Warnings);

public sealed class SkillService
{
    private static readonly Regex SkillNamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string PackageMarkerName = ".dsh-desktop-package.json";

    public string UserRoot(LauncherSettings settings) => Path.Combine(settings.DshHome, "skills");

    public string ProjectRoot(LauncherSettings settings)
    {
        var workspace = Path.GetFullPath(settings.Workspace);
        var cursor = new DirectoryInfo(workspace);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, ".git")) || File.Exists(Path.Combine(cursor.FullName, ".git")))
                return Path.Combine(cursor.FullName, ".dsh", "skills");
            cursor = cursor.Parent;
        }
        return Path.Combine(workspace, ".dsh", "skills");
    }

    public IReadOnlyList<SkillItem> Inspect(LauncherSettings settings)
    {
        var result = new List<SkillItem>();
        InspectRoot(UserRoot(settings), "用户", result);
        InspectRoot(ProjectRoot(settings), "项目", result);
        return result.OrderBy(x => x.Scope).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool PackageProvidesSkills(string profileDirectory, string packageName)
    {
        try
        {
            var packageDirectory = ResolvePackageDirectory(profileDirectory, packageName);
            return Directory.Exists(packageDirectory) && EnumeratePackagedSkillDirectories(packageDirectory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public PackagedSkillSyncResult SyncPackagedSkills(LauncherSettings settings, string profileDirectory, IReadOnlyCollection<string> currentDependencies)
    {
        var root = EnsureRoot(settings, true);
        var current = currentDependencies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skillPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var imported = 0;
        var updated = 0;
        var removed = 0;

        foreach (var packageName in current.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            string packageDirectory;
            try { packageDirectory = ResolvePackageDirectory(profileDirectory, packageName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"{packageName}: 无法定位包目录（{ex.Message}）");
                continue;
            }
            if (!Directory.Exists(packageDirectory)) continue;

            var version = ReadPackageVersion(packageDirectory);
            foreach (var source in EnumeratePackagedSkillDirectories(packageDirectory))
            {
                var metadata = ParseManifest(Path.Combine(source, "SKILL.md"));
                if (!metadata.Valid)
                {
                    warnings.Add($"{packageName}: {Path.GetFileName(source)} 不是有效 Skill（{metadata.Status}）");
                    continue;
                }
                skillPackages.Add(packageName);
                if (!claimedSkillNames.Add(metadata.Name))
                {
                    warnings.Add($"{packageName}: Skill 名称 {metadata.Name} 重复，已跳过后续条目。");
                    continue;
                }

                var target = Path.Combine(root, metadata.Name);
                string fingerprint;
                try { fingerprint = ComputeDirectoryFingerprint(source); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    warnings.Add($"{packageName}: 无法校验 Skill {metadata.Name}（{ex.Message}）");
                    continue;
                }
                var marker = new PackagedSkillMarker(packageName, version, source, fingerprint, DateTimeOffset.UtcNow);
                var currentMarker = Directory.Exists(target) ? ReadPackageMarker(target) : null;
                if (currentMarker is not null
                    && currentMarker.Package.Equals(packageName, StringComparison.OrdinalIgnoreCase)
                    && currentMarker.Version == version
                    && currentMarker.Fingerprint == fingerprint
                    && File.Exists(Path.Combine(target, "SKILL.md"))) continue;
                var staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
                string? rollback = null;
                try
                {
                    CopyDirectory(source, staging);
                    File.WriteAllText(Path.Combine(staging, PackageMarkerName), JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));

                    if (File.Exists(target))
                    {
                        warnings.Add($"{packageName}: 同名 Skill {metadata.Name} 是独立文件，未覆盖用户内容。");
                        Directory.Delete(staging, true);
                        continue;
                    }
                    if (Directory.Exists(target))
                    {
                        var existing = ReadPackageMarker(target);
                        if (existing is null || !existing.Package.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add($"{packageName}: 同名 Skill {metadata.Name} 不属于该包，未覆盖用户内容。");
                            Directory.Delete(staging, true);
                            continue;
                        }
                        rollback = MoveDirectoryToTrash(root, target, "package-update");
                    }

                    Directory.Move(staging, target);
                    if (rollback is null) imported++; else updated++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (Directory.Exists(staging) && IsInside(staging, root)) Directory.Delete(staging, true);
                    if (rollback is not null && Directory.Exists(rollback) && !Directory.Exists(target)) Directory.Move(rollback, target);
                    warnings.Add($"{packageName}: 同步 Skill {metadata.Name} 失败（{ex.Message}）");
                }
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
            var marker = ReadPackageMarker(directory);
            if (marker is null) continue;
            var stillDesired = current.Contains(marker.Package) && claimedSkillNames.Contains(Path.GetFileName(directory));
            if (stillDesired) continue;
            try
            {
                MoveDirectoryToTrash(root, directory, "package-removed");
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{marker.Package}: 回收 Skill {Path.GetFileName(directory)} 失败（{ex.Message}）");
            }
        }

        return new PackagedSkillSyncResult(skillPackages, imported, updated, removed, warnings);
    }

    public string ImportDirectory(LauncherSettings settings, string sourceDirectory, bool userScope)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var manifest = Path.Combine(source, "SKILL.md");
        if (!Directory.Exists(source) || !File.Exists(manifest))
            throw new InvalidOperationException("所选目录必须在根目录包含 SKILL.md。");
        var metadata = ParseManifest(manifest);
        if (!metadata.Valid) throw new InvalidDataException(metadata.Status);
        var root = EnsureRoot(settings, userScope);
        var target = Path.Combine(root, metadata.Name);
        EnsureTargetAvailable(target);
        var staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(source, staging);
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging) && IsInside(staging, root)) Directory.Delete(staging, true);
            throw;
        }
        return target;
    }

    public string ImportMarkdown(LauncherSettings settings, string sourceFile, bool userScope)
    {
        var source = Path.GetFullPath(sourceFile);
        if (!File.Exists(source)) throw new FileNotFoundException("Skill Markdown 文件不存在。", source);
        var metadata = ParseManifest(source);
        if (!metadata.Valid) throw new InvalidDataException(metadata.Status);
        var root = EnsureRoot(settings, userScope);
        var target = Path.Combine(root, metadata.Name + ".md");
        EnsureTargetAvailable(target);
        File.Copy(source, target, false);
        return target;
    }

    public string CreateTemplate(LauncherSettings settings, string name, bool userScope)
    {
        name = name.Trim();
        if (!SkillNamePattern.IsMatch(name)) throw new ArgumentException("Skill 名称必须是小写 kebab-case，例如 code-review。", nameof(name));
        var root = EnsureRoot(settings, userScope);
        var directory = Path.Combine(root, name);
        EnsureTargetAvailable(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: 请填写这个 Skill 的用途和触发场景。\n---\n\n# {name}\n\n请在这里编写完整说明。\n");
        return directory;
    }

    public void SetEnabled(SkillItem item, bool enabled)
    {
        if (item.Enabled == enabled) return;
        var source = Path.GetFullPath(item.ManifestPath);
        string target;
        if (Directory.Exists(item.EntryPath))
            target = Path.Combine(item.EntryPath, enabled ? "SKILL.md" : "SKILL.md.disabled");
        else
            target = enabled ? source[..^".disabled".Length] : source + ".disabled";
        if (File.Exists(target)) throw new IOException("目标状态文件已存在，请先检查 Skill 目录。");
        File.Move(source, target);
    }

    public string MoveToTrash(LauncherSettings settings, SkillItem item)
    {
        var userRoot = Path.GetFullPath(UserRoot(settings));
        var projectRoot = Path.GetFullPath(ProjectRoot(settings));
        var entry = Path.GetFullPath(item.EntryPath);
        var root = IsInside(entry, userRoot) ? userRoot : IsInside(entry, projectRoot) ? projectRoot : throw new InvalidOperationException("Skill 不在受管理的目录中。");
        var trash = Path.Combine(root, ".trash");
        Directory.CreateDirectory(trash);
        var fileName = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trash, fileName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        if (Directory.Exists(entry)) Directory.Move(entry, target);
        else File.Move(entry, target);
        return target;
    }

    private string EnsureRoot(LauncherSettings settings, bool userScope)
    {
        var root = userScope ? UserRoot(settings) : ProjectRoot(settings);
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static string ResolvePackageDirectory(string profileDirectory, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) || packageName.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("包名无效。", nameof(packageName));
        var nodeModules = Path.GetFullPath(Path.Combine(profileDirectory, "node_modules"));
        var candidate = Path.GetFullPath(Path.Combine(nodeModules, packageName.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(candidate, nodeModules) || candidate.Equals(nodeModules, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("包目录越出 node_modules。");
        return candidate;
    }

    private static IEnumerable<string> EnumeratePackagedSkillDirectories(string packageDirectory)
    {
        if (File.Exists(Path.Combine(packageDirectory, "SKILL.md"))) yield return packageDirectory;
        foreach (var relativeRoot in new[] { "skills", Path.Combine(".agents", "skills") })
        {
            var root = Path.Combine(packageDirectory, relativeRoot);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                if (File.Exists(Path.Combine(directory, "SKILL.md"))) yield return directory;
        }
    }

    private static string ReadPackageVersion(string packageDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageDirectory, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return string.Empty; }
    }

    private static PackagedSkillMarker? ReadPackageMarker(string skillDirectory)
    {
        try
        {
            var path = Path.Combine(skillDirectory, PackageMarkerName);
            return File.Exists(path) ? JsonSerializer.Deserialize<PackagedSkillMarker>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string MoveDirectoryToTrash(string root, string source, string reason)
    {
        var trash = Path.Combine(root, ".trash");
        Directory.CreateDirectory(trash);
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trash, $"{name}-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.Move(source, target);
        return target;
    }

    private static string ComputeDirectoryFingerprint(string source)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 目录不能是符号链接。");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 目录不能包含符号链接。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(path => Path.GetRelativePath(source, path), StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 目录不能包含符号链接。");
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')));
            using var stream = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void InspectRoot(string root, string scope, ICollection<SkillItem> result)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
            var enabled = Path.Combine(directory, "SKILL.md");
            var disabled = Path.Combine(directory, "SKILL.md.disabled");
            var manifest = File.Exists(enabled) ? enabled : File.Exists(disabled) ? disabled : null;
            if (manifest is not null) result.Add(CreateItem(directory, manifest, scope, manifest == enabled));
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var enabled = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            var disabled = name.EndsWith(".md.disabled", StringComparison.OrdinalIgnoreCase);
            if (enabled || disabled) result.Add(CreateItem(file, file, scope, enabled));
        }
    }

    private static SkillItem CreateItem(string entry, string manifest, string scope, bool enabled)
    {
        var metadata = ParseManifest(manifest);
        var fallbackName = Directory.Exists(entry)
            ? Path.GetFileName(entry)
            : Regex.Replace(Path.GetFileName(entry), "\\.md(?:\\.disabled)?$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new SkillItem(metadata.Valid ? metadata.Name : fallbackName, metadata.Description, scope, entry, manifest, enabled, metadata.ModelInvocable, metadata.UserInvocable, metadata.Valid, metadata.Status);
    }

    private static SkillMetadata ParseManifest(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            if (!text.StartsWith("---\n", StringComparison.Ordinal)) return Invalid("缺少 YAML frontmatter 起始标记 ---");
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return Invalid("缺少 YAML frontmatter 结束标记 ---");
            var lines = text[4..end].Split('\n');
            var name = ReadScalar(lines, "name");
            var description = ReadScalar(lines, "description");
            if (string.IsNullOrWhiteSpace(name)) return Invalid("Skill frontmatter 缺少 name。");
            if (!SkillNamePattern.IsMatch(name)) return Invalid($"Skill 名称 {name} 不是小写 kebab-case。");
            if (string.IsNullOrWhiteSpace(description)) return Invalid($"Skill {name} 缺少 description。");
            var disableModel = ReadBoolean(lines, "disable-model-invocation", false, out var modelValid);
            var userInvocable = ReadBoolean(lines, "user-invocable", true, out var userValid);
            if (!modelValid || !userValid) return Invalid($"Skill {name} 的调用策略必须是布尔值。");
            return new SkillMetadata(name, description, !disableModel, userInvocable, true, "有效");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid("无法读取 Skill：" + ex.Message);
        }
    }

    private static string ReadScalar(IReadOnlyList<string> lines, string key)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var match = Regex.Match(lines[index], $"^{Regex.Escape(key)}\\s*:\\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var value = match.Groups[1].Value.Trim();
            if (value is "|" or ">")
            {
                var parts = new List<string>();
                for (var next = index + 1; next < lines.Count && (lines[next].StartsWith(" ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lines[next])); next++)
                    if (!string.IsNullOrWhiteSpace(lines[next])) parts.Add(lines[next].Trim());
                return string.Join(value == ">" ? " " : "\n", parts);
            }
            return value.Trim('"', '\'');
        }
        return string.Empty;
    }

    private static bool ReadBoolean(IReadOnlyList<string> lines, string key, bool fallback, out bool valid)
    {
        var value = ReadScalar(lines, key);
        if (string.IsNullOrWhiteSpace(value)) { valid = true; return fallback; }
        valid = true;
        return value.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => InvalidBoolean(out valid)
        };
    }

    private static bool InvalidBoolean(out bool valid) { valid = false; return false; }
    private static SkillMetadata Invalid(string status) => new(string.Empty, string.Empty, false, false, false, status);
    private static void EnsureTargetAvailable(string target) { if (File.Exists(target) || Directory.Exists(target)) throw new IOException("同名 Skill 已存在：" + target); }
    private static bool IsInside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 目录不能是符号链接。");
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 目录不能包含符号链接。");
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 目录不能包含符号链接。");
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
        }
    }

    private sealed record SkillMetadata(string Name, string Description, bool ModelInvocable, bool UserInvocable, bool Valid, string Status);
    private sealed record PackagedSkillMarker(string Package, string Version, string SourcePath, string? Fingerprint, DateTimeOffset InstalledAt);
}
