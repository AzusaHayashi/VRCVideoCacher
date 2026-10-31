/**
 * browser-extension/background.mjs — MV3 service worker (Chrome) / event page (Firefox).
 *
 * Upstream listeners are preserved:
 *   - runtime.onMessage("send_youtube_cookies")  -> delayed getYoutubeCookies()
 *   - cookies.onChanged                          -> getYoutubeCookiesDebounce()
 *   - runtime.onInstalled                        -> initial sync
 *
 * Additions (this project):
 *   - persist the last sync outcome to chrome.storage.local under "syncStatus"
 *     (never cookie data — only counts/flags/error codes, CONTRACT §8)
 *   - badge: green "OK" = synced + cookie file written, amber "!" = host accepted but
 *     file not written, red "ERR" = failure. A single module-level timer clears it,
 *     so repeated syncs cannot stack timers.
 *   - options page wiring: "cookiebridge.sync-now" / "cookiebridge.status" messages,
 *     and clicking the toolbar icon opens options.
 */

import {
  getYoutubeCookies,
  getYoutubeCookiesDebounce,
  setSyncStatusListener,
  NATIVE_HOST_NAME,
} from "./utils.mjs";

/** chrome.storage.local key shared with options.js. Must stay in sync with options.js. */
const STATUS_KEY = "syncStatus";

/** Badge auto-clear delay. One timer, always cleared before re-arming. */
const BADGE_CLEAR_MS = 60 * 1000;

const BADGE_GREEN = "#1f9d55";
const BADGE_AMBER = "#b7791f";
const BADGE_RED = "#d93025";

// ---------------------------------------------------------------------------
// Badge
// ---------------------------------------------------------------------------

function getActionApi() {
  const g = globalThis;
  return (
    g.chrome?.action ??
    g.browser?.action ??
    g.chrome?.browserAction ??
    g.browser?.browserAction ??
    null
  );
}

let badgeClearTimer = null;

function scheduleBadgeClear() {
  if (badgeClearTimer !== null) {
    clearTimeout(badgeClearTimer);
  }
  badgeClearTimer = setTimeout(() => {
    badgeClearTimer = null;
    const action = getActionApi();
    if (!action?.setBadgeText) return;
    try {
      action.setBadgeText({ text: "" })?.catch?.(() => {});
    } catch {
      // Badge is cosmetic; never let it break a sync.
    }
  }, BADGE_CLEAR_MS);
  // Node's timer object exposes unref(); browsers do not, hence the optional call.
  // A cosmetic badge clear must never keep a worker alive (unit tests rely on this).
  badgeClearTimer?.unref?.();
}

function renderBadge(record) {
  const action = getActionApi();
  if (!action?.setBadgeText) return;

  let text;
  let color;
  if (record.ok && record.targetWritten) {
    text = "OK";
    color = BADGE_GREEN;
  } else if (record.ok) {
    text = "!";
    color = BADGE_AMBER;
  } else {
    text = "ERR";
    color = BADGE_RED;
  }

  try {
    action.setBadgeText({ text })?.catch?.(() => {});
    action.setBadgeBackgroundColor?.({ color })?.catch?.(() => {});
  } catch {
    return;
  }
  scheduleBadgeClear();
}

// ---------------------------------------------------------------------------
// Status persistence (chrome.storage.local)
//
// Whitelisted fields only: a hostile or buggy status object can never push cookie
// text into storage, because we copy field by field.
// ---------------------------------------------------------------------------

function toRecord(status) {
  const ok = status?.ok === true;
  const errorDetail =
    !ok && typeof status?.errorDetail === "string" && status.errorDetail.length > 0
      ? status.errorDetail.slice(0, 200)
      : null;
  return {
    lastSyncUtc: new Date().toISOString(),
    capturedUtc: typeof status?.capturedUtc === "string" ? status.capturedUtc : null,
    ok,
    targetWritten: status?.targetWritten === true,
    vaultUpdated: status?.vaultUpdated === true,
    cookieCount: Number.isInteger(status?.cookieCount) ? status.cookieCount : null,
    exportedCookieRecords: Number.isInteger(status?.exportedCookieRecords)
      ? status.exportedCookieRecords
      : null,
    loginCookiePresent: status?.loginCookiePresent === true,
    applied: typeof status?.applied === "string" ? status.applied : null,
    error: ok ? null : String(status?.error ?? "unknown").slice(0, 80),
    errorDetail,
    elapsedMs: Number.isFinite(status?.elapsedMs) ? status.elapsedMs : null,
    browser: typeof status?.browser === "string" ? status.browser : null,
    nativeHost: NATIVE_HOST_NAME,
  };
}

async function persistRecord(record) {
  try {
    await chrome.storage.local.set({ [STATUS_KEY]: record });
  } catch {
    // Storage failures must not abort the sync path.
  }
  return record;
}

/** Sink registered with utils.mjs: every sync (auto or manual) lands here. */
async function handleSyncStatus(status) {
  const record = toRecord(status);
  await persistRecord(record);
  renderBadge(record);
  return record;
}

setSyncStatusListener((status) => {
  void handleSyncStatus(status);
});

// ---------------------------------------------------------------------------
// Upstream listeners
// ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg === "send_youtube_cookies") {
    setTimeout(() => {
      getYoutubeCookies();
    }, 2000);
    return;
  }

  // Options page: manual "立即同步".
  if (msg && msg.type === "cookiebridge.sync-now") {
    getYoutubeCookies()
      .then((status) => sendResponse({ ok: true, status: toRecord(status) }))
      .catch((error) =>
        sendResponse({
          ok: false,
          error: String(error?.message ?? error ?? "unknown").slice(0, 200),
        }),
      );
    return true; // async response
  }

  // Options page: initial render without triggering a sync.
  if (msg && msg.type === "cookiebridge.status") {
    chrome.storage.local
      .get(STATUS_KEY)
      .then((items) => sendResponse({ ok: true, status: items?.[STATUS_KEY] ?? null }))
      .catch(() => sendResponse({ ok: false, status: null }));
    return true; // async response
  }

  return undefined;
});

chrome.cookies.onChanged.addListener((changeInfo) =>
  getYoutubeCookiesDebounce(changeInfo),
);

chrome.runtime.onInstalled.addListener(async () => {
  console.log("Extension installed, fetching YouTube cookies...");
  await getYoutubeCookies();
});

// ---------------------------------------------------------------------------
// Toolbar icon -> options page (no popup is declared in the manifest).
// ---------------------------------------------------------------------------

try {
  getActionApi()?.onClicked?.addListener(() => {
    try {
      chrome.runtime.openOptionsPage?.()?.catch?.(() => {});
    } catch {
      // Older Firefox builds expose runtime.openOptionsPage differently; ignore.
    }
  });
} catch {
  // No action API in this browser profile; badge/options still work via chrome://extensions.
}
