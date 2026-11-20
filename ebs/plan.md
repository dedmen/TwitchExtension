# EBS — Implementation Plan

## What to build
The Extension Backend Service (EBS): a small ASP.NET Core minimal API on **.NET 10**.
It stores the current track **per tenant** (one tenant = one streamer's extension + desktop app). The overlay (viewers) reads it; the desktop app (streamer's PC) writes it.
It also serves `../overlay/` as static files so local development runs from a single origin (no CORS pain).

Read `../AGENTS.md` first — architecture and Twitch constraints live there.
**Do not modify anything outside `ebs/`.** (Parallel subagents own the other folders.)

## Files to create (in this folder)
- `TwitchExtension.Ebs.csproj` — `Microsoft.NET.Sdk.Web`, `<TargetFramework>net10.0</TargetFramework>`, nullable + implicit usings enabled. No NuGet packages.
- `appsettings.json` — config: `Ebs:PushKey` (auth for the desktop channel), `Ebs:StreamDelayMs` (stream delay, default 10000), logging.
- `Track.cs` — the Track model (below).
- `Program.cs` — minimal API + static files + WebSocket push.
- `README.md` — run instructions + nginx reverse-proxy config (HTTPS + WebSocket upgrade).

## Contract (canonical — the desktop/overlay plans are written against this; do not deviate)

### Track JSON
```json
{
  "title": "",
  "artist": "",
  "album": "",
  "art": null,
  "source": "",
  "state": "idle",
  "updatedAt": 0,
  "positionMs": 0,
  "durationMs": 0,
  "startedAtUtcMs": 0,
  "pausedForMs": 0,
  "pausedSinceUtcMs": 0,
  "skipEnabled": false,
  "next": null
}
```
- `title`, `artist`, `album`, `source`: strings, **never null** (empty string when unknown).
- `art`: `data:` URL string (JPEG, ≤ 512 KB) or `null`.
- `state`: `"playing" | "paused" | "stopped" | "idle"` — `idle` means "no track".
- `updatedAt`: epoch **milliseconds**; set by the EBS on every successful write; `0` until first write. The EBS **ignores** any `updatedAt` in the request body.
- `positionMs`: int ≥ 0 — playback position (ms) **sampled by the client at the moment of each push** (track change / play / pause). The client must not poll position to push updates — the overlay advances the progress bar locally from this anchor.
- `durationMs`: int ≥ 0 — total track length in ms; `0` when unknown.
- `startedAtUtcMs`: long — epoch **milliseconds** (streamer-PC clock, UTC) at which the current track's position was 0 (playback start / seek baseline). The desktop sets it on a fresh track and resets it on a detected seek. `0` = unknown (older client) → the EBS falls back to the stored sample.
- `pausedForMs`: long — accumulated paused ms for this track (reset per track); `0` when unknown.
- `pausedSinceUtcMs`: long — epoch ms (streamer-PC clock, UTC) when the current pause began; `0` while playing or unknown.
- `skipEnabled`: bool — the desktop's tray-menu toggle; `true` → the overlay shows a skip-to-next button.
- `next`: `{"title": string, "artist": string}` or `null` — next track in queue when the source provides it. The desktop always sends `null` for now (SMTC has no queue API). The overlay hides it when null.
- The extended fields (`durationMs`, `startedAtUtcMs`, `pausedForMs`, `pausedSinceUtcMs`, `skipEnabled`, `next`) are all optional in a track frame (missing → `0` / `0` / `0` / `0` / `false` / `null`). The EBS stores them raw and applies the **Stream delay** (below) to `positionMs` in every served/broadcast Track.
- Inbound validation limits: an `art` string longer than **700 KB** (≈ the 512 KB art cap with base64 overhead) makes the frame `bad-request`; negative `positionMs`/`durationMs`/timeline longs make the frame `bad-request`.

### Tenant (multi-tenancy)
Every API path carries a `{tenantId}` segment. One tenant = one streamer (their extension instance + their desktop app); the EBS partitions the track store, overlay channel and desktop channel per tenant — **nothing crosses tenants**.
- Format: `^[A-Za-z0-9_-]{3,64}$`. The EBS **lowercases** the ID on ingest (IDs are case-insensitive).
- Invalid format → `400` before the upgrade (WS paths) / `400` JSON (GET). A path without a tenantId segment (e.g. `GET /api/track`) → `404`.
- An unknown (never-seen) tenant is **not** an error: the overlay channel serves the idle track, and a desktop channel connects normally — its first push creates the tenant. That is how a new streamer's tenant comes into being.
- **Tenants are created only by an accepted desktop-channel push.** The read paths (`GET /api/track/{id}`, overlay WS) never create a tenant — they serve the idle track from a throwaway object. The tenant dictionary is **bounded** by `Ebs:MaxTenants` (default `1000`): once the cap is reached, a new desktop-channel upgrade is rejected with `429` before the WS handshake (existing tenants are unaffected). A desktop that already connected (got `{"ok":true}`) and only then hits the cap on its first push is **not** dropped silently — it receives `{"error":"tenant-limit"}` first.
- The push key stays one **global** secret (`Ebs:PushKey`): it authorizes a desktop channel for *any* tenantId. Accepted for this deployment (the operator distributes the key to streamers); per-tenant keys are a future hardening option.
- Memory: a background timer (every `Ebs:PruneIntervalMs`, default `600000`) prunes tenants with **no open WS connection** whose stored track is `idle` **or** whose `updatedAt` is older than `Ebs:PruneStaleAfterMs` (default `3600000` — a disconnected desktop never re-pushed `idle`, so dead `playing`/`paused` tenants must be freed by age too, or at `Ebs:MaxTenants` the store wedges with `429` for every new streamer). The loop must not fault on shutdown (`OperationCanceledException` caught), the CTS is disposed, and **prune must not orphan a connecting client**: attach, merge, create and prune all run under one `tenantLock`; overlay clients that attach before the tenant exists are held in a `pendingOverlay` map and merged into the tenant on its first accepted push (see **Overlay channel**).

### Endpoints
All API responses JSON with `Access-Control-Allow-Origin: *` and `Cache-Control: no-store`. `{tenantId}` in a path must match the tenant format (above); invalid → `400`.

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/track/{tenantId}` | Returns the stored Track **for that tenant**. If nothing was ever stored, returns the idle track (all empty, `state: "idle"`, `updatedAt: 0`). |
| `GET` | `/health` | `200` plain text `ok`. |
| `GET` (WS upgrade) | `/api/track/ws/{tenantId}` | WebSocket (101). No auth — **overlay channel**: server pushes that tenant's Track JSON; client may send `{"cmd":"next"}` (skip request). See **Overlay channel**. |
| `GET` (WS upgrade) | `/api/desktop/ws/{tenantId}` | WebSocket (101). Auth: `X-Push-Key` header on the upgrade request must equal `Ebs:PushKey` (default `dev-push-key-change-me`); missing/wrong → `401` before upgrade. **Desktop channel**: track pushes + command delivery. See **Desktop channel**. |
| `/` | (static) | Serve `../overlay/` at the site root via `PhysicalFileProvider` + `RequestPath = ""`, so `http://127.0.0.1:5080/` serves the overlay's `index.html`. Resolve the overlay directory **relative to the repo root** (parent of `ebs/`), with a fallback to `overlay/` next to the app. |

## Overlay channel (`GET /api/track/ws/{tenantId}`)
- No auth — public, same exposure as `GET /api/track/{tenantId}`.
- Server → client: text frames containing Track JSON (same shape as `GET /api/track/{tenantId}`, stream delay applied). The first message on connect is that tenant's current track (idle track if nothing stored).
- On every accepted track push from **that tenant's** desktop channel: broadcast the stamped Track to all overlay clients connected to that tenant. Best effort: if a send fails, drop that client.
- Client → server: `{"cmd":"next"}` requests a skip-to-next; the EBS relays `{"type":"next"}` to the **same tenant's** desktop-channel clients **only when that tenant's stored track has `skipEnabled: true`**, with a per-tenant cooldown (a second skip within **1 s** is ignored). The overlay's hidden/disabled button is a UI gate only — **the EBS gate is authoritative** (tenant IDs are guessable; this channel is unauthenticated). All other inbound frames are ignored.
- **Inbound frame cap:** the accumulated size of one inbound text message is capped at **2 MB** — a message exceeding it closes the connection (the only legitimate inbound input is `{"cmd":"next"}`; anything bigger is an attack, and this channel is unauthenticated).
- **Send serialization:** a client socket never has two outstanding `SendAsync` calls (per-client send gate, e.g. `SemaphoreSlim`); a "busy" send is retried after the gate, and a client is dropped only on a real send failure. Fan-out to one tenant's clients runs **in parallel** with a **2 s** per-send timeout (a stale socket must not delay delivery to live clients).
- Keep-alive: `Ebs:KeepAliveSeconds` (default `60`) sets **both** `WebSocketOptions.KeepAliveInterval` (the PING cadence — browsers auto-pong, so clients must keep a read pending) and `KeepAliveTimeout` (the PONG window — a client that misses it is aborted). Half-open sockets are detected instead of lingering until TCP gives up. No app-level heartbeat.
- The overlay reads **only** via this endpoint — no HTTP polling fallback (decided 2026-11-20). `GET /api/track/{tenantId}` stays for debug and manual reads.

## Desktop channel (`GET /api/desktop/ws/{tenantId}`)
- Auth: the upgrade request must carry an `X-Push-Key` header equal to `Ebs:PushKey` from `appsettings.json` (default `dev-push-key-change-me`); missing/wrong → `401` before the upgrade.
- On connect: the server sends `{"ok":true}`.
- Client → server: `{"type":"track","title","artist","album","art","source","state","positionMs","durationMs","skipEnabled","next"}` — same validation as the old PUT (strings, `state` enum, `art` string|null, `next` object|null, ints ≥ 0); `updatedAt` omitted (the EBS stamps it). On success: stamp `updatedAt`, store under the tenant, broadcast to the tenant's overlay channel (delay applied), reply `{"ok":true}`. Invalid frame → `{"error":"bad-request"}`; the connection stays open.
- Server → client: `{"type":"next"}` when an overlay client of the **same tenant** requests a skip.
- One connection per desktop app; best-effort, no dedupe.

## Stream delay
- `Ebs:StreamDelayMs` in `appsettings.json` (default `10000`). Viewers hear the stream ~this many ms after the player's real position.
- The EBS stores `positionMs` raw and applies the delay in every output (GET response and overlay-channel messages): `positionMs = max(0, storedPositionMs - StreamDelayMs)`. The overlay does no delay math.
- Production: the reverse proxy must forward the `Upgrade`/`Connection` headers and allow long read timeouts on **both** WS paths — see `README.md` (nginx).

## Position at serve time
A fresh overlay connect (or a `GET`) must see the **true current** position, not the last-sampled push. When `startedAtUtcMs > 0` and `state` is `playing` or `paused`, the EBS computes the position **at serve time** for every output (GET, overlay-connect message, broadcast):
`pos = (pausedSinceUtcMs > 0 ? pausedSinceUtcMs : nowUtc) - startedAtUtcMs - pausedForMs`
then applies the stream delay and clamps ≥ 0 (and at `durationMs` when > 0). If the timeline fields are missing/0 (older desktop), fall back to `max(0, storedPositionMs - StreamDelayMs)`.
Clocks: the baseline comes from the streamer PC, the EBS uses its own `UtcNow` — both assumed NTP-synced; residual skew shows up as a constant offset in the bar (same class of assumption as the stream delay).

## State
In-memory only, **per tenant**. A successful track push updates that tenant's stored track; nothing is written to disk.
After an EBS restart every tenant's store is the idle track again (`state: "idle"`, `updatedAt: 0`).

## Run
- Local: `dotnet run --project ebs --urls http://127.0.0.1:5080` (from repo root).
- Production: user's own server behind their HTTPS reverse proxy (not your concern in code).

## Acceptance (all must pass)
1. `dotnet build ebs` → 0 errors.
2. `curl -s localhost:5080/api/track/local` → idle track JSON. `curl -s localhost:5080/api/track` → 404. `curl -s localhost:5080/api/track/x` (too short) → 400.
3. A desktop-channel WS client (e.g. PowerShell `ClientWebSocket` with the `X-Push-Key` request header) connects to `/api/desktop/ws/local`, receives `{"ok":true}`, sends a track frame `{"type":"track","title":"T","artist":"A","album":"","art":null,"source":"x","state":"playing","positionMs":35000,"durationMs":200000,"skipEnabled":true,"next":null}` → receives `{"ok":true}`.
4. `curl -s localhost:5080/api/track/local` → shows `title: "T"`, `state: "playing"`, `updatedAt > 0`.
5. Desktop-channel WS without the key (or a wrong key) → upgrade rejected with `401`; a track frame with `"state":"bogus"` → `{"error":"bad-request"}` and the connection stays open. A tenant path with an invalid ID (`/api/desktop/ws/x`, `/api/desktop/ws/BAD ID`) → `400` before the upgrade.
6. `curl -s localhost:5080/health` → `ok`.
7. `curl -s -I localhost:5080/api/track/local` includes `access-control-allow-origin: *`.
8. After an EBS restart, `GET /api/track/local` returns the idle track (state does not persist).
9. A WebSocket client (e.g. PowerShell `System.Net.WebSockets.ClientWebSocket`) connecting to `/api/track/ws/local` immediately receives the current track JSON; after a new accepted push for `local`, a broadcast message arrives.
10. `README.md` exists with run instructions + a working nginx config (HTTPS, WS upgrade headers + long `proxy_read_timeout` on **both** WS paths, tenantId in the path) and documents `Ebs:StreamDelayMs`.
11. A track push with `positionMs: 20000` → `GET /api/track/local` and the overlay-channel message show `positionMs: 10000` (default 10 s delay applied); a push with `positionMs: 5000` → `0` (clamped).
12. Skip relay is per-tenant: with desktop-channel clients on both `local` and `other`, an overlay client on `/api/track/ws/local` sends `{"cmd":"next"}` → only the `local` desktop client receives `{"type":"next"}`; an overlay client on `/api/track/ws/other` → only the `other` desktop client.
13. The new Track fields (`durationMs`, `skipEnabled`, `next`) survive a push round-trip.
14. **Isolation**: push track A to tenant `local`, track B to tenant `other` → `GET /api/track/local` shows A and `GET /api/track/other` shows B; overlay clients on each tenant receive only their own tenant's broadcasts.
15. **Serve-time position**: push a frame with `"state":"playing"`, `"positionMs":0`, `"startedAtUtcMs": <now − 120 s>`, `"pausedForMs":0`, `"pausedSinceUtcMs":0` → `GET /api/track/{t}` a few seconds later shows ≈ 110000 (120 s − 10 s delay), **not** 0. Then push `"state":"paused"` with the same baseline and `"pausedSinceUtcMs": <now>` → the served position freezes at the pause point.
16. **Old-client fallback**: a frame without the timeline fields → served `positionMs` = stored sample − delay (legacy behavior).
17. **Send serialization**: a track push arriving at the exact moment a new overlay client connects (two concurrent writes to that client's socket) → no `InvalidOperationException`, no client dropped, and the client receives both the connect track and the broadcast, in order.
18. **Skip gating**: stored track with `skipEnabled: false` → an overlay client's `{"cmd":"next"}` reaches no desktop client. With `skipEnabled: true` → relayed; a second `{"cmd":"next"}` within 1 s → ignored (the desktop client receives exactly one `{"type":"next"}` per skip interval).
19. **No tenant creation on reads**: `GET /api/track/brandnewxyz` and an overlay-WS connect to it → idle track, and the tenant does **not** enter the store. With `Ebs:MaxTenants` set small (e.g. `3`) in a test config: after 3 tenants exist, a desktop-channel upgrade for a **new** ID → `429` before the WS handshake; an upgrade for an existing tenant still succeeds.
20. **Inbound limits**: an overlay client sending a text frame > 2 MB → its connection is closed, the process stays up, other tenants unaffected. A desktop track frame with `art` > 700 KB (or negative `positionMs`) → `{"error":"bad-request"}`, connection stays open.
21. **Prune safety**: with a small `Ebs:PruneIntervalMs` in a test config, hammer overlay connects on a tenant with an idle track and no other connections → every connecting client gets the track on connect and still receives broadcasts after a later push (no orphaned client). On EBS shutdown the prune task ends without an unobserved exception.
22. **Keep-alive**: `Ebs:KeepAliveSeconds` drives **both** `KeepAliveInterval` and `KeepAliveTimeout` (a raw WS client observing an idle overlay connection sees a server PING within ~`KeepAliveSeconds`; a client that never PONGs is aborted).
23. **Parallel fan-out**: 5 overlay clients connected, one of them made "stale" (e.g. a client whose socket send blocks/times out) → the live clients receive a broadcast within ~2 s, not sequentially delayed.
24. **Pre-creation overlay client sees the real track (no stale idle)**: an overlay client connects to a tenant that does **not** yet exist (it gets the idle track), then the desktop's first push creates the tenant and merges the client → the client's **last** frame is the new track, never a stale `idle` arriving *after* the real broadcast (the connect frame must be re-sent from the tenant's current state once it exists).
25. **Stale non-idle tenants are pruned by age**: with a small `Ebs:PruneStaleAfterMs` in a test config, push a `playing` track to tenant X, then drop the desktop connection (tenant stays `playing`, no idle re-push) → after `PruneStaleAfterMs` + one prune tick, X is pruned; a new tenant can then register (no `429` wedge at the cap).
26. **Tenant-limit is not a silent drop**: a desktop that already connected (got `{"ok":true}`) and only then hits `Ebs:MaxTenants` on its first push → receives `{"error":"tenant-limit"}` before the connection closes.
27. **Cap is race-safe and re-checkable**: with `Ebs:MaxTenants = N`, N concurrent first-pushes of distinct new tenants → at most N are created; the loser(s) get `429` (pre-upgrade) or `{"error":"tenant-limit"}` (mid-connection), never a duplicate/partial tenant.
