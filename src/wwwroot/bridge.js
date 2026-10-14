"use strict";

// ================================================================ bridge shim
// On Windows the page talks to C# through WebView2's window.chrome.webview. On macOS the window is
// Photino on WebKit, whose bridge is window.external.sendMessage / receiveMessage. This gives the
// page the WebView2 shape there, so loader.js and app.js stay the same. Loaded first.
(() => {
  if (window.chrome && window.chrome.webview) return;
  const ext = window.external;
  if (!ext || typeof ext.sendMessage !== "function" || typeof ext.receiveMessage !== "function") return;
  const listeners = [];
  ext.receiveMessage((data) => {
    for (const fn of listeners) { try { fn({ data }); } catch (e) { console.error(e); } }
  });
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: (msg) => ext.sendMessage(typeof msg === "string" ? msg : JSON.stringify(msg)),
    addEventListener: (type, fn) => { if (type === "message") listeners.push(fn); },
    removeEventListener: (type, fn) => { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
  };
  document.documentElement.classList.add("mac");
})();
