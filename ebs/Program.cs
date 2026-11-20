using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using TwitchExtension.Ebs;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

var contentRoot = builder.Environment.ContentRootPath;
var pushKey = builder.Configuration["Ebs:PushKey"] ?? "dev-push-key-change-me";
var streamDelayMs = builder.Configuration.GetValue("Ebs:StreamDelayMs", 10000L);
var pruneIntervalMs = builder.Configuration.GetValue("Ebs:PruneIntervalMs", 600000L);

// --- Per-tenant state (in-memory only, not persisted) ----------------------
// One tenant = one streamer (extension instance + desktop app). Track store,
// overlay clients and desktop clients are partitioned per tenant; nothing
// crosses tenants.

var tenants = new ConcurrentDictionary<string, Tenant>();
var tenantIdPattern = new Regex("^[A-Za-z0-9_-]{3,64}$", RegexOptions.Compiled);

Tenant GetTenant(string id) => tenants.GetOrAdd(id, _ => new Tenant());

bool ValidTenant(string id) => tenantIdPattern.IsMatch(id);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = null };

const string OkJson = "{\"ok\":true}";
const string BadRequestJson = "{\"error\":\"bad-request\"}";
const string NextCmdJson = "{\"type\":\"next\"}";
const string BadTenantJson = "{\"error\":\"bad-tenant\"}";

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
    }

    var state = GetString("state");
    if (state is null || !TrackState.All.Contains(state)) return null;

    // Optional ints >= 0 (missing -> 0).
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
            string? nTitle = nEl.TryGetProperty("title", out var nt) && nt.ValueKind == JsonValueKind.String
                ? nt.GetString()
                : "";
            string? nArtist = nEl.TryGetProperty("artist", out var na) && na.ValueKind == JsonValueKind.String
                ? na.GetString()
                : "";
            if (nTitle is null || nArtist is null) return null;
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
// Keep-alive is the built-in .NET ping frame (~15 s); browsers auto-pong.

async Task SendTextAsync(WebSocket ws, string json)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, default);
}

/// <summary>Reads one full (possibly fragmented) text message; null when the client closes.</summary>
async Task<string?> ReadFullTextMessageAsync(WebSocket ws)
{
    var buffer = new byte[8 * 1024];
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), default);
        if (result.MessageType == WebSocketMessageType.Close)
            return null;
        if (result.MessageType is WebSocketMessageType.Text or WebSocketMessageType.Binary)
            ms.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

async Task CloseQuietlyAsync(WebSocket ws)
{
    try
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "send failed", default);
    }
    catch
    {
        // best effort
    }
}

async Task CompleteCloseHandshakeAsync(WebSocket ws)
{
    try
    {
        if (ws.State == WebSocketState.CloseReceived)
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, default);
    }
    catch
    {
        // best effort
    }
}

/// <summary>Best-effort fan-out to one client set; a client whose send fails is dropped.</summary>
async Task BroadcastAsync(ConcurrentDictionary<Guid, WebSocket> clients, string json)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    foreach (var (id, ws) in clients)
    {
        if (ws.State != WebSocketState.Open)
        {
            _ = clients.TryRemove(id, out _);
            continue;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        }
        catch (Exception)
        {
            _ = clients.TryRemove(id, out _);
            await CloseQuietlyAsync(ws);
        }
    }
}

// --- Overlay channel (no auth): pushes tenant's track, accepts {"cmd":"next"}

async Task ConnectOverlayClientAsync(Tenant t, WebSocket ws)
{
    var id = Guid.NewGuid();
    t.Overlay[id] = ws;
    try
    {
        await SendTextAsync(ws, ServedTrackJson(t)); // this tenant's current track on connect (delay applied)

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReadFullTextMessageAsync(ws);
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
                    await BroadcastAsync(t.Desktop, NextCmdJson);
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
        await CompleteCloseHandshakeAsync(ws);
        _ = t.Overlay.TryRemove(id, out _);
    }
}

// --- Desktop channel (X-Push-Key): track pushes in, {"type":"next"} out ----

async Task ConnectDesktopClientAsync(Tenant t, WebSocket ws)
{
    var id = Guid.NewGuid();
    t.Desktop[id] = ws;
    try
    {
        await SendTextAsync(ws, OkJson);

        while (ws.State == WebSocketState.Open)
        {
            var msg = await ReadFullTextMessageAsync(ws);
            if (msg is null) break;
            await HandleDesktopTrackFrameAsync(t, ws, msg);
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
        await CompleteCloseHandshakeAsync(ws);
        _ = t.Desktop.TryRemove(id, out _);
    }
}

async Task HandleDesktopTrackFrameAsync(Tenant t, WebSocket ws, string msg)
{
    JsonElement root;
    try
    {
        using var doc = JsonDocument.Parse(msg);
        root = doc.RootElement.Clone();
    }
    catch (JsonException)
    {
        await SendTextAsync(ws, BadRequestJson);
        return;
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("type", out var typeEl) ||
        typeEl.ValueKind != JsonValueKind.String ||
        typeEl.GetString() != "track")
    {
        await SendTextAsync(ws, BadRequestJson);
        return;
    }

    var track = TrackFromElement(root);
    if (track is null)
    {
        await SendTextAsync(ws, BadRequestJson);
        return;
    }

    track.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    lock (t.Lock)
    {
        t.Track = track; // stored raw; delay is applied in ServedTrackJson
    }

    await BroadcastAsync(t.Overlay, ServedTrackJson(t));
    await SendTextAsync(ws, OkJson);
}

// --- Static files: serve ../overlay/ at the site root ----------------------
// Resolve relative to the repo root (parent of ebs/); fall back to an
// overlay/ next to the app (e.g. published layout).

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
    var ba = System.Text.Encoding.UTF8.GetBytes(a);
    var bb = System.Text.Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(ba, bb);
}

// HEAD included so `curl -I` works against the read endpoint.
app.MapMethods("/api/track/{tenantId}", new[] { "GET", "HEAD" }, (string tenantId, HttpContext ctx) =>
{
    ApiHeaders(ctx);
    if (!ValidTenant(tenantId))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Results.Content(BadTenantJson, "application/json");
    }
    if (ctx.Request.Method == HttpMethods.Head) return Results.Content("", "application/json");
    return Results.Content(ServedTrackJson(GetTenant(tenantId.ToLowerInvariant())), "application/json");
});

app.MapGet("/api/track/ws/{tenantId}", async (string tenantId, HttpContext ctx) =>
{
    ApiHeaders(ctx);
    if (!ValidTenant(tenantId))
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
    await ConnectOverlayClientAsync(GetTenant(tenantId.ToLowerInvariant()), ws);
});

app.MapGet("/api/desktop/ws/{tenantId}", async (string tenantId, HttpContext ctx) =>
{
    ApiHeaders(ctx);
    if (!ValidTenant(tenantId))
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

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await ConnectDesktopClientAsync(GetTenant(tenantId.ToLowerInvariant()), ws);
});

app.MapGet("/health", (HttpContext ctx) =>
{
    ApiHeaders(ctx);
    return Results.Text("ok");
});

// --- Tenant pruning: drop tenants with no open WS connection and an idle ---
// --- stored track (a fresh tenant can always be recreated on demand). -------

var pruneCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, pruneIntervalMs)));
    while (await timer.WaitForNextTickAsync(pruneCts.Token))
    {
        foreach (var (id, t) in tenants)
        {
            bool idle;
            lock (t.Lock) idle = t.Track.State == TrackState.Idle;
            if (idle && t.Overlay.IsEmpty && t.Desktop.IsEmpty)
                tenants.TryRemove(id, out _);
        }
    }
}, CancellationToken.None);

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
/// Per-tenant state: track store (guarded by Lock), overlay-channel clients,
/// desktop-channel clients. positionMs is stored RAW; the stream delay is
/// applied in ServedTrackJson.
/// </summary>
sealed class Tenant
{
    public readonly object Lock = new();
    public Track Track = new();
    public readonly ConcurrentDictionary<Guid, WebSocket> Overlay = new(); // GET /api/track/ws/{id}
    public readonly ConcurrentDictionary<Guid, WebSocket> Desktop = new(); // GET /api/desktop/ws/{id}
}
