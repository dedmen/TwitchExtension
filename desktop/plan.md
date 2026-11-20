# Desktop App — Implementation Plan

## What to build
A Windows **tray app** (C#, .NET 10, WinForms) that watches what is playing on this PC via
Windows SMTC and pushes the track to the EBS under this streamer's **tenant ID**.
Read `../AGENTS.md` first.
**Do not modify anything outside `desktop/`.** (Parallel subagents own the other folders.)

## Stack
- TFM **`net10.0-windows10.0.17763.0`** — this gives the WinRT projections (`Windows.Media.Control`) with **no extra NuGet packages**.
- WinForms (`UseWindowsForms`), tray via `System.Windows.Forms.NotifyIcon`.

## Files to create (in this folder)
- `TwitchExtension.Desktop.csproj`
- `Program.cs` — startup, single-instance mutex, runs hidden (no main window).
- `SmtcMonitor.cs` — SMTC watching (below).
- `EbsClient.cs` — authenticated WebSocket to the EBS (track pushes, acks, commands).
- `AppConfig.cs` — settings loading.
- `settings.example.json` (committed) — `settings.json` (local, gitignored) is created by the user; if missing, use the defaults below.

## Settings (`settings.json`)
```json
{ "ebsUrl": "http://127.0.0.1:5080", "pushKey": "dev-push-key-change-me", "tenantId": "local", "skipButtonEnabled": true, "preferredSource": "sendspin" }
```
- `preferredSource` (optional): **case-insensitive substring** matched against a session's AUMID (`SourceAppUserModelId`), e.g. `"sendspin"` pins the monitor to the Sendspin session. Empty/missing → pure OS-current-session behavior. See the session-selection rule below.
- `tenantId` is **required**: the streamer's unique ID, `^[A-Za-z0-9_-]{3,64}$` (the EBS lowercases it). It is the same value the streamer enters on the extension's settings page. Missing/invalid → the app starts, the tray tooltip reads `No tenantId — set it in settings.json`, and it does **not** connect to the EBS (no retry loop); restarting the app after editing the file picks the value up.

## SMTC watching (`SmtcMonitor.cs`)
- `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` — keep the manager instance alive for the app's lifetime.
- Watch the **selected session** (selection rule below). Apps register SMTC sessions only while playing, so on `SessionListChanged`, re-resolve and re-subscribe. **Never cache a session across a list change.**
- **Session selection (source priority):** the OS's current session is not always the music — a video playing in a browser can win it over the music app. When `preferredSource` is set: if any session in `GetSessions()` has an AUMID containing the substring (case-insensitive) **and is an active track** — playing or paused, with content (non-empty title or artist) — select the first such session (prefer one that is playing); subscribe to it. Otherwise (no match, or the match is stopped / has no content — e.g. Sendspin stopped) → select the OS **current session** as before. The skip command and all pushes always target the selected session. *Implementation note:* the OS keeps a stopped session's title/artist (only the playback status flips), so "has content" alone would let a stopped Sendspin keep winning — a stopped/none preferred session is therefore ineligible. The SDK projection exposes no stable session id on a session object (only `SourceAppUserModelId`), so "re-resolve on every list change / content change" is implemented by re-subscribing on every event — events fire only on real state changes, so this is cheap. **The monitor subscribes to *all* sessions in the list, not just the selected one**: a non-selected session that starts playing later (Sendspin resumes while a browser video is current) fires no manager-level event (the list and the current session are unchanged), so only per-session subscriptions on every session detect it (verified live 2026-11-20: resume of a stopped session is caught).
- Subscribe on the current session: `MediaPropertiesChanged`, `PlaybackInfoChanged`.
- On every relevant change, build a Track (contract in `../ebs/plan.md`):
  - `title` / `artist` / `album` from `TryGetMediaPropertiesAsync()`.
  - `source` = the session's `SourceAppUserModelId` (AUMID string, e.g. Spotify's — fine as-is; the overlay shows it small).
  - `state` from `GetPlaybackInfo().PlaybackStatus`: `Playing` → `playing`, `Paused` → `paused`, else `stopped`. If there is **no** current session → push the idle track (`state: "idle"`, empty strings) once. A current session with **no content** (empty title *and* artist — e.g. a dying session after a tab closed) is pushed as the idle track too.
  - `art` = `MediaProperties.Thumbnail` (`IRandomAccessStreamReference`) → read bytes → base64 → `data:{mime};base64,{...}` (mime from the thumbnail's `FileExtension`, default `image/jpeg`). If the image is **> 512 KB, set `art: null`** (don't send it).
  - `positionMs` = `GetPlaybackInfo().Position` in ms, **sampled once at push time** (clamped ≥ 0); `durationMs` = `MediaProperties.PlaybackDuration` in ms (0 if unknown/≤ 0). Do **not** poll position on a timer — the overlay advances the progress bar locally.
    - **Implementation note:** the .NET SDK's WinRT projection (any TFM suffix) exposes neither `PlaybackInfo.Position` nor `MediaProperties.PlaybackDuration`. The implementation reads `session.GetTimelineProperties()` instead: `positionMs` = `Position − StartTime` (clamped ≥ 0), `durationMs` = `EndTime − StartTime` (0 if ≤ 0). Sources that don't populate a timeline report 0/0. Observed on this machine (2026-11-20): the user's Sendspin build reports no timeline at all (pos/dur 0/0) and maps **pause to `stopped`** (legacy SMTC quirk) — the "freeze at pause position" behavior can only be demonstrated with a source that reports `paused` (browser video / Spotify).
  - **Timeline fields** (so the EBS can compute the true position at serve time — see `../ebs/plan.md`): maintain per current track:
    - `startedAtUtcMs` = `nowUtc − sampledPositionMs` when the track becomes **new** (title/artist changed, or position reset) — set from the position sample even when the source reports no duration (served position then tracks elapsed play time; decided 2026-11-20: position must advance with unknown duration); `0` before the first sample.
    - `pausedSinceUtcMs` = now-UTC when state transitions to `paused`; `0` while playing.
    - `pausedForMs` = accumulated pause ms for this track; on resume `pausedForMs += nowUtc − pausedSinceUtcMs`, clear `pausedSinceUtcMs`. Reset to 0 on a new track.
- **Seek self-healing:** at each push, if the sampled `positionMs` deviates from the expected `((nowUtc − startedAtUtcMs) − pausedForMs)` (while playing, `startedAtUtcMs > 0`, and the source reports a real timeline — `durationMs > 0`; a 0/0 source's samples carry no position information and must not reset the baseline) by more than 10 s, treat it as a seek/unknown: `startedAtUtcMs = nowUtc − sampledPositionMs`, `pausedForMs = 0`.
    - All three fields are included in **every** track frame.
  - `skipEnabled` = the `skipButtonEnabled` setting; `next` = always `null` for now (SMTC has no queue API — keep the field in the payload for the contract).

## Pushing + commands (`EbsClient.cs`)
- One authenticated WebSocket to `{ebsUrl}/api/desktop/ws/{tenantId}` (tenantId lowercased; scheme-rewritten `http:` → `ws:` / `https:` → `wss:`) with the `X-Push-Key: {pushKey}` request header on the upgrade. **No HTTP** — `PUT /api/track` is gone.
- On (re)connect: receive `{"ok":true}` → immediately push the current snapshot, so a restarted EBS recovers state within ~1 s of reconnect.
- A track push = one text frame `{"type":"track", ...Track fields}` (no `updatedAt` — the EBS stamps it). Push only when content actually changed vs. the last pushed snapshot (the snapshot includes `skipEnabled`); coalesce bursts (500 ms debounce). Re-announcing content that is **already pending** must not reset the debounce timer — a stream of duplicate events (e.g. a dying SMTC session re-firing) would otherwise starve the push forever.
- Inbound frames: `{"ok":true}` (push landed — EBS reachable, drives the tray tooltip), `{"error":"bad-request"}` (log, don't crash), `{"type":"next"}` → re-resolve the **current** session and `TrySkipNextAsync()` (the projection's form of the skip — `TrySkipAsync(MediaControlCommand, …)` doesn't exist in the SDK projection) — never cache a session, never crash.
- On disconnect: tooltip "EBS unreachable", reconnect with exponential backoff (1 s doubling, capped at 15 s), push the current snapshot on each successful reconnect. Never crash; log at most one line per 30 s while failing.

## Tray UI
- `NotifyIcon` (system icon), tooltip: `Now playing: {artist} – {title}`, or `Paused: ...`, or `No media`, or `EBS unreachable`, or `No tenantId — set it in settings.json`.
- Context menu: **Open EBS** (open `ebsUrl` in default browser), **Skip button in overlay** (checkable, reflects `skipButtonEnabled`; on toggle persist the setting and push the current snapshot immediately so the overlay updates within ~1 s), **Exit** (dispose the icon properly — a live `NotifyIcon` keeps the process alive — then exit).
- Single instance via mutex; second launch exits silently.

## Acceptance (all must pass)
1. `dotnet build desktop` → 0 errors.
2. `dotnet run --project desktop` (EBS running) → tray icon appears; app has no visible window.
3. Play a song in Spotify (or any SMTC app) → within ~1 s the tray tooltip shows artist – title, and `curl -s localhost:5080/api/track/{tenantId}` shows the correct `title`, `artist`, `state: "playing"`.
4. Pause → `state: "paused"`; stop the player → `state: "idle"` with empty strings.
5. Kill the EBS → app keeps running, tooltip shows unreachable; restart EBS → track reappears at the EBS within ~10 s (snapshot re-pushed on WS reconnect).
6. With a song playing, `GET /api/track/{tenantId}` shows `positionMs ≥ 0` and `durationMs > 0`; pause updates `state: "paused"` with a sensible `positionMs`; no push traffic while only the position advances (no per-second pushes).
7. Toggling "Skip button in overlay" in the tray menu → the EBS `skipEnabled` flips within ~1 s.
8. With the desktop connected and a player on a known track: an overlay-side skip (`{"cmd":"next"}` sent by a test WS client to `/api/track/ws/{tenantId}`) → the player skips to the next track within ~1 s.
9. `settings.json` with a missing/invalid `tenantId` → the app runs, the tooltip says so, and no WS connection is attempted (the EBS sees no connection; no error spam in the log).
10. **Isolation**: with the desktop on tenant `local` and a second client (stub or second instance) on tenant `other`, both tracks exist at the EBS at the same time (`GET /api/track/local` vs. `GET /api/track/other`), and skip commands sent on `other`'s overlay channel never reach `local`'s desktop.
11. **Fresh-connect position**: start a track and let it play ~60 s; a new overlay-channel WS client (or `GET /api/track/{t}`) then shows ≈ the true current position (≥ 50 s), **not** 0. Pause → the value freezes; resume → it advances again.
12. **Source priority**: with `preferredSource: "sendspin"`, play Sendspin music while a video plays in a browser → the EBS shows the Sendspin track (not the video). Stop/clear Sendspin so it has no content → the app falls back to the OS current session (the video). With `preferredSource` empty → pure current-session behavior.
