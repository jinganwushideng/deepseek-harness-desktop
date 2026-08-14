using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using DeepSeekHarnessDesktop.Models;
using DeepSeekHarnessDesktop.Services;
using Xunit;

namespace DeepSeekHarnessDesktop.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsh-desktop-tests-" + Guid.NewGuid().ToString("N"));

    public CoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LogRedaction_RemovesCommonSecretShapes()
    {
        var clean = LogService.Redact("api_key=integration-secret-value token: abcdefghijklmnop sk-testplaceholder12345");
        Assert.DoesNotContain("integration-secret-value", clean);
        Assert.DoesNotContain("abcdefghijklmnop", clean);
        Assert.DoesNotContain("sk-testplaceholder12345", clean);
    }

    [Fact]
    public void LauncherSettings_DoesNotHaveASecretField()
    {
        var json = JsonSerializer.Serialize(new LauncherSettings());
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResponseNotification_DefaultsOnForExistingSettings()
    {
        var settings = JsonSerializer.Deserialize<LauncherSettings>("{}");
        Assert.NotNull(settings);
        Assert.True(settings.NotifyOnResponseComplete);
        Assert.Equal(ShellThemeService.FollowWeb, settings.ShellThemeMode);
    }

    [Theory]
    [InlineData(null, "FollowWeb")]
    [InlineData("", "FollowWeb")]
    [InlineData("unexpected", "FollowWeb")]
    [InlineData("FollowWeb", "FollowWeb")]
    [InlineData("FollowSystem", "FollowSystem")]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    public void ShellThemeMode_IsNormalized(string? input, string expected) =>
        Assert.Equal(expected, ShellThemeService.NormalizeMode(input));

    [Fact]
    public void ShellThemeMode_ResolvesEveryStrategy()
    {
        Assert.True(ShellThemeService.ResolveLight(ShellThemeService.Light, false, false));
        Assert.False(ShellThemeService.ResolveLight(ShellThemeService.Dark, true, true));
        Assert.True(ShellThemeService.ResolveLight(ShellThemeService.FollowSystem, false, true));
        Assert.False(ShellThemeService.ResolveLight(ShellThemeService.FollowSystem, true, false));
        Assert.True(ShellThemeService.ResolveLight(ShellThemeService.FollowWeb, true, false));
        Assert.False(ShellThemeService.ResolveLight(ShellThemeService.FollowWeb, false, true));
        Assert.True(ShellThemeService.ResolveLight(ShellThemeService.FollowWeb, null, true));
    }

    [Fact]
    public void WebThemeMonitor_UsesHarnessResolvedThemeSignal()
    {
        Assert.Contains("data-ds-dark-theme", WebThemeMonitor.Script, StringComparison.Ordinal);
        Assert.Contains("colorScheme", WebThemeMonitor.Script, StringComparison.Ordinal);
        Assert.True(WebThemeMonitor.TryReadMessage(WebThemeMonitor.LightMessage, out var light) && light);
        Assert.True(WebThemeMonitor.TryReadMessage(WebThemeMonitor.DarkMessage, out light) && !light);
        Assert.False(WebThemeMonitor.TryReadMessage("dsh-theme:unknown", out _));
    }

    [Fact]
    public void ResponseCompletionMonitor_UsesHarnessRunningSignals()
    {
        Assert.Contains("data-streaming", ResponseCompletionMonitor.Script, StringComparison.Ordinal);
        Assert.Contains("停止生成", ResponseCompletionMonitor.Script, StringComparison.Ordinal);
        Assert.Contains("stop generating", ResponseCompletionMonitor.Script, StringComparison.Ordinal);
        Assert.True(ResponseCompletionMonitor.IsCompletionMessage(ResponseCompletionMonitor.CompletionMessage));
        Assert.False(ResponseCompletionMonitor.IsCompletionMessage("other-message"));
    }

    [Fact]
    public void PortDetection_ReportsOwnedListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(HarnessProcessService.IsPortInUse(port));
        listener.Stop();
        Assert.False(HarnessProcessService.IsPortInUse(port));
    }

    [Fact]
    public void ConnectionSettings_AreTrimmedAndNormalized()
    {
        var workspace = Path.Combine(_root, "workspace");
        var home = Path.Combine(_root, "home");
        var result = SettingsService.ValidateConnectionInput("  " + workspace + "  ", "  " + home + "  ", "31809");
        Assert.Equal(Path.GetFullPath(workspace), result.Workspace);
        Assert.Equal(Path.GetFullPath(home), result.DshHome);
        Assert.Equal(31809, result.Port);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1023")]
    [InlineData("65536")]
    public void ConnectionSettings_RejectInvalidPorts(string port) =>
        Assert.Throws<ArgumentException>(() => SettingsService.ValidateConnectionInput(_root, Path.Combine(_root, "home"), port));

    [Fact]
    public void ConnectionSettings_RejectBlankDirectories()
    {
        Assert.Throws<ArgumentException>(() => SettingsService.ValidateConnectionInput(" ", Path.Combine(_root, "home"), "3080"));
        Assert.Throws<ArgumentException>(() => SettingsService.ValidateConnectionInput(_root, " ", "3080"));
    }

    [Fact]
    public async Task OfficialPlugin_CannotBeRemoved()
    {
        var paths = new AppPaths(Path.Combine(_root, "plugin"));
        paths.EnsureDirectories();
        using var log = new LogService(paths);
        using var server = new HarnessProcessService(paths, log);
        var helper = new NodeHelperService(paths, log);
        var service = new PluginService(paths, server, helper, log);
        var item = new PluginItem("timer", "@deepseek-ai/cordis-plugin-timer", "@deepseek-ai/dsh-base", "", true, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(new LauncherSettings(), item));
    }

    [Fact]
    public async Task UserAndOfficialPluginDirectories_AreSeparatedAndProtected()
    {
        var paths = new AppPaths(Path.Combine(_root, "plugin-separation"));
        paths.EnsureDirectories();
        using var log = new LogService(paths);
        using var server = new HarnessProcessService(paths, log);
        var service = new PluginService(paths, server, new NodeHelperService(paths, log), log);
        var settings = new LauncherSettings { DshHome = Path.Combine(paths.Root, "user-home"), CurrentRuntimeVersion = RuntimeInfo.SeedVersion };
        var userDirectory = Path.GetFullPath(service.UserPluginDirectory(settings));
        var officialDirectory = Path.GetFullPath(service.OfficialPluginDirectory(settings));
        Assert.StartsWith(Path.GetFullPath(settings.DshHome), userDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(paths.VersionRoot(RuntimeInfo.SeedVersion)), officialDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.False(userDirectory.Equals(officialDirectory, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(officialDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(settings, officialDirectory, false));
    }

    [Theory]
    [InlineData("@liustack/modlens@latest", "@liustack/modlens@latest")]
    [InlineData("npx -y @deepseek-ai/dsh plugin --profile web add @liustack/modlens@latest", "@liustack/modlens@latest")]
    [InlineData("pnpm dlx @deepseek-ai/dsh@latest plugin --profile=web add @scope/tool@next", "@scope/tool@next")]
    [InlineData("bunx @deepseek-ai/dsh plugin --profile web add github:owner/repo", "github:owner/repo")]
    [InlineData("npm install @scope/tool@latest", "@scope/tool@latest")]
    [InlineData("pnpm add @scope/tool", "@scope/tool")]
    [InlineData("yarn add @scope/tool@1.2.3", "@scope/tool@1.2.3")]
    [InlineData("bun add git+https://example.com/owner/repo.git", "git+https://example.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo", "git+https://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo/tree/v1.2.3", "git+https://github.com/owner/repo.git#v1.2.3")]
    [InlineData("https://gitlab.com/group/subgroup/repo/-/tree/main", "git+https://gitlab.com/group/subgroup/repo.git#main")]
    [InlineData("https://bitbucket.org/owner/repo/src/develop", "git+https://bitbucket.org/owner/repo.git#develop")]
    [InlineData("https://gitee.com/owner/repo/tree/master", "git+https://gitee.com/owner/repo.git#master")]
    [InlineData("git@github.com:owner/repo.git", "git+ssh://git@github.com/owner/repo.git")]
    public void PluginInstallSource_NormalizesCommandsAndRepositoryLinks(string input, string expected) =>
        Assert.Equal(expected, PluginService.NormalizeInstallSpec(input));

    [Theory]
    [InlineData("npm install one two")]
    [InlineData("npx -y @deepseek-ai/dsh plugin --profile tui add @scope/tool")]
    [InlineData("https://github.com/owner/repo/issues/1")]
    [InlineData("pnpm update @scope/tool")]
    public void PluginInstallSource_RejectsAmbiguousOrUnsafeCommands(string input) =>
        Assert.Throws<ArgumentException>(() => PluginService.NormalizeInstallSpec(input));

    [Theory]
    [InlineData("@liustack/modlens", "^3.6.0", "@liustack/modlens@^3.6.0")]
    [InlineData("vision-tool", "file:D:/plugins/vision-tool", "vision-tool@file:D:/plugins/vision-tool")]
    [InlineData("vision-tool", "", "vision-tool")]
    public void IsolatedPackageDependencySpec_PreservesSource(string packageName, string version, string expected) =>
        Assert.Equal(expected, PluginService.FormatDependencySpec(packageName, version));

    [Fact]
    public async Task EncryptedBackup_RoundTripsAndKeepsRollback()
    {
        var (paths, log, backup, settings) = CreateBackupFixture("roundtrip");
        using (log)
        {
            Directory.CreateDirectory(settings.DshHome);
            await File.WriteAllTextAsync(Path.Combine(settings.DshHome, "session.json"), "{\"ok\":true}");
            await File.WriteAllTextAsync(Path.Combine(settings.DshHome, ".credentials.yaml"), "TEST: placeholder");
            var archive = Path.Combine(paths.Backups, "test.dshbackup");
            await backup.CreateAsync(settings, "correct-horse-battery", false, archive);
            await File.WriteAllTextAsync(Path.Combine(settings.DshHome, "session.json"), "changed");
            var rollback = await backup.RestoreAsync(settings, archive, "correct-horse-battery");
            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(Path.Combine(settings.DshHome, "session.json")));
            Assert.False(File.Exists(Path.Combine(settings.DshHome, ".credentials.yaml")));
            Assert.True(Directory.Exists(rollback));
        }
    }

    [Fact]
    public async Task EncryptedBackup_RejectsWrongPasswordAndTampering()
    {
        var (paths, log, backup, settings) = CreateBackupFixture("integrity");
        using (log)
        {
            Directory.CreateDirectory(settings.DshHome);
            await File.WriteAllTextAsync(Path.Combine(settings.DshHome, "data.txt"), new string('x', 4096));
            var archive = Path.Combine(paths.Backups, "test.dshbackup");
            await backup.CreateAsync(settings, "right-password-123", false, archive);
            await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => backup.ValidateAsync(archive, "wrong-password-123"));
            await using (var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = stream.Length - 5;
                var value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 0x40));
            }
            await Assert.ThrowsAnyAsync<Exception>(() => backup.ValidateAsync(archive, "right-password-123"));
        }
    }

    [Fact]
    public async Task Restore_RejectsZipSlipBeforeSwitchingHome()
    {
        var (paths, log, backup, settings) = CreateBackupFixture("zipslip");
        using (log)
        {
            Directory.CreateDirectory(settings.DshHome);
            await File.WriteAllTextAsync(Path.Combine(settings.DshHome, "keep.txt"), "safe");
            var zip = Path.Combine(paths.Staging, "malicious.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escape.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("bad");
            }
            var encrypted = Path.Combine(paths.Backups, "malicious.dshbackup");
            var method = typeof(BackupService).GetMethod("EncryptAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await (Task)method.Invoke(null, [zip, encrypted, "zip-slip-password", CancellationToken.None])!;
            await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(settings, encrypted, "zip-slip-password"));
            Assert.Equal("safe", await File.ReadAllTextAsync(Path.Combine(settings.DshHome, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(paths.Root, "escape.txt")));
        }
    }

    [Fact]
    public void Skills_AreSeparatedByUserAndProjectScope()
    {
        var workspace = Path.Combine(_root, "skill-workspace");
        var dshHome = Path.Combine(_root, "skill-home");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var settings = new LauncherSettings { Workspace = workspace, DshHome = dshHome };
        var service = new SkillService();

        var user = service.CreateTemplate(settings, "user-helper", true);
        var project = service.CreateTemplate(settings, "project-helper", false);
        File.WriteAllText(Path.Combine(project, "SKILL.md"), "---\nname: project-helper\ndescription: Project workflow\ndisable-model-invocation: true\nuser-invocable: true\n---\n\n# Project\n");

        var items = service.Inspect(settings);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Name == "user-helper" && item.Scope == "用户" && item.ModelInvocable);
        Assert.Contains(items, item => item.Name == "project-helper" && item.Scope == "项目" && !item.ModelInvocable && item.UserInvocable);
        Assert.StartsWith(Path.GetFullPath(dshHome), Path.GetFullPath(user), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(workspace), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skill_CanBeDisabledEnabledAndMovedToRecoverableTrash()
    {
        var settings = new LauncherSettings { Workspace = Path.Combine(_root, "toggle-workspace"), DshHome = Path.Combine(_root, "toggle-home") };
        Directory.CreateDirectory(settings.Workspace);
        var service = new SkillService();
        service.CreateTemplate(settings, "toggle-helper", true);

        var item = Assert.Single(service.Inspect(settings));
        service.SetEnabled(item, false);
        item = Assert.Single(service.Inspect(settings));
        Assert.False(item.Enabled);
        Assert.EndsWith("SKILL.md.disabled", item.ManifestPath, StringComparison.OrdinalIgnoreCase);

        service.SetEnabled(item, true);
        item = Assert.Single(service.Inspect(settings));
        Assert.True(item.Enabled);
        var trash = service.MoveToTrash(settings, item);
        Assert.Empty(service.Inspect(settings));
        Assert.True(Directory.Exists(trash));
        Assert.Contains(Path.DirectorySeparatorChar + ".trash" + Path.DirectorySeparatorChar, trash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedSkills_AreImportedUpdatedAndRecoverablyRemoved()
    {
        var settings = new LauncherSettings { Workspace = Path.Combine(_root, "package-skill-workspace"), DshHome = Path.Combine(_root, "package-skill-home") };
        var profile = Path.Combine(settings.DshHome, "profiles", "web");
        var package = Path.Combine(profile, "node_modules", "@liustack", "modlens");
        var packagedSkill = Path.Combine(package, "skills", "modlens");
        Directory.CreateDirectory(packagedSkill);
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@liustack/modlens\",\"version\":\"3.6.0\"}");
        File.WriteAllText(Path.Combine(packagedSkill, "SKILL.md"), "---\nname: modlens\ndescription: Vision bridge\n---\n\n# First\n");
        File.WriteAllText(Path.Combine(packagedSkill, "helper.txt"), "first");

        var service = new SkillService();
        var first = service.SyncPackagedSkills(settings, profile, ["@liustack/modlens"]);
        var imported = Path.Combine(settings.DshHome, "skills", "modlens");
        Assert.Equal(1, first.Imported);
        Assert.Contains("@liustack/modlens", first.SkillPackages);
        Assert.True(File.Exists(Path.Combine(imported, ".dsh-desktop-package.json")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(imported, "helper.txt")));

        File.WriteAllText(Path.Combine(packagedSkill, "helper.txt"), "second");
        var second = service.SyncPackagedSkills(settings, profile, ["@liustack/modlens"]);
        Assert.Equal(1, second.Updated);
        Assert.Equal("second", File.ReadAllText(Path.Combine(imported, "helper.txt")));

        var removed = service.SyncPackagedSkills(settings, profile, []);
        Assert.Equal(1, removed.Removed);
        Assert.False(Directory.Exists(imported));
        Assert.NotEmpty(Directory.EnumerateDirectories(Path.Combine(settings.DshHome, "skills", ".trash")));
    }

    [Fact]
    public void PackagedSkills_DoNotOverwriteUnmanagedUserSkills()
    {
        var settings = new LauncherSettings { Workspace = Path.Combine(_root, "skill-collision-workspace"), DshHome = Path.Combine(_root, "skill-collision-home") };
        var profile = Path.Combine(settings.DshHome, "profiles", "web");
        var packagedSkill = Path.Combine(profile, "node_modules", "vendor", "tool", "skills", "shared-name");
        Directory.CreateDirectory(packagedSkill);
        File.WriteAllText(Path.Combine(profile, "node_modules", "vendor", "tool", "package.json"), "{\"name\":\"vendor/tool\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(packagedSkill, "SKILL.md"), "---\nname: shared-name\ndescription: Package copy\n---\n");
        var userSkill = Path.Combine(settings.DshHome, "skills", "shared-name");
        Directory.CreateDirectory(userSkill);
        File.WriteAllText(Path.Combine(userSkill, "SKILL.md"), "---\nname: shared-name\ndescription: User copy\n---\n");

        var result = new SkillService().SyncPackagedSkills(settings, profile, ["vendor/tool"]);

        Assert.Equal(0, result.Imported);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains("User copy", File.ReadAllText(Path.Combine(userSkill, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(userSkill, ".dsh-desktop-package.json")));
    }

    private (AppPaths Paths, LogService Log, BackupService Backup, LauncherSettings Settings) CreateBackupFixture(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.EnsureDirectories();
        var log = new LogService(paths);
        var settings = new LauncherSettings { DshHome = Path.Combine(paths.Root, "home"), Workspace = paths.Root };
        return (paths, log, new BackupService(paths, log), settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
