// Settings page (Configuration Path) logic — external file on purpose:
// Twitch's hosted CSP has no 'unsafe-inline' for script-src, so all JS
// must live in external .js files. See AGENTS.md (Twitch constraints).
//
// Uses the CURRENT config-service API (Twitch.ext.configuration.set /
// .broadcaster / .onChanged). The legacy setConfigurationValue/getConfiguration
// are NOT exposed unless the extension's "Configuration Service" capability is
// enabled in the dev console (Capabilities tab) — the status line below
// reports exactly what is missing when it isn't.
(function () {
  "use strict";

  var TENANT_RE = /^[A-Za-z0-9_-]{3,64}$/;
  var CONFIG_VERSION = "1";
  var input = document.getElementById("tenantId");
  var errorEl = document.getElementById("error");
  var savedEl = document.getElementById("saved");
  var saveBtn = document.getElementById("save");
  var plainNote = document.getElementById("plainNote");
  var statusEl = document.getElementById("status");
  var ready = false;

  // Guarded: outside Twitch window.Twitch may not exist at all.
  function twitchExt() {
    try {
      return window.Twitch && window.Twitch.ext ? window.Twitch.ext : null;
    } catch (err) {
      return null;
    }
  }

  function configApi() {
    var e = twitchExt();
    return e && e.configuration && typeof e.configuration.set === "function" ? e.configuration : null;
  }

  function extState() {
    var t;
    try { t = window.Twitch; } catch (err) { t = undefined; }
    var e = twitchExt();
    var c = e && e.configuration ? e.configuration : null;
    return "window.Twitch=" + (t ? "defined" : "undefined") +
      ", ext=" + (e ? "defined" : "undefined") +
      ", configuration=" + (c ? "defined" : "undefined") +
      ", config.set=" + (c && typeof c.set === "function" ? "function" : "missing");
  }

  function showError(msg) {
    if (msg) {
      errorEl.textContent = msg;
      errorEl.hidden = false;
      input.style.borderColor = "#f07167";
    } else {
      errorEl.hidden = true;
      input.style.borderColor = "";
    }
  }

  // Prefill from the broadcaster segment (JSON: {tenantId}).
  function prefill(cfg) {
    if (typeof cfg.onChanged !== "function") return;
    cfg.onChanged(function () {
      try {
        var rec = cfg.broadcaster;
        if (rec && typeof rec.content === "string") {
          var parsed = JSON.parse(rec.content);
          if (parsed && typeof parsed.tenantId === "string" && parsed.tenantId) {
            input.value = parsed.tenantId;
          }
        }
      } catch (err) {
        /* prefill is best-effort */
      }
    });
  }

  // The Helper may attach its API after our first pass — poll ~5 s, then
  // report exactly what (if anything) is missing.
  function pollReady(retries) {
    var c = configApi();
    if (c) {
      ready = true;
      plainNote.hidden = true;
      statusEl.textContent = "Twitch extension context: ready.";
      prefill(c);
      return;
    }
    if (retries <= 0) {
      plainNote.hidden = false;
      statusEl.textContent = "Twitch extension context NOT detected — " + extState() +
        ". Enable the \"Configuration Service\" capability in the dev console (Capabilities tab), then re-test.";
      return;
    }
    setTimeout(function () { pollReady(retries - 1); }, 400);
  }

  saveBtn.addEventListener("click", function () {
    var value = input.value.trim();
    if (!TENANT_RE.test(value)) {
      showError("Invalid tenant ID — use 3-64 characters: A-Z, a-z, 0-9, _ or -.");
      savedEl.hidden = true;
      return;
    }
    showError(null);
    savedEl.hidden = true;
    var c = configApi();
    if (!c) {
      plainNote.hidden = false;
      statusEl.textContent = "Not ready yet — " + extState();
      return;
    }
    saveBtn.disabled = true;
    savedEl.textContent = "Saving…";
    savedEl.hidden = false;
    try {
      // configuration.set is fire-and-forget (void); the value is verified
      // on the next open of this page (prefill via onChanged).
      c.set("broadcaster", CONFIG_VERSION, JSON.stringify({ tenantId: value }));
      savedEl.textContent = "Saved";
    } catch (err) {
      savedEl.hidden = true;
      showError("Save failed: " + (err && err.message ? err.message : err));
    }
    saveBtn.disabled = false;
  });

  pollReady(12); // 12 × 400 ms ≈ 5 s
})();
