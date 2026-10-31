/**
 * tests/manifest.test.mjs — packaging invariants for the shipped extension folder.
 *
 * Verifies the manifest contract (permissions, MV3 wiring, gecko id) and that the
 * manifest "key" really derives the extension id recorded in extension-id.txt.
 *
 * Run: node --test browser-extension/tests
 */

import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { describe, it } from "node:test";
import { fileURLToPath } from "node:url";

import { extensionIdFromSpki } from "../tools/genkey.mjs";

const EXT_DIR = join(dirname(fileURLToPath(import.meta.url)), "..");
const manifest = JSON.parse(readFileSync(join(EXT_DIR, "manifest.json"), "utf8"));

describe("manifest.json", () => {
  it("is MV3", () => {
    assert.equal(manifest.manifest_version, 3);
    assert.equal(typeof manifest.name, "string");
    assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
  });

  it("declares exactly the required permissions", () => {
    assert.deepEqual(
      [...manifest.permissions].sort(),
      ["cookies", "nativeMessaging", "storage"],
    );
  });

  it("only asks for youtube.com hosts", () => {
    assert.deepEqual(manifest.host_permissions, ["*://*.youtube.com/*"]);
  });

  it("wires the background as an ES module service worker", () => {
    assert.equal(manifest.background.service_worker, "background.mjs");
    assert.equal(manifest.background.type, "module");
    // Firefox < MV3 service worker support uses background.scripts.
    assert.ok(manifest.background.scripts.includes("background.mjs"));
  });

  it("runs the upstream content script on YouTube", () => {
    const script = manifest.content_scripts[0];
    assert.deepEqual(script.matches, ["*://*.youtube.com/*"]);
    assert.deepEqual(script.js, ["content.js"]);
  });

  it("declares the contract gecko identity", () => {
    assert.equal(manifest.browser_specific_settings.gecko.id, "vrexcookiebridge@local");
    assert.equal(manifest.browser_specific_settings.gecko.strict_min_version, "138.0");
  });

  it("ships a real RSA-2048 key that derives extension-id.txt's id", () => {
    assert.equal(typeof manifest.key, "string");
    assert.ok(manifest.key.length > 300, "key must be a full base64 SPKI blob");

    const spkiDer = Buffer.from(manifest.key, "base64");
    const derivedId = extensionIdFromSpki(spkiDer);
    assert.match(derivedId, /^[a-p]{32}$/);

    const idFile = readFileSync(join(EXT_DIR, "extension-id.txt"), "utf8");
    assert.ok(
      idFile.includes(`extension_id=${derivedId}`),
      `extension-id.txt must record ${derivedId}`,
    );
    assert.ok(idFile.includes(`origin=chrome-extension://${derivedId}/`));
  });

  it("references files that actually exist", () => {
    const referenced = [
      manifest.background.service_worker,
      ...manifest.content_scripts.flatMap((entry) => entry.js),
      manifest.options_ui.page,
      "utils.mjs",
      "options.js",
      "README.md",
    ];
    for (const relative of referenced) {
      assert.ok(existsSync(join(EXT_DIR, relative)), `missing file: ${relative}`);
    }
  });

  it("loads no remote resources in the options page", () => {
    const html = readFileSync(join(EXT_DIR, manifest.options_ui.page), "utf8");
    assert.ok(!/https?:\/\//i.test(html), "options.html must not reference remote URLs");
    assert.ok(!/<link\b[^>]*stylesheet/i.test(html), "styles must be inline");
  });

  it("talks Native Messaging only — no local HTTP API", () => {
    const utils = readFileSync(join(EXT_DIR, "utils.mjs"), "utf8");
    assert.ok(utils.includes("com.vrcvideocacher.cookiebridge"));
    assert.ok(
      !/\bfetch\s*\(/.test(utils),
      "the extension must not depend on VRCVideoCacher's local HTTP API",
    );
    for (const permission of manifest.host_permissions) {
      assert.ok(
        !/127\.0\.0\.1|localhost/i.test(permission),
        `unexpected local host permission: ${permission}`,
      );
    }
  });
});
