/**
 * browser-extension/content.js — kept from the upstream extension (content.js).
 * Its only job is to ask the background worker for a fresh export when a YouTube
 * page loads. No behaviour change.
 */
chrome.runtime.sendMessage("send_youtube_cookies");
