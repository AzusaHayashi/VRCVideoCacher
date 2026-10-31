/**
 * browser-extension/utils.mjs
 *
 * Upstream code (VRCVideoCacher "Cookies Exporter" v2.0.1 — utils.mjs) is kept
 * VERBATIM in behaviour:
 *   - getAllYouTubeCookies()      (incl. partitionKey compat fallback, cookie store handling)
 *   - getCurrentCookieStoreId()   (incognito "split" -> undefined, Firefox tab.cookieStoreId,
 *                                  Chrome cookie store lookup by tabIds)
 *   - the Netscape serializer    (7 tab separated fields, domain forced to "." prefix,
 *                                  literal "TRUE" includeSubdomains, secure mapping,
 *                                  Math.floor(expirationDate || 0))
 *
 * The ONLY behavioural change vs upstream: the transport.
 *   upstream:  HTTP POST text/plain to VRCVideoCacher's local API (127.0.0.1:9696)
 *   here:      chrome.runtime.sendNativeMessage("com.vrcvideocacher.cookiebridge", {...})
 *
 * Contract: docs/CONTRACT.md §4 (Netscape text) and §5 (Native Messaging protocol).
 *
 * Hard rule: this module never logs a cookie name/value pair, and never puts cookie
 * text into an error message or into chrome.storage.
 */

/** Native host name, see CONTRACT §5. */
export const NATIVE_HOST_NAME = "com.vrcvideocacher.cookiebridge";

/** sendNativeMessage budget. Host must answer well inside this (CONTRACT §5, 1 MiB limit). */
export const NATIVE_MESSAGE_TIMEOUT_MS = 20000;

/** Error text is truncated before it ever reaches storage/UI. */
const MAX_ERROR_DETAIL = 200;

// ---------------------------------------------------------------------------
// Upstream debounce helper (unchanged)
// ---------------------------------------------------------------------------

let debounceTimer = null;
const debounce = (func, delay) => {
  return (...args) => {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => func.apply(this, args), delay);
  };
};

// ---------------------------------------------------------------------------
// Sync result plumbing
//
// Upstream had no status channel; background.mjs registers a listener here so it can
// persist the result to chrome.storage.local and paint the badge. Nothing else changed.
// ---------------------------------------------------------------------------

let syncStatusListener = null;

/**
 * Register the sink for sync results. background.mjs sets this once at module load.
 * @param {(status: object) => void} listener
 */
export function setSyncStatusListener(listener) {
  syncStatusListener = typeof listener === "function" ? listener : null;
}

function reportSyncStatus(status) {
  try {
    syncStatusListener?.(status);
  } catch {
    // A broken listener must never break cookie capture.
  }
  return status;
}

// ---------------------------------------------------------------------------
// Upstream entry points
// ---------------------------------------------------------------------------

/**
 * Upstream behaviour: fire-and-forget capture+send.
 * Addition: returns the resulting status promise so callers (options page, onInstalled)
 * can await it. `await undefined` behaved as a no-op upstream, so this is a superset.
 */
export function getYoutubeCookies() {
  return getAndSendYoutubeCookies();
}

export function getYoutubeCookiesDebounce(changeInfo) {
  // Upstream echoed the cookie name here; CONTRACT §8 forbids cookie identifiers in
  // logs, so only the event itself is logged.
  console.log("YouTube cookie change detected, scheduling export.");
  debounce(getAndSendYoutubeCookies, 5000)();
}

// ---------------------------------------------------------------------------
// Upstream cookie collection (verbatim, incl. partitionKey / cookie store compat)
// ---------------------------------------------------------------------------

const getAllYouTubeCookies = async () => {
  const details = {
    domain: "youtube.com",

    storeId: await getCurrentCookieStoreId(),
  };
  const detailsWithPartitionKey = {
    ...details,
    partitionKey: {
      topLevelSite: "https://youtube.com",
    },
  };

  // https://github.com/kairi003/Get-cookies.txt-LOCALLY/tree/master/src/modules/get_all_cookies.mjs#L9-L17
  // Error handling for browsers that do not support partitionKey, such as chrome < 119.
  // `chrome.cookies.getAll()` returns Promise but cannot directly catch() chain.
  const cookiesWithPartitionKey = await Promise.resolve()
    .then(() => chrome.cookies.getAll(detailsWithPartitionKey))
    .catch(() => []);
  const cookies = await chrome.cookies.getAll(details);
  return [...cookies, ...cookiesWithPartitionKey];
};

const getCurrentCookieStoreId = async () => {
  // If the extension is in split incognito mode, return undefined to choose the default store.
  if (chrome.runtime.getManifest().incognito === "split") {
    return undefined;
  }

  // Firefox supports the `tab.cookieStoreId` property.
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (tab?.cookieStoreId) {
    return tab.cookieStoreId;
  }

  // Chrome does not support the `tab.cookieStoreId` property.
  const stores = await chrome.cookies.getAllCookieStores();
  return stores.find((store) => store.tabIds.includes(tab?.id))?.id;
};

// ---------------------------------------------------------------------------
// Netscape serialization (upstream body + two CONTRACT §4 additions)
//
//   1. drop cookies whose name starts with "ST-" (Firefox session tokens that break
//      yt-dlp's YouTube validation, see CONTRACT §4 / VRCVC ApiController.FilterCookies)
//   2. tolerate malformed entries instead of throwing (unit tested)
//
// Everything else — field order/count, "\t" join, "." domain prefix, "TRUE" for
// includeSubdomains, secure mapping, Math.floor(expirationDate || 0), header text,
// trailing newline — is byte-identical to upstream.
// ---------------------------------------------------------------------------

/**
 * @param {Array<{domain?: string, path?: string, secure?: boolean, expirationDate?: number, name?: string, value?: string}>} cookies
 * @returns {string} Netscape cookie file text
 */
export function serializeNetscapeCookies(cookies) {
  const header = [
    "# Netscape HTTP Cookie File",
    "# This file is generated by VRCVideoCacher.  Do not edit.",
    "",
  ].join("\n");
  const list = Array.isArray(cookies) ? cookies : [];
  const netscapeCookies = list
    .filter(
      (c) =>
        c &&
        typeof c.name === "string" &&
        c.name.length > 0 &&
        // CONTRACT §4: Firefox "ST-*" cookies must never reach the cookie file.
        !c.name.startsWith("ST-") &&
        typeof c.domain === "string" &&
        c.domain.length > 0,
    )
    .map((c) => {
      const domain = c.domain.startsWith('.') ? c.domain : '.' + c.domain;
      return [
        domain,
        "TRUE",
        typeof c.path === "string" && c.path.length > 0 ? c.path : "/",
        c.secure ? "TRUE" : "FALSE",
        Math.floor(c.expirationDate || 0),
        c.name,
        typeof c.value === "string" ? c.value : "",
      ].join("\t");
    })
    .join("\n");
  const formattedData = header + netscapeCookies + "\n";
  return formattedData;
}

/**
 * Mirrors VRCVideoCacher `Program.IsCookiesValid` (CONTRACT §4): the text must contain
 * both "youtube.com" and "LOGIN_INFO". Report-only here — never used to block a sync.
 * @param {string} text
 */
export function isCookiesValid(text) {
  if (typeof text !== "string" || text.length === 0) return false;
  return text.includes("youtube.com") && text.includes("LOGIN_INFO");
}

/**
 * Count cookie records (non-comment lines with exactly 7 tab separated fields).
 * Used only as a fallback when the host does not report cookieCount.
 * @param {string} text
 */
export function countCookieRecords(text) {
  if (typeof text !== "string" || text.length === 0) return 0;
  return text
    .split("\n")
    .filter((line) => line.length > 0 && !line.startsWith("#"))
    .filter((line) => line.split("\t").length === 7).length;
}

// ---------------------------------------------------------------------------
// Native Messaging request construction (CONTRACT §5)
// ---------------------------------------------------------------------------

/** ISO-8601 UTC with second precision, e.g. 2026-11-01T00:30:00Z. */
export function nowIsoSeconds(date = new Date()) {
  return date.toISOString().replace(/\.\d+Z$/, "Z");
}

export function detectBrowser() {
  const ua = typeof navigator !== "undefined" ? String(navigator.userAgent ?? "") : "";
  if (/Firefox\/|FxiOS\//i.test(ua)) return "firefox";
  if (/Edg\//i.test(ua)) return "edge";
  if (/Chrome\/|Chromium\//i.test(ua)) return "chrome";
  return "chrome";
}

/**
 * Current extension version, "" -> "0.0.0" when no extension runtime is present
 * (unit tests import this module under plain Node).
 */
export function getExtensionVersion() {
  try {
    const g = globalThis;
    const runtime = g.chrome?.runtime ?? g.browser?.runtime;
    const version = runtime?.getManifest?.().version;
    return typeof version === "string" && version.length > 0 ? version : "0.0.0";
  } catch {
    return "0.0.0";
  }
}

/**
 * Build the CONTRACT §5 request envelope.
 *
 * @param {string} netscapeText
 * @param {{browser?: string, extensionVersion?: string, capturedUtc?: string}} [meta]
 */
export function buildCookieUpdateMessage(netscapeText, meta = {}) {
  return {
    type: "cookie.update",
    browser: meta.browser ?? detectBrowser(),
    extensionVersion: meta.extensionVersion ?? getExtensionVersion(),
    cookies: typeof netscapeText === "string" ? netscapeText : "",
    capturedUtc: meta.capturedUtc ?? nowIsoSeconds(),
  };
}

// ---------------------------------------------------------------------------
// Native Messaging response parsing (CONTRACT §5)
//
// success: { ok: true, vaultUpdated: true, cookieCount: 37, applied: "vault+cookie-file",
//            targetWritten: true, message: "ok" }
// failure: { ok: false, error: "invalid-cookie-format" }
// ---------------------------------------------------------------------------

function toCookieCount(value) {
  if (Number.isInteger(value) && value >= 0) return value;
  if (typeof value === "string" && value.trim() !== "") {
    const parsed = Number(value);
    if (Number.isFinite(parsed) && parsed >= 0) return Math.floor(parsed);
  }
  if (typeof value === "number" && Number.isFinite(value) && value >= 0) {
    return Math.floor(value);
  }
  return undefined;
}

function truncate(text, max = MAX_ERROR_DETAIL) {
  const s = String(text ?? "");
  return s.length > max ? `${s.slice(0, max)}...` : s;
}

/**
 * Normalize whatever the native host answered into the shape background/options use.
 * Never throws; never echoes the request back.
 *
 * @param {unknown} response
 * @param {number} [fallbackCookieCount]
 */
export function normalizeNativeResponse(response, fallbackCookieCount) {
  const fallback = toCookieCount(fallbackCookieCount);
  if (response === undefined || response === null) {
    return {
      ok: false,
      targetWritten: false,
      vaultUpdated: false,
      cookieCount: fallback,
      applied: null,
      message: null,
      error: "empty-native-response",
      errorDetail: "native host returned no response",
    };
  }
  if (typeof response !== "object") {
    return {
      ok: false,
      targetWritten: false,
      vaultUpdated: false,
      cookieCount: fallback,
      applied: null,
      message: null,
      error: "invalid-native-response",
      errorDetail: `expected object, got ${typeof response}`,
    };
  }

  const cookieCount = toCookieCount(response.cookieCount) ?? fallback;
  const applied = typeof response.applied === "string" ? response.applied : null;
  const message = typeof response.message === "string" ? truncate(response.message) : null;

  if (response.ok !== true) {
    return {
      ok: false,
      targetWritten: false,
      vaultUpdated: false,
      cookieCount,
      applied,
      message,
      error:
        typeof response.error === "string" && response.error.length > 0
          ? truncate(response.error)
          : "native-host-rejected",
      errorDetail: message,
    };
  }

  return {
    ok: true,
    targetWritten: response.targetWritten === true,
    vaultUpdated: response.vaultUpdated === true,
    cookieCount,
    applied,
    message,
    error: null,
    errorDetail: null,
  };
}

// ---------------------------------------------------------------------------
// Native Messaging transport
// ---------------------------------------------------------------------------

function getNativeMessagingRuntime() {
  const g = globalThis;
  const runtime = g.browser?.runtime?.sendNativeMessage ? g.browser.runtime : g.chrome?.runtime;
  return runtime?.sendNativeMessage ? runtime : null;
}

function withTimeout(promise, timeoutMs) {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) return promise;
  let timer = null;
  const timeout = new Promise((_resolve, reject) => {
    timer = setTimeout(
      () => reject(new Error(`native-message-timeout after ${timeoutMs}ms`)),
      timeoutMs,
    );
  });
  return Promise.race([promise, timeout]).finally(() => {
    if (timer !== null) clearTimeout(timer);
  });
}

/**
 * Promise wrapper around runtime.sendNativeMessage that works on both namespaces
 * (Chrome: callback + optional promise ; Firefox: promise/callback) and surfaces
 * chrome.runtime.lastError as a rejection.
 */
function sendNativeMessageRaw(message, timeoutMs) {
  const runtime = getNativeMessagingRuntime();
  if (!runtime) {
    return Promise.reject(new Error("native-messaging-unavailable"));
  }

  const call = new Promise((resolve, reject) => {
    let settled = false;
    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      const lastError = runtime.lastError ?? globalThis.chrome?.runtime?.lastError;
      if (lastError) {
        reject(new Error(lastError.message ?? String(lastError)));
        return;
      }
      fn(value);
    };
    try {
      const maybePromise = runtime.sendNativeMessage(
        NATIVE_HOST_NAME,
        message,
        (response) => finish(resolve, response),
      );
      if (maybePromise && typeof maybePromise.then === "function") {
        maybePromise.then(
          (response) => finish(resolve, response),
          (error) => {
            if (settled) return;
            settled = true;
            reject(error);
          },
        );
      }
    } catch (error) {
      if (settled) return;
      settled = true;
      reject(error);
    }
  });

  return withTimeout(call, timeoutMs);
}

function describeNativeError(error) {
  const detail = truncate(error?.message ?? error ?? "unknown");
  let code = "native-error";
  if (/native-messaging-unavailable/i.test(detail)) code = "native-messaging-unavailable";
  else if (/timeout/i.test(detail)) code = "native-timeout";
  else if (/not found|no such native application|host not found|could not be found/i.test(detail))
    code = "native-host-not-found";
  else if (/forbidden|not allowed|access to the specified native messaging host/i.test(detail))
    code = "native-host-forbidden";
  else if (/exited|disconnected|channel closed|message channel closed|port closed|terminated/i.test(detail))
    code = "native-host-disconnected";
  return { code, detail };
}

/**
 * Send a Netscape cookie text to the native host (CONTRACT §5).
 *
 * Resolves (never rejects) with:
 *   { ok, targetWritten, vaultUpdated, cookieCount, applied, message,
 *     error, errorDetail, browser, extensionVersion, capturedUtc, elapsedMs }
 *
 * `error` is a short machine code ("native-host-not-found", "native-timeout",
 * "empty-native-response", ...) or the host supplied code; cookie text is never
 * included.
 *
 * @param {string} netscapeText
 * @param {{timeoutMs?: number, cookieCount?: number, browser?: string,
 *          extensionVersion?: string, capturedUtc?: string}} [opts]
 */
export async function sendToNativeHost(netscapeText, opts = {}) {
  const message = buildCookieUpdateMessage(netscapeText, opts);
  const timeoutMs = Number.isFinite(opts.timeoutMs) ? opts.timeoutMs : NATIVE_MESSAGE_TIMEOUT_MS;
  const fallbackCount = toCookieCount(opts.cookieCount) ?? countCookieRecords(message.cookies);
  const startedAt = Date.now();

  const base = {
    browser: message.browser,
    extensionVersion: message.extensionVersion,
    capturedUtc: message.capturedUtc,
  };

  try {
    const raw = await sendNativeMessageRaw(message, timeoutMs);
    const normalized = normalizeNativeResponse(raw, fallbackCount);
    return { ...normalized, ...base, elapsedMs: Date.now() - startedAt };
  } catch (error) {
    const { code, detail } = describeNativeError(error);
    return {
      ok: false,
      targetWritten: false,
      vaultUpdated: false,
      cookieCount: fallbackCount,
      applied: null,
      message: null,
      error: code,
      errorDetail: detail,
      ...base,
      elapsedMs: Date.now() - startedAt,
    };
  }
}

// ---------------------------------------------------------------------------
// Capture -> serialize -> native send (upstream getAndSendYoutubeCookies)
// ---------------------------------------------------------------------------

const getAndSendYoutubeCookies = async () => {
  let cookies;
  try {
    cookies = await getAllYouTubeCookies();
  } catch (error) {
    return reportSyncStatus({
      ok: false,
      targetWritten: false,
      vaultUpdated: false,
      cookieCount: null,
      applied: null,
      message: null,
      error: "cookie-read-failed",
      errorDetail: truncate(error?.message ?? error ?? "unknown"),
      browser: detectBrowser(),
      extensionVersion: getExtensionVersion(),
      capturedUtc: nowIsoSeconds(),
      elapsedMs: null,
    });
  }

  console.log(`Fetched ${cookies.length} YouTube cookies`);
  const formattedData = serializeNetscapeCookies(cookies);

  const result = await sendToNativeHost(formattedData, {
    cookieCount: cookies.length,
    browser: detectBrowser(),
    extensionVersion: getExtensionVersion(),
  });

  return reportSyncStatus({
    ...result,
    // Tell the UI whether the exported file actually carries a login cookie.
    loginCookiePresent: isCookiesValid(formattedData),
    exportedCookieRecords: countCookieRecords(formattedData),
    bytesSent: formattedData.length,
  });
};
