using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TwitchExtension.Desktop;

/// <summary>
/// Authenticated WebSocket to the EBS: <c>{ebsUrl}/api/desktop/ws/{tenantId}</c> (tenantId
/// lowercased — the EBS normalizes; the settings value keeps the user's casing),
/// http → ws / https → wss, with the <c>X-Push-Key</c> request header on the upgrade.
/// This is the only write path — <c>PUT /api/track</c> is gone.
///
/// Push policy:
/// - Pushes only when the snapshot changed vs. the last pushed one. The change key
///   deliberately excludes <c>positionMs</c>/<c>durationMs</c>, so position-only advance
///   never produces push traffic (the overlay moves the bar locally).
/// - Bursts coalesce (500 ms debounce); re-announcing content that is <em>already pending</em>
///   must not reset the timer (a stream of duplicate events would starve the push forever).
/// - On (re)connect the current snapshot is pushed unconditionally, so a restarted EBS
///   recovers state within one reconnect.
/// - Inbound: <c>{"ok":true}</c> → reachable; <c>{"error":...}</c> → log, don't crash;
///   <c>{"type":"next"}</c> → <see cref="SkipRequested"/>.
/// - Unreachable → exponential backoff 1 s doubling, capped at 15 s; at most one log line
///   per 30 s; never crashes.
/// </summary>
public sealed class EbsClient : IDisposable
{
    private const int DebounceMs = 500;
    private const int ConnectTimeoutMs = 5000;
    private const int MinBackoffMs = 1000;
    private const int MaxBackoffMs = 15000;
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

    private readonly Uri _wsUri;
    private readonly string _pushKey;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private volatile bool _started;
    private Track? _latest;                 // newest monitor snapshot (skipEnabled merged in at push time)
    private bool _skipEnabled;
    private string? _lastPushedKey;         // change key of the last frame the EBS accepted
    private string? _pendingKey;
    private string? _pendingJson;
    private CancellationTokenSource? _debounce;
    private ClientWebSocket? _socket;       // the live connection, if any
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastLogUtc = DateTime.MinValue;

    /// <summary>Raised when the EBS is reachable (on connect and on every <c>{"ok":true</c>).</summary>
    public event Action? PushSucceeded;

    /// <summary>Raised when the EBS becomes unreachable (connect failed or connection lost).</summary>
    public event Action? PushFailed;

    /// <summary>Raised on an inbound <c>{"type":"next"}</c> (overlay-side skip request).</summary>
    public event Action? SkipRequested;

    public EbsClient(AppConfig config)
    {
        _wsUri = RewriteScheme(config.EbsUrl.TrimEnd('/') + "/api/desktop/ws/"
                               + config.TenantId.Trim().ToLowerInvariant());
        _pushKey = config.PushKey;
        _skipEnabled = config.SkipButtonEnabled;
    }

    /// <summary>Starts the connect/reconnect loop. Call once, before the app runs.</summary>
    public void Start()
    {
        _started = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Queues a track for pushing (500 ms debounce; skipped when the change key is
    /// unchanged). <c>immediate</c> bypasses the debounce (tray toggle).
    /// </summary>
    public void Enqueue(Track track, bool immediate = false)
    {
        // Not started (no valid tenantId): never connect, never log.
        if (!_started) return;
        CancellationTokenSource? debounce;
        lock (_gate)
        {
            _latest = track;
            string key = DedupeKey(track);
            if (_lastPushedKey == key) return; // nothing push-relevant changed
            if (_pendingKey != key)
            {
                // New content: schedule it. Identical pending content is left alone —
                // its timer is already running (starvation guard).
                _pendingKey = key;
                _pendingJson = Serialize(track);
                _debounce?.Cancel();
                _debounce = new CancellationTokenSource();
            }
            debounce = _debounce;
        }

        if (immediate) _ = SendPendingAsync();
        else _ = PushAfterDebounceAsync(debounce!.Token);
    }

    /// <summary>Updates the <c>skipButtonEnabled</c> setting; re-pushes the current snapshot immediately.</summary>
    public void SetSkipEnabled(bool enabled)
    {
        lock (_gate)
        {
            _skipEnabled = enabled;
        }
        Track? latest;
        lock (_gate)
        {
            latest = _latest;
        }
        if (latest is not null)
        {
            Enqueue(latest, immediate: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce = null;
            _socket?.Abort(); // unblocks any in-flight send/receive
            _socket = null;
        }
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutdown
        }
        _sendGate.Dispose();
    }

    // --- connection loop ----------------------------------------------------

    private async Task RunLoopAsync(CancellationToken token)
    {
        int backoff = MinBackoffMs;
        while (!token.IsCancellationRequested)
        {
            bool connected = false;
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("X-Push-Key", _pushKey);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(ConnectTimeoutMs);
                await ws.ConnectAsync(_wsUri, connectCts.Token);

                connected = true;
                backoff = MinBackoffMs;
                lock (_gate)
                {
                    _socket = ws;
                }
                PushSucceeded?.Invoke();
                Console.WriteLine($"[ebs] connected to {_wsUri}");
                await PushFreshSnapshotAsync(ws, token); // (re)started EBS: recover state
                await ReceiveLoopAsync(ws, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // shutdown
            }
            catch
            {
                // unreachable or dropped: fall through to the backoff
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_socket, ws)) _socket = null;
                }
                ws?.Dispose();
            }

            if (token.IsCancellationRequested) break;

            LogFailThrottled(connected ? "connection lost" : "EBS unreachable");
            PushFailed?.Invoke();

            try
            {
                await Task.Delay(backoff, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = Math.Min(backoff * 2, MaxBackoffMs);
        }
    }

    /// <summary>Pushes the current snapshot to a freshly connected EBS (change detection deliberately bypassed — the EBS has no state yet).</summary>
    private async Task PushFreshSnapshotAsync(ClientWebSocket ws, CancellationToken token)
    {
        Track? track;
        lock (_gate)
        {
            track = _latest;
        }
        if (track is null) return; // monitor not up yet; its first Enqueue will deliver the state

        string key = DedupeKey(track);
        string json = Serialize(track);
        if (!await TrySendAsync(ws, json, token)) return;

        lock (_gate)
        {
            _lastPushedKey = key;
            if (_pendingKey == key)
            {
                _pendingKey = null;
                _pendingJson = null;
            }
        }
        PushSucceeded?.Invoke();
    }

    private async Task PushAfterDebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMs, token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by newer content (or shutdown)
        }
        await SendPendingAsync();
    }

    private async Task SendPendingAsync()
    {
        string? json;
        ClientWebSocket? ws;
        lock (_gate)
        {
            json = _pendingJson;
            ws = _socket;
        }
        if (json is null) return;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            // No live connection: the reconnect loop pushes the fresh snapshot when it
            // comes back — there is nothing to retry here.
            LogFailThrottled("not connected; snapshot will be pushed on reconnect");
            return;
        }

        if (!await TrySendAsync(ws, json, _cts?.Token ?? CancellationToken.None)) return;

        lock (_gate)
        {
            if (ReferenceEquals(_socket, ws))
            {
                _lastPushedKey = _pendingKey;
                _pendingKey = null;
                _pendingJson = null;
            }
        }
        PushSucceeded?.Invoke();
    }

    private async Task<bool> TrySendAsync(ClientWebSocket ws, string json, CancellationToken token)
    {
        await _sendGate.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, token);
            return true;
        }
        catch (Exception)
        {
            LogFailThrottled("push send failed");
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // --- inbound ------------------------------------------------------------

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0) continue;
            HandleInbound(ms.ToArray());
        }
    }

    private void HandleInbound(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                PushSucceeded?.Invoke(); // push landed — EBS reachable
                return;
            }
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                // e.g. {"error":"bad-request"} — log only; the EBS keeps the connection open.
                Console.WriteLine($"[ebs] EBS replied: {error.GetString()}");
                return;
            }
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "next")
            {
                Console.WriteLine("[ebs] skip command received");
                SkipRequested?.Invoke();
            }
        }
        catch
        {
            // malformed frame: ignore, never crash
        }
    }

    // --- serialization ------------------------------------------------------

    /// <summary>Track → push frame. <c>updatedAt</c> is omitted (the EBS stamps it); <c>next</c> is always null for now.</summary>
    private string Serialize(Track track) => JsonSerializer.Serialize(new
    {
        type = "track",
        title = track.Title,
        artist = track.Artist,
        album = track.Album,
        art = track.Art,
        source = track.Source,
        state = track.State,
        positionMs = track.PositionMs,
        durationMs = track.DurationMs,
        startedAtUtcMs = track.StartedAtUtcMs,
        pausedForMs = track.PausedForMs,
        pausedSinceUtcMs = track.PausedSinceUtcMs,
        skipEnabled = _skipEnabled,
        next = default(object),
    });

    /// <summary>
    /// Change-detection key. Deliberately excludes <c>positionMs</c>/<c>durationMs</c>:
    /// position-only advance must not push. Includes <c>skipEnabled</c> so the tray
    /// toggle flips at the EBS, and the timeline baseline fields so a seek re-baseline
    /// (position-only change) still reaches the EBS — otherwise it would keep computing
    /// the served position from the stale baseline.
    /// </summary>
    private string DedupeKey(Track track) =>
        $"{_skipEnabled}|{track.State}|{track.Source}|{track.Title}|{track.Artist}|{track.Album}|{track.Art}" +
        $"|{track.StartedAtUtcMs}|{track.PausedForMs}|{track.PausedSinceUtcMs}";

    private static Uri RewriteScheme(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri("wss://" + url["https://".Length..]);
        }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri("ws://" + url["http://".Length..]);
        }
        return new Uri(url);
    }

    private void LogFailThrottled(string reason)
    {
        DateTime now = DateTime.UtcNow;
        bool log;
        lock (_gate)
        {
            log = (now - _lastLogUtc).Duration() >= LogInterval;
            if (log) _lastLogUtc = now;
        }
        if (log)
        {
            Console.WriteLine($"[ebs] {reason} ({_wsUri}); retrying with backoff");
        }
    }
}
