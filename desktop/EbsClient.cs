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
/// - Graceful shutdown (Dispose) pushes one <c>state: "idle"</c> frame first (bounded,
///   change-detection bypassed), so the overlay tile drops instead of going stale.
/// - Inbound: <c>{"ok":true}</c> → reachable; <c>{"error":...}</c> → log, don't crash;
///   <c>{"type":"next"}</c> → <see cref="SkipRequested"/>.
/// - Unreachable → exponential backoff 1 s doubling, capped at 15 s; at most one log line
///   per 30 s; never crashes.
/// </summary>
public sealed class EbsClient : IDisposable
{
    private const int DebounceMs = 500;
    private const int ConnectTimeoutMs = 5000;
    /// <summary>
    /// A push send that takes longer than this has hung (the EBS stopped reading the
    /// socket). <c>ClientWebSocket.SendAsync</c> has no timeout of its own, and a hung
    /// send would hold the send gate forever and wedge every later push — so the send is
    /// cancelled after this and the socket is aborted (the reconnect loop re-pushes the
    /// current snapshot on the fresh connection).
    /// </summary>
    private const int SendTimeoutMs = 10_000;
    /// <summary>
    /// Graceful-shutdown idle push: exit must not wait long on a dead socket (the
    /// EBS-side age prune covers the ungraceful-death case anyway).
    /// </summary>
    private const int IdleSendTimeoutMs = 2_000;
    private const int MinBackoffMs = 1000;
    private const int MaxBackoffMs = 15000;
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

    private readonly Uri? _wsUri;
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
    private bool _disposed;
    private DateTime _lastLogUtc = DateTime.MinValue;

    /// <summary>Raised when the EBS is reachable (on connect and on every <c>{"ok":true</c>).</summary>
    public event Action? PushSucceeded;

    /// <summary>Raised when the EBS becomes unreachable (connect failed or connection lost).</summary>
    public event Action? PushFailed;

    /// <summary>Raised on an inbound <c>{"type":"next"}</c> (overlay-side skip request).</summary>
    public event Action? SkipRequested;

    public EbsClient(AppConfig config)
    {
        // Never throws (plan.md, Settings): a malformed ebsUrl yields a null _wsUri and
        // Start() then refuses to connect (tray-only mode, "Bad ebsUrl" tooltip).
        // Null-safe locals: the values are normalized by AppConfig.Load, but this holds
        // for a hand-built config too — "never throws" is a guarantee, not a hope.
        string ebsUrl = (config.EbsUrl ?? string.Empty).TrimEnd('/');
        string tenant = (config.TenantId ?? string.Empty).Trim().ToLowerInvariant();
        _wsUri = RewriteScheme(ebsUrl + "/api/desktop/ws/" + tenant);
        _pushKey = config.PushKey;
        _skipEnabled = config.SkipButtonEnabled;
    }

    /// <summary>Starts the connect/reconnect loop. Call once, before the app runs.</summary>
    public void Start()
    {
        if (_wsUri is null)
        {
            AppLog.Write("[ebs] not started: invalid ebsUrl in settings.json");
            return;
        }
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
        // Not started (no valid tenantId) or already disposed: never connect, never log.
        if (!_started || _disposed) return;
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
        Track? latest;
        lock (_gate)
        {
            _skipEnabled = enabled;
            latest = _latest;
        }
        if (latest is not null)
        {
            Enqueue(latest, immediate: true);
        }
    }

    /// <summary>Idempotent — a double Dispose (double Shutdown) must not throw from an unobserved task.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PushIdleOnShutdown(); // graceful exit: one idle frame first (bounded, best effort)

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
        try
        {
            _sendGate.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
    }

    /// <summary>
    /// Graceful shutdown (plan.md, acceptance 18): pushes one <c>state: "idle"</c> frame
    /// before the socket closes, so the overlay tile drops immediately instead of
    /// sitting stale-dimmed. Bounded by <see cref="IdleSendTimeoutMs"/> — exit must not
    /// wait on a dead socket. Deliberately bypasses change detection: even if the last
    /// pushed state was already idle, the EBS must receive the final frame (it stores
    /// latest, so a duplicate is harmless).
    /// </summary>
    private void PushIdleOnShutdown()
    {
        ClientWebSocket? ws;
        string json;
        lock (_gate)
        {
            ws = _socket;
            json = Serialize(Track.Idle);
        }
        if (ws is null || ws.State != WebSocketState.Open)
        {
            AppLog.Write("[ebs] exit: no live socket; skipping idle push (EBS prunes by age)");
            return;
        }

        bool sent = false;
        try
        {
            var t = TrySendAsync(ws, json, CancellationToken.None, IdleSendTimeoutMs);
            sent = t.Wait(IdleSendTimeoutMs + 500) && t.Result;
        }
        catch
        {
            // shutdown in flight: the socket is aborted right after regardless
        }
        AppLog.Write(sent
            ? "[ebs] exit: idle track pushed, closing cleanly"
            : "[ebs] exit: idle push incomplete; closing anyway");
    }

    // --- connection loop ----------------------------------------------------

    private async Task RunLoopAsync(CancellationToken token)
    {
        var uri = _wsUri;
        if (uri is null) return; // Start() already guards this; belt and braces

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
                await ws.ConnectAsync(uri, connectCts.Token);

                connected = true;
                backoff = MinBackoffMs;
                lock (_gate)
                {
                    _socket = ws;
                }
                PushSucceeded?.Invoke();
                AppLog.Write($"[ebs] connected to {_wsUri?.AbsoluteUri}");
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
        AppLog.Write($"[ebs] snapshot push: state={track.State} bytes={json.Length}");
        if (!await TrySendAsync(ws, json, token))
        {
            AbortSocket(ws); // stuck connection: force an immediate reconnect
            return;
        }

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
        if (_disposed) return;
        string? json;
        ClientWebSocket? ws;
        lock (_gate)
        {
            json = _pendingJson;
            ws = _socket;
        }
        if (json is null) return;
        if (_disposed) return; // shutdown won the race: the pending content is discarded
        if (ws is null || ws.State != WebSocketState.Open)
        {
            // No live connection: clear the pending content (plan.md "a failed push must
            // not wedge the pipeline") so the next changed event re-arms the debounce;
            // the reconnect's snapshot re-send covers this one.
            ClearPending(json);
            LogFailThrottled("not connected; snapshot will be pushed on reconnect");
            return;
        }

        if (!await TrySendAsync(ws, json, _cts?.Token ?? CancellationToken.None))
        {
            // Send failed or timed out (socket died mid-send, EBS stopped reading):
            // drop the pending content so the next event re-arms the debounce, and
            // abort the socket *this send used* so the reconnect loop tears the stuck
            // connection down and re-pushes the current snapshot on a fresh one.
            // Never abort _socket blindly: the reconnect loop may have installed a
            // newer connection, and killing the fresh one would churn reconnects.
            ClearPending(json);
            AbortSocket(ws);
            return;
        }

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

    /// <summary>
    /// Drops the pending push (only if it is still <paramref name="json"/> — a newer
    /// Enqueue may have replaced it meanwhile). The next changed event re-arms the debounce.
    /// </summary>
    private void ClearPending(string json)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pendingJson, json))
            {
                _pendingKey = null;
                _pendingJson = null;
            }
        }
    }

    /// <summary>
    /// Aborts the socket a failed send used so its in-flight receive/send unwinds and
    /// the reconnect loop tears it down. <c>_socket</c> is cleared only when it is
    /// still the same instance — a newer connection the reconnect loop installed in
    /// the meantime must survive (aborting it would kill a healthy connection and
    /// churn reconnects).
    /// </summary>
    private void AbortSocket(ClientWebSocket ws)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_socket, ws))
            {
                _socket = null;
            }
        }
        try
        {
            ws.Abort();
        }
        catch
        {
            // already closed/disposed by the reconnect loop: nothing left to unwind
        }
    }

    private async Task<bool> TrySendAsync(ClientWebSocket ws, string json, CancellationToken token,
                                          int timeoutMs = SendTimeoutMs)
    {
        try
        {
            await _sendGate.WaitAsync();
        }
        catch (Exception)
        {
            // gate disposed (shutdown in flight): never throw from this fire-and-forget path
            return false;
        }
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            sendCts.CancelAfter(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // shutdown in flight
            return false;
        }
        catch (OperationCanceledException)
        {
            LogFailThrottled("push send timed out");
            return false;
        }
        catch (Exception)
        {
            LogFailThrottled("push send failed");
            return false;
        }
        finally
        {
            try
            {
                _sendGate.Release();
            }
            catch (Exception)
            {
                // gate disposed between WaitAsync and Release: shutdown, ignore
            }
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
                AppLog.Write($"[ebs] EBS replied: {error.GetString()}");
                return;
            }
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "next")
            {
                AppLog.Write("[ebs] skip command received");
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
    ///
    /// A <em>hash</em>, not a concatenation (plan.md): the ~700 KB base64 <c>art</c> string
    /// must not be copied into a composite key on every SMTC event (GC churn on a hot
    /// path) — <see cref="HashCode.Combine"/> hashes each value in place.
    /// (Not <c>HashCode.ToString()</c> — that is the type name, a constant.)
    /// </summary>
    private string DedupeKey(Track track) =>
        HashCode.Combine(_skipEnabled, track.State, track.Source, track.Title, track.Artist,
                         track.Album, track.Art,
                         HashCode.Combine(track.StartedAtUtcMs, track.PausedForMs, track.PausedSinceUtcMs))
            .ToString("x8");

    /// <summary>
    /// http → ws / https → wss. Returns null when the URL is not parseable (plan.md,
    /// Settings: a malformed ebsUrl must not throw at startup).
    /// </summary>
    private static Uri? RewriteScheme(string url)
    {
        try
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
        catch
        {
            return null;
        }
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
            AppLog.Write($"[ebs] {reason} ({_wsUri?.AbsoluteUri ?? "(invalid ebsUrl)"}); retrying with backoff");
        }
    }
}
