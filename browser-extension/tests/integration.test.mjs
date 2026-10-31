/**
 * tests/integration.test.mjs — end-to-end wiring of background.mjs + utils.mjs with a
 * stubbed WebExtension API: capture -> serialize -> sendNativeMessage -> storage -> badge.
 *
 * Synthetic cookies only (FAKE_* values). No real account material.
 *
 * Run: node --test browser-extension/tests
 */

import assert from "node:assert/strict";
import { after, describe, it } from "node:test";

import { NATIVE_HOST_NAME } from "../utils.mjs";

const calls = {
  nativeMessages: [],
  storageSets: [],
  badgeText: [],
  badgeColors: [],
};

const listeners = {
  message: [],
  cookieChanged: [],
  installed: [],
};

/** Mutable so individual tests can choose what the fake host answers. */
let hostResponder = () => ({
  ok: true,
  vaultUpdated: true,
  cookieCount: 2,
  applied: "vault+cookie-file",
  targetWritten: true,
  message: "ok",
});

const fakeCookies = [
  {
    domain: "youtube.com",
    path: "/",
    secure: true,
    expirationDate: 1790000000,
    name: "LOGIN_INFO",
    value: "FAKE_LOGIN_VALUE",
  },
  {
    domain: ".youtube.com",
    path: "/",
    secure: false,
    expirationDate: 1800000000,
    name: "PREF",
    value: "FAKE_PREF_VALUE",
  },
];

const fakePartitionCookies = [
  {
    domain: ".youtube.com",
    path: "/",
    secure: true,
    expirationDate: 1900000000,
    name: "ST-fake",
    value: "FAKE_SESSION_TOKEN",
  },
];

globalThis.chrome = {
  runtime: {
    getManifest: () => ({ version: "1.0.0" }),
    sendNativeMessage: (host, message, callback) => {
      calls.nativeMessages.push({ host, message });
      callback(hostResponder());
    },
    onMessage: { addListener: (fn) => listeners.message.push(fn) },
    onInstalled: { addListener: (fn) => listeners.installed.push(fn) },
    openOptionsPage: () => {},
  },
  cookies: {
    getAll: async (details) =>
      details?.partitionKey ? fakePartitionCookies : fakeCookies,
    getAllCookieStores: async () => [{ id: "0", tabIds: [1] }],
    onChanged: { addListener: (fn) => listeners.cookieChanged.push(fn) },
  },
  tabs: { query: async () => [{ id: 1 }] },
  storage: {
    local: {
      set: async (items) => {
        calls.storageSets.push(items);
      },
      get: async () => ({}),
    },
    onChanged: { addListener: () => {} },
  },
  action: {
    setBadgeText: async ({ text }) => {
      calls.badgeText.push(text);
    },
    setBadgeBackgroundColor: async ({ color }) => {
      calls.badgeColors.push(color);
    },
    onClicked: { addListener: () => {} },
  },
};

await import("../background.mjs");

function latestRecord() {
  const last = calls.storageSets.at(-1);
  assert.ok(last, "background must persist a status record");
  assert.ok(last.syncStatus, 'status must live under the "syncStatus" key');
  return last.syncStatus;
}

function reset() {
  calls.nativeMessages.length = 0;
  calls.storageSets.length = 0;
  calls.badgeText.length = 0;
  calls.badgeColors.length = 0;
}

/** Drive the options-page "立即同步" message and wait for the async response. */
function syncNow() {
  const listener = listeners.message[0];
  assert.ok(listener, "background must register a runtime.onMessage listener");
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("no response from background")), 5000);
    const keepAlive = listener({ type: "cookiebridge.sync-now" }, {}, (response) => {
      clearTimeout(timer);
      resolve(response);
    });
    assert.equal(keepAlive, true, "async handler must return true");
  });
}

describe("background <-> native host integration", () => {
  it("exchanges a contract-shaped cookie.update message", async () => {
    reset();
    const response = await syncNow();

    assert.equal(calls.nativeMessages.length, 1);
    const { host, message } = calls.nativeMessages[0];
    assert.equal(host, NATIVE_HOST_NAME);
    assert.equal(message.type, "cookie.update");
    assert.equal(message.extensionVersion, "1.0.0");
    assert.match(message.capturedUtc, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/);

    const records = message.cookies
      .split("\n")
      .filter((line) => line.length > 0 && !line.startsWith("#"));
    assert.equal(records.length, 2, "ST- cookie from the partition query must be dropped");
    for (const line of records) {
      assert.equal(line.split("\t").length, 7);
    }
    assert.ok(!message.cookies.includes("FAKE_SESSION_TOKEN"));

    assert.equal(response.ok, true);
  });

  it("persists a sanitized success record and paints a green badge", async () => {
    reset();
    await syncNow();
    const record = latestRecord();

    assert.equal(record.ok, true);
    assert.equal(record.targetWritten, true);
    assert.equal(record.cookieCount, 2);
    assert.equal(record.nativeHost, NATIVE_HOST_NAME);
    assert.equal(record.error, null);
    assert.equal(record.loginCookiePresent, true);

    const serialized = JSON.stringify(record);
    for (const secret of ["FAKE_LOGIN_VALUE", "FAKE_PREF_VALUE", "LOGIN_INFO", "# Netscape"]) {
      assert.ok(!serialized.includes(secret), `storage record leaked: ${secret}`);
    }

    assert.equal(calls.badgeText.at(-1), "OK");
    assert.equal(calls.badgeColors.at(-1), "#1f9d55");
  });

  it("records host failures and paints a red badge", async () => {
    reset();
    hostResponder = () => ({ ok: false, error: "invalid-cookie-format" });
    await syncNow();
    hostResponder = () => ({
      ok: true,
      vaultUpdated: true,
      cookieCount: 2,
      applied: "vault+cookie-file",
      targetWritten: true,
      message: "ok",
    });

    const record = latestRecord();
    assert.equal(record.ok, false);
    assert.equal(record.error, "invalid-cookie-format");
    assert.equal(record.targetWritten, false);
    assert.equal(calls.badgeText.at(-1), "ERR");
    assert.equal(calls.badgeColors.at(-1), "#d93025");
  });

  it("distinguishes 'accepted but not written' with an amber badge", async () => {
    reset();
    hostResponder = () => ({ ok: true, vaultUpdated: true, targetWritten: false });
    await syncNow();
    hostResponder = () => ({
      ok: true,
      vaultUpdated: true,
      cookieCount: 2,
      applied: "vault+cookie-file",
      targetWritten: true,
      message: "ok",
    });

    const record = latestRecord();
    assert.equal(record.ok, true);
    assert.equal(record.targetWritten, false);
    assert.equal(calls.badgeText.at(-1), "!");
    assert.equal(calls.badgeColors.at(-1), "#b7791f");
  });

  it("re-syncs from the browser install hook", async () => {
    reset();
    const onInstalled = listeners.installed[0];
    assert.ok(onInstalled, "background must register runtime.onInstalled");
    await onInstalled({ reason: "install" });
    assert.equal(calls.nativeMessages.length, 1);
    assert.equal(latestRecord().ok, true);
  });

  it("registers a cookies.onChanged listener", () => {
    assert.equal(typeof listeners.cookieChanged[0], "function");
  });
});

after(() => {
  delete globalThis.chrome;
});
