# Desktop App — Implementation Plan

## What to build
A Windows **tray app** (C#, .NET 10, WinForms) that watches what is playing on this PC via
Windows SMTC and pushes the track to the EBS. Read `../AGENTS.md` first.
**Do not modify anything outside `desktop/`.** (Parallel subagents own the other folders.)

## Stack
- TFM **`net10.0-windows10.0.17763.0`** — this gives the WinRT projections (`Windows.Media.Control`) with **no extra NuGet packages**.
- WinForms (`UseWindowsForms`), tray via `System.Windows.Forms.NotifyIcon`.

## Files to create (in this folder)
- `TwitchExtension.Desktop.csproj`
- `Program.cs` — startup, single-instance mutex, runs hidden (no main window).
- `SmtcMonitor.cs` — SMTC watching (below).
- `EbsClient.cs` — HTTP PUT to the EBS.
- `AppConfig.cs` — settings loading.
- `settings.example.json` (committed) — `settings.json` (local, gitignored) is created by the user; if missing, use the defaults below.

## Settings (`settings.json`)
```json
{ "ebsUrl": "http://127.0.0.1:5080", "pushKey": "dev-push-key-change-me" }
```

## SMTC watching (`SmtcMonitor.cs`)
- `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` — keep the manager instance alive for the app's lifetime.
- Watch the **current session** (`Get_CurrentSession()`). Apps register SMTC sessions only while playing, so on `SessionListChanged`, re-resolve the current session and re-subscribe to it. **Never cache a session across a list change.**
- Subscribe on the current session: `MediaPropertiesChanged`, `PlaybackInfoChanged`.
- On every relevant change, build a Track (contract in `../ebs/plan.md`):
  - `title` / `artist` / `album` from `TryGetMediaPropertiesAsync()`.
  - `source` = the session's `SourceAppUserModelId` (AUMID string, e.g. Spotify's — fine as-is; the overlay shows it small).
  - `state` from `GetPlaybackInfo().PlaybackStatus`: `Playing` → `playing`, `Paused` → `paused`, else `stopped`. If there is **no** current session → push the idle track (`state: "idle"`, empty strings) once.
  - `art` = `MediaProperties.Thumbnail` (`IRandomAccessStreamReference`) → read bytes → base64 → `data:{mime};base64,{...}` (mime from the thumbnail's `FileExtension`, default `image/jpeg`). If the image is **> 512 KB, set `art: null`** (don't send it).

## Pushing (`EbsClient.cs`)
- `PUT {ebsUrl}/api/track`, JSON body `{"title","artist","album","art","source","state"}` (no `updatedAt` — the EBS stamps it), header `X-Push-Key: {pushKey}`. 5 s timeout.
- Only push when content actually changed vs. the last pushed snapshot; coalesce bursts (500 ms debounce).
- On failure: keep the last known state, retry every 5 s, never crash, log at most one line per 30 s while failing.

## Tray UI
- `NotifyIcon` (system icon), tooltip: `Now playing: {artist} – {title}`, or `Paused: ...`, or `No media`, or `EBS unreachable`.
- Context menu: **Open EBS** (open `ebsUrl` in default browser), **Exit** (dispose the icon properly — a live `NotifyIcon` keeps the process alive — then exit).
- Single instance via mutex; second launch exits silently.

## Acceptance (all must pass)
1. `dotnet build desktop` → 0 errors.
2. `dotnet run --project desktop` (EBS running) → tray icon appears; app has no visible window.
3. Play a song in Spotify (or any SMTC app) → within ~1 s the tray tooltip shows artist – title, and `curl -s localhost:5080/api/track` shows the correct `title`, `artist`, `state: "playing"`.
4. Pause → `state: "paused"`; stop the player → `state: "idle"` with empty strings.
5. Kill the EBS → app keeps running, tooltip shows unreachable; restart EBS → track reappears at the EBS within ~10 s.
