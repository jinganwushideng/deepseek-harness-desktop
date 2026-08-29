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
        Assert.True(settings.CheckUpdates);
        Assert.Equal(RuntimeUpdateSource.Auto, settings.HarnessUpdateSource);
        Assert.Equal(string.Empty, settings.DismissedDesktopUpdateVersion);
        Assert.Equal(ShellThemeService.FollowWeb, settings.ShellThemeMode);
        Assert.True(settings.FollowSkinAppearance);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v2.0.0", "1.9.9", true)]
    [InlineData("1.1.0-rc.2", "1.1.0-rc.1", true)]
    [InlineData("1.1.0", "1.1.0-rc.9", true)]
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    public void DesktopUpdateVersionComparison_FollowsSemVer(string candidate, string current, bool expected) =>
        Assert.Equal(expected, DesktopUpdateService.IsNewerVersion(candidate, current));

    [Fact]
    public void DesktopUpdateAtomFallback_ReadsCanonicalRedirectedReleaseUrl()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><updated>2026-08-15T00:00:00Z</updated><link rel="alternate" href="https://github.com/new-owner/deepseek-harness-desktop/releases/tag/v1.2.0"/><title>Desktop 1.2.0</title></entry>
              <entry><updated>2026-08-14T00:00:00Z</updated><link rel="alternate" href="https://github.com/new-owner/deepseek-harness-desktop/releases/tag/v1.1.0"/><title>Desktop 1.1.0</title></entry>
            </feed>
            """;
        var update = DesktopUpdateService.ReadAtomReleases(xml, "1.0.0");
        Assert.NotNull(update);
        Assert.Equal("1.2.0", update.Version);
        Assert.Contains("new-owner", update.ReleaseUrl);
    }

    [Fact]
    [Trait("Category", "NetworkIntegration")]
    public async Task DesktopUpdateCheck_UsesPublicReleaseSourcesWithoutAuthentication()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DSH_RUN_UPDATE_NETWORK_INTEGRATION"), "1", StringComparison.Ordinal)) return;
        var paths = new AppPaths(Path.Combine(_root, "desktop-update-network"));
        paths.EnsureDirectories();
        using var log = new LogService(paths);
        var update = await new DesktopUpdateService(log).CheckAsync();
        Assert.True(update is null || DesktopUpdateService.IsNewerVersion(update.Version, DesktopUpdateService.CurrentVersion));
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
    public void WebThemeMonitor_ParsesValidatedSkinAppearance()
    {
        const string json = """{"kind":"dsh-appearance","isLight":true,"accent":"rgb(10, 20, 30)","background":"#ffffff","surface":"rgb(245,245,245)","border":"#dddddd","text":"rgb(20, 20, 20)","muted":"#777777","skinId":"china-blue"}""";
        Assert.True(WebThemeMonitor.TryReadAppearanceJson(json, out var appearance));
        Assert.True(appearance.IsLight);
        Assert.Equal("china-blue", appearance.SkinId);
        Assert.True(WebThemeMonitor.TryParseCssColor(appearance.Accent, out var accent));
        Assert.Equal((byte)20, accent.G);
        Assert.False(WebThemeMonitor.TryReadAppearanceJson("{\"kind\":\"other\"}", out _));
    }

    [Fact]
    public void SkinPalette_RepairsWhiteAccentBordersAndUnreadableText()
    {
        var appearance = new WebSkinAppearance(false, "#ffffff", "#30394a", "#30394a", "#ffffff", "#ffffff", "#e8e8e8", "community");
        var palette = ShellThemeService.BuildSkinPalette(appearance);
        Assert.True(ShellThemeService.ContrastRatio(palette.Text, palette.Background) >= 4.5);
        Assert.True(ShellThemeService.ContrastRatio(palette.AccentText, palette.Accent) >= 4.5);
        Assert.True(ShellThemeService.ContrastRatio(palette.Border, palette.Background) <= 1.8);
        Assert.True(ShellThemeService.ContrastRatio(palette.Surface, palette.Background) <= 1.38);
        Assert.False(palette.Accent.R == 255 && palette.Accent.G == 255 && palette.Accent.B == 255);
    }

    [Fact]
    public void WebThemeMonitor_UsesStableSemanticColorSampling()
    {
        Assert.Contains("cssVariable", WebThemeMonitor.Script, StringComparison.Ordinal);
        Assert.Contains("colorful", WebThemeMonitor.Script, StringComparison.Ordinal);
        Assert.Contains("setTimeout(emit, 480)", WebThemeMonitor.Script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@dshthemes/ui", "Skin")]
    [InlineData("dsh-ui-appearance", "Skin")]
    [InlineData("@liustack/modlens", "Plugin")]
    public void PluginClassification_SeparatesSkins(string package, string expected) =>
        Assert.Equal(expected, PluginClassificationService.Classify(package).ToString());

    [Fact]
    public void PluginCatalog_ValidatesFiltersAndPrefersChineseSummary()
    {
        var catalog = new PluginCatalog
        {
            SchemaVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Items =
            [
                new PluginCatalogItem { Id = "theme", Name = "Theme", Description = "English", DescriptionZh = "中文简介", InstallSpec = "theme@latest", Package = "theme", Category = PluginCategory.Skin, Verified = true, Popularity = 9 },
                new PluginCatalogItem { Id = "tool", Name = "Tool", Description = "Developer tool", InstallSpec = "tool@latest", Package = "tool", Category = PluginCategory.DeveloperTool, Verified = true, Popularity = 20 }
            ]
        };
        PluginRepositoryService.Validate(catalog);
        var filtered = PluginRepositoryService.Filter(catalog, "中文", PluginCategory.Skin);
        Assert.Single(filtered);
        Assert.Equal("中文简介", filtered[0].DisplayDescription);
    }

    [Fact]
    public void PluginCatalog_AllowsTrustedPreviewAndDropsUntrustedPreview()
    {
        var trusted = new PluginCatalogItem { Id = "trusted", Name = "Trusted", InstallSpec = "trusted@latest", Package = "trusted", PreviewImageUrl = "https://raw.githubusercontent.com/example/project/main/docs/preview.png" };
        var untrusted = new PluginCatalogItem { Id = "untrusted", Name = "Untrusted", InstallSpec = "untrusted@latest", Package = "untrusted", PreviewImageUrl = "https://tracking.example/preview.png" };
        var catalog = new PluginCatalog { SchemaVersion = 1, Items = [trusted, untrusted] };
        PluginRepositoryService.Validate(catalog);
        Assert.NotEmpty(trusted.PreviewImageUrl);
        Assert.Empty(untrusted.PreviewImageUrl);
        Assert.Contains("PluginPreviewPlaceholder.png", untrusted.PreviewImagePath, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewImageValidation_AcceptsOnlyKnownRasterFormats()
    {
        Assert.Equal(".png", PluginPreviewService.DetectImageExtension([137, 80, 78, 71, 13, 10, 26, 10]));
        Assert.Equal(".jpg", PluginPreviewService.DetectImageExtension([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(PluginPreviewService.DetectImageExtension([0x3C, 0x73, 0x76, 0x67]));
    }

    [Fact]
    public void RepositoryPreview_FallsBackToGithubSocialImage()
    {
        var item = new PluginCatalogItem
        {
            RepositoryUrl = "https://github.com/example/project",
            PreviewImageUrl = string.Empty
        };
        var preview = PluginPreviewService.ResolvePreviewUrl(item);
        Assert.Equal("https://opengraph.githubassets.com/1/example/project", preview);
        Assert.True(PluginRepositoryService.TryGetTrustedPreviewUri(preview, out _));
    }

    [Fact]
    public void PluginAndCatalogState_ExposeInstalledAndEnabledLabels()
    {
        Assert.Equal("未启用", new PluginItem("skin", "skin", "user", "1.0", false, true, PluginCategory.Skin).StateText);
        var catalog = new PluginCatalogItem { IsInstalled = true, IsEnabled = false };
        Assert.Equal("已安装 · 未启用", catalog.InstallStateText);
        Assert.Equal("启用", catalog.InstallActionText);
        Assert.True(catalog.CanRunInstallAction);
        catalog.IsEnabled = true;
        Assert.Equal("已启用", catalog.InstallStateText);
        Assert.False(catalog.CanRunInstallAction);
    }

    [Fact]
    public void FeaturedSkinDefinitions_ArePinnedAndIndependentlyManaged()
    {
        Assert.Equal(2, FeaturedSkinService.Definitions.Count);
        Assert.Contains(FeaturedSkinService.Definitions, item => item.PrimaryPackage == "@dsh-external/dsh-client-ui-skin-maid-atelier" && item.License.Contains("禁止商用"));
        Assert.Contains(FeaturedSkinService.Definitions, item => item.PrimaryPackage == "@dshthemes/ui" && item.ManagedPackages.Contains("@dshthemes/core"));
        Assert.Equal(2, FeaturedSkinService.Definitions.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FeaturedSkinPayloads_InstallIntoAnIsolatedProfileAndStayDisabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DSH_RUN_FEATURED_SKIN_INTEGRATION"), "1", StringComparison.Ordinal)) return;
        var paths = new AppPaths(Path.Combine(_root, "featured-skin-integration"));
        paths.EnsureDirectories();
        using var log = new LogService(paths);
        var runtime = new RuntimeService(paths, log);
        await runtime.EnsureSeedAsync();
        var settings = new LauncherSettings
        {
            Initialized = true,
            Workspace = Path.Combine(paths.Root, "workspace"),
            DshHome = Path.Combine(paths.Root, "dsh-home"),
            CurrentRuntimeVersion = RuntimeInfo.SeedVersion
        };
        Directory.CreateDirectory(settings.Workspace);
        Directory.CreateDirectory(settings.DshHome);
        using var server = new HarnessProcessService(paths, log);
        var plugins = new PluginService(paths, server, new NodeHelperService(paths, log), log);
        var featured = new FeaturedSkinService(paths, plugins, log);
        Assert.True(featured.HasEmbeddedPayloads());
        await featured.ApplyFirstRunChoicesAsync(settings, new Dictionary<string, FeaturedSkinSetupChoice>
        {
            [FeaturedSkinService.DeepWhaleId] = FeaturedSkinSetupChoice.KeepDisabled,
            [FeaturedSkinService.ThemeCollectionId] = FeaturedSkinSetupChoice.KeepDisabled
        });
        var installed = await plugins.InspectAsync(settings);
        Assert.Contains(installed, item => item.Package == "@dsh-external/dsh-client-ui-skin-maid-atelier" && item.Disabled);
        Assert.Contains(installed, item => item.Package == "@dshthemes/ui" && item.Disabled);
        Assert.DoesNotContain(installed, item => item.Package == "@dshthemes/core");
        Assert.DoesNotContain(installed, item => item.Package == "clsx");
        using (var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(settings.DshHome, "profiles", "web", "package.json"))))
        {
            var dependencies = profile.RootElement.GetProperty("dependencies");
            Assert.True(dependencies.TryGetProperty("@dshthemes/core", out _));
            var bundles = profile.RootElement.GetProperty("dsh").GetProperty("profile").GetProperty("bundles")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("@dshthemes/ui", bundles);
            Assert.DoesNotContain("@dshthemes/core", bundles);
        }

        await featured.ApplyFirstRunChoicesAsync(settings, new Dictionary<string, FeaturedSkinSetupChoice>
        {
            [FeaturedSkinService.DeepWhaleId] = FeaturedSkinSetupChoice.KeepDisabled,
            [FeaturedSkinService.ThemeCollectionId] = FeaturedSkinSetupChoice.Enable
        });
        installed = await plugins.InspectAsync(settings);
        Assert.Contains(installed, item => item.Package == "@dshthemes/ui" && !item.Disabled);
        Assert.DoesNotContain(installed, item => item.Package == "@dshthemes/core");
    }

    [Fact]
    public void GeneratedPluginCatalog_IsValidAndContainsNoOfficialRuntimePackages()
    {
        using var stream = typeof(PluginRepositoryService).Assembly.GetManifestResourceStream("DeepSeekHarnessDesktop.plugin-index.json");
        Assert.NotNull(stream);
        var catalog = JsonSerializer.Deserialize<PluginCatalog>(stream!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });
        Assert.NotNull(catalog);
        PluginRepositoryService.Validate(catalog!);
        Assert.InRange(catalog!.Items.Count, 1, PluginRepositoryService.MaxCatalogItems);
        Assert.DoesNotContain(catalog!.Items, item => item.Package.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase));
        Assert.True(catalog.Items.Count(item => item.Category == PluginCategory.Skin) >= 60);
        Assert.Equal(PluginCategory.Skin, catalog.Items.Single(item => item.Package == "dsh-pixel-ui").Category);
        Assert.Equal(PluginCategory.Skin, catalog.Items.Single(item => item.Package == "dsh-matugen").Category);
    }

    [Theory]
    [InlineData("ERR_PNPM_META_FETCH_FAIL request failed", true)]
    [InlineData("ETIMEDOUT registry.npmjs.org", true)]
    [InlineData("package has no matching version", false)]
    public void ChinaMirror_OnlyRetriesNetworkFailures(string line, bool expected) =>
        Assert.Equal(expected, ChinaMirrorService.LooksLikeNetworkFailure([line]));

    [Fact]
    public void ChinaMirror_DirectRetryClearsAllProxyVariables()
    {
        var environment = new Dictionary<string, string> { ["HTTP_PROXY"] = "http://127.0.0.1:7890", ["https_proxy"] = "http://127.0.0.1:7890" };
        ChinaMirrorService.ForceDirectConnection(environment);
        Assert.Equal(string.Empty, environment["HTTP_PROXY"]);
        Assert.Equal(string.Empty, environment["https_proxy"]);
        Assert.Equal("*", environment["NO_PROXY"]);
    }

    [Fact]
    public void OfficialRegistry_AutomaticallyUsesSystemProxy()
    {
        var environment = new Dictionary<string, string>();
        ChinaMirrorService.ApplySystemProxyForOfficial(environment, new Uri(ChinaMirrorService.OfficialNpmRegistry), new WebProxy("http://127.0.0.1:7890"), considerProcessEnvironment: false);
        Assert.Equal("http://127.0.0.1:7890/", environment["HTTPS_PROXY"]);
    }

    [Fact]
    public void RuntimeUpdateSourcePlans_RespectManualChoiceAndAutomaticFallback()
    {
        Assert.Equal([RuntimeUpdateSource.Official], RuntimeService.GetInstallSourcePlan(RuntimeUpdateSource.Official, RuntimeUpdateSource.ChinaMirror));
        Assert.Equal([RuntimeUpdateSource.ChinaMirror], RuntimeService.GetInstallSourcePlan(RuntimeUpdateSource.ChinaMirror, RuntimeUpdateSource.Official));
        Assert.Equal([RuntimeUpdateSource.ChinaMirror, RuntimeUpdateSource.Official], RuntimeService.GetInstallSourcePlan(RuntimeUpdateSource.Auto, RuntimeUpdateSource.ChinaMirror));
        Assert.Equal([RuntimeUpdateSource.Official, RuntimeUpdateSource.ChinaMirror], RuntimeService.GetInstallSourcePlan(RuntimeUpdateSource.Auto, RuntimeUpdateSource.Official));
    }

    [Fact]
    public void RuntimePeerClosure_FindsRequiredPeersButSkipsOptionalPeers()
    {
        var app = Path.Combine(_root, "peer-closure-app");
        var package = Path.Combine(app, "node_modules", "example-parent");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), """
            {
              "name": "example-parent",
              "peerDependencies": {
                "required-peer": "^1.2.0",
                "optional-peer": "^2.0.0"
              },
              "peerDependenciesMeta": {
                "optional-peer": { "optional": true }
              }
            }
            """);

        var missing = RuntimeService.FindMissingPeerDependencies(app);
        Assert.Equal("^1.2.0", missing["required-peer"]);
        Assert.DoesNotContain("optional-peer", missing.Keys);

        var installedPeer = Path.Combine(app, "node_modules", "required-peer");
        Directory.CreateDirectory(installedPeer);
        File.WriteAllText(Path.Combine(installedPeer, "package.json"), "{\"name\":\"required-peer\",\"version\":\"1.2.3\"}");
        Assert.Empty(RuntimeService.FindMissingPeerDependencies(app));
    }

    [Fact]
    public void HarnessEventMonitor_UsesOfficialCompletedTurnEvent()
    {
        var completed = """
            {"rpcId":"frame-1","payload":{"type":"session/event","sessionId":"session-1","event":{"type":"turn/end","data":{"turn":3,"reason":{"kind":"completed"}}}}}
            """;
        Assert.True(HarnessEventMonitor.TryParseCompletedTurn(completed, out var result));
        Assert.Equal("session-1", result.SessionId);
        Assert.Equal(3, result.Turn);
        Assert.Equal("completed", result.Reason);

        var interrupted = completed.Replace("completed", "interrupted", StringComparison.Ordinal);
        Assert.False(HarnessEventMonitor.TryParseCompletedTurn(interrupted, out _));
        Assert.False(HarnessEventMonitor.TryParseCompletedTurn("not-json", out _));
    }

    [Fact]
    public async Task StartupFailure_ReportsExactStageAndSuggestion()
    {
        var root = Path.Combine(_root, "startup-failure");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        using var log = new LogService(paths);
        using var server = new HarnessProcessService(paths, log);
        var stages = new List<StartupStage>();
        server.StartupProgressChanged += progress => stages.Add(progress.Stage);
        var settings = new LauncherSettings
        {
            Initialized = true,
            Workspace = Path.Combine(root, "workspace"),
            DshHome = Path.Combine(root, "home"),
            CurrentRuntimeVersion = "missing-runtime",
            Port = GetFreePort()
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() => server.StartAsync(settings));
        Assert.Contains(StartupStage.ValidatingSettings, stages);
        Assert.Contains(StartupStage.CheckingRuntime, stages);
        Assert.Equal(StartupStage.CheckingRuntime, server.LastFailure?.Stage);
        Assert.Equal(ServerState.Faulted, server.State);
        Assert.Contains("重装", server.LastFailure?.Suggestion ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticBundle_IsZipAndExcludesSecretsAndPrivatePaths()
    {
        var root = Path.Combine(_root, "diagnostics");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        var home = Path.Combine(root, "private-home");
        var workspace = Path.Combine(root, "private-workspace");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
        File.WriteAllText(Path.Combine(home, ".credentials.yaml"), "provider:\n  apiKey: super-secret-value-123\n");
        File.WriteAllText(Path.Combine(home, "settings.yaml"), "appSecret: another-secret-value-456\npath: " + home);
        File.WriteAllText(Path.Combine(home, "profiles", "web", "package.json"), "{\"name\":\"test-profile\"}");
        File.WriteAllText(paths.Config, JsonSerializer.Serialize(new LauncherSettings { Workspace = workspace, DshHome = home }));
        using var log = new LogService(paths);
        log.Error("test", "token=super-secret-value-123 path=" + home);
        var runtime = new RuntimeService(paths, log);
        using var server = new HarnessProcessService(paths, log);
        var diagnostics = new DiagnosticService(paths, runtime, server, log);
        var settings = new LauncherSettings { Workspace = workspace, DshHome = home };

        var zip = await diagnostics.ExportBundleAsync(settings, "apiKey=another-secret-value-456\n" + workspace);
        Assert.True(File.Exists(zip));
        using var archive = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("credentials", StringComparison.OrdinalIgnoreCase));
        var combined = string.Join("\n", archive.Entries.Where(entry => entry.Length < 1024 * 1024).Select(entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }));
        Assert.DoesNotContain("super-secret-value-123", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret-value-456", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(home, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace, combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%DSH_HOME%", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogDetailNavigation_OnlyKeepsSameHttpsOriginEmbedded()
    {
        var origin = new Uri("https://github.com/example/project");
        Assert.True(CatalogDetailWindow.IsSafeRepositoryUri(origin));
        Assert.True(CatalogDetailWindow.IsSameOrigin(origin, new Uri("https://github.com/example/project/issues")));
        Assert.False(CatalogDetailWindow.IsSameOrigin(origin, new Uri("https://example.com/redirect")));
        Assert.False(CatalogDetailWindow.IsSameOrigin(origin, new Uri("http://github.com/example/project")));
        Assert.False(CatalogDetailWindow.IsSafeRepositoryUri(new Uri("file:///C:/temp/readme.html")));
    }

    [Fact]
    public async Task SecondInstance_NotifiesPrimaryInsteadOfShowingAnotherWindow()
    {
        var suffix = "test-" + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(suffix);
        Assert.True(primary.IsPrimary);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(() => activated.TrySetResult());
        using var secondary = new SingleInstanceCoordinator(suffix);
        Assert.False(secondary.IsPrimary);
        Assert.True(await secondary.NotifyPrimaryAsync());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(4));
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
    public void HarnessStartupArguments_DisableCliBrowserWhenSupported()
    {
        var current = HarnessProcessService.BuildServerArguments("D:/runtime/bin.js", "D:/launcher.patch.yml", 3080, "0.1.1-rc.2");
        var future = HarnessProcessService.BuildServerArguments("D:/runtime/bin.js", "D:/launcher.patch.yml", 3080, "0.2.0");
        var legacy = HarnessProcessService.BuildServerArguments("D:/runtime/bin.js", "D:/launcher.patch.yml", 3080, "0.1.0-rc.6");
        Assert.Contains("--no-open", current, StringComparison.Ordinal);
        Assert.Contains("--no-open", future, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-open", legacy, StringComparison.Ordinal);
        Assert.Contains("--host 127.0.0.1", current, StringComparison.Ordinal);
        Assert.Contains("--port 3080", current, StringComparison.Ordinal);
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
    [InlineData("https://github.com/openllmsh/dsh", "git+https://github.com/openllmsh/dsh.git")]
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

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 4 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, true);
                return;
            }
            catch (IOException) when (attempt < 3) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (attempt < 3) { Thread.Sleep(150); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
