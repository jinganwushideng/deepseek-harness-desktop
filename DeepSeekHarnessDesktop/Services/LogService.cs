using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessDesktop.Services;

public sealed class LogService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _file = "";
    private const long MaxBytes = 10 * 1024 * 1024;
    private static readonly Regex SecretPattern = new(@"(?i)(sk-[A-Za-z0-9_-]{8,}|(?:(?:api[_-]?key)|(?:token)|(?:secret))\s*[:=]\s*)[^\s,;]+", RegexOptions.Compiled);

    public event Action<string>? LineAdded;
    public ConcurrentQueue<string> Recent { get; } = new();

    public LogService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.Logs);
        Rotate();
        Prune();
    }

    public void Info(string source, string message) => Write("INFO", source, message);
    public void Warn(string source, string message) => Write("WARN", source, message);
    public void Error(string source, string message) => Write("ERROR", source, message);

    public static string Redact(string value) => SecretPattern.Replace(value, match => match.Value.Contains(':') || match.Value.Contains('=') ? Regex.Replace(match.Value, @"(?<=[:=]).+", " ***") : "***");

    private void Write(string level, string source, string message)
    {
        var clean = Redact(message.Replace('\0', ' '));
        foreach (var raw in clean.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{source}] {raw}";
            lock (_gate)
            {
                if (_writer?.BaseStream.Length >= MaxBytes) Rotate();
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            Recent.Enqueue(line);
            while (Recent.Count > 2000) Recent.TryDequeue(out _);
            LineAdded?.Invoke(line);
        }
    }

    private void Rotate()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _file = Path.Combine(_paths.Logs, $"desktop-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            _writer = new StreamWriter(new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_paths.Logs).GetFiles("*.log").OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
        foreach (var file in files.Skip(20).Concat(files.Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))).Distinct())
        {
            try { file.Delete(); } catch { }
        }
    }

    public void Dispose() { lock (_gate) _writer?.Dispose(); }
}
