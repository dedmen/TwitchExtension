using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using TwitchExtension.Ebs;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Keep-alive: the server sends a WS PING every 60 s (KeepAliveInterval);
// browsers auto-pong, and a connection that does not PONG within the same
// interval is aborted (KeepAliveTimeout) — half-open sockets are detected
// instead of lingering until TCP gives up. (Ebs:KeepAliveSeconds overrides,
// default 60 — mainly for testing.) Note: KeepAliveTimeout alone only sets
// the PONG wait and sends no pings; the interval is what drives them.
var keepAliveSeconds = builder.Configuration.GetValue("Ebs:KeepAliveSeconds", 60);
var keepAlive = TimeSpan.FromSeconds(Math.Max(1, keepAliveSeconds));
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = keepAlive, KeepAliveTimeout = keepAlive });

var contentRoot = builder.Environment.ContentRootPath;
var pushKey = builder.Configuration["Ebs:PushKey"] ?? "dev-push-key-change-me";
var streamDelayMs = builder.Configuration.GetValue("Ebs:StreamDelayMs", 10000L);
var pruneIntervalMs = builder.Configuration.GetValue("Ebs:PruneIntervalMs", 600000L);
var pruneStaleAfterMs = builder.Configuration.GetValue("Ebs:PruneStaleAfterMs", 3600000L);
var maxTenants = builder.Configuration.GetValue("Ebs:MaxTenants", 1000);

const string OkJson = "{\"ok\":true}";
const string BadRequestJson = "{\"error\":\"bad-request\"}";
const string TenantLimitJson = "{\"error\":\"tenant-limit\"}";
const string NextCmdJson = "{\"type\":\"next\"}";
const string BadTenantJson = "{\"error\":\"bad-tenant\"}";

const int MaxInboundMessageBytes = 2 * 1024 * 1024; // inbound text message cap
const int MaxArtChars = 700 * 1024;                 // art string cap (512 KB cap + base64 overhead)
const int SendTimeoutMs = 2000;                     // per-send fan-out timeout
const int SkipRelayCooldownMs = 1000;               // per-tenant skip-relay cooldown

var jsonOptions = new JsonSerializerOptions();

// --- Per-tenant state (in-memory only, not persisted) ----------------------
// One tenant = one streamer (extension instance + desktop app). Track store,
// overlay clients and desktop clients are partitioned per tenant; nothing
// crosses tenants.
//
// Tenants are created ONLY by an accepted desktop-channel push. The read
// paths (GET, overlay WS) never create a tenant — they serve the idle track
// from a throwaway object. Overlay clients that connect to a tenant with no
// store entry yet wait in pendingOverlay and are merged into the tenant when
// its first push creates it. tenantLock serializes creation, attach and the
// prune sweep so pruning can never orphan a connecting client, and bounds
// the store at Ebs:MaxTenants (429 for a new tenant at the cap).

var tenants = new ConcurrentDictionary<string, Tenant>();
var pendingOverlay = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, WsClient>>();
var tenantLock = new object();
var tenantIdPattern = new Regex("^[A-Za-z0-9_-]{3,64}$", RegexOptions.Compiled);

bool ValidTenant(string id) => tenantIdPattern.IsMatch(id);

/// <summary>Overlay connect: register on the tenant, or in the pending set if it has no store entry.</summary>
void TryAttachOverlay(string id, Guid clientId, WsClient client, out Tenant? t)
{
    lock (tenantLock)
    {
        if (tenants.TryGetValue(id, out var existing))
        {
            existing.Overlay[clientId] = client;
            t = existing;
            return;
        }
        pendingOverlay.GetOrAdd(id, _ => new())[clientId] = client;
        t = null;
    }
}

void DetachOverlay(string id, Guid clientId)
{
    lock (tenantLock)
    {
        if (tenants.TryGetValue(id, out var t))
        {
            t.Overlay.TryRemove(clientId, out _);
            return;
        }
        if (pendingOverlay.TryGetValue(id, out var p))
        {
            p.TryRemove(clientId, out _);
            if (p.IsEmpty) pendingOverlay.TryRemove(id, out _);
        }
    }
}

/// <summary>Desktop connect: register only when the tenant already exists (unknown tenants are not created).</summary>
void AttachDesktopIfExists(string id, Guid clientId, WsClient client)
{
    lock (tenantLock)
    {
        if (tenants.TryGetValue(id, out var t))
            t.Desktop[clientId] = client;
    }
}

void DetachDesktop(string id, Guid clientId)
{
    lock (tenantLock)
    {
        if (tenants.TryGetValue(id, out var t))
            t.Desktop.TryRemove(clientId, out _);
    }
}

/// <summary>Create-or-get for a push. Caller holds tenantLock. Null = cap reached.</summary>
Tenant? CreateOrGetTenantLocked(string id)
{
    if (tenants.TryGetValue(id, out var existing)) return existing;
    if (tenants.Count >= maxTenants) return null;
    var t = new Tenant();
    tenants[id] = t;
    if (pendingOverlay.TryRemove(id, out var waiting))
        foreach (var kv in waiting) t.Overlay[kv.Key] = kv.Value;
    return t;
}

/// <summary>Serialized track as served/broadcast: stream delay applied to positionMs.</summary>
string ServedTrackJson(Tenant t)
{
    lock (t.Lock)
    {
        var s = t.Track;

        // Position at serve time: when the timeline baseline is known and the track is
        // playing or paused, compute the true current position now; otherwise fall
        // back to the stored sample (legacy, older desktop clients).
        long position;
        if (s.StartedAtUtcMs > 0 && (s.State == TrackState.Playing || s.State == TrackState.Paused))
        {
            var end = s.PausedSinceUtcMs > 0 ? s.PausedSinceUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            position = end - s.StartedAtUtcMs - s.PausedForMs - streamDelayMs;
            if (position < 0) position = 0;
            if (s.DurationMs > 0 && position > s.DurationMs) position = s.DurationMs;
        }
        else
        {
            position = Math.Max(0, s.PositionMs - streamDelayMs);
        }

        var served = new Track
        {
            Title = s.Title,
            Artist = s.Artist,
            Album = s.Album,
            Art = s.Art,
            Source = s.Source,
            State = s.State,
            UpdatedAt = s.UpdatedAt,
            PositionMs = position,
            DurationMs = s.DurationMs,
            StartedAtUtcMs = s.StartedAtUtcMs,
            PausedForMs = s.PausedForMs,
            PausedSinceUtcMs = s.PausedSinceUtcMs,
            SkipEnabled = s.SkipEnabled,
            Next = s.Next,
        };
        return JsonSerializer.Serialize(served, jsonOptions);
    }
}

Track? TrackFromElement(JsonElement root)
{
    if (root.ValueKind != JsonValueKind.Object) return null;

    // A string property: null when missing or not a string (callers reject null
    // for the required ones).
    string? GetString(string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    var title = GetString("title");
    var artist = GetString("artist");
    var album = GetString("album");
    var source = GetString("source");
    if (title is null || artist is null || album is null || source is null) return null;

    string? art = null;
    if (root.TryGetProperty("art", out var artEl))
    {
        if (artEl.ValueKind == JsonValueKind.Null) art = null;
        else if (artEl.ValueKind == JsonValueKind.String) art = artEl.GetString();
        else return null;
        // Inbound limit: an oversized art string is a bad frame, not a track.
        if (art is not null && art.Length > MaxArtChars) return null;
    }

    var state = GetString("state");
    if (state is null || !TrackState.All.Contains(state)) return null;

    // Optional ints/longs >= 0 (missing -> 0; negative -> bad frame).
    long positionMs = 0;
    if (root.TryGetProperty("positionMs", out var pEl))
    {
        if (pEl.ValueKind != JsonValueKind.Number) return null;
        try { positionMs = pEl.GetInt64(); }
        catch { return null; }
        if (positionMs < 0) return null;
    }

    long durationMs = 0;
    if (root.TryGetProperty("durationMs", out var dEl))
    {
        if (dEl.ValueKind != JsonValueKind.Number) return null;
        try { durationMs = dEl.GetInt64(); }
        catch { return null; }
        if (durationMs < 0) return null;
    }

    // Optional timeline baseline (missing -> 0 = unknown).
    long startedAtUtcMs = 0;
    if (root.TryGetProperty("startedAtUtcMs", out var saEl))
    {
        if (saEl.ValueKind != JsonValueKind.Number) return null;
        try { startedAtUtcMs = saEl.GetInt64(); }
        catch { return null; }
        if (startedAtUtcMs < 0) return null;
    }

    long pausedForMs = 0;
    if (root.TryGetProperty("pausedForMs", out var pfEl))
    {
        if (pfEl.ValueKind != JsonValueKind.Number) return null;
        try { pausedForMs = pfEl.GetInt64(); }
        catch { return null; }
        if (pausedForMs < 0) return null;
    }

    long pausedSinceUtcMs = 0;
    if (root.TryGetProperty("pausedSinceUtcMs", out var psEl))
    {
        if (psEl.ValueKind != JsonValueKind.Number) return null;
        try { pausedSinceUtcMs = psEl.GetInt64(); }
        catch { return null; }
        if (pausedSinceUtcMs < 0) return null;
    }

    bool skipEnabled = false;
    if (root.TryGetProperty("skipEnabled", out var sEl))
    {
        if (sEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        skipEnabled = sEl.GetBoolean();
    }

    // Optional {"title","artist"} object or null (missing -> null).
    NextTrack? next = null;
    if (root.TryGetProperty("next", out var nEl))
    {
        if (nEl.ValueKind == JsonValueKind.Null) next = null;
        else if (nEl.ValueKind == JsonValueKind.Object)
        {
            // Non-string / missing members fall back to "" (never null).
            string nTitle = nEl.TryGetProperty("title", out var nt) && nt.ValueKind == JsonValueKind.String
                ? nt.GetString()!
                : "";
            string nArtist = nEl.TryGetProperty("artist", out var na) && na.ValueKind == JsonValueKind.String
                ? na.GetString()!
                : "";
            next = new NextTrack { Title = nTitle, Artist = nArtist };
        }
        else return null;
    }

    return new Track
    {
        Title = title,
        Artist = artist,
        Album = album,
        Art = art,
        Source = source,
        State = state,
        UpdatedAt = 0, // stamped by the caller
        PositionMs = positionMs,
        DurationMs = durationMs,
        StartedAtUtcMs = startedAtUtcMs,
        PausedForMs = pausedForMs,
        PausedSinceUtcMs = pausedSinceUtcMs,
        SkipEnabled = skipEnabled,
        Next = next,
    };
}

// --- WebSocket plumbing ----------------------------------------------------
// Keep-alive is the .NET ping frame: the server PINGs every
// KeepAliveInterval (Ebs:KeepAliveSeconds, default 60 s) and aborts a client
// that misses the KeepAliveTimeout PONG window; browsers auto-pong. No
// app-level heartbeat.

/// <summary>
/// Reads one full (possibly fragmented) text message; null when the client
/// closes or the 2 MB message cap is exceeded (the connection is closed).
/// </summary>
async Task<string?> ReadFullTextMessageAsync(WsClient client)
{
    var ws = client.Socket;
    var buffer = new byte[8 * 1024];
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), default);
        if (result.MessageType == WebSocketMessageType.Close)
            return null;
        if (result.MessageType is WebSocketMessageType.Text or WebSocketMessageType.Binary)
        {
            ms.Write(buffer, 0, result.Count);
            // Inbound limit: the only legitimate inbound input is a small track
            // frame / skip command; anything bigger is an attack.
            if (ms.Length > MaxInboundMessageBytes)
            {
                await client.CloseAsync("message too big");
                return null;
            }
        }
        if (result.EndOfMessage)
            return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// Parallel best-effort fan-out to one tenant's clients. Per-send 2 s timeout
/// so one stale socket never delays delivery to live clients; a client whose
/// send fails (or times out) is dropped. Per-socket send gating lives in WsClient.
/// </summary>
async Task BroadcastAsync(ConcurrentDictionary<Guid, WsClient> clients, string json)
{
    var sends = clients.Select(async kv =>
    {
        var (id, client) = kv;
        try
        {
            if (client.Socket.State != WebSocketState.Open)
            {
                _ = clients.TryRemove(id, out _);
                return;
            }
            await client.SendTextAsync(json, TimeSpan.FromMilliseconds(SendTimeoutMs));
        }
        catch (Exception)
        {
            _ = clients.TryRemove(id, out _);
            await client.CloseAsync();
        }
    });
    await Task.WhenAll(sends);
}

// --- Overlay channel (no auth): pushes tenant's track, accepts {"cmd":"next"}

async Task ConnectOverlayClientAsync(string id, WebSocket ws)
{
    var client = new WsClient(ws);
    var clientId = Guid.NewGuid();
    TryAttachOverlay(id, clientId, client, out var t);
    try
    {
        // This tenant's current track on connect (throwaway idle when the tenant
        // has no store entry yet); stream delay applied.
        if (!await client.TrySendTextAsync(ServedTrackJson(t ?? new Tenant()), TimeSpan.FromMilliseconds(SendTimeoutMs)))
            return; // dead socket: the finally below completes the close and detaches

        // Pre-creation race (item 24): between the attach above and the connect
        // send, the tenant's first push may have created the tenant, merged
        // this client and broadcast — the broadcast can then reach the client
        // before the idle connect frame, leaving a STALE IDLE as the last
        // frame. If the tenant exists now, re-send its current track
        // (snapshotted under tenantLock, so it is the stamped track) — the
        // last frame is then always the freshest.
        if (t is null)
        {
            string? fresh = null;
            lock (tenantLock)
            {
                if (tenants.TryGetValue(id, out var cur))
                    fresh = ServedTrackJson(cur);
            }
            if (fresh is not null)
                await client.TrySendTextAsync(fresh, TimeSpan.FromMilliseconds(SendTimeoutMs));
        }

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReadFullTextMessageAsync(client);
            if (msg is null) break;

            // {"cmd":"next"} -> relay the skip to THIS tenant's desktop clients; ignore everything else.
            try
            {
                using var doc = JsonDocument.Parse(msg);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("cmd", out var cmd) &&
                    cmd.ValueKind == JsonValueKind.String &&
                    cmd.GetString() == "next")
                {
                    // The EBS gate is authoritative (this channel is unauthenticated
                    // and tenant IDs are guessable): relay only when the stored
                    // track has skipEnabled, at most once per tenant per second.
                    var cur = tenants.GetValueOrDefault(id);
                    if (cur is not null)
                    {
                        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        bool relay;
                        lock (cur.Lock)
                        {
                            relay = cur.Track.SkipEnabled && nowMs - cur.LastSkipRelayUtcMs >= SkipRelayCooldownMs;
                            if (relay) cur.LastSkipRelayUtcMs = nowMs;
                        }
                        if (relay) await BroadcastAsync(cur.Desktop, NextCmdJson);
                    }
                }
            }
            catch (JsonException)
            {
                // malformed frame: ignore
            }
        }
    }
    catch (WebSocketException)
    {
        // abrupt disconnect (no close handshake): normal
    }
    catch (IOException)
    {
        // connection reset / socket disposed: same
    }
    finally
    {
        await client.CompleteCloseHandshakeAsync();
        DetachOverlay(id, clientId);
    }
}

// --- Desktop channel (X-Push-Key): track pushes in, {"type":"next"} out ----

async Task ConnectDesktopClientAsync(string id, WebSocket ws)
{
    var client = new WsClient(ws);
    var clientId = Guid.NewGuid();
    AttachDesktopIfExists(id, clientId, client); // unknown tenant: not registered, nothing to relay yet
    try
    {
        if (!await client.TrySendTextAsync(OkJson, TimeSpan.FromMilliseconds(SendTimeoutMs)))
            return; // dead socket: nothing left to do

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReadFullTextMessageAsync(client);
            if (msg is null) break;
            await HandleDesktopTrackFrameAsync(id, client, clientId, msg);
        }
    }
    catch (WebSocketException)
    {
        // abrupt disconnect: normal
    }
    catch (IOException)
    {
        // same
    }
    finally
    {
        await client.CompleteCloseHandshakeAsync();
        DetachDesktop(id, clientId);
    }
}

async Task HandleDesktopTrackFrameAsync(string id, WsClient client, Guid clientId, string msg)
{
    JsonElement root;
    try
    {
        using var doc = JsonDocument.Parse(msg);
        root = doc.RootElement.Clone();
    }
    catch (JsonException)
    {
        await client.TrySendTextAsync(BadRequestJson, TimeSpan.FromMilliseconds(SendTimeoutMs));
        return;
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("type", out var typeEl) ||
        typeEl.ValueKind != JsonValueKind.String ||
        typeEl.GetString() != "track")
    {
        await client.TrySendTextAsync(BadRequestJson, TimeSpan.FromMilliseconds(SendTimeoutMs));
        return;
    }

    var track = TrackFromElement(root);
    if (track is null)
    {
        await client.TrySendTextAsync(BadRequestJson, TimeSpan.FromMilliseconds(SendTimeoutMs));
        return;
    }

    track.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Create-or-get (only pushes create tenants), register this client and
    // stamp the track atomically so the prune sweep and new connects can't
    // interleave. No awaits while tenantLock is held.
    Tenant? t = null;
    string json = string.Empty;
    lock (tenantLock)
    {
        t = CreateOrGetTenantLocked(id);
        if (t is not null)
        {
            t.Desktop[clientId] = client;
            lock (t.Lock) t.Track = track; // stored raw; delay is applied in ServedTrackJson
            json = ServedTrackJson(t);
        }
    }
    if (t is null)
    {
        // Cap reached after the greeting (a concurrent push took the last
        // slot): tell the desktop, then close — never a silent drop (item 26).
        await client.TrySendTextAsync(TenantLimitJson, TimeSpan.FromMilliseconds(SendTimeoutMs));
        await client.CloseAsync();
        return;
    }

    await BroadcastAsync(t.Overlay, json);
    await client.TrySendTextAsync(OkJson, TimeSpan.FromMilliseconds(SendTimeoutMs));
}

// --- Static files: serve ../overlay/ at the site root ----------------------
// Resolve relative to the repo root (parent of ebs/); fall back to an
// overlay/ next to the app (published layout).

var overlayCandidates = new[]
{
    Path.GetFullPath(Path.Combine(contentRoot, "..", "overlay")),
    Path.Combine(AppContext.BaseDirectory, "overlay"),
};
var overlayDir = overlayCandidates.FirstOrDefault(Directory.Exists);

// --- API -------------------------------------------------------------------

void ApiHeaders(HttpContext ctx)
{
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    ctx.Response.Headers["Cache-Control"] = "no-store";
}

bool FixedTimeEquals(string a, string b)
{
    var ba = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(ba, bb);
}

// HEAD included so `curl -I` works against the read endpoint.
// Read paths never create a tenant: unknown ids serve a throwaway idle track.
app.MapMethods("/api/track/{tenantId}", new[] { "GET", "HEAD" }, (string tenantId, HttpContext ctx) =>
{
    ApiHeaders(ctx);
    var id = tenantId.ToLowerInvariant();
    if (!ValidTenant(id))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Results.Content(BadTenantJson, "application/json");
    }
    if (ctx.Request.Method == HttpMethods.Head) return Results.Content("", "application/json");
    var t = tenants.GetValueOrDefault(id);
    return Results.Content(ServedTrackJson(t ?? new Tenant()), "application/json");
});

app.MapGet("/api/track/ws/{tenantId}", async (string tenantId, HttpContext ctx) =>
{
    ApiHeaders(ctx);
    var id = tenantId.ToLowerInvariant();
    if (!ValidTenant(id))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("invalid tenant id");
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket upgrade required");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await ConnectOverlayClientAsync(id, ws);
});

app.MapGet("/api/desktop/ws/{tenantId}", async (string tenantId, HttpContext ctx) =>
{
    ApiHeaders(ctx);
    var id = tenantId.ToLowerInvariant();
    if (!ValidTenant(id))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("invalid tenant id");
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket upgrade required");
        return;
    }

    // Auth before the upgrade: X-Push-Key must equal Ebs:PushKey (constant-time compare).
    // One global key authorizes any tenantId (per-tenant keys = future hardening).
    if (!ctx.Request.Headers.TryGetValue("X-Push-Key", out var headerKey) ||
        !FixedTimeEquals(headerKey.ToString(), pushKey))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("unauthorized");
        return;
    }

    // Tenant cap: a NEW tenant at Ebs:MaxTenants is rejected before the WS
    // handshake; existing tenants are unaffected.
    bool atTenantCap = false;
    lock (tenantLock)
    {
        atTenantCap = !tenants.ContainsKey(id) && tenants.Count >= maxTenants;
    }
    if (atTenantCap)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.Response.WriteAsync("tenant limit reached");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await ConnectDesktopClientAsync(id, ws);
});

app.MapGet("/health", (HttpContext ctx) =>
{
    ApiHeaders(ctx);
    return Results.Text("ok");
});

// --- Tenant pruning: drop tenants with no open WS connection whose stored
// --- track is idle OR older than Ebs:PruneStaleAfterMs — a disconnected
// --- desktop never re-pushes idle, so dead playing/paused tenants must be
// --- freed by age too, or at Ebs:MaxTenants the store wedges with 429 for
// --- every new streamer. The sweep holds tenantLock (serialization with
// --- attach and creation, so it can't orphan a connecting client) and must
// --- not fault on shutdown (OperationCanceledException caught). -----------

var pruneCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, pruneIntervalMs)));
    try
    {
        while (await timer.WaitForNextTickAsync(pruneCts.Token))
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (tenantLock)
            {
                foreach (var (id, t) in tenants)
                {
                    if (!t.Overlay.IsEmpty || !t.Desktop.IsEmpty) continue;
                    bool prune;
                    lock (t.Lock)
                        prune = t.Track.State == TrackState.Idle ||
                                nowMs - t.Track.UpdatedAt > pruneStaleAfterMs;
                    if (prune) tenants.TryRemove(id, out _);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        // shutdown: expected
    }
    finally
    {
        pruneCts.Dispose();
    }
});

// --- Static serving (after API routing so /api/* and /health win) ----------

if (overlayDir is not null)
{
    var fileProvider = new PhysicalFileProvider(overlayDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = ""
    });
    app.Logger.LogInformation("Serving overlay from {Dir}", overlayDir);
}
else
{
    app.Logger.LogWarning("Overlay directory not found (looked in: {Candidates}); static serving disabled.", string.Join(", ", overlayCandidates));
}

app.Run();

// Local types must follow all top-level statements in a top-level program.

/// <summary>
/// A client socket plus its per-socket send gate. The gate guarantees at most
/// one outstanding send per socket (the WebSocket API forbids two concurrent
/// sends — and a CloseOutputAsync racing an in-flight SendAsync); a "busy"
/// send simply waits on the gate. Only a real send failure drops the client
/// (the caller catches and removes it).
/// </summary>
sealed class WsClient
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WsClient(WebSocket socket) => Socket = socket;

    public WebSocket Socket { get; }

    public async Task SendTextAsync(string json, TimeSpan timeout)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Non-throwing send for the direct (non-broadcast) paths: a 2 s send
    /// timeout (OperationCanceledException), a reset or a socket-state
    /// failure returns false instead of escaping as an unhandled request
    /// error. Gate-safe: the gate is released on every path.
    /// </summary>
    public async Task<bool> TrySendTextAsync(string json, TimeSpan timeout)
    {
        try
        {
            await SendTextAsync(json, timeout);
            return true;
        }
        catch (Exception)
        {
            return false; // caller treats it as a dead send
        }
    }

    /// <summary>
    /// Close serialized with SendTextAsync through the same gate (a
    /// CloseOutputAsync racing an in-flight SendAsync is exactly the
    /// InvalidOperationException the gate exists for), best effort.
    /// </summary>
    public async Task CloseAsync(string reason = "send failed")
    {
        await _sendGate.WaitAsync();
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, reason, default);
        }
        catch
        {
            // best effort
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Answer the peer's close frame (if any), through the gate.</summary>
    public async Task CompleteCloseHandshakeAsync()
    {
        await _sendGate.WaitAsync();
        try
        {
            if (Socket.State == WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, default);
        }
        catch
        {
            // best effort
        }
        finally
        {
            _sendGate.Release();
        }
    }
}

/// <summary>
/// Per-tenant state: track store (guarded by Lock), overlay-channel clients,
/// desktop-channel clients, skip-relay cooldown. positionMs is stored RAW;
/// the stream delay is applied in ServedTrackJson.
/// </summary>
sealed class Tenant
{
    public readonly object Lock = new();
    public Track Track = new();
    public readonly ConcurrentDictionary<Guid, WsClient> Overlay = new(); // GET /api/track/ws/{id}
    public readonly ConcurrentDictionary<Guid, WsClient> Desktop = new(); // GET /api/desktop/ws/{id}
    public long LastSkipRelayUtcMs;                                       // skip-relay cooldown (per tenant)
}
