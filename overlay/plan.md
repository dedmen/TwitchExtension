# Overlay — Implementation Plan

## What to build
A **static HTML/JS** Twitch overlay extension (no framework, no build step) that shows a compact
"now playing" music tile (album art, artist, title) in the corner of the stream for one streamer
(the **tenant**), fed live by a WebSocket connection to the EBS. Read `../AGENTS.md` first — it
carries the hard Twitch constraints (Extension Helper script, transparent background, CSP allowlist).
**Do not modify anything outside `overlay/`.** (Parallel subagents own the other folders.)

## Files to create (in this folder)
- `index.html` — **must** load the Extension Helper: `<script src="https://extension-files.twitch.tv/helper/v1/twitch-ext.min.js"></script>`; then `config.js`, `overlay.css`, `overlay.js`.
- `config.js` — `const DEFAULT_EBS_URL = "https://twitch-extension.india.dedmen.de";`. The URL can be overridden at runtime via the `?ebsUrl=` query param (used for local testing).
- `settings.html` — the extension's **Configuration Path** page (the version's Configuration Path in the dev console points at it): loads the Extension Helper + `settings.js`; one input for the **tenant ID** (the streamer's unique ID — the same value as the desktop app's `settings.json` `tenantId`). Plain page (it renders in Twitch's settings UI, not over the video — a normal dark background matching the tile palette is fine).
- `settings.js` — the settings page logic, in an **external file on purpose**: Twitch's hosted CSP `script-src` has **no `unsafe-inline`**, so inline `<script>` blocks in overlay HTML never execute. Uses the **current config-service API**: prefill via `configuration.onChanged` → `broadcaster.content` (JSON `{tenantId}`); Save → `configuration.set("broadcaster", "1", JSON.stringify({tenantId: value}))` (fire-and-forget — verified on reopen). Polls ~5 s for the API to attach, then shows a status line reporting exactly what is missing (e.g. the dev-console "Configuration Service" capability is off). Validate `^[A-Za-z0-9_-]{3,64}$` with an inline error. Must not throw in a plain browser (guard `window.Twitch.ext`).
- `overlay.css` — styling. Page background **must be transparent** (the overlay renders on top of the video).
- `overlay.js` — WebSocket client + rendering.

## Contract
**Tenant ID** (which streamer's track to show) resolves in this order:
1. The Twitch **config service** — `Twitch.ext.configuration`: `onChanged` (fires once on load) → parse `broadcaster.content` (JSON `{tenantId}`); a 3 s fallback reads `broadcaster` directly in case the event doesn't fire. Set by the streamer on the settings page.
2. `?tenantId=` query param (local testing).
3. Neither, or the value fails `^[A-Za-z0-9_-]{3,64}$` → **no connection, no tile at all** (fully transparent no-op), `console.warn` once.

Connect to `WebSocket {wsUrl}/api/track/ws/{tenantId}` (tenantId lowercased), where `wsUrl` is `ebsUrl` with the scheme rewritten `http:` → `ws:` / `https:` → `wss:`. No auth. The server sends that tenant's current Track JSON immediately on connect, then one message per track change **for that tenant** — live push, no polling (decided 2026-11-20). Track JSON — canonical definition in `../ebs/plan.md`:
`title`, `artist`, `album`, `art` (data URL or null), `source`, `state` (`playing|paused|stopped|idle`), `updatedAt` (epoch ms), plus: `positionMs` (int ≥ 0 — position sampled at push time, **stream delay already applied by the EBS**), `durationMs` (int, 0 when unknown), `skipEnabled` (bool), `next` (`{title, artist}` or null).

## Behavior
- No tenant ID (or invalid) → do nothing at all: no WS connection, no DOM output, page fully transparent.
- `state === "idle"` or `title === ""` → hide the tile entirely (`display: none`).
- Otherwise show: album art (or a music-note placeholder when `art` is null), artist (small, letter-spaced), title (bold, single-line ellipsis). `state === "paused"` → dim the art and show a small `PAUSED` tag.
- Track change (compare `title + artist`): short CSS fade/slide-in, ~300 ms.
- **Progress**: a 2 px bar along the bottom edge of the card, shown only when `durationMs > 0`. The overlay advances it locally — no per-second WS messages. On each track message received at local time `T`: `anchorPos = positionMs`, `anchorAt = T`. While `state === "playing"`, displayed position = `anchorPos + (now - anchorAt)` (tick ~250 ms); while `state` is not `playing` it holds at `anchorPos`. Clamp at `durationMs` → 100 %. On a new track, reset the anchor.
- WS `close`/`error` → keep the last known tile but add a `stale` class (opacity ~0.45); reconnect with exponential backoff (1 s, doubling, capped at 15 s), reset on successful connect. The first message after (re)connect clears it. **No HTTP polling fallback** — if the socket can't connect, the tile stays hidden (idle) or stale-dimmed.
- **Skip button**: shown only when `skipEnabled === true` — a small inline-SVG skip icon at the right edge of the tile (`pointer-events: auto`). Click → send the text frame `{"cmd":"next"}` on the existing track WS connection (no HTTP), fire-and-forget.
- **Next track**: when `next` is non-null → a small third line under the title: `Next: {artist} – {title}` (11 px, muted, single-line ellipsis). When null → no line and no layout change.
- **Time display**: inline on the artist row, fixed at the right end — `elapsed / total` (10 px, muted, tabular numerals, `m:ss` / `h:mm:ss`). Elapsed advances with the progress anchor (same rule as the bar); total is `durationMs`, shown only when known (> 0) — with an unknown duration the row shows elapsed only. No card height change.
- **Drag**: the streamer can drag the tile anywhere on screen (pointer events; drag target is the tile, **excluding** the skip button). The dropped position is persisted to `localStorage` (key `tilePos`, `{x, y}` absolute viewport coordinates) and restored on load; the default bottom-left applies only when nothing is stored. On `resize` and on load, the tile is clamped so it stays fully inside the viewport.
- The `positionMs` in a track message is **already the true current position** (the EBS computes it at serve time from the timeline fields — see `../ebs/plan.md`), so the overlay just anchors on it as before; no client-side UTC math.
- Must work standalone in a normal browser (e.g. `http://127.0.0.1:5080/?tenantId=local` served by the EBS) — **guard every `window.Twitch.ext` access** so it never throws outside Twitch.

## Look & feel
- Bottom-left corner, 12 px from the edges. Compact card: ~56 px rounded album art on the left, two text lines on the right.
- Dark translucent card background (`rgba(10, 10, 14, 0.72)`), white text, system-ui font, 12 px radius. Readable over any video.
- The tile is compact (≤ ~360 px wide; ≤ ~90 px tall without the next-track line, ≤ ~108 px with it; ≤ 40% viewport width on mobile). No gradients. The progress bar is a solid 2 px accent line (no gradient, no animation beyond the width transition).
- Page-level CSS: `pointer-events: none` on the page, `pointer-events: auto` only on the tile — so video controls stay clickable around it.

## Acceptance (all must pass)
1. EBS running, open `http://127.0.0.1:5080/?tenantId=local` → page loads, no tile (idle). Open `http://127.0.0.1:5080/` without `tenantId` (and no Twitch config) → fully transparent no-op, no console errors.
2. A desktop-channel WS test client on `/api/desktop/ws/local` sends a track frame `{"type":"track","title":"Fake Song","artist":"Fake Artist","album":"","art":"data:image/jpeg;base64,/9j/4AAQSkZJRg==","source":"test","state":"playing"}` → within ~1 s the tile appears with art, "FAKE ARTIST", "Fake Song" (live WS push).
3. Push the idle track (or one with `title:""`) → tile disappears (within ~1 s).
4. Kill the EBS → the last tile is dimmed (`stale`); restart the EBS → the tile recovers on WS reconnect (backoff from 1 s).
5. In a Twitch **Local Test** session (Base URI → EBS URL, `?tenantId=` supplied or config set), the tile renders over the video with no page background.
6. A track push with `state: "playing"`, `positionMs: 30000`, `durationMs: 180000` → the progress bar starts at ~17 % and advances in real time (~20 % after 5 s, no jump-back); the same track with `state: "paused"` → the bar freezes.
7. A push with `skipEnabled: true` → the skip button is visible; clicking it sends the `{"cmd":"next"}` frame over the WS (verifiable with a stub or the real EBS). A push with `skipEnabled: false` → the button is hidden.
8. A push with `next: {"title":"N","artist":"NA"}` → the "Next: NA – N" line appears; `next: null` → it's gone and the tile is back to the 2-line height.
9. A push with `positionMs: 30000`, `durationMs: 180000` → the artist row reads `0:30 / 3:00` and the elapsed part advances while playing; `durationMs: 0` → elapsed only (no `/ …`), keeps advancing.
10. **Isolation**: two tabs open, `?tenantId=local` and `?tenantId=other` — push a track to `local` → the tile appears only in the first tab; `other`'s tab stays empty.
11. `settings.html` opened in a plain browser does not throw (Twitch.ext guarded), shows the input, and rejects an invalid tenant ID with the inline error; in a Twitch Local Test with the Configuration Path set, the tenant ID survives a save + reopen round-trip.
12. **Drag**: drag the tile to mid-screen and release → it stays; reload → restored from `localStorage`; shrink the browser window → the tile is clamped fully inside the viewport (no overflow). The skip button still works after a drag.
