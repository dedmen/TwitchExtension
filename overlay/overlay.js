// Now Playing overlay: reads the current track over a WebSocket from the EBS
// (live push, NO HTTP polling) and renders a compact tile.
// Which streamer's track to show (the "tenant") resolves from the Twitch
// extension config (settings page) -> ?tenantId= query param; without a valid
// tenant the page is a fully transparent no-op (no connection, no DOM output).
// Works inside a Twitch extension AND standalone in a normal browser —
// every window.Twitch.ext access goes through twitchExt() below, so it never throws.

(function () {
  "use strict";

  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 15000;
  const PROGRESS_TICK_MS = 250;
  const TENANT_RE = /^[A-Za-z0-9_-]{3,64}$/;

  // --- EBS URL: default from config.js, overridable via ?ebsUrl= (local testing). ---
  const params = new URLSearchParams(window.location.search);
  const EBS_URL = (params.get("ebsUrl") || DEFAULT_EBS_URL).trim().replace(/\/+$/, "");
  // wsBase: ebsUrl with the scheme rewritten http: -> ws: / https: -> wss:
  const WS_BASE = EBS_URL.replace(/^http(s?):\/\//i, function (_, s) {
    return s ? "wss://" : "ws://";
  });
  // Full WS URL — set once the tenant ID is resolved (includes the tenant).
  let WS_URL = null;

  // --- DOM ---
  const tile = document.getElementById("tile");
  const artImg = document.getElementById("artImg");
  const artPlaceholder = document.getElementById("artPlaceholder");
  const artistEl = document.getElementById("artist");
  const titleEl = document.getElementById("title");
  const nextLineEl = document.getElementById("nextLine");
  const skipBtn = document.getElementById("skipBtn");
  const progressEl = document.getElementById("progress");
  const progressFillEl = document.getElementById("progressFill");
  const timeEl = document.getElementById("time");

  // --- State ---
  let lastKey = null; // "title \u0000 artist" of the currently shown track
  let tileVisible = false;
  let currentState = "idle"; // drives progress-bar advance/hold
  // Skip lock: after a skip click the button stays disabled (dimmed) until
  // the track actually changes.
  let skipLockKey = null; // pre-click "title \u0000 artist" while locked
  let prevSkipEnabled = null; // last message's skipEnabled (false->true flip re-enables)
  // Progress anchor: re-set on EVERY track message (the EBS already applied
  // the stream delay to positionMs — no delay math here).
  let anchorPos = 0;
  let anchorAt = 0;
  let durationMs = 0;
  let lastTimeStr = null;

  // --- Guarded access to the Twitch extension helper (may not exist outside Twitch). ---
  function twitchExt() {
    try {
      return window.Twitch && window.Twitch.ext ? window.Twitch.ext : null;
    } catch (err) {
      return null;
    }
  }

  // --- Rendering ---
  // The last art URL whose image failed to load: a later message with the
  // SAME url must keep the placeholder (assigning an identical src fires no
  // load — the <img> would sit empty). A different src loads normally.
  let failedArtSrc = null;
  function setArt(art) {
    if (typeof art === "string" && art.length > 0) {
      if (art === failedArtSrc) {
        artImg.hidden = true;
        artPlaceholder.hidden = false;
        return;
      }
      artPlaceholder.hidden = true;
      if (artImg.hidden || artImg.getAttribute("src") !== art) {
        artImg.onerror = function () {
          // `art` (closure) is the URL THIS load belongs to. A slow load can
          // fail after we already moved on to a newer art URL — then the
          // error is stale: it must not touch the current image, and the
          // failed-URL set must record `art`, not the live attribute.
          if (artImg.getAttribute("src") !== art) return;
          failedArtSrc = art;
          artImg.hidden = true;
          artPlaceholder.hidden = false;
        };
        artImg.hidden = false;
        artImg.src = art;
      }
    } else {
      artImg.hidden = true;
      artPlaceholder.hidden = false;
    }
  }

  function render(track) {
    const title = typeof track.title === "string" ? track.title : "";
    const artist = typeof track.artist === "string" ? track.artist : "";
    currentState = typeof track.state === "string" ? track.state : "idle";
    const key = title + "\u0000" + artist;

    // --- Progress anchor: every track message re-anchors (reset on new track). ---
    anchorPos = Math.max(0, Number(track.positionMs) || 0);
    anchorAt = Date.now();
    durationMs = Math.max(0, Number(track.durationMs) || 0);
    progressEl.hidden = !(durationMs > 0);
    // Inline time on the artist row: shown whenever a track is shown.
    timeEl.hidden = false;
    lastTimeStr = null; // force a refresh on the next paint
    updateProgress(); // paint immediately, don't wait for the next tick

    // --- Next track: non-null object -> third line; null -> no line, no layout change. ---
    const next = track.next && typeof track.next === "object" ? track.next : null;
    if (next) {
      nextLineEl.textContent =
        "Next: " + (typeof next.artist === "string" ? next.artist : "") +
        " \u2013 " + (typeof next.title === "string" ? next.title : "");
      nextLineEl.hidden = false;
    } else {
      nextLineEl.hidden = true;
    }

    // --- Skip button: only when skipEnabled. A locked button (post-skip)
    // re-enables once the track actually changes (a different title+artist)
    // or when skipEnabled flips back to true after being false. ---
    const skipEnabledNow = track.skipEnabled === true;
    if (skipLockKey !== null && currentState !== "idle" && title !== "") {
      if (key !== skipLockKey || (skipEnabledNow && prevSkipEnabled === false)) {
        skipLockKey = null;
        skipBtn.disabled = false;
      }
    }
    prevSkipEnabled = skipEnabledNow;
    skipBtn.hidden = !skipEnabledNow;

    // No track to show: hide the tile entirely.
    if (currentState === "idle" || title === "") {
      if (tileVisible) {
        tile.hidden = true;
        tileVisible = false;
      }
      lastKey = null;
      timeEl.hidden = true;
      return;
    }

    if (!tileVisible) {
      tile.hidden = false;
      tileVisible = true;
      if (pendingStored) {
        // First visibility: restore the stored position (clamped to the
        // viewport with the tile's real size).
        applyPos(pendingStored.x, pendingStored.y);
        pendingStored = null;
      } else if (customPos) {
        applyPos(customPos.x, customPos.y); // re-clamp after a size change
      }
    }

    artistEl.textContent = artist;
    titleEl.textContent = title;
    setArt(track.art);
    tile.classList.toggle("paused", currentState === "paused");

    // Track change (title + artist): retrigger the ~300 ms fade/slide-in.
    if (key !== lastKey) {
      lastKey = key;
      tile.classList.remove("anim-in");
      void tile.offsetWidth; // force reflow so the animation restarts
      tile.classList.add("anim-in");
    }
  }

  // "m:ss" (or "h:mm:ss" past the hour) for the clocks.
  function fmtClock(ms) {
    const s = Math.max(0, Math.floor(ms / 1000));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    const pad = (n) => String(n).padStart(2, "0");
    return h > 0 ? h + ":" + pad(m) + ":" + pad(sec) : m + ":" + pad(sec);
  }

  // Advance the bar + elapsed clock locally: anchorPos + elapsed while
  // playing, held at anchorPos otherwise; the bar clamps to durationMs (100 %).
  function updateProgress() {
    const raw = currentState === "playing" ? anchorPos + (Date.now() - anchorAt) : anchorPos;
    const pos = durationMs > 0 ? Math.min(raw, durationMs) : raw;

    if (durationMs > 0) {
      progressFillEl.style.width = (100 * pos / durationMs).toFixed(3) + "%";
    }

    // Time on the artist row: "elapsed" or "elapsed / total" (total only
    // when durationMs is known). Only rewrite when the displayed second changes.
    const timeStr = fmtClock(pos) + (durationMs > 0 ? " / " + fmtClock(durationMs) : "");
    if (timeStr !== lastTimeStr) {
      lastTimeStr = timeStr;
      timeEl.textContent = timeStr;
    }
  }

  function setStale(stale) {
    tile.classList.toggle("stale", stale);
  }

  // --- WebSocket: current track on connect, then one message per change
  // (for this tenant only). On close/error: keep the last tile, dim it
  // (stale), reconnect with exponential backoff (1 s, doubling, capped at
  // 15 s), reset on connect. NO polling fallback. ---
  let socket = null;
  let reconnectTimer = null;
  let attempt = 0;

  function openSocket() {
    let ws;
    try {
      ws = new WebSocket(WS_URL);
    } catch (err) {
      console.warn("[now-playing] WS create failed:", err);
      scheduleReconnect();
      return;
    }
    socket = ws;

    ws.onopen = function () {
      attempt = 0; // successful connect resets the backoff
      if (reconnectTimer !== null) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      console.info("[now-playing] WS connected: " + WS_URL);
    };

    ws.onmessage = function (event) {
      let track;
      try {
        track = JSON.parse(event.data);
      } catch (err) {
        return; // ignore non-JSON messages
      }
      setStale(false); // first message after (re)connect clears stale
      render(track);
    };

    ws.onerror = function () {
      // Browsers always fire onclose right after onerror; the close
      // handler drives the stale state and the reconnect.
    };

    ws.onclose = function (event) {
      socket = null;
      console.info("[now-playing] WS closed (code " + event.code + "); keeping last tile");
      setStale(true); // keep the last known tile dimmed — no polling fallback
      scheduleReconnect();
    };
  }

  function scheduleReconnect() {
    if (reconnectTimer !== null) return; // a retry is already pending
    const delay = Math.min(RECONNECT_MAX_MS, RECONNECT_BASE_MS * Math.pow(2, attempt));
    attempt += 1;
    console.info("[now-playing] WS reconnect in " + delay + " ms (attempt " + attempt + ")");
    reconnectTimer = setTimeout(function () {
      reconnectTimer = null;
      openSocket();
    }, delay);
  }

  // --- Skip button: fire-and-forget {"cmd":"next"} on the existing track WS. ---
  // A click right after a drag is not a real click — the flag below
  // swallows it (set by the drag code, cleared on the next pointerdown).
  let suppressClick = false;
  skipBtn.addEventListener("click", function () {
    if (suppressClick) {
      suppressClick = false;
      return;
    }
    if (socket && socket.readyState === WebSocket.OPEN) {
      try {
        socket.send('{"cmd":"next"}');
        // Skip lock: keep the button disabled (dimmed) until the track
        // actually changes — render() releases it on a different key or a
        // skipEnabled false->true flip.
        skipLockKey = lastKey;
        skipBtn.disabled = true;
      } catch (err) {
        // fire-and-forget; a failed send dies with the socket's close handler
      }
    }
  });

  // --- Drag: the streamer can drag the tile anywhere on the screen.
  // Pointer-based (setPointerCapture), with a 4 px move threshold so a
  // tiny accidental nudge never moves the tile, and a click-suppression
  // guard so releasing over the skip button can't trigger a skip. The
  // dropped position persists to localStorage ("tilePos", {x, y},
  // absolute viewport coordinates) and is restored on load; the default
  // bottom-left applies only when nothing is stored. On load and on
  // resize the tile is clamped fully inside the viewport. The skip
  // button itself is not a drag target. ---
  const POS_KEY = "tilePos";
  const DRAG_THRESHOLD_PX = 4;
  let customPos = null; // {x, y} once a stored/dropped position is in effect
  let pendingStored = null; // original stored {x, y}; applied when the tile first
                            // becomes visible (only then does it have a real size
                            // to clamp against — hidden tiles measure as 0)

  function readStoredPos() {
    try {
      const raw = window.localStorage.getItem(POS_KEY);
      if (raw) {
        const p = JSON.parse(raw);
        if (p && Number.isFinite(p.x) && Number.isFinite(p.y)) return { x: p.x, y: p.y };
      }
    } catch (err) {
      /* corrupt/absent -> default position */
    }
    return null;
  }

  function storePos(pos) {
    try {
      window.localStorage.setItem(POS_KEY, JSON.stringify(pos));
    } catch (err) {
      /* storage unavailable -> the position simply won't persist */
    }
  }

  // Position the tile at (x, y), clamped fully inside the viewport.
  function applyPos(x, y) {
    const w = tile.offsetWidth || 320;
    const h = tile.offsetHeight || 80;
    const p = {
      x: Math.max(0, Math.min(x, Math.max(0, window.innerWidth - w))),
      y: Math.max(0, Math.min(y, Math.max(0, window.innerHeight - h))),
    };
    tile.style.left = Math.round(p.x) + "px";
    tile.style.top = Math.round(p.y) + "px";
    tile.style.bottom = "auto";
    customPos = p;
  }

  function initDrag() {
    // Restoring is deferred to the first time the tile becomes visible:
    // a hidden (display:none) tile has no size, so clamping now would use
    // a guess. applyPos() at first render clamps against the real size.
    pendingStored = readStoredPos();

    let drag = null; // {id, startX, startY, rect, moved}

    tile.addEventListener("pointerdown", function (e) {
      // A new gesture starts: clear any stale suppression flag BEFORE any
      // early return — otherwise a drag that ended over the button would
      // eat the next real click (S9).
      suppressClick = false;
      if (!e.isPrimary || e.button !== 0) return;
      if (e.target.closest && e.target.closest(".skip-btn")) return; // not a drag target
      drag = {
        id: e.pointerId,
        startX: e.clientX,
        startY: e.clientY,
        rect: tile.getBoundingClientRect(),
        moved: false,
      };
      try {
        tile.setPointerCapture(e.pointerId);
      } catch (err) {
        /* drag still works without capture; click suppression is belt-and-braces */
      }
    });

    tile.addEventListener("pointermove", function (e) {
      if (!drag || e.pointerId !== drag.id) return;
      const dx = e.clientX - drag.startX;
      const dy = e.clientY - drag.startY;
      if (!drag.moved) {
        if (Math.sqrt(dx * dx + dy * dy) < DRAG_THRESHOLD_PX) return;
        drag.moved = true;
        tile.classList.add("dragging");
      }
      applyPos(drag.rect.left + dx, drag.rect.top + dy);
    });

    const endDrag = function (e) {
      if (!drag || e.pointerId !== drag.id) return;
      const moved = drag.moved;
      drag = null;
      tile.classList.remove("dragging");
      // A release over the skip button (the press started on the tile
      // body) must not trigger a skip; so must a click after a real drag.
      const under = document.elementFromPoint(e.clientX, e.clientY);
      if (moved || (under && under.closest && under.closest(".skip-btn"))) {
        suppressClick = true;
      }
      if (moved && customPos) storePos(customPos);
    };
    tile.addEventListener("pointerup", endDrag);
    tile.addEventListener("pointercancel", endDrag);

    // Belt-and-braces at the tile level too (the button handler has the
    // same guard; it runs first in the bubble chain).
    tile.addEventListener("click", function (e) {
      if (suppressClick) {
        suppressClick = false;
        e.preventDefault();
        e.stopPropagation();
      }
    });

    window.addEventListener("resize", function () {
      if (customPos) applyPos(customPos.x, customPos.y); // re-clamp to the new viewport
    });
  }

  // --- Start: only once a valid tenant ID is known (else: no-op, see below). ---
  function start(tenantId) {
    WS_URL = WS_BASE + "/api/track/ws/" + tenantId.toLowerCase();
    console.info("[now-playing] tenant " + tenantId.toLowerCase() + ", EBS " + EBS_URL + " (WS " + WS_URL + ")");
    openSocket();
    setInterval(updateProgress, PROGRESS_TICK_MS);
  }

  function noTenant() {
    console.warn(
      "[now-playing] no valid tenantId (3-64 chars of A-Z a-z 0-9 _ -); " +
        "no connection, page stays fully transparent"
    );
  }

  function validTenantId(v) {
    return typeof v === "string" && TENANT_RE.test(v);
  }

  // Parse the config-service broadcaster segment (JSON: {tenantId}).
  function tenantFromConfigRecord(record) {
    if (!record || typeof record.content !== "string") return "";
    try {
      const parsed = JSON.parse(record.content);
      return parsed && typeof parsed.tenantId === "string" ? parsed.tenantId.trim() : "";
    } catch (err) {
      return "";
    }
  }

  // Tenant ID resolution, in order:
  //   1. the Twitch config service (Twitch.ext.configuration — the broadcaster
  //      segment the settings page writes) — 2. ?tenantId= query param —
  //   3. neither/invalid: warn once, connect nothing, render nothing.
  // Docs: configuration.onChanged fires once, on load, when the config is
  // received; a 3 s fallback covers a non-firing event (reads the record
  // directly in that case).
  function resolveTenant() {
    const fromParam = (params.get("tenantId") || "").trim();
    const ext = twitchExt();
    const cfg = ext && ext.configuration && typeof ext.configuration.onChanged === "function"
      ? ext.configuration
      : null;
    if (!cfg) {
      // Plain browser (no extension helper): the query param is the only source.
      if (validTenantId(fromParam)) {
        start(fromParam);
      } else {
        noTenant();
      }
      return;
    }
    let settled = false;
    const decide = function () {
      if (settled) return;
      settled = true;
      const fromCfg = tenantFromConfigRecord(cfg.broadcaster);
      if (validTenantId(fromCfg)) {
        start(fromCfg);
      } else if (validTenantId(fromParam)) {
        start(fromParam);
      } else {
        noTenant();
      }
    };
    const fallback = setTimeout(decide, 3000);
    cfg.onChanged(function () {
      clearTimeout(fallback);
      decide();
    });
  }

  initDrag();
  resolveTenant();
})();
