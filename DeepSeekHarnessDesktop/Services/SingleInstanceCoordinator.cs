using System.Security.Principal;

namespace DeepSeekHarnessDesktop.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly ManualResetEvent _stop = new(false);
    private Task? _listener;
    private bool _disposed;

    public SingleInstanceCoordinator(string? suffix = null)
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var token = Normalize(identity + (string.IsNullOrWhiteSpace(suffix) ? string.Empty : "." + suffix));
        _mutex = new Mutex(true, "DeepSeekHarnessDesktop.SingleInstance." + token, out var primary);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, "DeepSeekHarnessDesktop.Activation." + token);
        IsPrimary = primary;
    }

    public bool IsPrimary { get; }

    public void StartListening(Action activate)
    {
        if (!IsPrimary || _listener is not null) return;
        _listener = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activation, _stop };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try { activate(); } catch { }
            }
        });
    }

    public Task<bool> NotifyPrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (IsPrimary || cancellationToken.IsCancellationRequested) return Task.FromResult(false);
        try { return Task.FromResult(_activation.Set()); }
        catch { return Task.FromResult(false); }
    }

    internal static string Normalize(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _activation.Dispose();
        _stop.Dispose();
        _mutex.Dispose();
    }
}
