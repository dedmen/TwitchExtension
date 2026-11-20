# EBS — Implementation Plan

## What to build
The Extension Backend Service (EBS): a small ASP.NET Core minimal API on **.NET 10**.
It stores the current track. The overlay (viewers) reads it; the desktop app (streamer's PC) writes it.
It also serves `../overlay/` as static files so local development runs from a single origin (no CORS pain).

Read `../AGENTS.md` first — architecture and Twitch constraints live there.
**Do not modify anything outside `ebs/`.** (Parallel subagents own the other folders.)

## Files to create (in this folder)
- `TwitchExtension.Ebs.csproj` — `Microsoft.NET.Sdk.Web`, `<TargetFramework>net10.0</TargetFramework>`, nullable + implicit usings enabled. No NuGet packages.
- `Track.cs` — the Track model (below).
- `Program.cs` — minimal API + static files + persistence.

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
  "updatedAt": 0
}
```
- `title`, `artist`, `album`, `source`: strings, **never null** (empty string when unknown).
- `art`: `data:` URL string (JPEG, ≤ 512 KB) or `null`.
- `state`: `"playing" | "paused" | "stopped" | "idle"` — `idle` means "no track".
- `updatedAt`: epoch **milliseconds**; set by the EBS on every successful write; `0` until first write. The EBS **ignores** any `updatedAt` in the request body.

### Endpoints
All API responses JSON with `Access-Control-Allow-Origin: *` and `Cache-Control: no-store`.

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/track` | Returns the stored Track. If nothing was ever stored, returns the idle track (all empty, `state: "idle"`, `updatedAt: 0`). |
| `PUT` | `/api/track` | Body = Track JSON (client omits `updatedAt`; `art` may be null). Auth: header `X-Push-Key` must equal env var `EBS_PUSH_KEY` (default `dev-push-key-change-me`). On success: stamp `updatedAt = now`, persist, return `200 {"ok":true}`. `401 {"error":"unauthorized"}` on missing/wrong key. `400 {"error":"bad-request"}` on invalid `state` or non-string fields. |
| `GET` | `/health` | `200` plain text `ok`. |
| `/` | (static) | Serve `../overlay/` at the site root via `PhysicalFileProvider` + `RequestPath = ""`, so `http://127.0.0.1:5080/` serves the overlay's `index.html`. Resolve the overlay directory **relative to the repo root** (parent of `ebs/`), with a fallback to `overlay/` next to the app. |

## Persistence
After each successful PUT, write the Track (pretty JSON) to `track.json` in the content root.
On startup, load it if present and valid (otherwise ignore). `track.json` is gitignored.
This keeps the last track visible across EBS restarts.

## Run
- Local: `dotnet run --project ebs --urls http://127.0.0.1:5080` (from repo root).
- Production: user's own server behind their HTTPS reverse proxy (not your concern in code).

## Acceptance (all must pass)
1. `dotnet build ebs` → 0 errors.
2. `curl -s localhost:5080/api/track` → idle track JSON.
3. `curl -s -X PUT localhost:5080/api/track -H "Content-Type: application/json" -H "X-Push-Key: dev-push-key-change-me" -d '{"title":"T","artist":"A","album":"","art":null,"source":"x","state":"playing"}'` → `{"ok":true}`
4. `curl -s localhost:5080/api/track` → shows `title: "T"`, `state: "playing"`, `updatedAt > 0`.
5. PUT without the key → `401`; PUT with `"state":"bogus"` → `400`.
6. `curl -s localhost:5080/health` → `ok`.
7. `curl -s -I localhost:5080/api/track` includes `access-control-allow-origin: *`.
8. `track.json` exists after a PUT and is restored after an EBS restart.
