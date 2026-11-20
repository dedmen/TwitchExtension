# Overlay — Implementation Plan

## What to build
A **static HTML/JS** Twitch overlay extension (no framework, no build step) that shows a compact
"now playing" music tile (album art, artist, title) in the corner of the stream, polling the EBS
for the current track. Read `../AGENTS.md` first — it carries the hard Twitch constraints
(Extension Helper script, transparent background, CSP allowlist).
**Do not modify anything outside `overlay/`.** (Parallel subagents own the other folders.)

## Files to create (in this folder)
- `index.html` — **must** load the Extension Helper: `<script src="https://extension-files.twitch.tv/helper/v1/twitch-ext.min.js"></script>`; then `config.js`, `overlay.css`, `overlay.js`.
- `config.js` — `const DEFAULT_EBS_URL = "https://<your-ebs-domain>"; // TODO: replace when the EBS is deployed`. The URL can be overridden at runtime via the `?ebsUrl=` query param (used for local testing).
- `overlay.css` — styling. Page background **must be transparent** (the overlay renders on top of the video).
- `overlay.js` — polling + rendering.

## Contract
Poll `GET {ebsUrl}/api/track` every **5 s** (4 s fetch timeout). Track JSON — canonical definition in `../ebs/plan.md`:
`title`, `artist`, `album`, `art` (data URL or null), `source`, `state` (`playing|paused|stopped|idle`), `updatedAt` (epoch ms).

## Behavior
- `state === "idle"` or `title === ""` → hide the tile entirely (`display: none`).
- Otherwise show: album art (or a music-note placeholder when `art` is null), artist (small, letter-spaced), title (bold, single-line ellipsis). `state === "paused"` → dim the art and show a small `PAUSED` tag.
- Track change (compare `title + artist`): short CSS fade/slide-in, ~300 ms.
- 3 consecutive failed polls → keep the last known tile but add a `stale` class (opacity ~0.45). Any success clears it.
- Must work standalone in a normal browser (e.g. `http://127.0.0.1:5080/` served by the EBS) — **guard every `window.Twitch.ext` access** so it never throws outside Twitch.

## Look & feel
- Bottom-left corner, 12 px from the edges. Compact card: ~56 px rounded album art on the left, two text lines on the right.
- Dark translucent card background (`rgba(10, 10, 14, 0.72)`), white text, system-ui font, 12 px radius. Readable over any video.
- The tile is compact (≤ ~360 px wide, ≤ ~90 px tall on web; ≤ 40% viewport width on mobile). No full-width bars, no gradients.
- Page-level CSS: `pointer-events: none` on the page, `pointer-events: auto` only on the tile — so video controls stay clickable around it.

## Acceptance (all must pass)
1. EBS running, open `http://127.0.0.1:5080/` → page loads, no tile (idle).
2. `curl -s -X PUT localhost:5080/api/track -H "Content-Type: application/json" -H "X-Push-Key: dev-push-key-change-me" -d '{"title":"Fake Song","artist":"Fake Artist","album":"","art":"data:image/jpeg;base64,/9j/4AAQSkZJRg==","source":"test","state":"playing"}'` → within ~6 s the tile appears with art, "FAKE ARTIST", "Fake Song".
3. PUT the idle track → tile disappears.
4. Kill the EBS → after ~15 s the last tile is dimmed (`stale`); restart the EBS → normal again.
5. In a Twitch **Local Test** session (Base URI → EBS URL), the tile renders over the video with no page background.
