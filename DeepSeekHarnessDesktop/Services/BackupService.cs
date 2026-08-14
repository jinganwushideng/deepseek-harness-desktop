using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessDesktop.Models;

namespace DeepSeekHarnessDesktop.Services;

public sealed class BackupService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DSHBK1\0\0");
    private const int Iterations = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly LogService _log;
    public BackupService(AppPaths paths, LogService log) { _paths = paths; _log = log; }

    public async Task<string> CreateAsync(LauncherSettings settings, string password, bool includeSecrets, string? destination = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (password.Length < 8) throw new ArgumentException("备份密码至少需要 8 个字符。");
        if (!Directory.Exists(settings.DshHome)) throw new DirectoryNotFoundException("DSH_HOME 尚不存在。");
        destination ??= Path.Combine(_paths.Backups, $"DeepSeek-Harness-{DateTime.Now:yyyyMMdd-HHmmss}.dshbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var zipPath = Path.Combine(_paths.Staging, "backup-" + Guid.NewGuid().ToString("N") + ".zip");
        progress?.Report("正在收集 Harness 数据…");
        await CreateZipAsync(settings.DshHome, zipPath, includeSecrets, progress, cancellationToken);
        progress?.Report("正在加密备份…");
        await EncryptAsync(zipPath, destination, password, cancellationToken);
        File.Delete(zipPath);
        _log.Info("backup", $"created {destination}");
        return destination;
    }

    public async Task<string> ValidateAsync(string backup, string password, CancellationToken cancellationToken = default)
    {
        var zipPath = Path.Combine(_paths.Staging, "validate-" + Guid.NewGuid().ToString("N") + ".zip");
        await DecryptAsync(backup, zipPath, password, cancellationToken);
        using var archive = ZipFile.OpenRead(zipPath);
        var summary = $"有效备份：{archive.Entries.Count} 个条目，{archive.Entries.Sum(x => x.Length) / 1024d / 1024d:N1} MiB";
        File.Delete(zipPath);
        return summary;
    }

    public async Task<string> RestoreAsync(LauncherSettings settings, string backup, string password, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var zipPath = Path.Combine(_paths.Staging, "restore-" + Guid.NewGuid().ToString("N") + ".zip");
        var stage = Path.Combine(_paths.Staging, "restore-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        progress?.Report("正在验证并解密备份…");
        await DecryptAsync(backup, zipPath, password, cancellationToken);
        progress?.Report("正在检查备份路径…");
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (!destination.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("备份包含越界路径，已拒绝恢复。");
                if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(destination);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = entry.Open(); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                    await input.CopyToAsync(output, cancellationToken);
                }
            }
        }
        File.Delete(zipPath);
        var rollback = settings.DshHome.TrimEnd(Path.DirectorySeparatorChar) + ".rollback-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        progress?.Report("正在切换用户数据…");
        try
        {
            if (Directory.Exists(settings.DshHome)) Directory.Move(settings.DshHome, rollback);
            Directory.Move(stage, settings.DshHome);
        }
        catch
        {
            if (!Directory.Exists(settings.DshHome) && Directory.Exists(rollback)) Directory.Move(rollback, settings.DshHome);
            throw;
        }
        _log.Info("backup", $"restored {backup}; rollback={rollback}");
        return rollback;
    }

    private static async Task CreateZipAsync(string root, string destination, bool includeSecrets, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(x => x.Equals("node_modules", StringComparison.OrdinalIgnoreCase))) continue;
            if (!includeSecrets && (relative.Equals(".credentials.yaml", StringComparison.OrdinalIgnoreCase) || relative.Equals(".env", StringComparison.OrdinalIgnoreCase))) continue;
            var entry = archive.CreateEntry(relative.Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.SmallestSize);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var output = entry.Open(); await input.CopyToAsync(output, cancellationToken);
            if (++count % 50 == 0) progress?.Report($"已收集 {count} 个文件…");
        }
    }

    private static async Task EncryptAsync(string source, string destination, string password, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(Magic, cancellationToken); await output.WriteAsync(salt, cancellationToken);
        await output.WriteAsync(BitConverter.GetBytes(Iterations), cancellationToken); await output.WriteAsync(BitConverter.GetBytes(ChunkSize), cancellationToken);
        var plain = new byte[ChunkSize];
        using var aes = new AesGcm(key, 16);
        while (true)
        {
            var read = await input.ReadAsync(plain, cancellationToken); if (read == 0) break;
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[read];
            aes.Encrypt(nonce, plain.AsSpan(0, read), cipher, tag);
            await output.WriteAsync(BitConverter.GetBytes(read), cancellationToken); await output.WriteAsync(nonce, cancellationToken);
            await output.WriteAsync(tag, cancellationToken); await output.WriteAsync(cipher, cancellationToken);
        }
        await output.WriteAsync(BitConverter.GetBytes(0), cancellationToken); CryptographicOperations.ZeroMemory(key);
    }

    private static async Task DecryptAsync(string source, string destination, string password, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var magic = new byte[Magic.Length]; await input.ReadExactlyAsync(magic, cancellationToken);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("不是受支持的 .dshbackup 文件。");
        var salt = new byte[16]; await input.ReadExactlyAsync(salt, cancellationToken);
        var number = new byte[4]; await input.ReadExactlyAsync(number, cancellationToken); var iterations = BitConverter.ToInt32(number);
        await input.ReadExactlyAsync(number, cancellationToken); var chunkSize = BitConverter.ToInt32(number);
        if (iterations is < 100_000 or > 2_000_000 || chunkSize is < 4096 or > 4 * 1024 * 1024) throw new InvalidDataException("备份头参数无效。");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var aes = new AesGcm(key, 16);
        while (true)
        {
            await input.ReadExactlyAsync(number, cancellationToken); var length = BitConverter.ToInt32(number); if (length == 0) break;
            if (length < 0 || length > chunkSize) throw new InvalidDataException("备份块长度无效。");
            var nonce = new byte[12]; var tag = new byte[16]; var cipher = new byte[length]; var plain = new byte[length];
            await input.ReadExactlyAsync(nonce, cancellationToken); await input.ReadExactlyAsync(tag, cancellationToken); await input.ReadExactlyAsync(cipher, cancellationToken);
            try { aes.Decrypt(nonce, cipher, tag, plain); }
            catch (CryptographicException) { throw new CryptographicException("备份密码错误或文件已被篡改。"); }
            await output.WriteAsync(plain, cancellationToken);
        }
        CryptographicOperations.ZeroMemory(key);
    }
}
