using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessDesktop.Services;

public sealed record HarnessTurnCompleted(string SessionId, int? Turn, string Reason);

public sealed class HarnessEventMonitor : IDisposable
{
    private readonly LogService _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task? _runner;
    private int _port;

    public HarnessEventMonitor(LogService log) => _log = log;

    public event Action<HarnessTurnCompleted>? TurnCompleted;

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_runner is { IsCompleted: false } && _port == port) return;
            StopLocked();
            _port = port;
            _seen.Clear();
            _stop = new CancellationTokenSource();
            _runner = Task.Run(() => RunAsync(port, _stop.Token));
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        try { _stop?.Cancel(); } catch { }
        _stop?.Dispose();
        _stop = null;
        _runner = null;
        _port = 0;
    }

    private async Task RunAsync(int port, CancellationToken cancellationToken)
    {
        var uri = new Uri($"ws://127.0.0.1:{port}/api/events.mux");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
                await socket.ConnectAsync(uri, cancellationToken);
                _log.Info("notification", "connected to Harness events.mux");
                await ReceiveAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.Warn("notification", "events.mux disconnected: " + LogService.Redact(ex.Message));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0) message.Write(buffer, 0, result.Count);
                if (message.Length > 2 * 1024 * 1024) throw new InvalidDataException("Harness 事件帧超过 2 MiB，已断开监视连接。");
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            if (!TryParseCompletedTurn(json, out var completed)) continue;
            var key = completed.SessionId + ":" + completed.Turn?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_gate)
            {
                if (!_seen.Add(key)) continue;
                if (_seen.Count > 512) _seen.Clear();
            }
            TurnCompleted?.Invoke(completed);
        }
    }

    internal static bool TryParseCompletedTurn(string? json, out HarnessTurnCompleted completed)
    {
        completed = new HarnessTurnCompleted(string.Empty, null, string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var payload = root.TryGetProperty("payload", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : root;
            if (!payload.TryGetProperty("type", out var frameType) || frameType.GetString() != "session/event") return false;
            if (!payload.TryGetProperty("sessionId", out var session) || string.IsNullOrWhiteSpace(session.GetString())) return false;
            if (!payload.TryGetProperty("event", out var eventValue) || eventValue.ValueKind != JsonValueKind.Object) return false;
            if (!eventValue.TryGetProperty("type", out var eventType) || eventType.GetString() != "turn/end") return false;
            if (!eventValue.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            if (!data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.Object ||
                !reason.TryGetProperty("kind", out var kind) || kind.GetString() != "completed") return false;
            int? turn = data.TryGetProperty("turn", out var turnValue) && turnValue.TryGetInt32(out var value) ? value : null;
            completed = new HarnessTurnCompleted(session.GetString()!, turn, "completed");
            return true;
        }
        catch (JsonException) { return false; }
    }

    public void Dispose() => Stop();
}
