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
  track, and a desktop channel's first push creates the tenant.
- The push key is one **global** secret (`Ebs:PushKey`): it authorizes a
  desktop channel for *any* tenantId.
- Tenants with no open WebSocket connection and an idle stored track are
  pruned by a background timer (every `Ebs:PruneIntervalMs`, default 10 min).

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
to that tenant's connected desktop clients only.

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
{ "Ebs": { "PushKey": "dev-push-key-change-me", "StreamDelayMs": 10000, "PruneIntervalMs": 600000 } }
```

- **`Ebs:PushKey`** — auth for the desktop channel (`X-Push-Key` header on the
  upgrade). **In production, set a real secret** via `appsettings.Production.json`
  (keep it out of git) or the `Ebs__PushKey` environment variable
  (double underscore = config section divider).
- **`Ebs:StreamDelayMs`** — viewers hear the stream ~this many ms behind the
  player. The EBS stores `positionMs` raw and serves
  `max(0, positionMs - StreamDelayMs)` in every output (GET + overlay channel),
  so the overlay's progress bar lags the player by exactly the stream delay.
  Default `10000`.
- **`Ebs:PruneIntervalMs`** — how often the background timer prunes tenants
  that have no open WebSocket connection and an idle stored track.
  Default `600000` (10 min).

## Production: nginx reverse proxy (HTTPS + WebSocket)

Publish the app to the server and run it, e.g.:

```bash
dotnet publish ebs -c Release -o /opt/ebs
dotnet /opt/ebs/TwitchExtension.Ebs.dll --urls http://127.0.0.1:5080
```

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
