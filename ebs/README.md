# EBS — now-playing backend for the Twitch overlay

Small .NET 10 ASP.NET Core minimal API that holds the current now-playing
track **per tenant**. The desktop app (streamer's PC) pushes tracks over an
authenticated WebSocket channel; the overlay extension reads them live over a
public WebSocket channel. The contract (endpoints, Track JSON, auth) lives in
`plan.md`.

## Tenants (multi-tenancy)

One tenant = one streamer (their extension instance + their desktop app).
Every API path carries a `{tenantId}` segment; the track store, the overlay
channel and the skip relay are partitioned per tenant — nothing crosses
tenants.

- Format: `^[A-Za-z0-9_-]{3,64}$`. The EBS **lowercases** the ID on ingest
  (case-insensitive). Invalid format → `400`. A path without the segment
  (e.g. `GET /api/track`) → `404`.
- An unknown tenant is not an error: the overlay channel serves the idle
  track, and a desktop channel connects normally — its first **accepted
  push** creates the tenant.
- Tenants are **created only by an accepted desktop-channel push**. The read
  paths (`GET /api/track/{id}`, overlay WS) never create one — they serve the
  idle track from a throwaway object. Overlay clients that connect to a not-yet
  known tenant are merged into the tenant when its first push arrives.
- The store is bounded by `Ebs:MaxTenants` (default `1000`): once the cap is
  reached, a desktop-channel upgrade for a *new* tenantId is rejected with
  `429` before the WS handshake (existing tenants are unaffected). A desktop
  that already got its greeting and only then hits the cap on its first push
  is not dropped silently — it receives `{"error":"tenant-limit"}` before the
  close.
- The push key is one **global** secret (`Ebs:PushKey`): it authorizes a
  desktop channel for *any* tenantId.
- Tenants with no open WebSocket connection are pruned by a background timer
  (every `Ebs:PruneIntervalMs`, default 10 min) when their stored track is
  `idle` **or** older than `Ebs:PruneStaleAfterMs` (default `3600000`, 1 h) —
  a disconnected desktop never re-pushes `idle`, so dead `playing`/`paused`
  tenants must be freed by age too, or the store wedges at the cap.

## Endpoints

| Method          | Path                      | What                                                        |
|-----------------|---------------------------|-------------------------------------------------------------|
| GET             | `/api/track/{tenantId}`   | That tenant's track JSON — debug/manual; stream delay applied |
| GET (WS upgrade)| `/api/track/ws/{tenantId}`| **Overlay channel** (public): tenant's track push + `{"cmd":"next"}` in |
| GET (WS upgrade)| `/api/desktop/ws/{tenantId}` | **Desktop channel** (auth: `X-Push-Key` header on the upgrade): track pushes in, `{"type":"next"}` out |
| GET             | `/health`                 | Plain text `ok`                                             |
| —               | `/`                       | Serves `../overlay/` as static files                        |

There is no HTTP write endpoint — tracks are pushed as frames on the desktop
channel: `{"type":"track", ...Track fields}` → ack `{"ok":true}` (or
`{"error":"bad-request"}`, connection stays open). The overlay's skip button
sends `{"cmd":"next"}` on the overlay channel; the EBS relays `{"type":"next"}`
to that tenant's connected desktop clients only — **only when the tenant's
stored track has `skipEnabled: true`**, with a 1 s per-tenant cooldown
(the overlay channel is unauthenticated and tenant IDs are guessable, so the
EBS gate is authoritative — the overlay's hidden/disabled button is a UI gate
only).

Inbound limits: one inbound text message per client is capped at **2 MB**
(exceeding it closes that connection); a track frame with an `art` string
longer than **700 KB**, or a negative `positionMs`/`durationMs`/timeline
value, is `bad-request`.

Send path: a client socket never has two outstanding sends (per-client send
gate); fan-out to one tenant's overlay clients runs in parallel with a 2 s
per-send timeout, so one stale socket never delays live clients. Keep-alive:
the server sends a WebSocket **PING** every `Ebs:KeepAliveSeconds` (default
`60` s; `WebSocketOptions.KeepAliveInterval`) and aborts a client that does
not PONG within the same window (`KeepAliveTimeout`) — a client must keep a
read pending to auto-pong, which is what detects half-open sockets.

## Position at serve time

The desktop samples `positionMs` only once per push. So a fresh overlay
connect (or a `GET`) computes the **true current** position at serve time from
the timeline baseline the desktop sends with the frame:

- `startedAtUtcMs` — epoch ms (streamer-PC clock) when the track's position was 0
- `pausedForMs` — accumulated paused ms for this track
- `pausedSinceUtcMs` — epoch ms when the current pause began (0 while playing)

When `startedAtUtcMs > 0` and the state is `playing` or `paused`, every
output serves:
`positionMs = (pausedSinceUtcMs > 0 ? pausedSinceUtcMs : nowUtc) − startedAtUtcMs − pausedForMs − StreamDelayMs`,
clamped ≥ 0 and at `durationMs` when it is > 0. While paused the value is
frozen at the pause point. When the timeline fields are 0/missing (older
desktop), the EBS falls back to the legacy `max(0, storedPositionMs − StreamDelayMs)`.
Clocks: the baseline comes from the streamer PC, the EBS uses its own
`UtcNow` — both assumed NTP-synced.

## Run locally (from the repo root)

```bash
dotnet run --project ebs --urls http://127.0.0.1:5080
```

## Configuration

Settings live in `appsettings.json`:

```json
{ "Ebs": { "PushKey": "dev-push-key-change-me", "StreamDelayMs": 10000, "PruneIntervalMs": 600000, "PruneStaleAfterMs": 3600000, "MaxTenants": 1000, "KeepAliveSeconds": 60 } }
```

- **`Ebs:PushKey`** — auth for the desktop channel (`X-Push-Key` header on the
  upgrade). **In production, set a real secret** via `appsettings.Production.json`
  (keep it out of git) or the `Ebs__PushKey` environment variable
  (double underscore = config section divider).
- **`Ebs:StreamDelayMs`** — viewers hear the stream ~this many ms behind the
  player. The EBS stores `positionMs` raw and applies the delay in every
  output (GET + overlay channel): the serve-time formula (above) when the
  timeline baseline is known, the legacy `max(0, positionMs - StreamDelayMs)`
  when it is not — so the overlay's progress bar lags the player by about the
  stream delay. Default `10000`.
- **`Ebs:PruneIntervalMs`** — how often the background timer prunes tenants
  that have no open WebSocket connection and whose stored track is `idle` or
  older than `Ebs:PruneStaleAfterMs`. Default `600000` (10 min).
- **`Ebs:PruneStaleAfterMs`** — age threshold for pruning connection-less
  tenants: a `playing`/`paused` track older than this is pruned even though a
  disconnected desktop never re-pushed `idle` (without it, the store wedges
  at `Ebs:MaxTenants`). Default `3600000` (1 h).
- **`Ebs:MaxTenants`** — cap on the tenant store. When the cap is reached, a
  desktop-channel upgrade for a *new* tenantId gets `429` before the WS
  handshake; existing tenants are unaffected. Default `1000`.
- **`Ebs:KeepAliveSeconds`** — WebSocket keep-alive: sets `KeepAliveInterval`
  (PING cadence) and `KeepAliveTimeout` (abort when no PONG arrives).
  Default `60`; shorten only for testing.

## Production: nginx reverse proxy (HTTPS + WebSocket)

Publish the app to the server and run it, e.g.:

```bash
dotnet publish ebs -c Release -o /opt/ebs
# the app serves the overlay from <publish dir>/overlay (its fallback path,
# used when ../overlay next to the source tree is absent) — copy it there:
cp -r <repo>/overlay /opt/ebs/overlay
dotnet /opt/ebs/TwitchExtension.Ebs.dll --urls http://127.0.0.1:5080
```

Verify with the startup log: `Serving overlay from /opt/ebs/overlay`. Without
the copy, `/` would 404 in production (the repo-relative `../overlay` does
not exist on the server).

Then front it with nginx. Both WebSocket paths need HTTP/1.1 +
`Upgrade`/`Connection` header forwarding and long idle timeouts; everything
else is a plain reverse proxy.

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name track.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 on;
    server_name track.example.com;

    ssl_certificate     /etc/letsencrypt/live/track.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/track.example.com/privkey.pem;

    # WebSocket live-push endpoints (overlay + desktop), tenantId in the path:
    # forward the upgrade, keep long-lived connections open
    location ~ ^/api/(track|desktop)/ws/ {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # Everything else: API + overlay static files
    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

> **Twitch allowlist:** add `track.example.com` (your domain) to the extension
> version's **"Allowlist for URL Fetching Domains"** (dev console → Capabilities).
> It lands in the `connect-src` CSP, which covers `wss://` as well as plain `fetch()`.
> The public EBS must be HTTPS — `block-all-mixed-content` blocks a plain-HTTP origin.
