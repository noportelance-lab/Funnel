"use strict";

// ================================================================ loading screen (page side)
// The loading screen itself is drawn by the window (LoadingView.cs) so it's up before the page
// exists. The page loads underneath it and reports its own startup steps; the window lifts the
// loading screen once they're done and tells the page ("revealed"). Loaded before app.js.
const Loader = (() => {
  const bridge = window.chrome && window.chrome.webview;
  // A tab dragged out into a window of its own opens without the loading and start screens.
  let visible = !!bridge && !/[?&]win=(tab|spare)/.test(location.search), gone = [];
  let n = 1e9;   // request ids well clear of app.js's, which start at 1

  return {
    /** Report a startup step finished: "settings", "market" or "start". */
    done(id) { if (bridge) bridge.postMessage(JSON.stringify({ cmd: "loadStep", reqId: n++, payload: { id } })); },
    /** Without the window there's no loading screen to finish. */
    doneAll() {},
    /** True while the window's loading screen still covers the page. */
    get visible() { return visible; },
    /** Run fn once the loading screen has lifted (right away if it already has). */
    whenGone(fn) { if (visible) gone.push(fn); else fn(); },
    /** The window lifted the loading screen: replay the start screen's entrance and run what waited. */
    revealed() {
      if (!visible) return;
      visible = false;
      const panel = document.querySelector(".gate-panel");
      if (panel) { panel.style.animation = "none"; void panel.offsetWidth; panel.style.animation = ""; }
      gone.splice(0).forEach((fn) => fn());
    },
  };
})();
