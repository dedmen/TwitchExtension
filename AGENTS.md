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
`Desktop app (Windows)` → **EBS** (small HTTPS backend holding the current track) → `Overlay extension` (polls EBS, renders tile).

- The overlay runs in *viewers'* browsers. Viewers cannot reach the streamer's machine, so track state must flow through a public EBS. Never design the overlay to call the desktop app directly.
- Local-testing exception: in "Local Test" mode the extension loads in the streamer's own browser, so a `localhost` EBS is fine while developing.
- EBS → overlay: plain polling is fine (track changes are rare). PubSub works too but is capped at 1 msg/s / 5 KB per channel.

## Twitch constraints (verified: dev.twitch.tv/docs/extensions)
- Every HTML file must load the Extension Helper: `<script src="https://extension-files.twitch.tv/helper/v1/twitch-ext.min.js"></script>`
- Overlay HTML needs a **transparent background** — it renders on top of the video.
- Local test: Base URI (must end with `/`) + viewer path point at the dev server (e.g. `https://localhost:8080/`). HTTPS via `mkcert -install localhost`, or the Chrome flag `chrome://flags/#allow-insecure-localhost`.
- The EBS origin must be added to the extension version's **"Allowlist for URL Fetching Domains"** (dev console, Capabilities tab). CSP is server-set; `<meta>` tags are ignored. Public EBS must be HTTPS (`block-all-mixed-content`).
- **No Twitch OAuth is needed.** The overlay never calls the Twitch API. The `onAuthorized` JWT and the shared secret (Settings > Secret Keys) are only needed if we want to authenticate EBS writes; a read-only public endpoint can go unauthenticated.
- Twitch's configuration service (5 KB, delivered at bootstrap only) is NOT a channel for live-updating data.

## Now-playing detection (Windows)
- Use Windows **SMTC**: `GlobalSystemMediaTransportControlsSessionManager` (Win10 1809+ / 11). Exposes title, artist, album, playback state, and album-art thumbnail for any app that registers a media session (Spotify, Apple Music, browsers).
- Node access: `windows-media-sessions` (Gyom03) — .NET 8 backend bridged over stdio, Node 20+, thumbnail as base64 data URL. Alternatives: Python `winrt`, C# `WindowsMediaController`, Rust `windows` crate.
- Gotcha: apps often register an SMTC session only while playing. Query the **current/focused session** dynamically; never cache a session ID.

## Stack (decided)
- **EBS: .NET 10 (C#), ASP.NET Core minimal API**, hosted on the user's own server (their reverse proxy provides HTTPS; its domain goes in the URL Fetching allowlist).
- **Desktop app: C# (.NET 10) Windows tray app** reading SMTC. Target `net10.0-windows10.0.17763.0` so the WinRT projections (`Windows.Media.Control`) are available without extra packages.
- **Overlay: static HTML/JS** (Extension Helper + poll EBS + tile). No framework or build step unless it grows.

## Decisions (closed)
Music source: **any Windows app via SMTC** — no Spotify OAuth, no API keys. The EBS store is source-agnostic (track: title/artist/album/art/state), so a Spotify reader could be added later without rework.
