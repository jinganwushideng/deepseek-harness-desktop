namespace DeepSeekHarnessDesktop.Services;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("DSH_DESKTOP_ROOT") ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    public string Root { get; }
    public string Config => Path.Combine(Root, "launcher.json");
    public string Runtime => Path.Combine(Root, "runtime");
    public string Versions => Path.Combine(Runtime, "versions");
    public string Staging => Path.Combine(Runtime, "staging");
    public string CurrentRuntime => Path.Combine(Runtime, "current.json");
    public string LauncherPatch => Path.Combine(Root, "launcher.patch.yml");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string WebViewData => Path.Combine(Root, "webview-data");
    public string Helper => Path.Combine(Runtime, "helper.mjs");
    public string VersionRoot(string version) => Path.Combine(Versions, version);
    public string NodeExe(string version) => Path.Combine(VersionRoot(version), "node", "node.exe");
    public string NpmCmd(string version) => Path.Combine(VersionRoot(version), "node", "npm.cmd");
    public string DshBin(string version) => Path.Combine(VersionRoot(version), "app", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    public string PnpmScript(string version) => Path.Combine(VersionRoot(version), "app", "node_modules", "pnpm", "bin", "pnpm.cjs");
    public string PnpmBinDir(string version) => Path.Combine(VersionRoot(version), "app", "node_modules", ".bin");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(Versions);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(WebViewData);
        if (!File.Exists(LauncherPatch)) File.WriteAllText(LauncherPatch, "[]\n");
    }
}
