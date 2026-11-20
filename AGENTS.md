# AGENTS.md

## Project
Twitch extension showing a "now playing" music tile (title, artist, album art) over the stream, plus a Windows desktop app on the streamer's machine that pushes the current track.

## Repo layout & commands
- `ebs/` — .NET 10 ASP.NET Core minimal API (the EBS). Run: `dotnet run --project ebs --urls http://127.0.0.1:5080` (from repo root).
- `desktop/` — C# WinForms tray app, TFM `net10.0-windows10.0.17763.0`. Run: `dotnet run --project desktop`.
- `overlay/` — static HTML/JS extension, no build step. Served by the EBS at `/`.
- Each project builds standalone (`dotnet build <folder>`); no root solution file.
- **Implementation plans:** `ebs/plan.md`, `desktop/plan.md`, `overlay/plan.md`. The cross-component contracts (Track JSON, API endpoints, ports, push key) are canonical *there* — keep them in sync if any plan changes.

## Architecture
`Desktop app (Windows)` → **EBS** (small HTTPS backend holding the current track) → `Overlay extension` (WebSocket-fed tile, renders it).

- The overlay runs in *viewers'* browsers. Viewers cannot reach the streamer's machine, so track state must flow through a public EBS. Never design the overlay to call the desktop app directly.
- Local-testing exception: in "Local Test" mode the extension loads in the streamer's own browser, so a `localhost` EBS is fine while developing.
- **Multi-tenant:** every API path carries a `{tenantId}` (one tenant = one streamer; format `^[A-Za-z0-9_-]{3,64}$`, EBS lowercases). The streamer enters the ID on the extension's settings page (`overlay/settings.html` = the version's Configuration Path in the dev console) and puts the same value in the desktop's `settings.json`. The EBS partitions track store, overlay channel and skip relay per tenant; tenants with no connections and an idle track get pruned; the push key stays one global secret (per-tenant keys = future hardening).
- EBS → overlay: **WebSocket live push** — `GET /api/track/ws/{tenantId}`: the EBS sends the tenant's current track on connect and broadcasts on every accepted track push (same tenant only); .NET WS ping frames keep the connection alive; the overlay reconnects with backoff. Pure WS for overlay reads, no polling fallback (decided 2026-11-20); `GET /api/track/{tenantId}` stays for debug. Production reverse proxy must forward WS upgrades — nginx config in `ebs/README.md`. PubSub would work too but is capped at 1 msg/s / 5 KB per channel.
- Progress: the desktop samples `positionMs` **once per push** (no per-second pushes) and sends a timeline baseline (`startedAtUtcMs`, `pausedForMs`, `pausedSinceUtcMs`); the EBS computes the true current position **at serve time** from it (so a fresh overlay connect mid-track is correct) and applies `Ebs:StreamDelayMs` (default 10 s); the overlay advances the bar locally from its anchor and clamps to 100 %. The tile is draggable (streamer side), position in `localStorage`, clamped to the viewport on resize.
- Commands (other direction): the overlay's skip button sends `{"cmd":"next"}` over its own track WS; the EBS relays `{"type":"next"}` over the **same tenant's** desktop authenticated WS channel (`GET /api/desktop/ws/{tenantId}`, `X-Push-Key` header on the upgrade), which executes the SMTC skip. `PUT /api/track` is gone (2026-11-20) — the desktop pushes tracks over that same WS; `GET /api/track` stays for debug. The overlay shows the button only when the desktop's `skipEnabled` flag (tray-menu toggle) is on.

## Twitch constraints (verified: dev.twitch.tv/docs/extensions)
- Every HTML file must load the Extension Helper: `<script src="https://extension-files.twitch.tv/helper/v1/twitch-ext.min.js"></script>`
- Overlay HTML needs a **transparent background** — it renders on top of the video.
- Local test: Base URI (must end with `/`) + viewer path point at the dev server (e.g. `https://localhost:8080/`). HTTPS via `mkcert -install localhost`, or the Chrome flag `chrome://flags/#allow-insecure-localhost`.
- The EBS origin must be added to the extension version's **"Allowlist for URL Fetching Domains"** (dev console, Capabilities tab). CSP is server-set; `<meta>` tags are ignored. Public EBS must be HTTPS (`block-all-mixed-content`).
- **No inline scripts:** the hosted CSP `script-src` is `'self'` + the extension origin + `extension-files.twitch.tv` — no `'unsafe-inline'`, no nonces. All overlay JS must live in external `.js` files (`index.html`/`settings.html` carry only `<script src>` tags). Verified broken: an inline script in `settings.html` silently never ran (2026-11-20).
- **Config service:** the **"Configuration Service" capability must be enabled** in the dev console (Capabilities tab) — otherwise the helper exposes no config API at all (`Twitch.ext.configuration`/legacy `setConfigurationValue` are undefined; verified 2026-11-20). Current API: `Twitch.ext.configuration.set("broadcaster", version, jsonContent)` + read/prefill via `configuration.onChanged` → `broadcaster.content`. Values only persist when the settings page runs inside Twitch's own settings iframe (channel → extensions → settings); opening the ext-twitch.tv URL directly in a browser sends the request but stores nothing. The version's **Configuration Path** must be a relative filename (e.g. `settings.html`) — a full URL makes the page load from the wrong origin, where the config service won't authenticate.
- **No Twitch OAuth is needed.** The overlay never calls the Twitch API. The `onAuthorized` JWT and the shared secret (Settings > Secret Keys) are only needed if we want to authenticate EBS writes; a read-only public endpoint can go unauthenticated.
- Twitch's configuration service (5 KB, delivered at bootstrap only) is NOT a channel for live-updating data.

## Now-playing detection (Windows)
- Use Windows **SMTC**: `GlobalSystemMediaTransportControlsSessionManager` (Win10 1809+ / 11). Exposes title, artist, album, playback state, and album-art thumbnail for any app that registers a media session (Spotify, Apple Music, browsers).
- Node access: `windows-media-sessions` (Gyom03) — .NET 8 backend bridged over stdio, Node 20+, thumbnail as base64 data URL. Alternatives: Python `winrt`, C# `WindowsMediaController`, Rust `windows` crate.
- Gotcha: apps often register an SMTC session only while playing. Query the **current/focused session** dynamically; never cache a session ID.
- **Session selection / priority:** the OS "current session" is whatever the OS last deemed current — a browser video can win it over the music app. The desktop's `preferredSource` setting (case-insensitive substring of the AUMID, e.g. `"sendspin"`) pins the monitor to a session from that source while it has content, and falls back to the current session otherwise.
- Apps on the legacy SMTC API (e.g. Sendspin) can still expose position/duration via `UpdateTimelineProperties` (SDK 19041+, works on Win10 2004+) — the Global reader then reports real `TimelineProperties`. Fix branch on `dedmen/sendspin-windows`: `fix/smtc-playback-position-and-duration`.

## Stack (decided)
- **EBS: .NET 10 (C#), ASP.NET Core minimal API**, hosted on the user's own server (their reverse proxy provides HTTPS; its domain goes in the URL Fetching allowlist).
- **Desktop app: C# (.NET 10) Windows tray app** reading SMTC. Target `net10.0-windows10.0.17763.0` so the WinRT projections (`Windows.Media.Control`) are available without extra packages.
- **Overlay: static HTML/JS** (Extension Helper + poll EBS + tile). No framework or build step unless it grows.

## Decisions (closed)
Music source: **any Windows app via SMTC** — no Spotify OAuth, no API keys. The EBS store is source-agnostic (track: title/artist/album/art/state), so a Spotify reader could be added later without rework.

## Environment gotchas (this dev machine)
- Windows is **German-locale**: PowerShell errors and `netstat` state words are localized (`LISTENING` shows as `ABHÖREN`). Use `Get-NetTCPConnection` for port/PID checks; never parse `netstat` state strings.
- TCP excluded port ranges (`netsh interface ipv4 show excludedportrange protocol=tcp`) are **dynamic** (WSL/Hyper-V re-reserve them). A `SocketException 10013` on bind may be transient — retest before changing a canonical port.
