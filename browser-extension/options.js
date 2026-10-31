/**
 * browser-extension/options.js — settings / status page (no external resources).
 *
 * Reads the last sync record written by background.mjs (chrome.storage.local key
 * "syncStatus") and offers a manual sync. Shows counts/flags/error codes only —
 * the extension never surfaces cookie names or values.
 */

(function () {
  "use strict";

  /** Must match STATUS_KEY in background.mjs. */
  const STATUS_KEY = "syncStatus";
  const NATIVE_HOST = "com.vrcvideocacher.cookiebridge";

  // Prefer the `chrome` namespace: it accepts callbacks in both Chrome and Firefox,
  // while Firefox's promise-only `browser.*` methods reject extra callback arguments.
  const api = globalThis.chrome ?? globalThis.browser;

  const el = {
    lastSync: document.getElementById("last-sync"),
    cookieCount: document.getElementById("cookie-count"),
    writtenDot: document.getElementById("written-dot"),
    writtenText: document.getElementById("written-text"),
    errorSummary: document.getElementById("error-summary"),
    errorBox: document.getElementById("error-box"),
    errorCode: document.getElementById("error-code"),
    errorDetail: document.getElementById("error-detail"),
    nativeHost: document.getElementById("native-host"),
    extensionId: document.getElementById("extension-id"),
    extensionVersion: document.getElementById("extension-version"),
    syncNow: document.getElementById("sync-now"),
    docsLink: document.getElementById("docs-link"),
    docsPanel: document.getElementById("docs-panel"),
    docsContent: document.getElementById("docs-content"),
    hint: document.getElementById("hint"),
  };

  /** Promise wrapper that works with both the Chrome callback and Firefox promise APIs. */
  function invoke(fn, thisArg, args) {
    return new Promise((resolve, reject) => {
      let settled = false;
      const settle = (fn2, value) => {
        if (settled) return;
        settled = true;
        fn2(value);
      };
      const callback = (value) => {
        const lastError = globalThis.chrome?.runtime?.lastError;
        if (lastError) settle(reject, new Error(lastError.message ?? String(lastError)));
        else settle(resolve, value);
      };
      let returned;
      try {
        returned = fn.apply(thisArg, [...args, callback]);
      } catch (error) {
        settle(reject, error);
        return;
      }
      if (returned && typeof returned.then === "function") {
        returned.then(
          (value) => settle(resolve, value),
          (error) => settle(reject, error),
        );
      }
    });
  }

  function formatTime(iso) {
    if (typeof iso !== "string" || iso.length === 0) return null;
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    return date.toLocaleString(undefined, {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  }

  function setDot(state) {
    el.writtenDot.className = "dot";
    if (state) el.writtenDot.classList.add(state);
  }

  function render(record) {
    if (!record) {
      el.lastSync.textContent = "尚未同步";
      el.cookieCount.textContent = "—";
      setDot("");
      el.writtenText.textContent = "未知";
      el.errorSummary.textContent = "无";
      el.errorBox.classList.remove("visible");
      return;
    }

    const when = formatTime(record.lastSyncUtc);
    el.lastSync.textContent = when ?? "尚未同步";
    if (when && record.capturedUtc) {
      const captured = formatTime(record.capturedUtc);
      if (captured) el.lastSync.textContent = `${when}（抓取于 ${captured}）`;
    }

    el.cookieCount.textContent = Number.isInteger(record.cookieCount)
      ? `${record.cookieCount} 条`
      : "—";

    if (record.ok && record.targetWritten) {
      setDot("ok");
      el.writtenText.textContent = "是（vault + youtube_cookies.txt）";
    } else if (record.ok) {
      setDot("warn");
      el.writtenText.textContent = "否（host 已接受，但未写入 Cookie 文件）";
    } else {
      setDot("err");
      el.writtenText.textContent = "否";
    }

    if (record.ok) {
      el.errorSummary.textContent = "无";
      el.errorBox.classList.remove("visible");
      el.errorCode.textContent = "";
      el.errorDetail.textContent = "";
    } else {
      const code = record.error ?? "unknown";
      el.errorSummary.textContent = code;
      el.errorBox.classList.add("visible");
      el.errorCode.textContent = code;
      el.errorDetail.textContent = record.errorDetail ?? "";
    }

    if (Number.isInteger(record.elapsedMs)) {
      el.hint.textContent = `上次同步耗时 ${record.elapsedMs} ms`;
    }
  }

  async function loadStatus() {
    try {
      const items = await invoke(api.storage.local.get, api.storage.local, [STATUS_KEY]);
      render(items?.[STATUS_KEY] ?? null);
    } catch (error) {
      el.errorSummary.textContent = "读取状态失败";
      el.errorBox.classList.add("visible");
      el.errorCode.textContent = "storage-read-failed";
      el.errorDetail.textContent = String(error?.message ?? error ?? "");
    }
  }

  async function syncNow() {
    el.syncNow.disabled = true;
    el.hint.textContent = "同步中…";
    try {
      const response = await invoke(api.runtime.sendMessage, api.runtime, [
        { type: "cookiebridge.sync-now" },
      ]);
      if (response && response.status) {
        render(response.status);
        if (response.status.ok) {
          el.hint.textContent = "同步完成。";
        } else {
          el.hint.textContent = "同步失败，请查看错误信息。";
        }
      } else {
        el.hint.textContent = "同步未返回状态。";
        await loadStatus();
      }
    } catch (error) {
      el.hint.textContent = "同步失败，请查看错误信息。";
      render({
        ok: false,
        error: "background-unreachable",
        errorDetail: String(error?.message ?? error ?? ""),
        lastSyncUtc: new Date().toISOString(),
      });
    } finally {
      el.syncNow.disabled = false;
    }
  }

  /**
   * "打开安装说明": render README.md inline (extension-local fetch, no remote resource).
   * The href stays a real link so middle-click / "open in new tab" still works.
   */
  async function toggleDocs(event) {
    if (!el.docsPanel) return;
    if (!el.docsPanel.hidden) {
      el.docsPanel.hidden = true;
      return;
    }
    event.preventDefault();
    if (!el.docsContent.textContent) {
      try {
        const response = await fetch(api.runtime.getURL("README.md"));
        el.docsContent.textContent = await response.text();
      } catch (error) {
        el.docsContent.textContent =
          `无法读取 README.md（${String(error?.message ?? error)}）。` +
          "可直接在扩展目录中打开该文件。";
      }
    }
    el.docsPanel.hidden = false;
    el.docsPanel.scrollIntoView({ block: "nearest" });
  }

  function init() {
    el.nativeHost.textContent = NATIVE_HOST;

    try {
      el.extensionId.textContent = api?.runtime?.id ?? "—";
      el.extensionVersion.textContent = api?.runtime?.getManifest?.().version ?? "—";
    } catch {
      // Static getters only; nothing to recover from.
    }

    try {
      el.docsLink.href = api.runtime.getURL("README.md");
    } catch {
      el.docsLink.removeAttribute("href");
    }

    el.syncNow.addEventListener("click", () => {
      void syncNow();
    });

    el.docsLink.addEventListener("click", (event) => {
      void toggleDocs(event);
    });

    try {
      api.storage.onChanged.addListener((changes, area) => {
        if (area === "local" && changes?.[STATUS_KEY]) {
          render(changes[STATUS_KEY].newValue ?? null);
        }
      });
    } catch {
      // Older builds may not expose storage.onChanged; manual sync still renders.
    }

    void loadStatus();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
})();
