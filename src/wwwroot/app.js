"use strict";

// ---------- bridge to C# ----------
const pending = new Map();
let nextId = 1;
const wv = window.chrome && window.chrome.webview;

function post(obj) { wv.postMessage(JSON.stringify(obj)); }

// opts.quiet suppresses the thin busy bar — for background chatter (search-as-you-type,
// the market auto-refresh) the user didn't explicitly ask for.
let busyCount = 0;
function call(cmd, payload, opts) {
  return new Promise((resolve, reject) => {
    const reqId = nextId++;
    const quiet = !!(opts && opts.quiet);
    if (!quiet) setBusy(+1);
    const settle = (fn) => (v) => { if (!quiet) setBusy(-1); fn(v); };
    pending.set(reqId, { resolve: settle(resolve), reject: settle(reject) });
    post({ cmd, reqId, payload: payload || {} });
  });
}
function setBusy(d) {
  busyCount = Math.max(0, busyCount + d);
  $("busybar").classList.toggle("hidden", busyCount === 0);
}
if (wv) {
  wv.addEventListener("message", (ev) => {
    let m; try { m = JSON.parse(ev.data); } catch { return; }
    if (m.type === "response") {
      const p = pending.get(m.reqId); if (!p) return;
      pending.delete(m.reqId);
      if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error));
    } else if (m.type === "event") {
      handleEvent(m.name, m.data);
    }
  });
}

const noticeSeen = new Map();
function handleEvent(name, data) {
  if (name === "progress") {
    $("pp-msg").textContent = data.message || "";
    $("pp-fill").style.width = (data.percent || 0) + "%";
  } else if (name === "statementCheck") {
    state.checks[`${data.symbol}|${data.kind}|${data.frequency}`] = data.check;
    warnShares(data.symbol, data.check.flags);
    if (data.symbol === state.symbol && data.kind === state.stKind && data.frequency === state.stFreq) paintStatement();
  } else if (name === "companyCheck") {
    state.coChecks[data.symbol] = data.check;
    warnShares(data.symbol, data.check.flags);
    if (data.symbol === state.symbol) { renderCoHead(); if (state.tab === "overview") paintShareStat(); }
  } else if (name === "modelCheck") {
    state.modelFlags[data.symbol] = data.flags;
    warnShares(data.symbol, data.flags);
    if (state.model && state.model.symbol === data.symbol) { decorateModelFlags(); recalc(); }
  } else if (name === "yoloProgress") {
    onYoloProgress(data);
  } else if (["tabDragging", "tabMoved", "initChanged", "tabDetached", "tabAttached", "adopt", "unadopt", "adopted"].includes(name)) {
    onTabWindowEvent(name, data);
  } else if (name === "revealed") {
    Loader.revealed();
  } else if (name === "notice") {
    // A source fell back to another one. Say so once a minute, not once per request.
    const last = noticeSeen.get(data.message) || 0;
    if (Date.now() - last > 60000) { noticeSeen.set(data.message, Date.now()); toast("Using a fallback source", data.message, "warn"); }
  }
}

// ---------- helpers ----------
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const icon = (id, cls) => `<svg class="ic ${cls || ""}"><use href="#${id}"/></svg>`;

const fmt = {
  num(v, d = 2) {
    return v == null || !isFinite(v) ? "–" : v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });
  },
  price(v) { return fmt.num(v, 2); },
  big(v) {
    if (v == null || !isFinite(v)) return "–";
    const a = Math.abs(v), s = v < 0 ? "-" : "";
    if (a >= 1e12) return s + (a / 1e12).toFixed(2) + "T";
    if (a >= 1e9) return s + (a / 1e9).toFixed(2) + "B";
    if (a >= 1e6) return s + (a / 1e6).toFixed(2) + "M";
    if (a >= 1e3) return s + (a / 1e3).toFixed(1) + "K";
    return s + a.toFixed(0);
  },
  x(v) { return v == null || !isFinite(v) ? "–" : v.toFixed(1) + "x"; },
  pct(v, d = 1) { return v == null || !isFinite(v) ? "–" : (v * 100).toFixed(d) + "%"; },
  // Day changes arrive already in percent units (0.37 = 0.37%).
  chg(v) { return v == null || !isFinite(v) ? "–" : (v >= 0 ? "+" : "") + v.toFixed(2) + "%"; },
  signed(v, d = 2) { return v == null || !isFinite(v) ? "–" : (v >= 0 ? "+" : "") + fmt.num(v, d); },
  metric(def, v) {
    switch (def && def.format) {
      case "money": return fmt.big(v);
      case "x": return fmt.x(v);
      case "pct": return fmt.pct(v);
      case "price": return fmt.price(v);
      default: return fmt.num(v, 2);
    }
  },
  date(d) { return new Date(d).toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" }); },
};
const dir = (v) => (v == null ? "" : v >= 0 ? "up" : "down");

function chgPill(pct, abs) {
  if (pct == null) return "";
  const d = dir(pct);
  return `<span class="pill ${d}">${icon(pct >= 0 ? "i-up" : "i-down")}${abs != null ? fmt.signed(abs) + " · " : ""}${fmt.chg(pct)}</span>`;
}

function toast(title, body, kind) {
  const t = document.createElement("div");
  t.className = "toast " + (kind || "");
  t.innerHTML = `<b>${esc(title)}</b>${body ? `<div>${esc(body)}</div>` : ""}`;
  $("toasts").appendChild(t);
  setTimeout(() => t.remove(), kind === "err" || kind === "warn" ? 8000 : 4500);
}

// ---------- cross-checks ----------
// Figures are compared with a second source (usually SEC EDGAR) in the background. "severe" =
// the sources disagree sharply (red); "warn" = they differ, often by definition (amber underline).
const flagCls = (f) => (f ? (f.severity === "severe" ? "flag-severe" : "flag-warn") : "");
const flagTip = (f) => (f ? (f.severity === "severe" ? "⚠ Sources disagree sharply. " : "Sources differ. ") + f.message : "");
const isShareKey = (k) => /(^|:)(BasicAverageShares|DilutedAverageShares|OrdinarySharesNumber|ShareIssued|sharesOutstanding)$/.test(k);

// Share counts drive every per-share number, so a sharp disagreement gets a toast (once per company).
const shareWarned = new Set();
function warnShares(symbol, flags) {
  const f = (flags || []).find((x) => x.severity === "severe" && isShareKey(x.key));
  if (!f || shareWarned.has(symbol)) return;
  shareWarned.add(symbol);
  toast(`${symbol}: share counts don't match`, f.message + " Per-share figures may be off; the cells are marked in red.", "err");
}

// ---------- state ----------
const COMPANY_TABS = ["overview", "comps", "analysts", "financials", "debt", "model", "thesis"];
const STARTERS = ["AAPL", "MSFT", "NVDA", "AMZN", "JPM", "SHOP.TO", "RY.TO"];
const state = {
  sources: null, dcf: null, dcfDefaults: null, metrics: [], metricMap: {}, compKeys: [], recent: [],
  feeds: null, model: null, thesis: null, debt: null,
  tab: "markets", mode: "research", yolo: null,
  symbol: null, company: null,
  compSet: [], compRows: null, compSort: null,
  chartRange: "1y", chartSeries: null,
  stKind: "income", stFreq: "annual", stUnits: "millions", statements: {},
  exFreq: "annual", exUnits: "millions",
  market: null,
  // Cross-checks against a second source, filled in as they land.
  checks: {}, coChecks: {}, modelFlags: {},
};

// ================================================================ start screen
// Guest-only for now. The app loads behind the gate, so by the time someone clicks through,
// the market board is usually already there. "Continue as guest" opens the internal-use
// disclaimer; the gate only lifts once the user ticks MIC committee or DMCC delegate.
function wireGate() {
  const gate = $("gate"), btn = $("gate-continue"), disc = $("disc"), accept = $("disc-accept");
  const checks = [$("disc-mic"), $("disc-dmcc")];
  const enter = () => {
    if (gate.classList.contains("leaving")) return;
    gate.classList.add("leaving");
    setTimeout(() => { const v = $("gate-video"); v.pause(); v.removeAttribute("src"); v.load(); gate.remove(); $("search").focus(); }, 460);
  };
  const discOpen = () => !disc.classList.contains("hidden");
  const openDisc = () => { disc.classList.remove("hidden"); checks[0].focus(); };
  const closeDisc = () => { disc.classList.add("hidden"); btn.focus(); };
  const eligible = () => checks.some((c) => c.checked);

  btn.addEventListener("click", openDisc);
  checks.forEach((c) => c.addEventListener("change", () => { accept.disabled = !eligible(); }));
  accept.addEventListener("click", () => { if (eligible()) enter(); });
  $("disc-cancel").addEventListener("click", closeDisc);
  disc.addEventListener("click", (e) => { if (e.target === disc) closeDisc(); });

  document.addEventListener("keydown", function onKey(e) {
    if (!document.body.contains(gate)) { document.removeEventListener("keydown", onKey); return; }
    if (Loader.visible) return;   // keys do nothing until the loading screen is gone
    if (e.key === "Escape" && discOpen()) { e.preventDefault(); closeDisc(); return; }
    if (e.key !== "Enter") return;
    // Inside the disclaimer, Enter only continues once a box is ticked (buttons handle their own Enter).
    if (!discOpen()) { e.preventDefault(); openDisc(); }
    else if (e.target.tagName !== "BUTTON" && eligible()) { e.preventDefault(); enter(); }
  });
  Loader.whenGone(() => btn.focus());
}

// ================================================================ init
async function init() {
  const first = makeTab("research"); first.snap = null; Tabs.list.push(Tabs.cur = first);
  wireGate();
  // A tab dragged out into a window of its own: the start screen was passed in the first window.
  if (SECONDARY || SPARE || /[?&]dev=nogate/.test(location.search)) { const v = $("gate-video"); if (v) { v.pause(); v.removeAttribute("src"); } $("gate").remove(); }
  wireWindow();
  // Names on hover, for when the sidebar is folded into icons.
  document.querySelectorAll(".nav-item").forEach((b) => { if (!b.title) b.title = b.textContent.trim(); });
  wireNav(); wireSearch(); wireOverview(); wireComps(); wireFinancials(); wireSources(); wireModel(); wireYolo();
  wireThesis(); wireDebt(); wireTabs();
  wireModes();
  paintTabs(); paintHistBtns();
  $("market-refresh").addEventListener("click", () => loadMarket(false));
  $("btn-export-top").addEventListener("click", () => {
    showTab("financials");
    const c = $("export-card"); c.classList.remove("pulse"); void c.offsetWidth; c.classList.add("pulse");
  });
  $("co-empty-chips").innerHTML = STARTERS.map((s) => `<button class="chip" data-sym="${s}">${s}</button>`).join("");
  $("co-empty-chips").addEventListener("click", (e) => { const b = e.target.closest("[data-sym]"); if (b) openCompany(b.dataset.sym); });

  if (!SECONDARY && !SPARE) watchStartVideo();
  if (!wv) { $("market-asof").textContent = "Run inside Funnel.exe — the web page alone has no data bridge."; Loader.doneAll(); return; }
  skelMarket();
  try {
    const r = await call("init");
    applyInit(r.init);
    Win.id = r.windowId || 0;
    if (r.seed) {
      // Opens on the tab it was dragged out as, in place of the blank one.
      const blank = Tabs.list[0];
      adoptTab(JSON.parse(r.seed), 1);
      Tabs.list.splice(Tabs.list.indexOf(blank), 1);
      paintTabs();
    }
  } catch (e) { toast("Startup problem", e.message, "err"); }
  Loader.done("settings");
  // Keep the board live while it's on screen.
  setInterval(() => { if (state.tab === "markets" && !document.hidden && !Win.spare) loadMarket(true); }, 60000);
  setInterval(() => { if (state.symbol && !document.hidden && !Win.spare) refreshQuote(); }, 60000);
  if (Win.spare) return;   // a spare window waits for a tab before it fetches anything
  // The loader waits for the first market board, but not forever: past 6 s the board keeps
  // loading behind the start screen instead.
  Promise.race([loadMarket(false), new Promise((r) => setTimeout(r, 6000))]).then(() => Loader.done("market"));
  watchDay();
}

// The open company's price moves with the market: the header, and on the Model tab the current
// price, upside and sensitivity colours that compare against it.
async function refreshQuote() {
  const sym = state.symbol;
  let q;
  try { q = await call("quote", { symbol: sym }, { quiet: true }); } catch { return; }
  if (sym !== state.symbol || !state.company || q.price == null) return;
  Object.assign(state.company, { price: q.price, change: q.change, changePct: q.changePct, marketState: q.marketState });
  renderCoHead();
  const m = state.model;
  if (m && m.symbol === sym) {
    m.base.price = q.price;
    if (m.res) recalc();
    if (m.base.kit && m.a.alt && altModel() !== "dcf" && state.tab === "model") recalcAlt();
  }
  paintTabsSoon();
}

// The start screen is ready once its background video can play (or after 5 s, or if it fails).
function watchStartVideo() {
  const v = $("gate-video"), ready = () => Loader.done("start");
  if (!v || v.readyState >= 3) { ready(); return; }
  v.addEventListener("canplay", ready, { once: true });
  v.addEventListener("error", ready, { once: true });
  setTimeout(ready, 5000);
}

// Every number is refreshed at least once a day: cached answers go stale at local midnight on
// the C# side, so when the date rolls over with Funnel still open, reload what's on screen.
function watchDay() {
  let day = new Date().toDateString();
  setInterval(async () => {
    const now = new Date().toDateString();
    if (now === day) return;
    day = now;
    try { applyInit(await call("newDay", {}, { quiet: true })); } catch { /* keep going on yesterday's settings */ }
    loadMarket(true);
    if (state.yolo && state.yolo.data) { state.yolo.data = null; if (state.tab === "yolo") loadYolo(false); }
    staleOthers();
    if (state.symbol) { state.model = null; openCompany(state.symbol); }
  }, 60000);
}

function applyInit(r) {
  state.sources = r.sources;
  state.dcf = r.dcf; state.dcfDefaults = r.dcfDefaults;
  state.metrics = r.metrics; state.metricMap = Object.fromEntries(r.metrics.map((m) => [m.key, m]));
  state.compKeys = r.compKeys; state.recent = r.recent || [];
  state.stUnits = state.exUnits = r.units || "millions";
  state.exFreq = r.frequency || "annual";
  $("version").textContent = `Funnel v${r.version}`;
  state.keepSavedDays = r.keepSavedDays; state.keepSavedChoices = r.keepSavedChoices || [1, 7, 30, 90, 365, 0];
  state.savedCount = r.savedCount || 0;
  paintSourceSummary(); paintDcfSummary(); paintSavedSummary();
  setSeg("st-units", state.stUnits); setSeg("ex-units", state.exUnits); setSeg("ex-freq", state.exFreq);
  renderRecent();
}

// ================================================================ navigation
function wireNav() {
  $("nav").addEventListener("click", (e) => {
    const b = e.target.closest(".nav-item"); if (!b) return;
    const t = Tabs.cur, name = b.dataset.tab;
    if (t.loading && COMPANY_TABS.includes(name)) { t.loadTab = name; skelCompany(name); return; }
    if (t.loading) { t.nav++; t.loading = t.loadTab = null; paintTabsSoon(); }
    showTab(name);
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "/" && document.activeElement.tagName !== "INPUT") { e.preventDefault(); $("search").focus(); $("search").select(); }
  });
}

// Every view change goes through here, so it's also where a tab's history is written.
function showTab(name, opts) {
  paintView(name);
  if (opts && opts.scroll) { const c = $("content"); c.scrollTop = opts.scroll; requestAnimationFrame(() => { c.scrollTop = opts.scroll; }); }
  recordNav(); paintTabsSoon();
}

function paintView(name) {
  state.tab = name;
  document.querySelectorAll(".nav-item").forEach((b) => b.classList.toggle("active", b.dataset.tab === name));
  const isCo = COMPANY_TABS.includes(name);
  const noCo = isCo && !state.company;
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", !noCo && t.id === "tab-" + name));
  $("co-empty").classList.toggle("hidden", !noCo);
  $("co-head").classList.toggle("hidden", !isCo || noCo);
  $("content").scrollTop = 0;
  if (noCo) return;
  if (name === "overview") renderOverview();
  else if (name === "comps") renderComps();
  else if (name === "analysts") renderAnalysts();
  else if (name === "financials") renderFinancials();
  else if (name === "model") renderModel();
  else if (name === "thesis") renderThesis();
  else if (name === "debt") renderDebt();
  else if (name === "yolo") renderYolo();
}

function setSeg(id, v) {
  document.querySelectorAll(`#${id} button`).forEach((b) => b.classList.toggle("on", (b.dataset.v || b.dataset.r) === v));
}
function onSeg(id, fn) {
  $(id).addEventListener("click", (e) => {
    const b = e.target.closest("button"); if (!b) return;
    const v = b.dataset.v || b.dataset.r;
    setSeg(id, v); fn(v);
  });
}

function renderRecent() {
  const list = state.recent || [];
  $("recent-wrap").classList.toggle("hidden", list.length === 0);
  $("recent").innerHTML = list.map((s) =>
    `<button class="recent-item ${s === state.symbol ? "on" : ""}" data-sym="${esc(s)}"><b>${esc(s)}</b></button>`).join("");
  $("recent").onclick = (e) => { const b = e.target.closest("[data-sym]"); if (b) openCompany(b.dataset.sym); };
}

// ================================================================ search
let searchTimer = 0, searchSeq = 0, searchHits = [], searchSel = -1;
function wireSearch() {
  const input = $("search");
  input.addEventListener("input", () => {
    clearTimeout(searchTimer);
    const q = input.value.trim();
    if (!q) { closeSearch(); return; }
    searchTimer = setTimeout(() => runSearch(q), 180);
  });
  input.addEventListener("keydown", (e) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      if (!searchHits.length) return;
      searchSel = (searchSel + (e.key === "ArrowDown" ? 1 : -1) + searchHits.length) % searchHits.length;
      paintSearch();
    } else if (e.key === "Enter") {
      e.preventDefault();
      const hit = searchHits[searchSel >= 0 ? searchSel : -1], sym = hit ? hit.symbol : input.value;
      if (e.ctrlKey || e.altKey) openInNewTab(sym, false); else openCompany(sym);
    } else if (e.key === "Escape") { closeSearch(); input.blur(); }
  });
  input.addEventListener("blur", () => setTimeout(closeSearch, 150));
  $("search-drop").addEventListener("mousedown", (e) => {
    const it = e.target.closest("[data-sym]"); if (!it) return;
    e.preventDefault();
    if (e.button === 1 || e.ctrlKey || e.metaKey) openInNewTab(it.dataset.sym, e.button === 1 || !e.shiftKey);
    else if (e.button === 0) openCompany(it.dataset.sym);
  });
}
async function runSearch(q) {
  const seq = ++searchSeq;
  try {
    const hits = await call("search", { q }, { quiet: true });
    if (seq !== searchSeq) return;   // a newer keystroke already asked
    searchHits = hits; searchSel = hits.length ? 0 : -1;
    paintSearch(q);
  } catch { /* typing through a hiccup is fine — Enter still opens the raw ticker */ }
}
function paintSearch(q) {
  const drop = $("search-drop");
  if (!searchHits.length) {
    drop.innerHTML = `<div class="sd-empty">No matches. Press Enter to open “${esc(q || $("search").value)}” as a ticker.</div>`;
  } else {
    drop.innerHTML = searchHits.map((h, i) => `
      <div class="sd-item ${i === searchSel ? "sel" : ""}" data-sym="${esc(h.symbol)}">
        <span class="sd-sym">${esc(h.symbol)}</span>
        <span class="sd-name">${esc(h.name)}</span>
        <span class="sd-meta">${esc(h.type)} · ${esc(h.exchange)}</span>
      </div>`).join("") + `<div class="sd-hint">Enter opens it here · Ctrl+Enter or Ctrl+click opens a new tab</div>`;
  }
  drop.classList.remove("hidden");
}
function closeSearch() { $("search-drop").classList.add("hidden"); searchHits = []; searchSel = -1; }

// ================================================================ skeletons
// Grey placeholders in the shape of what's on its way, so a slow call reads as loading rather
// than empty, and the page doesn't jump when the numbers land.
const sk = (w, h = 11, css = "") =>
  `<i class="skel" aria-hidden="true" style="width:${typeof w === "number" ? w + "%" : w};height:${h}px${css ? ";" + css : ""}"></i>`;
// Ragged but repeatable widths, so a column of placeholders looks like text.
const skW = (i, lo = 40, hi = 90) => lo + ((i * 37 + 11) % (hi - lo));
const skRep = (n, fn) => Array.from({ length: n }, (_, i) => fn(i)).join("");
const skCard = (lines, title = 30) => `${sk(title, 13, "margin-bottom:14px")}${skRep(lines, (i) => sk(skW(i, 55, 95), 10, "margin:10px 0"))}`;

function skelMarket() {
  $("market-asof").innerHTML = sk("260px", 10, "margin-top:5px");
  $("board").innerHTML = skRep(2, (g) => `
    <div class="board-group" aria-busy="true">
      <div class="board-title">${sk(g ? "90px" : "130px", 10)}</div>
      <div class="tiles">${skRep(g ? 4 : 8, (i) => `<div class="tile skel-box">
        ${sk(skW(i, 40, 70), 10)}${sk(skW(i + 3, 35, 55), 18, "margin:8px 0 7px")}${sk(skW(i + 5, 40, 60), 10)}</div>`)}</div>
    </div>`);
  $("movers").innerHTML = skRep(3, () => `
    <div class="card" aria-busy="true"><div class="card-title">${sk(40, 12)}</div>
      ${skRep(8, (i) => `<div class="mv-row skel-box">${sk("44px")}${sk(skW(i, 45, 90), 10)}${sk("48px")}${sk("52px", 18, "border-radius:999px")}</div>`)}
    </div>`);
}

function skelHead() {
  $("co-head").innerHTML = `
    <div aria-busy="true">${sk("280px", 22)}${sk("360px", 11, "margin-top:9px")}</div>
    <div class="co-price">${sk("140px", 26, "margin-left:auto")}${sk("110px", 18, "margin:8px 0 0 auto;border-radius:999px")}</div>`;
}

function skelChart() {
  // A faint mountain where the price line will be, volume stubs underneath.
  $("chart").innerHTML = `<div class="skel-chart" aria-busy="true">
    <div class="skel skel-area"></div>
    <div class="skel-vol">${skRep(36, (i) => `<i class="skel" style="height:${8 + ((i * 53) % 34)}px"></i>`)}</div>
  </div>`;
  $("chart-readout").innerHTML = sk("220px", 14);
  $("chart-stats").innerHTML = skRep(6, (i) => `<div class="cs">${sk("48px", 9)}${sk(`${skW(i, 56, 80)}px`, 13, "margin-top:5px")}</div>`);
}

const skelFeed = (badge) => skRep(5, (i) => `<div class="feed-row skel-box">
  ${badge ? sk("38px", 18, "flex-shrink:0;border-radius:4px") : ""}
  <span class="feed-main">${sk(skW(i, 60, 95), 11)}${sk(skW(i + 2, 25, 45), 9, "margin-top:7px")}</span>
  ${badge ? sk("62px", 10, "flex-shrink:0") : ""}</div>`);

function skelOverview() {
  skelChart();
  $("ov-street").innerHTML = `${sk(36, 13)}${sk("96px", 22, "margin-top:14px;border-radius:999px")}
    <div class="kv-grid">${skRep(4, (i) => `<div class="kv">${sk(50, 9)}${sk(skW(i, 40, 70), 15, "margin-top:5px")}</div>`)}</div>
    ${sk(100, 10, "margin-top:16px;border-radius:999px")}`;
  $("ov-multiples").innerHTML = `${sk(40, 13)}
    <div class="kv-grid">${skRep(8, (i) => `<div class="kv">${sk(skW(i, 45, 75), 9)}${sk(skW(i + 1, 35, 60), 15, "margin-top:5px")}</div>`)}</div>`;
  $("ov-stats").innerHTML = skRep(4, (g) => `<div class="stat-group"><h4>${sk(skW(g, 35, 55), 9)}</h4>
    ${skRep(6, (i) => `<div class="stat-row">${sk(skW(g * 6 + i, 35, 60), 10)}${sk(skW(g + i, 18, 30), 10)}</div>`)}</div>`);
  $("ov-about").innerHTML = `${sk(30, 13)}${skRep(4, (i) => sk(i === 3 ? 55 : 100, 10, "margin-top:11px"))}`;
  paintFeeds({});
}

// known: the tickers are already settled (this company's peer set), so they show for real.
function skelComps(known = true) {
  const M = state.metricMap, keys = state.compKeys || [];
  const syms = known ? [state.symbol, ...state.compSet.filter((s) => s !== state.symbol)] : Array(6).fill(null);
  const cells = (i) => skRep(keys.length + 2, (k) => `<td>${sk(`${skW(i + k, 34, 58)}px`, 10, "margin-left:auto")}</td>`);
  $("comps-table").innerHTML = `<thead><tr><th class="l">Ticker</th><th class="l">Company</th><th>Price</th><th>Day</th>
      ${keys.map((k) => `<th title="${esc(M[k].label)}">${esc(COMP_SHORT[k] || M[k].label)}</th>`).join("")}</tr></thead>
    <tbody aria-busy="true">${syms.map((s, i) => `<tr class="${i ? "" : "subject"}"><td class="l">${s ? `<b>${esc(s)}</b>` : sk("42px", 11)}</td>
      <td class="l">${sk(`${skW(i, 90, 160)}px`, 10)}</td>${cells(i)}</tr>`).join("")}</tbody>`;
  $("comps-ranges").innerHTML = skRep(6, (i) => `<div>${sk(skW(i, 25, 45), 10)}${sk(100, 8, "margin:10px 0 4px;border-radius:999px")}</div>`);
}

function skelAnalysts() {
  $("an-top").innerHTML = skRep(3, () => `<div class="card">${skCard(2, 45)}</div>`);
  $("an-dist").innerHTML = skCard(5);
  $("an-trend").innerHTML = skCard(5);
  $("an-firms-sub").innerHTML = sk("220px", 10);
  $("an-firms").innerHTML = skRep(3, () => `<div>${skCard(4, 40)}</div>`);
}

function skelStatement() {
  $("st-meta").innerHTML = sk("min(520px, 70%)", 10, "margin:2px 0");
  const cols = 6;
  $("st-wrap").innerHTML = `<table class="st" aria-busy="true"><thead><tr><th>${sk("90px", 10)}</th>
      ${skRep(cols, () => `<th>${sk("52px", 11, "margin-left:auto")}${sk("40px", 8, "margin:5px 0 0 auto")}</th>`)}</tr></thead>
    <tbody>${skRep(16, (r) => `<tr><td style="text-align:left">${sk(`${skW(r, 110, 230)}px`, 10, r % 5 ? "" : "opacity:.6")}</td>
      ${skRep(cols, (c) => `<td>${sk(`${skW(r + c, 38, 64)}px`, 10, "margin-left:auto")}</td>`)}</tr>`)}</tbody></table>`;
}

function skelModel() {
  $("md-loading").innerHTML = `
    <div class="card md-bar skel-bar" aria-busy="true">
      <div class="md-res">${sk("150px", 30)}${skRep(4, (i) => `<div>${sk("70px", 9)}${sk(`${skW(i, 56, 90)}px`, 16, "margin-top:6px")}</div>`)}</div>
      <div class="md-loading-note">Building the model from the latest annual statements…</div>
    </div>
    <div class="md-layout">
      <div class="md-main"><div class="card">${sk("220px", 13)}${sk("320px", 10, "margin:8px 0 18px")}
        <div class="md-wrap"><table class="md"><thead><tr><th>${sk("120px", 10)}</th>${skRep(8, () => `<th>${sk("44px", 10, "margin-left:auto")}</th>`)}</tr></thead>
        <tbody>${skRep(14, (r) => `<tr><th>${sk(`${skW(r, 90, 160)}px`, 10)}</th>${skRep(8, (c) => `<td>${sk(`${skW(r + c, 34, 52)}px`, 10, "margin-left:auto")}</td>`)}</tr>`)}</tbody></table></div>
      </div></div>
      <aside class="md-side">${skRep(3, (i) => `<div class="card">${skCard(i ? 4 : 6, 45)}</div>`)}</aside>
    </div>`;
}

function skelYolo() {
  $("yolo-skel").innerHTML = `<div class="yolo-head">${sk("30px", 9)}${sk("70px", 9)}${sk("90px", 9)}${sk("60px", 9, "margin-left:auto")}${sk("70px", 9, "margin-left:auto")}</div>
    ${skRep(6, (i) => `<div class="yolo-row skel-box" aria-busy="true">
      ${sk("40px", 40, "border-radius:10px")}
      <div>${sk(skW(i, 45, 80), 14)}${sk(skW(i + 2, 30, 55), 10, "margin-top:7px")}
        <div style="display:flex;gap:6px;margin-top:10px">${skRep(3, (t) => sk(`${skW(i + t, 60, 96)}px`, 18, "border-radius:999px"))}</div></div>
      <div>${skRep(3, (l) => sk(skW(i + l, 60, 98), 10, "margin:3px 0 9px"))}</div>
      <div>${sk("70px", 14, "margin-left:auto")}${sk("44px", 9, "margin:7px 0 0 auto")}</div>
      <div>${sk(skW(i, 60, 95), 10, "margin-left:auto")}${sk("50px", 9, "margin:7px 0 0 auto")}</div>
    </div>`)}`;
}

// While a company loads, the tab it's about to open shows in outline.
function skelCompany(tab) {
  document.querySelectorAll(".nav-item").forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t.id === "tab-" + tab));
  $("co-empty").classList.add("hidden");
  $("co-head").classList.remove("hidden");
  $("content").scrollTop = 0;
  skelHead();
  if (tab === "overview") skelOverview();
  else if (tab === "comps") {
    $("peer-chips").innerHTML = skRep(4, (i) => sk(`${skW(i, 56, 76)}px`, 26, "border-radius:999px"));
    $("comps-basis").textContent = "";
    skelComps(false);
  } else if (tab === "analysts") skelAnalysts();
  else if (tab === "financials") skelStatement();
  else if (tab === "model") { $("md-body").classList.add("hidden"); $("md-error").classList.add("hidden"); skelModel(); $("md-loading").classList.remove("hidden"); }
  else if (tab === "thesis") skelThesis();
  else if (tab === "debt") skelDebt();
}

// ================================================================ tabs & history
// Chrome-style tabs over the one set of views. Each tab is its own page: a company (or none), the
// view it's on, the mode, and everything already loaded for it, so switching back is instant.
// The live tab's page is `state` itself; the others keep a snapshot. Each tab has its own Back /
// Forward history and remembers the last few companies it left, so Back is instant too.
const PAGE_KEYS = ["tab", "mode", "symbol", "company", "compSet", "compRows", "compSort", "_ffComps",
  "chartRange", "chartSeries", "stKind", "stFreq", "statements", "feeds", "model", "thesis", "debt"];
const CO_KEYS = ["symbol", "company", "compSet", "compRows", "compSort", "_ffComps", "chartRange", "chartSeries",
  "statements", "feeds", "model", "thesis", "debt"];
const SECTION = {
  markets: ["Markets", "i-markets"], yolo: ["YOLO board", "i-rocket"],
  overview: ["Overview", "i-overview"], comps: ["Comps", "i-comps"], analysts: ["Analysts", "i-analysts"],
  financials: ["Financials", "i-financials"], debt: ["Debt", "i-debt"], model: ["Model", "i-model"], thesis: ["YOLO rationale", "i-rocket"],
};
const ST_NAME = { income: "Income statement", balance: "Balance sheet", cashflow: "Cash flow" };
const MODEL_SHORT = { dcf: "DCF", nav: "NAV", lbo: "LBO", er: "Excess return" };
const MAX_TABS = 20, CACHE_MIN = 15;

// dragging: the tab element being dragged along the strip (or a placeholder for one from another
// window); dragOut: this window's tab being dragged; sent: the last one, until it's let go;
// incoming: another window's tab being dragged.
const Tabs = { list: [], cur: null, seq: 0, closed: [], dragging: null, dragOut: null, sent: null, incoming: null };
const pageOf = (t) => (t === Tabs.cur ? state : t.snap);
const tabById = (id) => Tabs.list.find((t) => t.id === id);
const curEntry = () => Tabs.cur && Tabs.cur.hist[Tabs.cur.idx];

function blankPage(mode) {
  return { tab: mode === "yolo" ? "yolo" : "markets", mode, symbol: null, company: null, compSet: [], compRows: null,
    compSort: null, _ffComps: null, chartRange: "1y", chartSeries: null, stKind: "income", stFreq: "annual",
    statements: {}, feeds: null, model: null, thesis: null, debt: null };
}
function makeTab(mode) {
  return { id: ++Tabs.seq, snap: blankPage(mode), hist: [], idx: -1, cache: new Map(), nav: 0, loading: null, loadTab: null, stale: false, opener: null };
}

// What a page keeps when it's left. A call still on its way is dropped if it lands on another
// company (each one checks the symbol), so its placeholder isn't kept either: coming back asks again.
function pickPage(keys, src = state) {
  const s = {};
  keys.forEach((k) => (s[k] = src[k]));
  if (s.feeds && !s.feeds.filings && !s.feeds.news) s.feeds = null;
  if (!s.compRows) s._ffComps = null;
  if (s.thesis && s.thesis.loading) s.thesis = null;
  return s;
}

// Everything outside the views that depends on the page.
function paintChrome() {
  setMode(state.mode, { keepTab: true });
  $("export-result").classList.add("hidden");
  $("btn-export-top").disabled = !state.company;
  renderRecent();
  if (state.company) renderCoHead();
}

function activateTab(t) {
  if (!t || t === Tabs.cur) return;
  if (Tabs.cur) Tabs.cur.snap = pickPage(PAGE_KEYS);
  Tabs.cur = t;
  Object.assign(state, t.snap); t.snap = null;
  closeSearch(); hideTabCard();
  paintChrome();
  if (t.loading) skelCompany(t.loadTab || "overview");
  else showTab(state.tab, { scroll: (curEntry() || {}).scroll });
  paintHistBtns();
  if (t.stale && !t.loading) { t.stale = false; if (state.symbol) { state.model = null; openCompany(state.symbol); } }
  else if (state.company && !t.loading) refreshQuote();
  paintTabs();
}

function newTab(opts = {}) {
  if (Tabs.list.length >= MAX_TABS) { toast("That's a lot of tabs", `Close one to open another (up to ${MAX_TABS}).`, "warn"); return null; }
  const t = makeTab(opts.mode || state.mode);
  let i = Tabs.list.length;
  if (opts.next && Tabs.cur) {
    // Like a browser: tabs opened from a page line up just after it, in the order they were opened.
    i = Tabs.list.indexOf(Tabs.cur) + 1;
    while (Tabs.list[i] && Tabs.list[i].opener === Tabs.cur.id) i++;
    t.opener = Tabs.cur.id;
  }
  Tabs.list.splice(i, 0, t);
  if (opts.activate !== false) activateTab(t); else paintTabs();
  return t;
}

function openInNewTab(sym, background) {
  sym = String(sym || "").trim().toUpperCase(); if (!sym) return;
  closeSearch(); $("search").value = ""; $("search").blur();
  const t = newTab({ next: true, activate: false });
  if (!t) return;
  openCompany(sym, { into: t, tab: "overview" });
  if (!background) activateTab(t);
}

function closeTab(t, opts = {}) {
  if (!t || Tabs.list.length < 2) return;
  const i = Tabs.list.indexOf(t);
  if (t === Tabs.cur) activateTab(Tabs.list[i + 1] || Tabs.list[i - 1]);
  Tabs.list.splice(i, 1);
  t.nav++; t.loading = t.loadTab = null;   // a company still loading into it is dropped
  Tabs.list.forEach((x) => { if (x.opener === t.id) x.opener = null; });
  if (!opts.moved) { Tabs.closed.push({ t, i }); if (Tabs.closed.length > 10) Tabs.closed.shift(); }
  hideTabCard(); paintTabs();
}
function reopenTab() {
  const c = Tabs.closed.pop(); if (!c) return;
  if (Tabs.list.length >= MAX_TABS) { Tabs.closed.push(c); return; }
  Tabs.list.splice(Math.min(c.i, Tabs.list.length), 0, c.t);
  activateTab(c.t);
}

// Tabs that aren't on screen reload their company the next time they're opened (a new day, new
// data sources or settings).
function staleOthers() {
  Tabs.list.forEach((t) => { t.cache.clear(); if (t !== Tabs.cur && pageOf(t).company) t.stale = true; });
}

// ---------------------------------------------------------------- history
// One entry per view: Markets, the YOLO board, or a company tab of one company. Sub-choices
// (statement, chart range, model) aren't steps of their own.
const entryKey = (e) => (COMPANY_TABS.includes(e.tab) ? `${e.tab}|${e.symbol}` : e.tab);
const entryLabel = (e) => (COMPANY_TABS.includes(e.tab) && e.symbol ? `${e.symbol} · ${SECTION[e.tab][0]}` : (SECTION[e.tab] || SECTION.markets)[0]);

function recordNav(t = Tabs.cur) {
  if (!t) return;
  const s = pageOf(t), e = { tab: s.tab, mode: s.mode, symbol: s.symbol, scroll: 0 };
  const at = t.hist[t.idx];
  if (at && entryKey(at) === entryKey(e)) return;
  t.hist.splice(t.idx + 1, Infinity, e);
  if (t.hist.length > 60) t.hist.shift();
  t.idx = t.hist.length - 1;
  if (t === Tabs.cur) paintHistBtns();
}

async function histGo(d) {
  const t = Tabs.cur, i = t.idx + d, e = t.hist[i];
  if (!e) return;
  const from = t.idx, mode0 = state.mode;
  t.nav++; t.loading = t.loadTab = null;   // whatever was loading is no longer wanted
  t.idx = i; paintHistBtns(); hideTabCard();
  if (e.mode !== state.mode) setMode(e.mode, { keepTab: true });
  if (COMPANY_TABS.includes(e.tab) && e.symbol && e.symbol !== state.symbol && !restoreCompany(t, e.symbol)) {
    await openCompany(e.symbol, { tab: e.tab, scroll: e.scroll, revert: () => {
      t.idx = from; paintHistBtns();
      if (state.mode !== mode0) setMode(mode0, { keepTab: true });
    } });
    return;
  }
  showTab(e.tab, { scroll: e.scroll });
}

function stashCompany(t, src = state) {
  if (!src.company) return;
  t.cache.delete(src.symbol);
  t.cache.set(src.symbol, { at: Date.now(), data: pickPage(CO_KEYS, src) });
  while (t.cache.size > 6) t.cache.delete(t.cache.keys().next().value);
}
function restoreCompany(t, sym) {
  const c = t.cache.get(sym);
  if (!c || Date.now() - c.at > CACHE_MIN * 60e3) return false;
  stashCompany(t);
  t.cache.delete(sym);
  Object.assign(state, c.data);
  paintChrome();
  refreshQuote();
  return true;
}

function paintHistBtns() {
  const t = Tabs.cur; if (!t) return;
  const b = t.hist[t.idx - 1], f = t.hist[t.idx + 1];
  $("nav-back").disabled = !b; $("nav-fwd").disabled = !f;
  $("nav-back").title = b ? `Back to ${entryLabel(b)} (Alt+←)` : "Back (Alt+←)";
  $("nav-fwd").title = f ? `Forward to ${entryLabel(f)} (Alt+→)` : "Forward (Alt+→)";
}

// ---------------------------------------------------------------- the strip
const tone = (v, txt) => `<span class="${dir(v)}">${txt}</span>`;
const ratingName = (a) => {
  const k = a && a.recommendationKey && a.recommendationKey !== "none" ? a.recommendationKey.replace(/_/g, " ") : "";
  return k ? k[0].toUpperCase() + k.slice(1) : "";
};

// The model's headline value, whichever model the tab is showing.
function modelSum(m) {
  if (!m || !m.res) return null;
  const id = (m.a.alt && m.a.alt.model) || (m.base.kit ? m.base.kit.recommended : "dcf");
  const value = id === "dcf" ? (m.res.gordonInvalid ? NaN : m.res.price) : m.sumAlt && m.sumAlt.id === id ? m.sumAlt.v : NaN;
  return { label: MODEL_SHORT[id] || "Model", value, up: isFinite(value) && m.base.price ? value / m.base.price - 1 : null };
}
const modelText = (ms) => !ms ? "" : !isFinite(ms.value) ? `${ms.label} –`
  : `${ms.label} ${fmt.price(ms.value)}${ms.up != null ? " " + tone(ms.up, (ms.up >= 0 ? "+" : "") + fmt.pct(ms.up, 0)) : ""}`;

function marketLine() {
  const m = state.market, q = m && m.board && m.board.ok && m.board.data[0];
  return q ? `${esc(q.name)} ${tone(q.changePct, fmt.chg(q.changePct))}` : "Today's board";
}
function yoloLine() {
  const d = state.yolo && state.yolo.data;
  if (!d) return "Today's 100x screen";
  const n = d.picks.filter((p) => p.level >= 9).length;
  return `${n} pick${n === 1 ? "" : "s"} at 9–10`;
}
function sectionLine(s) {
  const c = s.company;
  switch (s.tab) {
    case "comps": return `vs ${s.compSet.length} peer${s.compSet.length === 1 ? "" : "s"}`;
    case "analysts": { const a = c.analysts || {}, r = ratingName(a); return `${r || "No rating"}${a.targetMean ? ` · target ${fmt.price(a.targetMean)}` : ""}`; }
    case "financials": return `${ST_NAME[s.stKind] || ""} · ${s.stFreq}`;
    case "model": return modelText(modelSum(s.model)) || "building…";
    case "debt": { const st = s.debt && s.debt.stats; return st && st.yield != null ? `cost of debt ${fmt.pct(st.yield / 100, 2)}` : s.debt && s.debt.data ? "no traded bonds" : "loading…"; }
    case "thesis": {
      const d = s.thesis && s.thesis.data;
      return !d ? "scoring…" : !d.pick ? "left out of the screen" : `level ${d.pick.level}/10 · ${d.onBoard ? "on the board" : "off the board"}`;
    }
    default: return `${fmt.price(c.price)} ${tone(c.changePct, fmt.chg(c.changePct))}`;
  }
}
function tabSummary(t) {
  const s = pageOf(t), c = s.company, sec = SECTION[s.tab] || SECTION.markets;
  if (t.loading) return { icon: "", title: `<b>${esc(t.loading)}</b>`, sub: "Loading…" };
  if (s.tab === "markets") return { icon: sec[1], title: "<b>Markets</b>", sub: marketLine() };
  if (s.tab === "yolo") return { icon: sec[1], title: "<b>YOLO board</b>", sub: yoloLine() };
  if (!c) return { icon: sec[1], title: "<b>New tab</b>", sub: "Search a company" };
  return { icon: sec[1], title: `<b>${esc(c.symbol)}</b><span class="wt-n">${esc(c.name)}</span>`, sub: `${esc(sec[0])} · ${sectionLine(s)}` };
}

let tabsRaf = 0;
function paintTabsSoon() { if (!tabsRaf) tabsRaf = requestAnimationFrame(() => { tabsRaf = 0; paintTabs(); }); }
// Tabs are patched in place rather than rebuilt, so one being pressed or dragged stays put.
function paintTabs() {
  const el = $("wtabs"); if (!el || Tabs.dragging) return;
  const many = Tabs.list.length > 1, keep = new Set();
  Tabs.list.forEach((t, i) => {
    let w = el.querySelector(`.wtab[data-tid="${t.id}"]`);
    if (!w) {
      w = document.createElement("div");
      w.className = "wtab"; w.dataset.tid = t.id; w.draggable = true; w.setAttribute("role", "tab");
    }
    const s = tabSummary(t);
    const html = `<span class="wt-ic ${s.icon === "i-rocket" ? "yolo" : ""}">${t.loading ? `<i class="wt-spin"></i>` : icon(s.icon)}</span>
      <span class="wt-txt"><span class="wt-t">${s.title}</span><span class="wt-s">${s.sub}</span></span>
      ${many ? `<button class="wt-x" data-close="${t.id}" title="Close tab (Ctrl+W)" aria-label="Close tab">${icon("i-close")}</button>` : ""}`;
    if (w._html !== html) { w.innerHTML = html; w._html = html; }
    const on = t === Tabs.cur;
    w.classList.toggle("on", on); w.setAttribute("aria-selected", on);
    if (el.children[i] !== w) el.insertBefore(w, el.children[i] || null);
    keep.add(w);
  });
  [...el.children].forEach((w) => { if (!keep.has(w)) w.remove(); });
  const on = el.querySelector(".wtab.on");
  if (on) {
    if (on.offsetLeft < el.scrollLeft) el.scrollLeft = on.offsetLeft;
    else if (on.offsetLeft + on.offsetWidth > el.scrollLeft + el.clientWidth) el.scrollLeft = on.offsetLeft + on.offsetWidth - el.clientWidth;
  }
  $("tab-new").disabled = Tabs.list.length >= MAX_TABS;
  paintWinTitle();
}

// ---------------------------------------------------------------- hover summary
let cardTimer = 0, cardFor = null;
function tabCardHtml(t) {
  const s = pageOf(t), c = s.company, sec = SECTION[s.tab] || SECTION.markets;
  const row = (k, v) => (v ? `<div class="tc-row"><span>${k}</span><b>${v}</b></div>` : "");
  const back = t.idx, fwd = t.hist.length - 1 - t.idx;
  const hist = row("History", back > 0 || fwd > 0 ? `${back} back · ${fwd} forward` : "");
  if (t.loading) return `<div class="tc-title">Opening ${esc(t.loading)}…</div>${hist}`;
  const open = c ? row("Company open", `${esc(c.symbol)} · ${esc(c.name)}`) : "";
  if (s.tab === "markets") {
    const m = state.market, qs = (m && m.board && m.board.ok ? m.board.data : []).slice(0, 5);
    return `<div class="tc-title">Markets</div><div class="tc-sub">Indices, rates and today's movers</div>
      ${qs.map((q) => row(esc(q.name), tone(q.changePct, fmt.chg(q.changePct)))).join("")}${open}${hist}`;
  }
  if (s.tab === "yolo") {
    const d = state.yolo && state.yolo.data, top = d ? yoloSorted(d.picks, { key: "level", dir: -1 }).slice(0, 4) : [];
    return `<div class="tc-title">YOLO board</div><div class="tc-sub">Mining, biotech and small caps with a path to 100x</div>
      ${top.map((p) => row(`${esc(p.symbol)} <small>${esc(p.groupLabel)}</small>`, `level ${p.level}/10`)).join("")}${open}${hist}`;
  }
  if (!c) return `<div class="tc-title">New tab</div><div class="tc-sub">Search a company or ticker to start.</div>${hist}`;
  const a = c.analysts || {}, up = a.targetMean && c.price ? a.targetMean / c.price - 1 : null;
  const ms = modelSum(s.model);
  const yp = (s.thesis && s.thesis.data && s.thesis.data.pick) || (s.mode === "yolo" ? boardPick(c.symbol) : null);
  const peers = s.compSet.length ? esc(s.compSet.slice(0, 5).join(", ")) + (s.compSet.length > 5 ? ` +${s.compSet.length - 5}` : "") : "";
  return `<div class="tc-title">${esc(c.name)}</div>
    <div class="tc-sub"><b>${esc(c.symbol)}</b> · ${esc([c.exchange, c.sector, c.industry].filter(Boolean).join(" · "))}</div>
    <div class="tc-px">${fmt.price(c.price)}<small>${esc(c.currency)}</small>${chgPill(c.changePct)}</div>
    ${row("Viewing", esc(sec[0]))}
    ${row("Analysts", `${ratingName(a) || "No rating"}${a.targetMean ? ` · ${fmt.price(a.targetMean)}${up != null ? " " + tone(up, `(${up >= 0 ? "+" : ""}${fmt.pct(up, 0)})`) : ""}` : ""}`)}
    ${row("Model", ms ? modelText(ms) : s.model ? "building…" : "not opened yet")}
    ${row("Peers", peers)}
    ${yp ? row("YOLO", `level ${yp.level}/10 · score ${fmt.num(yp.score, 1)}`) : ""}
    ${hist}`;
}
function showTabCard(t, w) {
  if (!Tabs.list.includes(t) || Tabs.dragging) return;
  const card = $("tab-card"); cardFor = t;
  card.innerHTML = tabCardHtml(t);
  card.classList.remove("hidden");
  const r = w.getBoundingClientRect();
  card.style.left = Math.max(8, Math.min(r.left, innerWidth - card.offsetWidth - 8)) + "px";
  card.style.top = r.bottom + 6 + "px";
}
function hideTabCard() { cardFor = null; clearTimeout(cardTimer); const c = $("tab-card"); if (c) c.classList.add("hidden"); }

// ---------------------------------------------------------------- wiring
function wireTabs() {
  const el = $("wtabs");
  el.addEventListener("mousedown", (e) => {
    if (e.button === 1) { e.preventDefault(); return; }   // no autoscroll; auxclick closes
    if (e.button !== 0 || e.target.closest(".wt-x")) return;
    hideTabCard();
    const w = e.target.closest(".wtab"); if (w) activateTab(tabById(+w.dataset.tid));   // tabs switch on press, as in a browser
  });
  el.addEventListener("click", (e) => {
    const x = e.target.closest("[data-close]"); if (x) { e.stopPropagation(); closeTab(tabById(+x.dataset.close)); }
  });
  el.addEventListener("auxclick", (e) => {
    if (e.button !== 1) return;
    const w = e.target.closest(".wtab"); if (w) { e.preventDefault(); closeTab(tabById(+w.dataset.tid)); }
  });
  el.addEventListener("wheel", (e) => {
    if (el.scrollWidth <= el.clientWidth || Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
    el.scrollLeft += e.deltaY; e.preventDefault();
  }, { passive: false });
  el.addEventListener("mouseover", (e) => {
    const w = e.target.closest(".wtab"); if (!w) return;
    const t = tabById(+w.dataset.tid); if (!t || t === cardFor) return;
    clearTimeout(cardTimer);
    cardTimer = setTimeout(() => showTabCard(t, w), $("tab-card").classList.contains("hidden") ? 450 : 60);
  });
  el.addEventListener("mouseleave", hideTabCard);

  wireTabDrag();

  $("tab-new").addEventListener("click", () => { if (newTab()) $("search").focus(); });
  $("nav-back").addEventListener("click", () => histGo(-1));
  $("nav-fwd").addEventListener("click", () => histGo(1));
  // Each view remembers how far down it was, for Back and for coming back to the tab.
  $("content").addEventListener("scroll", () => {
    const e = curEntry(); if (e && !Tabs.cur.loading) e.scroll = $("content").scrollTop;
  }, { passive: true });

  // Ctrl+click or middle-click a company anywhere opens it in a new tab behind this one;
  // Ctrl+Shift+click opens it and switches to it.
  const symAt = (e) => {
    const el2 = e.target.closest("[data-sym]");
    return el2 && !el2.closest("#search-drop") ? el2.dataset.sym : null;
  };
  document.addEventListener("click", (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.button !== 0) return;
    const sym = symAt(e); if (!sym) return;
    e.preventDefault(); e.stopPropagation();
    openInNewTab(sym, !e.shiftKey);
  }, true);
  document.addEventListener("auxclick", (e) => {
    if (e.button !== 1) return;
    const sym = symAt(e); if (!sym) return;
    e.preventDefault(); e.stopPropagation();
    openInNewTab(sym, true);
  }, true);
  document.addEventListener("mousedown", (e) => {
    if (e.button === 1 && symAt(e)) e.preventDefault();
    if (e.button === 3 || e.button === 4) e.preventDefault();
  }, true);
  // The mouse's own back / forward buttons.
  document.addEventListener("mouseup", (e) => {
    if (e.button !== 3 && e.button !== 4) return;
    e.preventDefault();
    if (!$("gate")) histGo(e.button === 3 ? -1 : 1);
  });

  document.addEventListener("keydown", (e) => {
    if ($("gate")) return;
    const k = e.key;
    if (e.altKey && !e.ctrlKey && !e.shiftKey && (k === "ArrowLeft" || k === "ArrowRight")) {
      e.preventDefault(); histGo(k === "ArrowLeft" ? -1 : 1); return;
    }
    if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
    if (k === "Tab" || k === "PageDown" || k === "PageUp") {
      e.preventDefault();
      const n = Tabs.list.length, i = Tabs.list.indexOf(Tabs.cur);
      activateTab(Tabs.list[(i + ((k === "Tab" && !e.shiftKey) || k === "PageDown" ? 1 : -1) + n) % n]);
      return;
    }
    const lk = k.toLowerCase();
    if (lk === "t") { e.preventDefault(); if (e.shiftKey) reopenTab(); else if (newTab()) $("search").focus(); return; }
    if (lk === "w" || k === "F4") { e.preventDefault(); closeTab(Tabs.cur); return; }
    if (/^[1-9]$/.test(k)) { e.preventDefault(); activateTab(k === "9" ? Tabs.list[Tabs.list.length - 1] : Tabs.list[+k - 1]); }
  });
}

// ---------------------------------------------------------------- windows
// Tabs move between windows as in a browser. Dragged along the strip, a tab moves there; once it
// leaves the strip, a window carrying it appears under the pointer and follows it (C# keeps a
// spare window loaded and hidden for this, and moves it); back over a strip, that window folds
// away and the strip takes the tab again. A window's only tab moves its whole window. The tab's
// page goes with it (company, view, everything loaded, history), passed through C#.
const TAB_MIME = "application/x-funnel-tab";
const SECONDARY = /[?&]win=tab/.test(location.search);
const SPARE = /[?&]win=spare/.test(location.search);
// spare: a hidden window waiting for a tab; hosting: the id of the drag it's showing the tab of.
const Win = { id: 0, title: null, spare: SPARE, hosting: null };
// A tab's name as plain text ("MSFT Microsoft Corporation"), for the title bar and the drag.
const tabText = (n) => (n ? [...n.childNodes].map((x) => x.textContent).join(" ").replace(/\s+/g, " ").trim() : "");

// A tab's page as JSON, for another window. A tab still loading a company opens it there instead.
function tabPayload(t) {
  const page = t === Tabs.cur ? pickPage(PAGE_KEYS) : t.snap;
  const p = { mode: page.mode, hist: t.hist, idx: t.idx, open: null, page: null };
  const reopen = () => { p.page = null; p.open = page.symbol ? { symbol: page.symbol, tab: page.tab } : null; };
  if (t.loading) p.open = { symbol: t.loading, tab: t.loadTab || "overview" };
  else if (t.stale) reopen();
  else {
    p.page = Object.assign({}, page);
    const d = p.page.debt;   // a call on its way doesn't follow the tab
    if (d && d.loading) p.page.debt = d.data ? Object.assign({}, d, { loading: false }) : null;
  }
  try { return JSON.stringify(p); } catch { reopen(); return JSON.stringify(p); }
}

// Opens a tab that came from another window, at position `at`.
function adoptTab(p, at) {
  const t = makeTab(p.mode || "research");
  if (p.page) t.snap = Object.assign(blankPage(p.mode), p.page);
  t.hist = Array.isArray(p.hist) ? p.hist : [];
  t.idx = Math.min(p.idx == null ? -1 : p.idx, t.hist.length - 1);
  Tabs.list.splice(at == null ? Tabs.list.length : Math.max(0, Math.min(at, Tabs.list.length)), 0, t);
  activateTab(t);
  if (p.open) openCompany(p.open.symbol, { tab: p.open.tab });
  return t;
}

// This window shows only `keep` (dropping the others without a trace).
function onlyTab(keep) {
  Tabs.list.forEach((x) => { if (x !== keep) { x.nav++; x.loading = x.loadTab = null; } });
  Tabs.list = [keep]; Tabs.closed = [];
  paintTabs();
}

// The tab left for another window: no "reopen closed tab" for it, and an emptied window closes.
function dropTab(t) {
  if (!Tabs.list.includes(t)) return;
  if (Tabs.list.length === 1) { call("closeWindow", {}, { quiet: true }).catch(() => {}); return; }
  closeTab(t, { moved: true });
}

async function takeTab(id, at) {
  if (!id || !wv) return;
  let r; try { r = await call("tabTake", { id }, { quiet: true }); } catch { return; }
  if (r && r.tab) adoptTab(JSON.parse(r.tab), at);
}

function ghostTab(d) {
  const g = document.createElement("div");
  g.className = "wtab ghost";
  g.innerHTML = `<span class="wt-ic">${icon("i-plus")}</span><span class="wt-txt"><span class="wt-t">${esc((d && d.title) || "Tab")}</span><span class="wt-s">${esc((d && d.sub) || "from another window")}</span></span>`;
  return g;
}
function dropGhost() {
  $("wtabs").querySelectorAll(".ghost").forEach((g) => g.remove());
  if (Tabs.dragging && !Tabs.dragging.isConnected) Tabs.dragging = null;
}

function wireTabDrag() {
  const el = $("wtabs"), strip = $("tabstrip");
  const inStrip = (n) => n && strip.contains(n);

  el.addEventListener("dragstart", (e) => {
    const w = e.target.closest(".wtab"); if (!w) return;
    hideTabCard();
    const t = tabById(+w.dataset.tid), r = w.getBoundingClientRect(), f = el.firstElementChild.getBoundingClientRect();
    const o = { t, id: `${Win.id}-${t.id}-${Date.now()}`, from: [...el.children].indexOf(w), dropped: false };
    Tabs.dragging = w; Tabs.dragOut = Tabs.sent = o;
    w.classList.add("drag");
    e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData(TAB_MIME, o.id);
    // gx/gy: the grabbed point as if this were the strip's first tab, which it is in a new window.
    if (wv) call("tabDrag", { id: o.id, tab: tabPayload(t), title: tabText(w.querySelector(".wt-t")), sub: tabText(w.querySelector(".wt-s")),
      gx: f.left + (e.clientX - r.left), gy: f.top + (e.clientY - r.top), single: Tabs.list.length === 1 }, { quiet: true }).catch(() => {});
  });
  strip.addEventListener("dragover", (e) => {
    if (Win.hosting) return;   // this window is the one carrying the tab (see wireWindow)
    // A tab from another window gets a placeholder where it would land.
    if (!Tabs.dragging && !Tabs.dragOut && (Tabs.incoming || e.dataTransfer.types.includes(TAB_MIME))) {
      if (Tabs.list.length >= MAX_TABS) return;
      Tabs.dragging = ghostTab(Tabs.incoming); el.appendChild(Tabs.dragging);
    }
    if (!Tabs.dragging) return;
    e.preventDefault(); e.dataTransfer.dropEffect = "move";
    const w = e.target.closest(".wtab");
    if (w && w !== Tabs.dragging && el.contains(w)) {
      const r = w.getBoundingClientRect();
      el.insertBefore(Tabs.dragging, e.clientX < r.left + r.width / 2 ? w : w.nextSibling);
    } else if (!w) {
      const last = [...el.children].filter((x) => x !== Tabs.dragging && !x.classList.contains("detached")).pop();
      if (last && e.clientX > last.getBoundingClientRect().right) el.appendChild(Tabs.dragging);
    }
  });
  strip.addEventListener("dragleave", (e) => {
    if (inStrip(e.relatedTarget)) return;
    if (Tabs.dragOut) {
      const w = Tabs.dragging, others = [...el.children].filter((x) => x !== w);
      if (w) el.insertBefore(w, others[Tabs.dragOut.from] || null);
    } else dropGhost();
  });
  strip.addEventListener("drop", (e) => {
    if (!Tabs.dragging || Win.hosting) return;
    e.preventDefault();
    if (Tabs.dragOut) { Tabs.dragOut.dropped = true; return; }
    const at = [...el.children].indexOf(Tabs.dragging);
    const id = (Tabs.incoming && Tabs.incoming.id) || e.dataTransfer.getData(TAB_MIME);
    dropGhost(); Tabs.dragging = null; Tabs.incoming = null;
    takeTab(id, at);
  });
  el.addEventListener("dragend", (e) => {
    const o = Tabs.dragOut, w = Tabs.dragging;
    if (!o) return;
    if (w) w.classList.remove("drag", "detached");
    Tabs.dragging = Tabs.dragOut = null;
    if (o.dropped) {
      const order = [...el.querySelectorAll(".wtab")].map((x) => +x.dataset.tid);
      Tabs.list.sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
    }
    paintTabs();
    // C# decides what's next: a window carrying the tab stays, or the tab opens in one.
    if (wv) call("tabDragEnd", { id: o.id, dropped: o.dropped || e.dataTransfer.dropEffect !== "none" }, { quiet: true }).catch(() => {});
  });
}

// The window carrying a dragged tab takes the drop anywhere (C# finishes the move), and C# is told
// where this window's tab strip is, so it knows when a dragged tab is over it.
function wireWindow() {
  document.addEventListener("dragover", (e) => { if (Win.hosting) { e.preventDefault(); e.dataTransfer.dropEffect = "move"; } });
  document.addEventListener("drop", (e) => { if (Win.hosting) e.preventDefault(); });
  const strip = $("tabstrip");
  const report = () => {
    const r = strip.getBoundingClientRect();
    if (wv) call("strip", { x: r.left, y: r.top, w: r.width, h: r.height }, { quiet: true }).catch(() => {});
  };
  new ResizeObserver(report).observe(strip);
  report();
}

function onTabWindowEvent(name, data) {
  const o = Tabs.dragOut;
  if (name === "tabDragging") {
    if (data.on) Tabs.incoming = data;
    else if (Tabs.incoming && Tabs.incoming.id === data.id) { Tabs.incoming = null; if (!Tabs.dragOut) dropGhost(); }
  } else if (name === "tabMoved") {
    if (Tabs.sent && Tabs.sent.id === data.id) { const t = Tabs.sent.t; Tabs.sent = null; dropTab(t); }
  } else if (name === "tabDetached" && o && o.id === data.id) {
    // The tab is in its own window now: it leaves this strip (the page stays put until it's let go).
    if (Tabs.dragging) Tabs.dragging.classList.add("detached");
  } else if (name === "tabAttached" && o && o.id === data.id) {
    if (Tabs.dragging) Tabs.dragging.classList.remove("detached");
  } else if (name === "adopt") {
    // Spare window: show the dragged tab (while hidden, so it's drawn by the time it's shown).
    applyInit(data.init);
    Win.hosting = data.hosting;
    onlyTab(adoptTab(JSON.parse(data.tab), 0));
  } else if (name === "unadopt") {
    Win.hosting = null;
    const b = makeTab("research"); Tabs.list.push(b); activateTab(b); onlyTab(b);
  } else if (name === "adopted") {
    // It's a real window now.
    Win.hosting = null; Win.spare = false;
    loadMarket(true);
    watchDay();
  } else if (name === "initChanged") {
    // Settings saved in another window.
    applyInit(data.init);
    staleOthers();
    if (data.reload) {
      loadMarket(true);
      if (state.symbol && !Tabs.cur.loading) { state.model = null; openCompany(state.symbol); }
    }
  }
}

// The window's taskbar button names its open tab.
function paintWinTitle() {
  const title = tabText($("wtabs").querySelector(".wtab.on .wt-t"));
  if (!wv || title === Win.title) return;
  Win.title = title;
  call("winTitle", { title }, { quiet: true }).catch(() => {});
}

// ================================================================ YOLO rationale (company tab, YOLO mode only)
// Why the YOLO engine likes (or doesn't like) this company: the board's own write-up when it's on
// today's board, otherwise the same review run on this one company (YoloScreen.ScoreOneAsync).
const YOLO_CUTS = [32, 38, 44, 50, 55, 60, 65, 69, 73.5];
const FAC_COLORS = ["#f97316", "#db2777", "#2563eb", "#16a34a", "#9333ea", "#0891b2", "#ca8a04", "#dc2626", "#4f46e5", "#059669", "#e11d48", "#0d9488", "#7c3aed", "#65a30d"];
const boardPick = (sym) => { const d = state.yolo && state.yolo.data; return d ? d.picks.find((p) => p.symbol === sym) : null; };

function wireThesis() {
  $("thesis").addEventListener("click", (e) => {
    if (e.target.closest("[data-thesis-retry]")) { state.thesis = null; renderThesis(); return; }
    const go = e.target.closest("[data-go]"); if (go) showTab(go.dataset.go);
  });
}

function renderThesis() {
  const c = state.company; if (!c) return;
  const th = state.thesis && state.thesis.symbol === c.symbol ? state.thesis : null;
  if (th && th.data) { paintThesis(th.data); return; }
  if (th && th.error) {
    $("thesis").innerHTML = `${thesisHead(c, "")}<div class="err-box">${esc(th.error)} <button class="card-link" data-thesis-retry>Try again</button></div>`;
    return;
  }
  if (th && th.loading) { skelThesis(); return; }
  const on = boardPick(c.symbol);
  if (on) { state.thesis = { symbol: c.symbol, data: { pick: on, onBoard: true, outside: [], asOf: state.yolo.data.asOf } }; paintThesis(state.thesis.data); paintTabsSoon(); return; }
  loadThesis();
}

async function loadThesis() {
  const sym = state.symbol, tok = state.thesis = { symbol: sym, loading: true };
  skelThesis();
  try {
    const d = await call("yoloScore", { symbol: sym }, { quiet: true });
    if (state.thesis !== tok) return;
    state.thesis = { symbol: sym, data: d };
  } catch (e) {
    if (state.thesis !== tok) return;
    state.thesis = { symbol: sym, error: e.message };
  }
  if (state.tab === "thesis") renderThesis();
  paintTabsSoon();
}

const thesisHead = (c, sub) => `<div class="page-head">
    <div><h1>YOLO rationale <span class="yolo-badge">100x hunt</span></h1><div class="sub">${sub}</div></div>
    <button class="btn ghost" data-go="yolo"><svg class="ic"><use href="#i-rocket"/></svg>YOLO board</button>
  </div>`;

// Where the score falls on the fixed level cuts.
function levelScale(score, level, cuts) {
  const lo = 20, hi = 90, x = (v) => ((Math.min(hi, Math.max(lo, v)) - lo) / (hi - lo)) * 100;
  const edges = [lo, ...cuts, hi];
  const segs = edges.slice(0, -1).map((a, i) =>
    `<i class="ls-seg ${yoloLevelCls(i + 1)} ${i + 1 === level ? "on" : ""}" style="left:${x(a)}%;width:${x(edges[i + 1]) - x(a)}%"
       title="Level ${i + 1}: ${i ? `a score of ${a}+` : `under ${cuts[0]}`}">${i + 1}</i>`).join("");
  return `<div class="lscale">
      <div class="ls-mark" style="left:${x(score)}%"><b>${fmt.num(score, 1)}</b></div>
      <div class="ls-track">${segs}</div>
      <div class="ls-ends"><span>${lo}</span><span>Score out of 100 → level</span><span>${hi}</span></div>
    </div>`;
}

function paintThesis(d) {
  const c = state.company, p = d.pick, el = $("thesis");
  const y = state.yolo && state.yolo.data, cuts = (y && y.levelCuts) || YOLO_CUTS;
  const when = d.asOf ? new Date(d.asOf).toLocaleDateString("en-US", { month: "short", day: "numeric" }) : "today";
  const outside = (d.outside || []).map((o) => `<li>${esc(o)}</li>`).join("");
  if (!p) {
    el.innerHTML = `${thesisHead(c, `${esc(c.symbol)} isn't something YOLO mode looks for`)}
      <div class="card th-out"><div class="card-title">Left out of the screen</div>
        <p>${esc(d.leftOut || "The YOLO review doesn't score this kind of company.")}</p>
        ${outside ? `<ul class="yolo-risk">${outside}</ul>` : ""}</div>`;
    return;
  }
  let rank = "";
  if (d.onBoard && y) {
    const same = yoloSorted(y.picks.filter((x) => x.group === p.group), { key: "level", dir: -1 });
    const i = same.findIndex((x) => x.symbol === p.symbol);
    if (i >= 0) rank = `<span class="th-rank">#${i + 1} of ${same.length} ${esc(p.groupLabel.toLowerCase())} picks</span>`;
  }
  const sub = d.onBoard
    ? `On today's YOLO board · reviewed ${esc(when)}`
    : `Not on today's board · scored just now with the same review the board uses`;
  const total = p.factors.reduce((n, f) => n + f.weight, 0) || 1;
  const facs = p.factors.map((f, i) => ({ f, color: FAC_COLORS[i % FAC_COLORS.length], share: f.weight / total,
    pts: ((f.score == null ? 0.4 : f.score) * f.weight / total) * 100 })).sort((a, b) => b.pts - a.pts);
  const raw = facs.reduce((n, x) => n + x.pts, 0), cal = p.score - raw;
  const stack = facs.map((x) => `<i style="width:${x.pts}%;background:${x.color}" title="${esc(x.f.label)}: +${fmt.num(x.pts, 1)}"></i>`).join("")
    + (cal > 0.5 ? `<i class="th-cal" style="width:${cal}%" title="Group calibration: +${fmt.num(cal, 1)}"></i>` : "");
  const ticks = [[cuts[cuts.length - 2], "9"], [cuts[cuts.length - 1], "10"]]
    .map(([v, l]) => `<span class="th-tick" style="left:${v}%"><em>${l}</em></span>`).join("");
  const facRows = facs.map((x) => {
    const s = x.f.score, note = x.f.why || x.f.risk;
    return `<div class="th-fac" title="${esc(YOLO_FACTOR_HELP[x.f.key] || "")}">
        <i class="th-sw" style="background:${x.color}"></i>
        <span class="th-fl">${esc(x.f.label)}</span>
        <span class="th-fw">${Math.round(x.share * 100)}%</span>
        <span class="yfac-bar"><i style="width:${s == null ? 0 : Math.round(s * 100)}%" class="${s == null ? "" : s >= 0.7 ? "hi" : s < 0.35 ? "lo" : ""}"></i></span>
        <span class="th-fs">${s == null ? "n/a" : Math.round(s * 100)}</span>
        <span class="th-fp">+${fmt.num(x.pts, 1)}</span>
        ${note ? `<span class="th-fn ${x.f.why ? "good" : "bad"}">${esc(note)}</span>` : ""}
      </div>`;
  }).join("");
  const yc = p.yearChange;
  el.innerHTML = `${thesisHead(c, sub)}
    <div class="card th-hero">
      <div class="yolo-lvl th-lvl ${yoloLevelCls(p.level)}"><b>${p.level}</b><small>/10</small></div>
      <div class="th-main">
        <div class="th-line"><span class="yolo-grp g-${esc(p.group)}">${esc(p.groupLabel)}</span><span>${esc([p.industry, p.country].filter(Boolean).join(" · "))}</span>${rank}</div>
        <div class="th-facts">
          <div>Market value<b>${yoloCap(p)}</b></div>
          <div>Price<b>${fmt.price(p.price)}</b></div>
          <div>1 year<b class="${dir(yc)}">${yc == null ? "–" : (yc >= 0 ? "+" : "") + fmt.pct(yc, 0)}</b></div>
          <div>To 100x<b>${p.currency && p.currency !== "USD" ? esc(p.currency) + " " : "$"}${fmt.big((p.marketCap || 0) * 100)}</b></div>
        </div>
        ${p.tags.length ? `<div class="yolo-tags">${p.tags.map((t) => `<span>${esc(t)}</span>`).join("")}</div>` : ""}
        ${p.summary ? `<div class="yolo-sum">${esc(p.summary)}</div>` : ""}
      </div>
      <div class="th-scale">${levelScale(p.score, p.level, cuts)}</div>
    </div>
    ${!d.onBoard ? `<div class="card th-out"><div class="card-title">Why it isn't on today's board</div>
      ${outside ? `<ul class="yolo-risk">${outside}</ul>`
        : `<p>It passes the screen's filters, but the board only reviews the top ${y ? Math.max(...y.groups.map((g) => g.reviewed), 0) || 40 : 40} of each group on a quick first pass (size, trend, place in its 52-week range and liquidity), and it didn't make that cut today.</p>`}</div>` : ""}
    <div class="card"><div class="yolo-cols">
      <div><div class="yolo-h up">Why it's here</div><ul class="yolo-why">${p.why.map((w) => `<li>${esc(w)}</li>`).join("") || `<li class="muted">No single standout; it scores evenly across the board.</li>`}</ul></div>
      <div><div class="yolo-h down">What could go wrong</div><ul class="yolo-risk">${p.risks.map((w) => `<li>${esc(w)}</li>`).join("")}</ul></div>
    </div></div>
    <div class="card">
      <div class="card-title">How the ${fmt.num(p.score, 1)} points add up</div>
      <div class="card-sub">Each trait scores 0–100 for this company and counts by its weight for a ${esc(p.groupLabel.toLowerCase())} name. Missing data counts as 40, so gaps are never rewarded.${cal > 0.5 ? ` Junior miners get +${fmt.num(cal, 0)} because they rarely have the analyst coverage or revenue the other groups get credit for.` : ""}</div>
      <div class="th-stackwrap"><div class="th-stack">${stack}</div>${ticks}</div>
      <div class="th-axis"><span>0</span><span>Level 9 and 10 start at the marks</span><span>100</span></div>
      <div class="th-facs">${facRows}</div>
    </div>
    <div class="yolo-warn"><b>Speculative by design.</b> The level is a screen, not advice: most would-be 100-baggers go to zero. Use the other tabs to check the story before sizing a position you can afford to lose.</div>`;
}

function skelThesis() {
  $("thesis").innerHTML = `<div class="page-head" aria-busy="true"><div>${sk("240px", 22)}${sk("320px", 10, "margin-top:9px")}</div>${sk("120px", 34, "border-radius:8px")}</div>
    <div class="card th-hero">
      ${sk("72px", 72, "border-radius:16px")}
      <div>${sk("50%", 12)}<div class="th-facts">${skRep(4, (i) => `<div>${sk(60, 9)}${sk(skW(i, 40, 80), 15, "margin-top:5px")}</div>`)}</div>
        <div style="display:flex;gap:6px">${skRep(3, (i) => sk(`${skW(i, 70, 110)}px`, 20, "border-radius:999px"))}</div>${sk(90, 10, "margin-top:12px")}</div>
      <div>${sk(100, 22, "margin-top:26px")}${sk(100, 9, "margin-top:8px")}</div>
    </div>
    <div class="card"><div class="yolo-cols"><div>${skCard(4, 30)}</div><div>${skCard(3, 40)}</div></div></div>
    <div class="card">${sk("220px", 13)}${sk(70, 10, "margin:10px 0 14px")}${sk(100, 26, "border-radius:7px")}
      <div class="th-facs">${skRep(10, (i) => `<div class="th-fac">${sk("10px", 10)}${sk(skW(i, 50, 90), 10)}${sk("30px", 10)}${sk(100, 6)}${sk("26px", 10)}${sk("34px", 10)}</div>`)}</div></div>`;
}

// ================================================================ debt
// The company's bonds trading in the US (FINRA TRACE last trades), weighted by the amount the
// 10-K says is outstanding on each note when it says so, into a market cost of debt. Everything
// here is worked out in the page, so ticking a bond in or out or switching the weighting is instant.
const DEBT_STALE_DAYS = 30;
const YEAR_MS = 365.25 * 864e5;

function debtState() {
  if (!state.debt || state.debt.symbol !== state.symbol)
    state.debt = { symbol: state.symbol, data: null, loading: false, error: null, weight: "auto", view: "used", y: "yield", sort: { key: "years", dir: 1 }, over: {} };
  return state.debt;
}

function wireDebt() {
  const el = $("debt");
  el.addEventListener("click", (e) => {
    const d = debtState();
    const seg = e.target.closest("[data-dseg]");
    if (seg) { d[seg.dataset.dseg] = seg.dataset.v; paintDebt(); return; }
    const iss = e.target.closest("[data-issuer]");
    if (iss) {
      const picked = d.data.issuers.filter((i) => i.selected).map((i) => i.name);
      const n = iss.dataset.issuer, next = picked.includes(n) ? picked.filter((x) => x !== n) : picked.concat(n);
      loadDebt(next); return;
    }
    const th = e.target.closest("th[data-dsort]");
    if (th) { const k = th.dataset.dsort; d.sort = d.sort.key === k ? { key: k, dir: -d.sort.dir } : { key: k, dir: k === "issuer" ? 1 : -1 }; paintDebt(); return; }
    if (e.target.closest("[data-debt-retry]")) { loadDebt(); return; }
    if (e.target.closest("[data-debt-use]")) { useDebtCost(+e.target.closest("[data-debt-use]").dataset.debtUse); return; }
  });
  el.addEventListener("change", (e) => {
    const cb = e.target.closest("input[data-use]"); if (!cb) return;
    debtState().over[cb.dataset.use] = cb.checked;
    paintDebt();
  });
  el.addEventListener("mousemove", (e) => {
    const dot = e.target.closest("[data-bond]"), tip = $("debt-tip");
    if (!tip) return;
    if (!dot) { tip.classList.add("hidden"); return; }
    const r = debtState().rows.find((x) => x.cusip === dot.dataset.bond); if (!r) return;
    tip.innerHTML = debtTip(r);
    tip.classList.remove("hidden");
    const host = dot.closest(".debt-chart").getBoundingClientRect();
    const x = e.clientX - host.left, y = e.clientY - host.top;
    tip.style.left = Math.min(x + 14, host.width - tip.offsetWidth - 6) + "px";
    tip.style.top = Math.max(4, y - tip.offsetHeight - 10) + "px";
  });
  el.addEventListener("mouseleave", () => { const t = $("debt-tip"); if (t) t.classList.add("hidden"); });
  new ResizeObserver(() => { if (state.tab === "debt" && debtState().data) paintDebtCharts(); }).observe(el);
}

function renderDebt() {
  const d = debtState();
  if (d.data) paintDebt();
  else if (d.error) paintDebtError(d.error);
  else { skelDebt(); if (!d.loading) loadDebt(); }
}

async function loadDebt(issuers) {
  const d = debtState(), sym = state.symbol, tok = {};
  d.loading = tok; d.error = null;
  if (!d.data) skelDebt(); else $("debt").classList.add("stale");
  try {
    // A tab left meanwhile still owns d, so the answer lands there.
    const data = await call("debt", issuers ? { symbol: sym, issuers } : { symbol: sym });
    if (d.loading !== tok) return;
    d.data = data;
  } catch (e) {
    if (d.loading !== tok) return;
    d.error = e.message;
  } finally {
    if (d.loading === tok) d.loading = false;
  }
  $("debt").classList.remove("stale");
  if (state.tab === "debt" && state.debt === d) renderDebt();
  paintTabsSoon();
}

// ---------------------------------------------------------------- the numbers
// Treasury par yield at a maturity: straight lines between the curve's points, flat past the ends.
function tsyAt(curve, years) {
  const p = curve && curve.points; if (!p || !p.length) return null;
  if (years <= p[0][0]) return p[0][1];
  for (let i = 1; i < p.length; i++) if (years <= p[i][0]) {
    const [x0, y0] = p[i - 1], [x1, y1] = p[i];
    return y0 + ((y1 - y0) * (years - x0)) / (x1 - x0);
  }
  return p[p.length - 1][1];
}

// Modified duration of a semi-annual bond, in years: how much its price moves per point of yield.
function modDuration(coupon, ytm, years) {
  if (!(years > 0) || ytm == null) return null;
  const y = ytm / 100 / 2, c = (coupon || 0) / 100 / 2, n = Math.max(1, Math.round(years * 2));
  let pv = 0, w = 0;
  for (let t = 1; t <= n; t++) { const cf = c * 100 + (t === n ? 100 : 0), df = Math.pow(1 + y, -t); pv += cf * df; w += t * cf * df; }
  return pv > 0 ? w / pv / 2 / (1 + y) : null;
}

// Why a bond is left out of the averages (null = it counts).
function debtReason(r) {
  if (r.years == null || r.years > 100) return "Perpetual, or no maturity date";
  if (r.years < 0.25) return "Matures within three months";
  if (r.yield == null) return "FINRA reports no yield for it (often a structured or floating-rate note)";
  if (!(r.yield > 0 && r.yield <= 25)) return `A yield of ${fmt.num(r.yield, 2)}% looks off (an odd-lot or distressed trade)`;
  if (r.price != null && !(r.price >= 40 && r.price <= 160)) return `A price of ${fmt.num(r.price, 2)} looks off for a plain bond`;
  if (!(r.age <= DEBT_STALE_DAYS)) return r.lastTrade ? `Last traded ${fmt.date(r.lastTrade + "T12:00:00")}: more than ${DEBT_STALE_DAYS} days ago` : "Never traded";
  return null;
}

function debtRows(d) {
  const now = Date.now(), data = d.data;
  return data.bonds.map((b) => {
    const years = b.maturity ? (Date.parse(b.maturity + "T12:00:00") - now) / YEAR_MS : null;
    const age = b.lastTrade ? (now - Date.parse(b.lastTrade + "T12:00:00")) / 864e5 : Infinity;
    const r = { ...b, years, age };
    const t = years != null && years <= 100 ? tsyAt(data.curve, years) : null;
    r.spread = r.yield != null && t != null ? (r.yield - t) * 100 : null;   // basis points
    r.duration = modDuration(r.coupon, r.yield, years);
    r.reason = debtReason(r);
    // A bond can be ticked in or out by hand, except one with no yield to average.
    const o = d.over[r.cusip];
    r.used = o == null ? !r.reason : o && r.yield != null && years != null && years <= 100;
    return r;
  });
}

function debtStats(d) {
  const rows = d.rows = debtRows(d);
  const used = rows.filter((r) => r.used), withFace = used.filter((r) => r.face > 0);
  const canAmount = withFace.length >= 2;
  const mode = d.weight === "equal" || !canAmount ? "equal"
    : d.weight === "amount" ? "amount" : withFace.length / Math.max(1, used.length) >= 0.6 ? "amount" : "equal";
  const pool = mode === "amount" ? withFace : used;
  const wsum = pool.reduce((n, r) => n + (mode === "amount" ? r.face : 1), 0);
  const avg = (k) => {
    let s = 0, w = 0;
    for (const r of pool) { const v = r[k]; if (v == null || !isFinite(v)) continue; const wt = mode === "amount" ? r.face : 1; s += v * wt; w += wt; }
    return w ? s / w : null;
  };
  const mode1 = (k) => { const c = {}; used.forEach((r) => { const v = r[k]; if (v && v !== "NR") c[v] = (c[v] || 0) + 1; }); return Object.entries(c).sort((a, b) => b[1] - a[1])[0]?.[0] || null; };
  return {
    rows, used, pool, mode, canAmount, withFace,
    faceTotal: withFace.reduce((n, r) => n + r.face, 0), wsum,
    yield: avg("yield"), coupon: avg("coupon"), years: avg("years"), spread: avg("spread"), price: avg("price"), duration: avg("duration"),
    sp: mode1("sandP"), moodys: mode1("moodys"),
    grade: used.length ? (used.filter((r) => r.grade === "H").length > used.length / 2 ? "High yield" : "Investment grade") : null,
    lastTrade: used.map((r) => r.lastTrade).filter(Boolean).sort().pop() || null,
  };
}

// The tax rate for the after-tax figure: the model's first forecast year, else Settings.
function debtTax() {
  const m = state.model;
  if (m && m.symbol === state.symbol && m.a.taxRate && m.a.taxRate[0] != null) return { rate: m.a.taxRate[0], basis: "the model's rate" };
  // Settings' fixed rate only when the model is set to use it (otherwise it's each company's own).
  const t = state.dcf && !state.dcf.historicalTax ? state.dcf.taxRate : null;
  return { rate: t != null ? t : 0.21, basis: t != null ? "Settings" : "US federal rate" };
}

// ---------------------------------------------------------------- painting
function paintDebtError(msg) {
  $("debt").innerHTML = `<div class="card"><div class="card-title">Debt & cost of debt</div>
    <div class="err-box" style="margin-top:12px">${esc(msg)} <button class="card-link" data-debt-retry>Try again</button></div></div>`;
}

function paintDebt() {
  const d = debtState(), data = d.data, c = state.company || {};
  const s = d.stats = debtStats(d);
  const selected = data.issuers.filter((i) => i.selected);
  const issuerChips = data.issuers.slice(0, 14).map((i) =>
    `<button class="chip ${i.selected ? "on" : ""}" data-issuer="${esc(i.name)}" title="${i.selected ? "Leave out" : "Add"} ${esc(i.name)}">${esc(i.name)} <small>${i.bonds}</small></button>`).join("");
  const issuersCard = `<div class="card debt-issuers">
      <div class="card-head" style="margin-bottom:8px"><div>
        <div class="card-title">FINRA issuers</div>
        <div class="card-sub">The names FINRA files ${esc(c.name || data.name)}'s bonds under. Finance arms are picked automatically; tick any others that are the same borrower.</div>
      </div></div>
      <div class="chips">${issuerChips || `<span class="muted">FINRA has no issuer starting “${esc(data.name.split(" ")[0])}”.</span>`}</div>
    </div>`;

  if (!selected.length || !s.used.length) {
    $("debt").innerHTML = `<div class="card">
        <div class="card-title">No bonds to price</div>
        <p class="muted" style="margin:8px 0 0">${!selected.length
          ? `None of FINRA's issuers looks like ${esc(data.name)}. If one of the names below is the same company, tick it.`
          : `${data.bonds.length ? `${data.bonds.length} bond${data.bonds.length === 1 ? " is" : "s are"} outstanding, but none has traded at a usable price and yield in the last ${DEBT_STALE_DAYS} days.` : "None of its bonds is outstanding."}`}
          Companies that borrow from banks, privately or only outside the US won't show up in TRACE.</p>
      </div>${issuersCard}${data.bonds.length ? debtTable(d, s) : ""}${debtFoot(data)}`;
    return;
  }

  const tax = debtTax(), after = s.yield * (1 - tax.rate) / 100;
  const m = state.model && state.model.symbol === state.symbol ? state.model : null;
  const cur = m ? m.a.costOfDebt : null;
  const weightTxt = s.mode === "amount"
    ? `weighted by amount outstanding across ${s.pool.length} bond${s.pool.length === 1 ? "" : "s"} (${fmt.big(s.faceTotal)} from the ${esc(data.facesForm || "10-K")})`
    : `equal-weighted across ${s.pool.length} bond${s.pool.length === 1 ? "" : "s"}${s.canAmount ? "" : " (the 10-K doesn't give an amount for each note)"}`;
  const left = s.mode === "amount" ? s.used.length - s.withFace.length : 0;
  const td = c.metrics && c.metrics.totalDebt;
  const seg = (key, opts) => `<div class="seg">${opts.map(([v, l, dis, tip]) =>
    `<button data-dseg="${key}" data-v="${v}" class="${(key === "weight" ? s.mode : d[key]) === v ? "on" : ""}" ${dis ? "disabled" : ""} ${tip ? `title="${esc(tip)}"` : ""}>${l}</button>`).join("")}</div>`;

  $("debt").innerHTML = `
    <div class="card debt-bar">
      <div class="debt-res">
        <div class="md-price"><small>Market cost of debt (pre-tax)</small><b>${fmt.pct(s.yield / 100, 2)}</b>
          <span class="debt-basis">Average yield to maturity, ${weightTxt}</span></div>
        <div class="md-kv">After tax<b>${fmt.pct(after, 2)}</b><span>at ${fmt.pct(tax.rate, 0)} tax (${esc(tax.basis)})</span></div>
        <div class="md-kv">Spread over Treasuries<b>${s.spread == null ? "–" : `${Math.round(s.spread)} bp`}</b><span>${data.curve ? `vs the ${esc(fmt.date(data.curve.date + "T12:00:00"))} curve` : "no curve"}</span></div>
        <div class="md-kv">Average coupon<b>${fmt.pct(s.coupon / 100, 2)}</b><span>what it pays today</span></div>
        <div class="md-kv">Average maturity<b>${fmt.num(s.years, 1)} yrs</b><span>duration ${fmt.num(s.duration, 1)}</span></div>
        <div class="md-kv">Average price<b>${fmt.num(s.price, 1)}</b><span>per 100 of face</span></div>
        <div class="md-kv">Rating<b>${esc([s.sp, s.moodys].filter(Boolean).join(" / ") || "–")}</b><span>${esc(s.grade ? s.grade + " (TRACE)" : "")}</span></div>
      </div>
      <div class="debt-actions">
        ${seg("weight", [["amount", "By amount", !s.canAmount, s.canAmount ? "Weight each bond by the amount outstanding in the 10-K" : "The 10-K doesn't give amounts note by note"], ["equal", "Equal weight"]])}
        <button class="btn primary" data-debt-use="${(s.yield / 100).toFixed(5)}" title="Set the DCF's pre-tax cost of debt to this figure">Use ${fmt.pct(s.yield / 100, 2)} in the DCF</button>
        ${cur != null ? `<span class="debt-now">The model uses ${fmt.pct(cur, 2)} now</span>` : ""}
      </div>
    </div>
    ${left > 0 ? `<div class="debt-note">${left} bond${left === 1 ? "" : "s"} in use ${left === 1 ? "has" : "have"} no amount in the 10-K, so ${left === 1 ? "it's" : "they're"} left out of the amount-weighted figures. Switch to equal weight to count ${left === 1 ? "it" : "them"}.</div>` : ""}
    <div class="debt-grid">
      <div class="card">
        <div class="card-head"><div>
          <div class="card-title">${d.y === "spread" ? "Spread over Treasuries" : "Yield curve"}</div>
          <div class="card-sub">Each dot is a bond at its last trade${s.mode === "amount" ? ", sized by amount outstanding" : ""}; hollow dots are left out. ${d.y === "spread" ? "" : "Dashed: US Treasuries."}</div>
        </div>${seg("y", [["yield", "Yield"], ["spread", "Spread"]])}</div>
        <div class="debt-chart" id="debt-curve"></div>
      </div>
      <div class="card">
        <div class="card-head"><div>
          <div class="card-title">Maturity wall</div>
          <div class="card-sub">${s.withFace.length ? "Amount coming due each year, from the 10-K" : "Bonds coming due each year"}${td ? ` · total debt on the balance sheet ${fmt.big(td)}` : ""}</div>
        </div></div>
        <div class="debt-chart" id="debt-wall"></div>
      </div>
    </div>
    ${debtTable(d, s)}
    ${issuersCard}
    ${debtFoot(data)}
    <div class="chart-tip debt-tip hidden" id="debt-tip"></div>`;
  paintDebtCharts();
}

function debtFoot(data) {
  return `<div class="debt-foot">Prices and yields: ${esc(data.source)} last trades, as published on finra.org (public, may lag a day). ${data.facesForm ? `Amounts: the ${esc(data.facesForm)}. ` : ""}Treasuries: US Treasury daily par curve.
    ${(data.notes || []).map((n) => `<div>${esc(n)}</div>`).join("")}</div>`;
}

function debtTable(d, s) {
  const k = d.sort.key, dir = d.sort.dir;
  const val = (r) => ({ issuer: r.issuer, coupon: r.coupon, years: r.years, price: r.price, yield: r.yield, spread: r.spread, face: r.face, trade: r.age === Infinity ? 1e9 : r.age })[k];
  const list = s.rows.filter((r) => d.view === "all" || r.used || d.over[r.cusip] === false)
    .sort((a, b) => { const x = val(a), y = val(b); if (x == null) return 1; if (y == null) return -1; return (x < y ? -1 : x > y ? 1 : 0) * dir; });
  const th = (key, label, cls) => `<th data-dsort="${key}" class="${cls || ""} ${k === key ? "sorted" : ""}">${label}${k === key ? (dir < 0 ? " ↓" : " ↑") : ""}</th>`;
  const hidden = s.rows.length - list.length;
  return `<div class="card">
    <div class="card-head"><div>
      <div class="card-title">Bonds</div>
      <div class="card-sub">${s.used.length} of ${s.rows.length} outstanding count toward the averages. Tick or untick a bond to change that.</div>
    </div>
    <div class="seg"><button data-dseg="view" data-v="used" class="${d.view === "used" ? "on" : ""}">In use</button><button data-dseg="view" data-v="all" class="${d.view === "all" ? "on" : ""}">All outstanding</button></div></div>
    <div class="debt-wrap"><table class="debt-table">
      <thead><tr><th class="l">Use</th>${th("issuer", "Bond", "l")}${th("coupon", "Coupon")}${th("years", "Maturity")}${th("price", "Price")}${th("yield", "Yield")}${th("spread", "Spread")}<th>Rating</th>${th("trade", "Last trade")}${th("face", "Amount")}</tr></thead>
      <tbody>${list.map((r) => `<tr class="${r.used ? "" : "off"}" ${r.reason && !r.used ? `title="${esc(r.reason)}"` : ""}>
        <td class="l"><input type="checkbox" data-use="${esc(r.cusip)}" ${r.used ? "checked" : ""} ${r.yield == null || r.years == null || r.years > 100 ? "disabled" : ""}></td>
        <td class="l"><b>${esc(r.issuer)}</b><div class="debt-cusip">${esc(r.cusip)}${r.couponType && r.couponType !== "FXPV" ? ` · ${esc(r.couponType)}` : ""}${r.faceNote ? ` · ${esc(r.faceNote)}` : ""}</div></td>
        <td>${r.coupon == null ? "–" : fmt.num(r.coupon, 3) + "%"}</td>
        <td>${r.maturity && r.years <= 100 ? `${esc(fmt.date(r.maturity + "T12:00:00"))}<div class="debt-cusip">${fmt.num(r.years, 1)} yrs</div>` : "Perpetual"}</td>
        <td>${fmt.num(r.price, 2)}</td>
        <td><b>${r.yield == null ? "–" : fmt.num(r.yield, 2) + "%"}</b></td>
        <td>${r.spread == null ? "–" : Math.round(r.spread) + " bp"}</td>
        <td>${esc([r.sandP, r.moodys].filter((x) => x && x !== "NR").join(" / ") || "–")}</td>
        <td class="${r.age > DEBT_STALE_DAYS ? "muted" : ""}">${r.lastTrade ? esc(fmt.date(r.lastTrade + "T12:00:00")) : "–"}</td>
        <td>${r.face ? fmt.big(r.face) : "–"}</td>
      </tr>`).join("")}</tbody></table></div>
    ${hidden && d.view === "used" ? `<div class="debt-more"><button class="card-link" data-dseg="view" data-v="all">Show the ${hidden} left out</button></div>` : ""}
  </div>`;
}

function debtTip(r) {
  return `<b>${esc(r.issuer)}</b><div>${r.coupon == null ? "" : fmt.num(r.coupon, 3) + "% · "}due ${r.maturity ? esc(fmt.date(r.maturity + "T12:00:00")) : "–"}</div>
    <div>Price ${fmt.num(r.price, 2)} · yield ${r.yield == null ? "–" : fmt.num(r.yield, 2) + "%"}${r.spread == null ? "" : ` · ${Math.round(r.spread)} bp`}</div>
    ${r.face ? `<div>Outstanding ${fmt.big(r.face)}</div>` : ""}<div class="muted">${r.used ? "Counts toward the averages" : esc(r.reason || "Left out by hand")}</div>`;
}

function paintDebtCharts() {
  const d = debtState(), s = d.stats; if (!s || !$("debt-curve")) return;
  drawDebtCurve($("debt-curve"), d, s);
  drawDebtWall($("debt-wall"), s);
}

function niceMax(v) { const p = Math.pow(10, Math.floor(Math.log10(v || 1))); return Math.ceil(v / p * 2) / 2 * p; }

function drawDebtCurve(host, d, s) {
  const W = Math.max(320, host.clientWidth), H = 260, L = 44, R = 14, T = 12, B = 30;
  const spread = d.y === "spread";
  const pts = s.rows.filter((r) => r.years != null && r.years <= 100 && r.yield != null && r.yield > 0 && r.yield <= 25 && (!spread || r.spread != null));
  if (!pts.length) { host.innerHTML = `<div class="empty-sm">No bonds with a usable yield.</div>`; return; }
  const xMax = Math.max(5, Math.ceil(Math.max(...pts.map((r) => r.years)) / 5) * 5);
  const val = (r) => (spread ? r.spread : r.yield);
  const vs = pts.map(val).concat(spread ? [s.spread || 0, 0] : [s.yield, ...((d.data.curve && d.data.curve.points) || []).filter((p) => p[0] <= xMax).map((p) => p[1])]);
  let yMin = Math.min(...vs), yMax = Math.max(...vs);
  const pad = (yMax - yMin) * 0.12 || 1; yMin = spread ? Math.min(0, yMin - pad) : Math.max(0, yMin - pad); yMax += pad;
  const ticks = niceTicks(yMin, yMax, 5);
  yMin = Math.min(yMin, ticks[0]); yMax = Math.max(yMax, ticks[ticks.length - 1]);
  const X = (v) => L + ((W - L - R) * v) / xMax, Y = (v) => T + (H - T - B) * (1 - (v - yMin) / (yMax - yMin));
  const fmtY = (v) => (spread ? Math.round(v) + "" : v.toFixed(1) + "%");
  let g = ticks.map((t) => `<line x1="${L}" x2="${W - R}" y1="${Y(t)}" y2="${Y(t)}" class="dg"/><text x="${L - 6}" y="${Y(t) + 4}" class="dl" text-anchor="end">${fmtY(t)}</text>`).join("");
  for (let x = 0; x <= xMax; x += xMax > 30 ? 10 : 5) g += `<text x="${X(x)}" y="${H - 10}" class="dl" text-anchor="middle">${x}y</text>`;
  if (!spread && d.data.curve) {
    const cp = d.data.curve.points.filter((p) => p[0] <= xMax + 0.01);
    g += `<polyline class="dtsy" points="${cp.map((p) => `${X(p[0])},${Y(p[1])}`).join(" ")}"/>`;
    const lp = cp[cp.length - 1]; if (lp) g += `<text x="${X(lp[0]) - 4}" y="${Y(lp[1]) + 14}" class="dl" text-anchor="end">Treasuries</text>`;
  }
  const avg = spread ? s.spread : s.yield;
  if (avg != null) g += `<line x1="${L}" x2="${W - R}" y1="${Y(avg)}" y2="${Y(avg)}" class="davg"/><text x="${W - R}" y="${Y(avg) - 5}" class="dl davgt" text-anchor="end">Average ${fmtY(avg)}${spread ? " bp" : ""}</text>`;
  const maxFace = Math.max(1, ...pts.map((r) => r.face || 0));
  const rad = (r) => (s.mode === "amount" && r.face ? 3.5 + 7 * Math.sqrt(r.face / maxFace) : 4.5);
  g += pts.sort((a, b) => (b.face || 0) - (a.face || 0)).map((r) =>
    `<circle data-bond="${esc(r.cusip)}" cx="${X(r.years)}" cy="${Y(val(r))}" r="${rad(r)}" class="${r.used ? "ddot" : "ddot off"} ${r.grade === "H" ? "hy" : ""}"/>`).join("");
  host.innerHTML = `<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">${g}</svg>`;
}

function drawDebtWall(host, s) {
  const W = Math.max(320, host.clientWidth), H = 260, L = 44, R = 10, T = 18, B = 30;
  const byAmount = s.withFace.length > 0;
  const src = byAmount ? s.rows.filter((r) => r.face > 0 && r.years != null && r.years <= 100) : s.rows.filter((r) => r.years != null && r.years <= 100);
  if (!src.length) { host.innerHTML = `<div class="empty-sm">No maturities to show.</div>`; return; }
  const y0 = new Date().getFullYear(), cap = y0 + 30;
  const buckets = new Map();
  src.forEach((r) => { const y = Math.min(cap, +r.maturity.slice(0, 4)); buckets.set(y, (buckets.get(y) || 0) + (byAmount ? r.face : 1)); });
  const last = Math.max(...buckets.keys()), years = [];
  for (let y = y0; y <= last; y++) years.push(y);
  const top = Math.max(...buckets.values()), max = byAmount ? niceMax(top) : Math.max(2, Math.ceil(top / 2) * 2);
  const bw = (W - L - R) / years.length, Y = (v) => T + (H - T - B) * (1 - v / max);
  const ticks = [0, max / 2, max];
  let g = ticks.map((t) => `<line x1="${L}" x2="${W - R}" y1="${Y(t)}" y2="${Y(t)}" class="dg"/><text x="${L - 6}" y="${Y(t) + 4}" class="dl" text-anchor="end">${byAmount ? fmt.big(t) : Math.round(t)}</text>`).join("");
  const every = years.length > 24 ? 5 : years.length > 12 ? 2 : 1;
  years.forEach((y, i) => {
    const v = buckets.get(y) || 0, x = L + i * bw;
    if (v) g += `<rect x="${x + bw * 0.14}" y="${Y(v)}" width="${bw * 0.72}" height="${Y(0) - Y(v)}" rx="2" class="dbar"><title>${y === cap ? `${cap}+` : y}: ${byAmount ? fmt.big(v) : v + " bond" + (v === 1 ? "" : "s")}</title></rect>`;
    if ((y - y0) % every === 0 || y === cap) g += `<text x="${x + bw / 2}" y="${H - 10}" class="dl" text-anchor="middle">${y === cap ? `${String(cap).slice(2)}+` : `'${String(y).slice(2)}`}</text>`;
  });
  host.innerHTML = `<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">${g}</svg>`;
}

function skelDebt() {
  $("debt").innerHTML = `<div class="card debt-bar" aria-busy="true"><div class="debt-res">${sk("200px", 34)}${skRep(6, (i) => `<div>${sk("90px", 9)}${sk(`${skW(i, 50, 80)}px`, 16, "margin-top:6px")}</div>`)}</div></div>
    <div class="debt-grid">${skRep(2, () => `<div class="card">${sk("160px", 13)}${sk("70%", 10, "margin:8px 0 14px")}${sk(100, 220, "border-radius:8px")}</div>`)}</div>
    <div class="card">${sk("120px", 13)}${skRep(8, (i) => sk(100, 12, "margin-top:14px;opacity:" + (1 - i * 0.08)))}</div>`;
}

// Puts the market cost of debt into the DCF (loading the model first if it isn't open yet).
async function useDebtCost(v) {
  const sym = state.symbol;
  let m = state.model && state.model.symbol === sym ? state.model : null;
  try {
    if (!m) {
      const r = await call("model", { symbol: sym });
      if (sym !== state.symbol) return;
      m = state.model = modelFrom(r);
    }
    m.a.costOfDebt = v; m.custom = true;
    const b = m.base, baseRev = [...b.revenue].reverse().find((x) => x != null) || 0;
    m.res = dcfCompute(m.a, baseRev);
    await call("saveModel", { symbol: m.symbol, assumptions: m.a }, { quiet: true });
    refreshSavedCount();
    toast("Cost of debt updated", `${sym}'s DCF now uses a ${fmt.pct(v, 2)} pre-tax cost of debt. Its implied price is ${fmt.price(m.res.price)}.`, "ok");
    if (state.tab === "debt") paintDebt();
    paintTabsSoon();
  } catch (e) { toast("Couldn't update the model", e.message, "err"); }
}

// ================================================================ company
// Opens a company in the open tab, or in opts.into (a tab behind this one). opts.tab picks the
// view; Back / Forward pass the scroll to return to and a revert for when the open fails.
async function openCompany(sym, opts = {}) {
  sym = String(sym || "").trim().toUpperCase();
  if (!sym) return false;
  const t = opts.into || Tabs.cur, seq = ++t.nav, s0 = pageOf(t);
  if (t === Tabs.cur) { closeSearch(); $("search").value = ""; $("search").blur(); }
  const switching = sym !== s0.symbol;
  const target = opts.tab || (COMPANY_TABS.includes(s0.tab) ? s0.tab : "overview");
  if (switching) {
    t.loading = sym; t.loadTab = target; paintTabsSoon();
    if (t === Tabs.cur) skelCompany(target);
  }
  try {
    const r = await call("company", { symbol: sym });
    if (t.nav !== seq) return false;   // overtaken: another company, Back / Forward, or the tab closed
    const tab = t.loadTab || target;
    t.loading = t.loadTab = null;
    const live = t === Tabs.cur, s = pageOf(t);
    if (s.company && s.symbol !== r.company.symbol) stashCompany(t, s);
    s.company = r.company; s.symbol = r.company.symbol;
    s.compSet = r.compSet || []; s.compRows = null; s.compSort = null; s._ffComps = null;
    s.statements = {}; s.chartSeries = null; s.feeds = null; s.thesis = null; s.debt = null;
    if (s.model && s.model.symbol !== r.company.symbol) s.model = null;
    state.recent = r.recent || state.recent;
    renderRecent();
    if (live) {
      $("export-result").classList.add("hidden");
      $("btn-export-top").disabled = false;
      renderCoHead();
      showTab(tab, { scroll: opts.scroll });
    } else { s.tab = tab; recordNav(t); paintTabsSoon(); }
    return true;
  } catch (e) {
    if (t.nav !== seq) return false;
    t.loading = t.loadTab = null; paintTabsSoon();
    toast(`Couldn't open ${sym}`, e.message, "err");
    if (opts.revert) opts.revert();
    if (switching && t === Tabs.cur) { showTab(state.tab); if (state.company) renderCoHead(); }
    return false;
  }
}

function renderCoHead() {
  const c = state.company; if (!c) return;
  const meta = [c.exchange, c.sector, c.industry, c.currency].filter(Boolean).map(esc).join(" · ");
  const st = c.marketState && c.marketState !== "REGULAR" ? `<span class="co-state">${esc(c.marketState.toLowerCase())}</span>` : "";
  const sf = ((state.coChecks[c.symbol] || {}).flags || []).find((f) => f.severity === "severe");
  const chk = sf ? `<span class="chk bad" title="${esc(flagTip(sf))}">⚠ Share count disagrees with ${esc(state.coChecks[c.symbol].against)}</span>` : "";
  $("co-head").innerHTML = `
    <div>
      <div class="co-name">${esc(c.name)}</div>
      <div class="co-meta"><span class="tick">${esc(c.symbol)}</span><span>${meta}</span>${chk}</div>
    </div>
    <div class="co-price">
      <div class="co-px">${fmt.price(c.price)}<small>${esc(c.currency)}</small></div>
      <div>${chgPill(c.changePct, c.change)}${st}</div>
    </div>`;
}

// ================================================================ markets
async function loadMarket(quiet) {
  try {
    const m = await call("market", {}, { quiet });
    state.market = m;
    renderMarket();
    paintTabsSoon();
  } catch (e) {
    if (!quiet) toast("Couldn't load markets", e.message, "err");
    if (!state.market) { $("board").innerHTML = `<div class="err-box">${esc(e.message)}</div>`; $("movers").innerHTML = ""; $("market-asof").textContent = ""; }
  }
}

const fx = (q) => /=X$/.test(q.symbol);   // currency pairs move in the 4th decimal

function renderMarket() {
  const m = state.market; if (!m) return;
  $("market-asof").textContent = `As of ${new Date(m.asOf).toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit" })} · ${m.source || ""} · auto-refreshes every minute`;

  // The main board (each tile opens that chart) plus the daily rate / FX series from the
  // Bank of Canada and the US Treasury, which aren't tickers so they don't open anything.
  const tile = (q, daily) => {
    const d = q.change ? dir(q.change) : "";
    const fxq = q.unit === "fx" || fx(q);
    const px = q.unit === "pct" ? fmt.num(q.price, daily ? 2 : 3) + "%" : fmt.num(q.price, fxq ? 4 : 2);
    const chg = q.change == null ? "–"
      : q.unit === "pct" ? `${q.change >= 0 ? "+" : ""}${Math.round(q.change * 100)} bp`
      : `${fmt.signed(q.change, fxq ? 4 : 2)} (${fmt.chg(q.changePct)})`;
    return `<div class="tile ${d} ${daily ? "static" : ""}" ${daily ? "" : `data-sym="${esc(q.symbol)}"`} title="${esc(q.symbol)}">
      <div class="tile-name">${esc(q.name)}</div>
      <div class="tile-px">${px}</div>
      <div class="tile-chg ${d}">${chg}</div>
    </div>`;
  };
  const groupsOf = (list) => { const g = {}; list.forEach((q) => (g[q.group] = g[q.group] || []).push(q)); return g; };
  const section = (groups, daily) => Object.entries(groups).map(([g, qs]) => {
    const asOf = daily && qs.find((q) => q.asOf);
    return `
      <div class="board-group">
        <div class="board-title">${esc(g)}${asOf ? `<span class="board-note">daily · ${esc(fmt.date(asOf.asOf + "T12:00:00"))}</span>` : ""}</div>
        <div class="tiles">${qs.map((q) => tile(q, daily)).join("")}</div>
      </div>`;
  }).join("");
  $("board").innerHTML = (m.board.ok ? section(groupsOf(m.board.data), false) : `<div class="err-box">${esc(m.board.error)}</div>`)
    + (m.macro && m.macro.ok && m.macro.data.length ? section(groupsOf(m.macro.data), true) : "");

  const col = (title, block) => `
    <div class="card">
      <div class="card-title">${title}</div>
      ${!block.ok ? `<div class="err-box" style="margin:0 14px">${esc(block.error)}</div>`
        : block.data.length === 0 ? `<div class="empty-sm">Nothing yet today.</div>`
        : block.data.map((q) => `
          <div class="mv-row" data-sym="${esc(q.symbol)}">
            <span class="mv-sym">${esc(q.symbol)}</span>
            <span class="mv-name">${esc(q.name)}</span>
            <span class="mv-px">${fmt.price(q.price)}</span>
            <span class="pill ${dir(q.changePct)}">${fmt.chg(q.changePct)}</span>
          </div>`).join("")}
    </div>`;
  $("movers").innerHTML = col("Top gainers", m.gainers) + col("Top losers", m.losers) + col("Most active", m.actives);
}
document.addEventListener("click", (e) => {
  const t = e.target.closest("#board [data-sym], #movers [data-sym]");
  if (t) openCompany(t.dataset.sym);
});

// ================================================================ modes
// Research mode opens on the Markets board; YOLO mode on the board of 100x candidates. Both
// share the company tabs, so a pick opens in exactly the same Overview / Comps / … / Model views.
const MODE_KEY = "funnel.mode";
function setMode(mode, opts) {
  mode = mode === "yolo" ? "yolo" : "research";
  state.mode = mode;
  try { localStorage.setItem(MODE_KEY, mode); } catch { /* the mode just isn't remembered */ }
  document.body.classList.toggle("yolo-mode", mode === "yolo");
  document.querySelectorAll("#mode-switch button").forEach((b) => b.classList.toggle("on", b.dataset.mode === mode));
  document.querySelector('.nav-item[data-tab="markets"]').classList.toggle("hidden", mode === "yolo");
  document.querySelector('.nav-item[data-tab="yolo"]').classList.toggle("hidden", mode !== "yolo");
  document.querySelectorAll(".nav-item.yolo-only").forEach((b) => b.classList.toggle("hidden", mode !== "yolo"));
  $("brand-sub").textContent = mode === "yolo" ? "YOLO mode · hunting 100x" : "Market research → Excel";
  if (!(opts && opts.keepTab)) showTab(mode === "yolo" ? "yolo" : "markets");
}
function wireModes() {
  $("mode-switch").addEventListener("click", (e) => {
    const b = e.target.closest("[data-mode]"); if (!b || b.dataset.mode === state.mode) return;
    const t = Tabs.cur; if (t.loading) { t.nav++; t.loading = t.loadTab = null; }
    setMode(b.dataset.mode);
  });
  let saved = "research";
  try { saved = localStorage.getItem(MODE_KEY) || "research"; } catch { /* default */ }
  setMode(saved);
}

// ================================================================ YOLO board
const YOLO_GROUPS = [["all", "All"], ["mining", "Mining"], ["biotech", "Biotech"], ["smallcap", "Small caps"]];
const YOLO_FACTOR_HELP = {
  H: "Market value against a 100x: the smaller it is, the more room to grow (US$30M scores full marks, US$3B none).",
  R: "Years of cash at the current burn, or full marks if operations already bring in cash.",
  D: "Net cash scores full marks; net debt above half the market value scores none.",
  C: "Cash as a share of market value: high cash means the market is paying little for the science.",
  U: "Analysts' average target against the price (needs 2+ analysts; +150% scores full marks).",
  S: "Share of stock held by institutions (60%+ scores full marks).",
  I: "Share of stock held by insiders (20%+ scores full marks).",
  M: "Price against its 200-day average; discounted if it's already up 300%+ in a year.",
  X: "Short interest as a share of the float: 5% or less is fine, 30%+ scores none.",
  L: "Average dollars traded a day (US$10M+ scores full marks).",
  T: "The metal's move over the past year (+40% scores full marks).",
  B: "Price to book value: under 0.5x scores full marks, 3x+ none.",
  V: "Already producing or selling, and at what margin.",
  K: "How cyclical the industry is: growth in oil, shipping or metals is mostly the cycle.",
  G: "Revenue growth on a year ago (80%+ scores full marks), discounted in cyclical industries.",
  Q: "Gross margin (70%+ scores full marks).",
  P: "Sales multiple against growth: cheap for its growth scores high.",
  F: "Operating cash flow as a share of revenue: closer to self-funding scores higher.",
};

function yoloState() {
  return state.yolo || (state.yolo = { data: null, loading: false, group: "all", minLevel: 9, industry: "", sort: { key: "level", dir: -1 }, open: {} });
}

function wireYolo() {
  const y = yoloState();
  $("yolo-group").innerHTML = YOLO_GROUPS.map(([k, n]) => `<button data-v="${k}" class="${k === y.group ? "on" : ""}">${n}</button>`).join("");
  onSeg("yolo-group", (v) => { y.group = v; y.industry = ""; paintYolo(); });
  onSeg("yolo-level", (v) => { y.minLevel = +v; paintYolo(); });
  $("yolo-industry").addEventListener("change", (e) => { y.industry = e.target.value; paintYolo(); });
  $("yolo-refresh").addEventListener("click", () => loadYolo(true));
  $("yolo-head").addEventListener("click", (e) => {
    const h = e.target.closest("[data-sort]"); if (!h) return;
    const k = h.dataset.sort;
    y.sort = y.sort.key === k ? { key: k, dir: -y.sort.dir } : { key: k, dir: k === "industry" || k === "name" ? 1 : -1 };
    paintYolo();
  });
  $("yolo-list").addEventListener("click", (e) => {
    const t = e.target.closest("[data-why]");
    if (t) { e.stopPropagation(); y.open[t.dataset.why] = !y.open[t.dataset.why]; paintYolo(); return; }
    const more = e.target.closest("[data-show-level]");
    if (more) { y.minLevel = +more.dataset.showLevel; setSeg("yolo-level", String(y.minLevel)); paintYolo(); return; }
    const row = e.target.closest("[data-sym]");
    if (row) openCompany(row.dataset.sym);
  });
}

function renderYolo() {
  const y = yoloState();
  if (y.data) paintYolo();
  if (!y.data && !y.loading) loadYolo(false);
}

async function loadYolo(refresh) {
  const y = yoloState();
  if (y.loading) return;
  y.loading = true;
  $("yolo-refresh").disabled = true;
  $("yolo-progress").classList.remove("hidden");
  $("yp-msg").textContent = refresh ? "Running the screen again…" : "Loading today's screen…";
  $("yp-fill").style.width = "2%";
  if (!y.data) { skelYolo(); $("yolo-skel").classList.remove("hidden"); }
  try {
    y.data = await call("yolo", { refresh: !!refresh });
  } catch (e) {
    if (!y.data) $("yolo-asof").innerHTML = `<span class="down">${esc(e.message)}</span>`;
    toast("YOLO screen didn't finish", e.message, "err");
  } finally {
    y.loading = false;
    $("yolo-refresh").disabled = false;
    $("yolo-progress").classList.add("hidden");
    $("yolo-skel").classList.add("hidden");
  }
  if (y.data) paintYolo();
  paintTabsSoon();
}

function onYoloProgress(d) {
  $("yp-msg").textContent = d.message || "";
  $("yp-fill").style.width = Math.max(2, d.percent || 0) + "%";
}

const yoloCap = (p) => (p.currency && p.currency !== "USD" ? `${esc(p.currency)} ` : "$") + fmt.big(p.marketCap);
const yoloLevelCls = (l) => (l >= 10 ? "l10" : l >= 9 ? "l9" : l >= 7 ? "l7" : l >= 5 ? "l5" : "l1");

function yoloSorted(list, sort) {
  const by = {
    level: (p) => p.level * 1000 + p.score,
    cap: (p) => p.marketCapUsd,
    industry: (p) => (p.industry || "~").toLowerCase(),
    name: (p) => p.name.toLowerCase(),
  }[sort.key];
  return list.slice().sort((a, b) => {
    const x = by(a), z = by(b);
    if (x < z) return -sort.dir;
    if (x > z) return sort.dir;
    return b.score - a.score;   // ties: the stronger pick first
  });
}

function paintYolo() {
  const y = yoloState(), r = y.data; if (!r) return;
  const when = new Date(r.asOf);
  const screened = r.groups.reduce((n, g) => n + g.matched, 0), reviewed = r.groups.reduce((n, g) => n + g.reviewed, 0);
  $("yolo-asof").textContent = `Screened ${screened.toLocaleString("en-US")} US and Canadian listings, reviewed the best ${reviewed} in depth · ${when.toLocaleDateString("en-US", { month: "short", day: "numeric" })}, ${when.toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit" })} · ${esc(r.source)} · runs once a day`;
  $("yolo-card").classList.remove("hidden");

  const inGroup = r.picks.filter((p) => y.group === "all" || p.group === y.group);
  const industries = [...new Set(inGroup.map((p) => p.industry).filter(Boolean))].sort();
  if (y.industry && !industries.includes(y.industry)) y.industry = "";
  $("yolo-industry").innerHTML = `<option value="">All industries</option>` +
    industries.map((i) => `<option value="${esc(i)}" ${i === y.industry ? "selected" : ""}>${esc(i)} (${inGroup.filter((p) => p.industry === i && p.level >= y.minLevel).length})</option>`).join("");

  const shown = yoloSorted(inGroup.filter((p) => p.level >= y.minLevel && (!y.industry || p.industry === y.industry)), y.sort);
  $("yolo-count").textContent = `${shown.length} of ${r.picks.length} reviewed`;

  const th = (k, label, cls) => `<div class="yh ${cls || ""} ${y.sort.key === k ? "sorted" : ""}" data-sort="${k}">${label}${y.sort.key === k ? (y.sort.dir < 0 ? " ▾" : " ▴") : ""}</div>`;
  $("yolo-head").innerHTML = th("level", "Level") + th("name", "Company") + th("industry", "Industry") + th("cap", "Market cap", "r") + `<div class="yh r">Price · 1 year</div>`;

  if (!shown.length) {
    const lower = inGroup.filter((p) => p.level >= Math.min(7, y.minLevel - 1)).length;
    $("yolo-list").innerHTML = `<div class="empty-sm">Nothing at level ${y.minLevel}+ ${y.group === "all" ? "" : "in " + esc(YOLO_GROUPS.find((g) => g[0] === y.group)[1].toLowerCase())}${y.industry ? ` in ${esc(y.industry)}` : ""} today.
      ${y.minLevel > 1 && lower ? `<button class="card-link" data-show-level="${y.minLevel >= 9 ? 7 : 1}">Show ${y.minLevel >= 9 ? "7+" : "every level"}</button>` : ""}</div>`;
  } else {
    $("yolo-list").innerHTML = shown.map((p) => yoloRow(p, !!y.open[p.symbol])).join("");
  }
  paintYoloMethod(r);
}

function yoloRow(p, open) {
  const yc = p.yearChange;
  const usd = p.currency && p.currency !== "USD" ? `<small>≈ US$${fmt.big(p.marketCapUsd)}</small>` : "";
  const bars = open ? `<div class="yolo-factors">${p.factors.slice().sort((a, b) => b.weight - a.weight).map((f) => {
      const total = p.factors.reduce((n, x) => n + x.weight, 0);
      const s = f.score == null ? null : f.score;
      return `<div class="yfac" title="${esc(YOLO_FACTOR_HELP[f.key] || "")}">
        <span class="yfac-l">${esc(f.label)}</span>
        <span class="yfac-w">${Math.round((f.weight / total) * 100)}%</span>
        <span class="yfac-bar"><i style="width:${s == null ? 0 : Math.round(s * 100)}%" class="${s == null ? "" : s >= 0.7 ? "hi" : s < 0.35 ? "lo" : ""}"></i></span>
        <span class="yfac-s">${s == null ? "no data" : Math.round(s * 100)}</span>
      </div>`;
    }).join("")}<div class="yfac-note">Score ${fmt.num(p.score, 1)} of 100 = each trait's 0–100 score × its weight. Missing data counts as 40, so gaps are never rewarded. Hover a trait for how it's scored.</div></div>` : "";
  return `<div class="yolo-row" data-sym="${esc(p.symbol)}" title="Open ${esc(p.symbol)} in the research tabs">
      <div class="yolo-lvl ${yoloLevelCls(p.level)}"><b>${p.level}</b><small>/10</small></div>
      <div class="yolo-co">
        <div class="yolo-name">${esc(p.name)}</div>
        <div class="yolo-meta"><span class="tick">${esc(p.symbol)}</span>${esc([p.exchange, p.country].filter(Boolean).join(" · "))}</div>
      </div>
      <div class="yolo-ind"><span class="yolo-grp g-${esc(p.group)}">${esc(p.groupLabel)}</span><div>${esc(p.industry || "–")}</div></div>
      <div class="yolo-cap">${yoloCap(p)}${usd}</div>
      <div class="yolo-px">${fmt.price(p.price)}${yc != null ? `<span class="pill ${dir(yc)}">${yc >= 0 ? "+" : ""}${fmt.pct(yc, 0)}</span>` : ""}</div>
      <div class="yolo-body">
        ${p.tags.length ? `<div class="yolo-tags">${p.tags.map((t) => `<span>${esc(t)}</span>`).join("")}</div>` : ""}
        ${p.summary ? `<div class="yolo-sum">${esc(p.summary)}</div>` : ""}
        <div class="yolo-cols">
          <div><div class="yolo-h up">Why it's here</div><ul class="yolo-why">${p.why.map((w) => `<li>${esc(w)}</li>`).join("") || `<li class="muted">No single standout; it scores evenly across the board.</li>`}</ul></div>
          <div><div class="yolo-h down">What could go wrong</div><ul class="yolo-risk">${p.risks.map((w) => `<li>${esc(w)}</li>`).join("")}</ul></div>
        </div>
        <div class="yolo-actions">
          <button class="card-link" data-why="${esc(p.symbol)}">${open ? "Hide" : "Show"} the score breakdown</button>
          <span class="yolo-open">Open in research ›</span>
        </div>
        ${bars}
      </div>
    </div>`;
}

function paintYoloMethod(r) {
  const cuts = r.levelCuts || [];
  const weights = {};
  r.picks.forEach((p) => { if (!weights[p.group]) weights[p.group] = p.factors; });
  const wl = (g) => {
    const f = weights[g]; if (!f) return "";
    const total = f.reduce((n, x) => n + x.weight, 0);
    return f.slice().sort((a, b) => b.weight - a.weight).map((x) => `${esc(x.label)} ${Math.round((x.weight / total) * 100)}%`).join(" · ");
  };
  const stat = (k) => r.groups.find((g) => g.key === k) || { matched: 0, screened: 0, reviewed: 0 };
  const grp = (k, name, how) => {
    const s = stat(k);
    return `<div class="ym-g"><b>${name}</b> <span class="muted">${s.matched.toLocaleString("en-US")} listed → ${s.screened.toLocaleString("en-US")} tradable → ${s.reviewed} reviewed</span><div>${how}</div><div class="ym-w">${wl(k)}</div></div>`;
  };
  $("yolo-method").classList.remove("hidden");
  $("yolo-method").innerHTML = `
    <div class="card-title">How the picks are made</div>
    <div class="ym-steps">
      <div><b>1 · Screen.</b> Yahoo Finance's screener lists every US and Canadian exchange listing (no OTC) in each group, between about US$15M and US$2B in market value, that trades at least US$75K a day.
        Each gets a quick score: size (room to grow), trend against the 200-day average, place in the 52-week range, and liquidity.</div>
      <div><b>2 · Review.</b> The best ${esc(String(r.groups[0] ? Math.max(...r.groups.map((g) => g.reviewed)) : 40))} in each group get a full review of cash and burn, debt, insider and institutional ownership, analyst targets and short interest, plus the traits that matter for that kind of company. Every trait scores 0–100 and writes the reasons and risks you see.</div>
      <div><b>3 · Level.</b> The weighted score sets the level: ${cuts.map((c, i) => `${i + 2} at ${c}`).reverse().slice(0, 3).join(", ")}, and so on down to 1. The cuts are fixed, so a 9 means the same every day; junior miners get +6 because they rarely have the analyst coverage or revenue the others get credit for.</div>
    </div>
    ${grp("mining", "Mining", "Gold, silver, copper, uranium, platinum and other metals. The metal's price trend and book value count, and producers get credit for their margins.")}
    ${grp("biotech", "Biotech", "Cash against market value (is the pipeline priced near zero?), years of runway and analyst targets (analysts model the pipeline) carry the most weight.")}
    ${grp("smallcap", "Small caps", "Any industry growing revenue 20%+ a year. Growth, margins, the price paid for that growth and how scalable the business is (cyclical industries are marked down). Banks, insurers, REITs and utilities are left out.")}
    ${r.notes && r.notes.length ? `<div class="ff-note">${r.notes.map(esc).join(" ")}</div>` : ""}`;
}

// ================================================================ overview
function wireOverview() {
  onSeg("chart-ranges", (r) => { state.chartRange = r; loadChart(); });
  new ResizeObserver(() => { if (state.chartSeries && state.tab === "overview") drawChart(); }).observe($("chart"));
}

function renderOverview() {
  const c = state.company; if (!c) return;
  const m = c.metrics, M = state.metricMap;
  setSeg("chart-ranges", state.chartRange);
  if (!state.chartSeries || state.chartSeries.symbol !== c.symbol) loadChart(); else drawChart();

  // Street view
  const a = c.analysts;
  const t = a.trend && a.trend[0];
  const upside = a.targetMean && a.currentPrice ? a.targetMean / a.currentPrice - 1 : null;
  $("ov-street").innerHTML = `
    <div class="card-head" style="margin-bottom:6px">
      <div class="card-title">Street view</div>
      <button class="card-link" data-go="analysts">Details →</button>
    </div>
    ${consensusPill(a)}
    <div class="kv-grid">
      <div class="kv">Analysts<b>${a.analystCount ?? "–"}</b></div>
      <div class="kv">Mean target<b>${fmt.price(a.targetMean)}</b></div>
      <div class="kv">Implied upside<b class="${dir(upside)}">${upside == null ? "–" : (upside >= 0 ? "+" : "") + fmt.pct(upside)}</b></div>
      <div class="kv">Target range<b>${fmt.num(a.targetLow, 0)} – ${fmt.num(a.targetHigh, 0)}</b></div>
    </div>
    ${t ? ratingStack(t, false) : ""}`;

  const keys = ["marketCap", "enterpriseValue", "trailingPE", "forwardPE", "evToEbitda", "evToRevenue", "priceToBook", "dividendYield"];
  $("ov-multiples").innerHTML = `
    <div class="card-head" style="margin-bottom:0">
      <div class="card-title">Key multiples</div>
      <button class="card-link" data-go="comps">vs. peers →</button>
    </div>
    <div class="kv-grid">${keys.map((k) => `<div class="kv">${esc(M[k].label)}<b>${fmt.metric(M[k], m[k])}</b></div>`).join("")}</div>`;

  // Stats grouped
  const groups = {};
  state.metrics.forEach((d) => (groups[d.group] = groups[d.group] || []).push(d));
  $("ov-stats").innerHTML = Object.entries(groups).map(([g, defs]) => `
    <div class="stat-group"><h4>${esc(g)}</h4>
      ${defs.map((d) => `<div class="stat-row" data-stat="${d.key}"><span>${esc(d.label)}</span><span>${fmt.metric(d, m[d.key])}</span></div>`).join("")}
    </div>`).join("");

  paintShareStat();

  // About
  const hq = [c.city, c.country].filter(Boolean).join(", ");
  const facts = [
    ["Sector", c.sector], ["Industry", c.industry], ["HQ", hq],
    ["Employees", c.employees ? c.employees.toLocaleString("en-US") : null],
  ].filter((f) => f[1]);
  $("ov-about").innerHTML = `
    <div class="card-head" style="margin-bottom:0">
      <div class="card-title">About ${esc(c.name)}</div>
      ${c.website ? `<button class="card-link" data-url="${esc(c.website)}">${esc(c.website.replace(/^https?:\/\/(www\.)?/, ""))} ↗</button>` : ""}
    </div>
    ${c.summary ? `<p class="about-text clamp" id="about-text">${esc(c.summary)}</p>
      <button class="card-link" id="about-more" style="margin-top:6px">Show more</button>` : `<p class="about-text muted">No description available.</p>`}
    <div class="facts">${facts.map(([k, v]) => `<div>${k}<b>${esc(v)}</b></div>`).join("")}</div>`;
  const more = $("about-more");
  if (more) more.onclick = () => { const p = $("about-text"); const open = p.classList.toggle("clamp"); more.textContent = open ? "Show more" : "Show less"; };
  if (!state.feeds) loadFeeds(); else paintFeeds();
}

// The share count on the stats grid carries its cross-check (market cap is built from it).
function paintShareStat() {
  const row = document.querySelector('#ov-stats [data-stat="sharesOutstanding"]'); if (!row) return;
  const f = ((state.coChecks[state.symbol] || {}).flags || [])[0];
  row.classList.remove("flag-severe", "flag-warn");
  if (f) { row.classList.add(flagCls(f)); row.title = flagTip(f); }
}

// ---------------------------------------------------------------- filings & news
async function loadFeeds() {
  const sym = state.symbol;
  state.feeds = { filings: null, news: null };
  paintFeeds();
  const get = async (cmd) => { try { return { ok: true, data: await call(cmd, { symbol: sym }, { quiet: true }) }; } catch (e) { return { ok: false, error: e.message }; } };
  const [filings, news] = await Promise.all([get("filings"), get("news")]);
  if (sym !== state.symbol || !state.feeds) return;
  state.feeds = { filings, news };
  paintFeeds();
}

function paintFeeds(f = state.feeds || {}) {
  const body = (block, row, badge) => !block ? skelFeed(badge)
    : !block.ok ? `<div class="feed-err">${esc(block.error)}${/Sources/.test(block.error) ? ` <button class="card-link" data-open-sources>Open Sources →</button>` : ""}</div>`
    : !block.data.length ? `<div class="empty-sm">Nothing recent.</div>`
    : block.data.slice(0, 8).map(row).join("");
  $("ov-filings").innerHTML = `<div class="card-title">SEC filings</div><div class="card-sub">Latest reports, straight from EDGAR · opens in your browser</div>
    <div class="feed">${body(f.filings, (x) => `
      <div class="feed-row" data-url="${esc(x.url)}">
        <span class="form-badge">${esc(x.form)}</span>
        <span class="feed-main">${esc(x.description)}${x.period && /^(10-|20-F|40-F)/.test(x.form) ? `<small>Period ending ${esc(fmt.date(x.period + "T12:00:00"))}</small>` : ""}</span>
        <span class="feed-date">${esc(fmt.date(x.filed))}</span>
      </div>`, true)}</div>`;
  $("ov-news").innerHTML = `<div class="card-title">News</div><div class="card-sub">Recent headlines · opens in your browser</div>
    <div class="feed">${body(f.news, (x) => `
      <div class="feed-row news" data-url="${esc(x.url)}">
        <span class="feed-main">${esc(x.headline)}<small>${esc(x.source)} · ${esc(fmt.date(x.published))}</small></span>
      </div>`)}</div>`;
}

// Card links: jump to a tab, or open a website in the browser.
document.addEventListener("click", (e) => {
  const go = e.target.closest("[data-go]"); if (go) { showTab(go.dataset.go); return; }
  const u = e.target.closest("[data-url]"); if (u) call("openUrl", { url: u.dataset.url }, { quiet: true });
});

function consensusPill(a) {
  const mean = a.recommendationMean;
  const cls = mean == null ? "na" : mean <= 1.5 ? "sbuy" : mean <= 2.5 ? "buy" : mean <= 3.5 ? "hold" : mean <= 4.5 ? "sell" : "ssell";
  const label = a.recommendationKey && a.recommendationKey !== "none" ? a.recommendationKey.replace(/_/g, " ") : "No rating";
  return `<span class="cons ${cls}">${esc(label)}</span>${mean != null ? `<span class="muted" style="margin-left:8px;font-size:12.5px">${mean.toFixed(2)} on a 1–5 scale</span>` : ""}`;
}

const RATING_SEGS = [["strongBuy", "Strong Buy", "sbuy"], ["buy", "Buy", "buy"], ["hold", "Hold", "hold"], ["sell", "Sell", "sell"], ["strongSell", "Strong Sell", "ssell"]];
function ratingStack(t, tall) {
  const total = RATING_SEGS.reduce((s, [k]) => s + (t[k] || 0), 0);
  if (!total) return "";
  return `
    <div class="stack ${tall ? "tall" : ""}">${RATING_SEGS.map(([k, , c]) => t[k] ? `<div class="seg-${c}" style="width:${(t[k] / total) * 100}%">${tall && t[k] / total > 0.06 ? t[k] : ""}</div>` : "").join("")}</div>
    <div class="legend">${RATING_SEGS.map(([k, l, c]) => `<span><i class="seg-${c}"></i>${l} <b>${t[k] || 0}</b></span>`).join("")}</div>`;
}

// ---------------------------------------------------------------- chart
async function loadChart() {
  const sym = state.symbol, range = state.chartRange;
  if (!state.chartSeries || state.chartSeries.symbol !== sym) skelChart();
  else { $("chart").classList.add("stale"); $("chart-readout").innerHTML = sk("220px", 14); }
  try {
    const s = await call("chart", { symbol: sym, range }, { quiet: true });
    if (sym !== state.symbol || range !== state.chartRange) return;   // user moved on
    state.chartSeries = s;
    $("chart").classList.remove("stale");
    drawChart();
  } catch (e) {
    if (sym !== state.symbol || range !== state.chartRange) return;
    $("chart").classList.remove("stale");
    $("chart").innerHTML = `<div class="err-box">${esc(e.message)}</div>`;
    $("chart-readout").textContent = ""; $("chart-stats").innerHTML = "";
  }
}

function niceTicks(lo, hi, count) {
  const step0 = (hi - lo) / count;
  const mag = Math.pow(10, Math.floor(Math.log10(step0)));
  const norm = step0 / mag;
  const step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
  const out = [];
  for (let v = Math.ceil(lo / step) * step; v <= hi + 1e-9; v += step) out.push(+v.toFixed(10));
  return out;
}

function timeLabel(t, range, long) {
  const d = new Date(t * 1000);
  if (range === "1d") return d.toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit" });
  if (range === "5d") return long ? d.toLocaleString("en-US", { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" })
    : d.toLocaleDateString("en-US", { weekday: "short", day: "numeric" });
  if (range === "5y" || range === "max") return d.toLocaleDateString("en-US", long ? { month: "short", day: "numeric", year: "numeric" } : { month: "short", year: "2-digit" });
  return d.toLocaleDateString("en-US", long ? { month: "short", day: "numeric", year: "numeric" } : { month: "short", day: "numeric" });
}

const UP = "#16a34a", DOWN = "#dc2626";
function drawChart() {
  const s = state.chartSeries, host = $("chart"), range = state.chartRange;
  if (!s) return;
  const pts = s.points;
  if (!pts.length) { host.innerHTML = `<div class="empty-sm">No price data for this range.</div>`; $("chart-readout").textContent = ""; $("chart-stats").innerHTML = ""; return; }

  const W = Math.max(300, host.clientWidth), H = host.clientHeight || 340;
  const padL = 4, padR = 58, padT = 10, volH = 50, gap = 10, axisH = 22;
  const priceH = H - padT - volH - gap - axisH;
  const n = pts.length;
  const innerW = W - padL - padR;
  const xs = (i) => padL + (n === 1 ? innerW / 2 : (i * innerW) / (n - 1));

  const closes = pts.map((p) => p.c);
  const prev = range === "1d" && s.previousClose ? s.previousClose : null;
  const base = prev ?? closes[0];
  let lo = Math.min(...closes), hi = Math.max(...closes);
  if (prev) { lo = Math.min(lo, prev); hi = Math.max(hi, prev); }
  const span = hi - lo || hi * 0.01 || 1;
  lo -= span * 0.08; hi += span * 0.08;
  const ys = (v) => padT + ((hi - v) / (hi - lo)) * priceH;
  const last = closes[n - 1];
  const col = last >= base ? UP : DOWN;

  const ticks = niceTicks(lo, hi, 5).filter((v) => v >= lo && v <= hi);
  const line = pts.map((p, i) => `${i ? "L" : "M"}${xs(i).toFixed(1)},${ys(p.c).toFixed(1)}`).join("");
  const area = `${line}L${xs(n - 1).toFixed(1)},${(padT + priceH).toFixed(1)}L${xs(0).toFixed(1)},${(padT + priceH).toFixed(1)}Z`;

  const vols = pts.map((p) => p.v || 0);
  const vmax = Math.max(...vols) || 1;
  const vTop = padT + priceH + gap;
  const bw = Math.max(1, innerW / n - 1);
  const volBars = pts.map((p, i) => {
    const h = ((p.v || 0) / vmax) * volH;
    const up = i === 0 ? p.c >= (p.o ?? p.c) : p.c >= pts[i - 1].c;
    return `<rect x="${(xs(i) - bw / 2).toFixed(1)}" y="${(vTop + volH - h).toFixed(1)}" width="${bw.toFixed(1)}" height="${h.toFixed(1)}" fill="${up ? UP : DOWN}" opacity=".28"/>`;
  }).join("");

  const nx = Math.min(6, n);
  const xl = Array.from({ length: nx }, (_, k) => Math.round((k * (n - 1)) / Math.max(1, nx - 1)));
  const xLabels = xl.map((i, k) => {
    const anchor = k === 0 ? "start" : k === nx - 1 ? "end" : "middle";
    return `<text x="${xs(i).toFixed(1)}" y="${H - 6}" text-anchor="${anchor}" class="ax">${esc(timeLabel(pts[i].t, range, false))}</text>`;
  }).join("");

  const decimals = hi < 10 ? 3 : 2;
  host.innerHTML = `
    <svg width="${W}" height="${H}" id="chart-svg">
      <defs><linearGradient id="cg" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="${col}" stop-opacity=".18"/><stop offset="1" stop-color="${col}" stop-opacity="0"/>
      </linearGradient></defs>
      <style>.ax{font:11px "Segoe UI",sans-serif;fill:#94a3b8}</style>
      ${ticks.map((v) => `<line x1="${padL}" x2="${W - padR}" y1="${ys(v).toFixed(1)}" y2="${ys(v).toFixed(1)}" stroke="#eef1f5"/>
        <text x="${W - padR + 8}" y="${(ys(v) + 4).toFixed(1)}" class="ax">${fmt.num(v, decimals)}</text>`).join("")}
      ${prev ? `<line x1="${padL}" x2="${W - padR}" y1="${ys(prev).toFixed(1)}" y2="${ys(prev).toFixed(1)}" stroke="#94a3b8" stroke-dasharray="3 4"/>
        <text x="${W - padR + 8}" y="${(ys(prev) + 4).toFixed(1)}" class="ax" style="fill:#64748b">prev</text>` : ""}
      ${volBars}
      <path d="${area}" fill="url(#cg)"/>
      <path d="${line}" fill="none" stroke="${col}" stroke-width="1.8" stroke-linejoin="round"/>
      ${xLabels}
      <g id="hover" style="display:none">
        <line id="hv-line" y1="${padT}" y2="${vTop + volH}" stroke="#64748b" stroke-dasharray="2 3"/>
        <circle id="hv-dot" r="4" fill="${col}" stroke="#fff" stroke-width="2"/>
      </g>
      <rect x="${padL}" y="0" width="${innerW}" height="${H}" fill="transparent" id="hv-hit"/>
    </svg>
    <div class="chart-tip hidden" id="chart-tip"></div>`;

  const chg = last - base, chgPct = base ? (chg / base) * 100 : null;
  const rangeName = { "1d": "today", "5d": "5 days", "1mo": "1 month", "6mo": "6 months", ytd: "year to date", "1y": "1 year", "5y": "5 years", max: "all time" }[range];
  const restReadout = () => {
    $("chart-readout").innerHTML = `<b>${fmt.price(last)}</b> <span class="${dir(chg)}">${fmt.signed(chg)} (${fmt.chg(chgPct)})</span> ${rangeName}`;
  };
  restReadout();

  const highs = pts.map((p) => p.h ?? p.c), lows = pts.map((p) => p.l ?? p.c);
  const avgVol = vols.reduce((a, b) => a + b, 0) / n;
  $("chart-stats").innerHTML = `
    <div class="cs">Return<b class="${dir(chgPct)}">${fmt.chg(chgPct)}</b></div>
    <div class="cs">High<b>${fmt.price(Math.max(...highs))}</b></div>
    <div class="cs">Low<b>${fmt.price(Math.min(...lows))}</b></div>
    <div class="cs">Avg. volume<b>${fmt.big(avgVol)}</b></div>
    <div class="cs">Bars<b>${n} × ${esc(s.interval)}</b></div>`;

  const svg = $("chart-svg"), hv = $("hover"), tip = $("chart-tip");
  $("hv-hit").addEventListener("mousemove", (e) => {
    const rect = svg.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const i = Math.max(0, Math.min(n - 1, Math.round(((x - padL) / innerW) * (n - 1))));
    const p = pts[i];
    hv.style.display = "";
    $("hv-line").setAttribute("x1", xs(i)); $("hv-line").setAttribute("x2", xs(i));
    $("hv-dot").setAttribute("cx", xs(i)); $("hv-dot").setAttribute("cy", ys(p.c));
    tip.classList.remove("hidden");
    tip.style.left = Math.max(70, Math.min(W - 70, xs(i))) + "px";
    tip.textContent = `${timeLabel(p.t, range, true)} · ${fmt.price(p.c)} · vol ${fmt.big(p.v)}`;
    const d = p.c - base;
    $("chart-readout").innerHTML = `<b>${fmt.price(p.c)}</b> <span class="${dir(d)}">${fmt.signed(d)} (${fmt.chg(base ? (d / base) * 100 : null)})</span> since ${range === "1d" ? "prev. close" : "start of range"}`;
  });
  $("hv-hit").addEventListener("mouseleave", () => { hv.style.display = "none"; tip.classList.add("hidden"); restReadout(); });
}

// ================================================================ comps
function wireComps() {
  $("peer-add").addEventListener("keydown", (e) => {
    if (e.key !== "Enter") return;
    const vals = e.target.value.toUpperCase().split(/[\s,;]+/).filter(Boolean);
    e.target.value = "";
    const add = vals.filter((v) => v !== state.symbol && !state.compSet.includes(v));
    if (!add.length) return;
    state.compSet = state.compSet.concat(add);
    loadComps("set");
  });
  $("peer-chips").addEventListener("click", (e) => {
    const x = e.target.closest("[data-rm]"); if (!x) return;
    state.compSet = state.compSet.filter((s) => s !== x.dataset.rm);
    if (state.compRows) state.compRows = state.compRows.filter((r) => r.symbol !== x.dataset.rm);
    loadComps("set");
  });
  $("comps-suggest").addEventListener("click", () => { state.compSet = (state.company.peers || []).slice(0, 6); loadComps("clear"); });
  $("comps-refresh").addEventListener("click", () => loadComps());
  $("comps-table").addEventListener("click", (e) => {
    const th = e.target.closest("th[data-k]");
    if (th) {
      const k = th.dataset.k;
      state.compSort = state.compSort && state.compSort.key === k ? { key: k, dir: -state.compSort.dir } : { key: k, dir: -1 };
      paintComps(); return;
    }
    const tr = e.target.closest("tr.peer[data-sym]"); if (tr) openCompany(tr.dataset.sym);
  });
}

function renderComps() {
  paintPeerChips();
  if (!state.compRows) loadComps(); else paintComps();
}

function paintPeerChips() {
  $("peer-chips").innerHTML = state.compSet.map((s) =>
    `<span class="chip">${esc(s)}<svg class="x" data-rm="${esc(s)}"><use href="#i-close"/></svg></span>`).join("") ||
    `<span class="muted">No peers yet — add some tickers.</span>`;
  $("ex-comps-n").textContent = `(${state.compSet.length} peers)`;
  const basis = state.company && state.company.peersBasis;
  $("comps-basis").textContent = basis ? `Suggested peers: ${basis}` : "";
}

// save: "set" after the user edits the peers, "clear" when they go back to the suggested ones.
async function loadComps(save) {
  paintPeerChips(); paintTabsSoon();
  const sym = state.symbol;
  if (!state.compRows) skelComps();
  try {
    const rows = await call("comps", { symbol: sym, symbols: state.compSet, save: save || "" });
    if (save) refreshSavedCount();
    if (sym !== state.symbol) return;
    state.compRows = rows;
    paintComps();
    if (state.model && state.model.symbol === sym) paintFootball();
  } catch (e) {
    toast("Couldn't load comps", e.message, "err");
    if (sym === state.symbol && !state.compRows) { $("comps-table").innerHTML = `<tbody><tr><td class="l"><div class="err-box">${esc(e.message)}</div></td></tr></tbody>`; $("comps-ranges").innerHTML = ""; }
  }
}

// Compact headers so the whole comp table fits without scrolling sideways.
const COMP_SHORT = {
  marketCap: "Mkt cap", enterpriseValue: "EV", evToRevenue: "EV/Rev", evToEbitda: "EV/EBITDA",
  trailingPE: "P/E", forwardPE: "Fwd P/E", priceToBook: "P/B", revenueGrowth: "Rev growth",
  ebitdaMargin: "EBITDA mgn", profitMargin: "Net mgn",
};

function median(a) {
  if (!a.length) return null;
  const s = [...a].sort((x, y) => x - y), m = Math.floor(s.length / 2);
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}
const mean = (a) => (a.length ? a.reduce((x, y) => x + y, 0) / a.length : null);

function paintComps() {
  const rows = state.compRows || [], M = state.metricMap, keys = state.compKeys;
  const subject = rows.find((r) => r.symbol === state.symbol);
  let peers = rows.filter((r) => r !== subject);
  if (state.compSort) {
    const { key, dir: d } = state.compSort;
    const val = (r) => key === "price" ? r.price : key === "chg" ? r.changePct : r.metrics[key];
    peers = [...peers].sort((a, b) => {
      const va = val(a), vb = val(b);
      if (va == null) return 1; if (vb == null) return -1;
      return (va - vb) * -d;
    });
  }
  const sorted = (k) => (state.compSort && state.compSort.key === k ? "sorted" : "");
  const arrow = (k) => (state.compSort && state.compSort.key === k ? (state.compSort.dir < 0 ? " ↓" : " ↑") : "");

  const head = `<thead><tr>
    <th class="l">Ticker</th><th class="l">Company</th>
    <th data-k="price" class="${sorted("price")}">Price${arrow("price")}</th>
    <th data-k="chg" class="${sorted("chg")}">Day${arrow("chg")}</th>
    ${keys.map((k) => `<th data-k="${k}" class="${sorted(k)}" title="${esc(M[k].label)}">${esc(COMP_SHORT[k] || M[k].label)}${arrow(k)}</th>`).join("")}
  </tr></thead>`;
  const row = (r, cls) => `<tr class="${cls}" data-sym="${esc(r.symbol)}">
    <td class="l"><b>${esc(r.symbol)}</b></td>
    <td class="l cname" title="${esc(r.name)}">${r.error ? `<span class="row-err">${esc(r.error)}</span>` : esc(r.name)}</td>
    <td>${fmt.price(r.price)}</td>
    <td class="${dir(r.changePct)}">${fmt.chg(r.changePct)}</td>
    ${keys.map((k) => `<td>${fmt.metric(M[k], r.metrics[k])}</td>`).join("")}
  </tr>`;
  const ok = peers.filter((p) => !p.error);
  const agg = (label, fn, first) => `<tr class="agg ${first ? "first" : ""}"><td></td><td class="l">${label}</td><td></td><td></td>
    ${keys.map((k) => { const vals = ok.map((p) => p.metrics[k]).filter((v) => v != null); return `<td>${fmt.metric(M[k], fn(vals))}</td>`; }).join("")}</tr>`;

  $("comps-table").innerHTML = head + "<tbody>" +
    (subject ? row(subject, "subject") : "") +
    peers.map((p) => row(p, "peer")).join("") +
    (ok.length ? agg("Peer median", median, true) + agg("Peer mean", mean, false) : "") +
    "</tbody>";

  // Peer ranges
  const show = ["evToEbitda", "forwardPE", "evToRevenue", "trailingPE", "priceToBook", "ebitdaMargin"];
  $("comps-ranges").innerHTML = show.map((k) => {
    const vals = ok.map((p) => p.metrics[k]).filter((v) => v != null && isFinite(v));
    const me = subject ? subject.metrics[k] : null;
    if (vals.length < 2) return `<div><div class="rg-label"><span>${esc(M[k].label)}</span><span class="muted">not enough peer data</span></div></div>`;
    // One outlier (a 136x EV/EBITDA) would squash everyone else into the first few pixels, so the
    // axis spans the Tukey fences (Q1 − 1.5·IQR … Q3 + 1.5·IQR), widened to keep this company in
    // view. Anything outside is pinned to the edge and named in the end label.
    const s = [...vals].sort((x, y) => x - y);
    const q = (p) => { const i = (s.length - 1) * p, f = Math.floor(i); return s[f] + (s[Math.ceil(i)] - s[f]) * (i - f); };
    const iqr = q(0.75) - q(0.25);
    const inside = s.filter((v) => v >= q(0.25) - 1.5 * iqr && v <= q(0.75) + 1.5 * iqr);
    const lo = Math.min(...inside, me ?? Infinity), hi = Math.max(...inside, me ?? -Infinity);
    const clip = (v) => Math.max(lo, Math.min(hi, v));
    const pos = (v) => (hi === lo ? 50 : 2 + ((clip(v) - lo) / (hi - lo)) * 96);
    const med = median(vals);
    const outs = (test) => ok.filter((p) => p.metrics[k] != null && test(p.metrics[k]));
    const below = outs((v) => v < lo), above = outs((v) => v > hi);
    const endLabel = (v, list, left) => list.length
      ? `<span title="${esc(list.map((p) => `${p.symbol} ${fmt.metric(M[k], p.metrics[k])}`).join(", "))}">${left ? "◂ " : ""}${esc(list.map((p) => p.symbol).join(", "))} off-scale${left ? "" : " ▸"}</span>`
      : `<span>${fmt.metric(M[k], v)}</span>`;
    return `<div>
      <div class="rg-label"><span>${esc(M[k].label)}</span><span>${esc(state.symbol)} <b>${fmt.metric(M[k], me)}</b> · median <b>${fmt.metric(M[k], med)}</b></span></div>
      <div class="rg-track">
        <div class="rg-bar" style="left:${pos(s[0])}%;width:${pos(s[s.length - 1]) - pos(s[0])}%"></div>
        <div class="rg-med" style="left:${pos(med)}%"></div>
        ${me != null ? `<div class="rg-dot" style="left:${pos(me)}%"></div>` : ""}
      </div>
      <div class="rg-ends">${endLabel(lo, below, true)}${endLabel(hi, above, false)}</div>
    </div>`;
  }).join("");
}

// ================================================================ analysts
const pctOf = (n, total) => (total ? Math.round((n / total) * 100) + "%" : "–");

function renderAnalysts() {
  const c = state.company, a = c.analysts;
  const t = (a.trend && a.trend[0]) || null;
  const buys = t ? t.strongBuy + t.buy : 0, holds = t ? t.hold : 0, sells = t ? t.sell + t.strongSell : 0;
  const upside = a.targetMean && a.currentPrice ? a.targetMean / a.currentPrice - 1 : null;
  const meanPos = a.recommendationMean != null ? ((a.recommendationMean - 1) / 4) * 100 : null;

  let ptHtml = `<div class="an-note">No price targets published.</div>`;
  if (a.targetLow != null && a.targetHigh != null && a.currentPrice != null) {
    const lo = Math.min(a.targetLow, a.currentPrice), hi = Math.max(a.targetHigh, a.currentPrice);
    const pos = (v) => (hi === lo ? 50 : ((v - lo) / (hi - lo)) * 100);
    ptHtml = `
      <div class="pt-track">
        <div class="pt-range" style="left:${pos(a.targetLow)}%;width:${pos(a.targetHigh) - pos(a.targetLow)}%"></div>
        <div class="pt-mark cur" style="left:${pos(a.currentPrice)}%"></div>
        ${a.targetMean != null ? `<div class="pt-mark avg" style="left:${pos(a.targetMean)}%"></div>` : ""}
      </div>
      <div class="legend pt-legend"><span><i style="background:var(--ink)"></i>Now <b>${fmt.price(a.currentPrice)}</b></span>
        <span><i style="background:var(--accent)"></i>Mean target <b>${fmt.price(a.targetMean)}</b></span></div>
      <div class="pt-ends"><span>Low ${fmt.price(a.targetLow)}</span><span>Median ${fmt.price(a.targetMedian)}</span><span>High ${fmt.price(a.targetHigh)}</span></div>`;
  }

  $("an-top").innerHTML = `
    <div class="card">
      <div class="card-title">Consensus</div>
      <div style="margin-top:12px">${consensusPill(a)}</div>
      ${meanPos != null ? `<div class="gauge"><i style="left:${meanPos}%"></i></div>
        <div class="gauge-ends"><span>Strong Buy</span><span>Hold</span><span>Strong Sell</span></div>` : ""}
    </div>
    <div class="card">
      <div class="card-title">Coverage</div>
      <div class="an-big">${t ? t.total : a.analystCount ?? "–"}<small>analyst ratings</small></div>
      <div class="an-note">${t ? `<b class="up">${buys} Buy</b> · <b style="color:var(--hold-text)">${holds} Hold</b> · <b class="down">${sells} Sell</b>` : "No rating breakdown available."}</div>
      ${a.analystCount != null ? `<div class="an-note">${a.analystCount} publish a price target</div>` : ""}
    </div>
    <div class="card">
      <div class="card-title">12-month price target</div>
      <div class="an-big">${fmt.price(a.targetMean)}<small class="${dir(upside)}">${upside == null ? "" : (upside >= 0 ? "+" : "") + fmt.pct(upside) + " vs. now"}</small></div>
      ${ptHtml}
    </div>`;

  $("an-dist").innerHTML = `
    <div class="card-title">Rating distribution</div>
    <div class="card-sub">Current month${t ? ` · ${t.total} ratings` : ""}</div>
    ${t ? ratingStack(t, true) + `
      <div class="split3">
        <div><b class="up">${pctOf(buys, t.total)}</b>Buy or Strong Buy</div>
        <div><b style="color:var(--hold-text)">${pctOf(holds, t.total)}</b>Hold</div>
        <div><b class="down">${pctOf(sells, t.total)}</b>Sell or Strong Sell</div>
      </div>` : `<div class="empty-sm">No ratings available.</div>`}`;

  const trend = (a.trend || []).slice().reverse();   // oldest → newest, left to right
  const tmax = Math.max(1, ...trend.map((x) => x.total));
  $("an-trend").innerHTML = `
    <div class="card-title">Last 4 months</div>
    <div class="card-sub">How the mix has shifted</div>
    ${trend.length ? `<div class="trend-bars">${trend.map((x) => `
      <div class="trend-col" title="${RATING_SEGS.map(([k, l]) => `${l}: ${x[k]}`).join("\n")}">
        <div class="trend-n">${x.total}</div>
        <div class="trend-area"><div class="trend-stack" style="height:${(x.total / tmax) * 100}%">${RATING_SEGS.map(([k, , cl]) => x[k] ? `<div class="seg-${cl}" style="flex:${x[k]}"></div>` : "").join("")}</div></div>
        <div class="trend-lbl">${esc(x.period)}</div>
      </div>`).join("")}</div>` : `<div class="empty-sm">No history.</div>`}`;

  const firms = a.firms || [];
  $("an-firms-sub").textContent = firms.length
    ? `Latest rating from each named firm (rating actions in the last 18 months). ${firms.length} firms — the consensus count above comes from a separate survey, so the totals can differ.`
    : "No named-firm rating actions in the last 18 months.";
  const col = (bucket, cls) => {
    const list = firms.filter((f) => f.bucket === bucket);
    return `<div class="firm-col ${cls}">
      <h4>${bucket}<span class="n">${list.length}</span></h4>
      ${list.length ? list.map((f) => {
        const act = f.action === "Upgrade" ? `<span class="act upg">↑ Upgrade</span>` : f.action === "Downgrade" ? `<span class="act dng">↓ Downgrade</span>`
          : f.action === "Initiated" ? `<span class="act ini">New</span>` : "";
        const pt = f.priceTarget != null ? fmt.price(f.priceTarget) : "";
        const ptChg = f.priceTarget != null && f.priorTarget != null && f.priorTarget !== f.priceTarget
          ? ` <span class="${f.priceTarget > f.priorTarget ? "up" : "down"}">${f.priceTarget > f.priorTarget ? "▲" : "▼"}</span>` : "";
        return `<div class="firm">
          <div class="firm-top"><span class="firm-name">${esc(f.firm)}</span><span class="firm-pt">${pt}${ptChg}</span></div>
          <div class="firm-sub"><span>${esc(f.grade)}${act}</span><span>${fmt.date(f.date)}</span></div>
        </div>`;
      }).join("") : `<div class="firm-empty">None</div>`}
    </div>`;
  };
  $("an-firms").innerHTML = col("Buy", "buy") + col("Hold", "hold") + col("Sell", "sell");
}

// ================================================================ financials
function wireFinancials() {
  onSeg("st-kind", (v) => { state.stKind = v; renderFinancials(); });
  onSeg("st-freq", (v) => { state.stFreq = v; renderFinancials(); });
  onSeg("st-units", (v) => { state.stUnits = v; paintStatement(); });
  onSeg("ex-freq", (v) => { state.exFreq = v; });
  onSeg("ex-units", (v) => { state.exUnits = v; });
  $("btn-export").addEventListener("click", doExport);
}

function renderFinancials() {
  paintTabsSoon();
  $("ex-comps-n").textContent = `(${state.compSet.length} peers)`;
  const key = `${state.stKind}|${state.stFreq}`;
  if (state.statements[key]) { paintStatement(); return; }
  loadStatement(key);
}

async function loadStatement(key) {
  const [kind, frequency] = key.split("|");
  const sym = state.symbol;
  skelStatement();
  try {
    const st = await call("statement", { symbol: sym, kind, frequency });
    if (sym !== state.symbol) return;
    state.statements[key] = st;
  } catch (e) {
    if (sym !== state.symbol) return;
    state.statements[key] = { error: e.message };
  }
  if (`${state.stKind}|${state.stFreq}` === key) paintStatement();
}

const DIV = { actual: 1, thousands: 1e3, millions: 1e6, billions: 1e9 };
const UNIT_LABEL = { actual: "actual figures", thousands: "in thousands", millions: "in millions", billions: "in billions" };

function stCell(v, line, units, flag) {
  if (v == null) return `<td class="na">–</td>`;
  const fc = flagCls(flag), tip = flag ? ` title="${esc(flagTip(flag))}"` : "";
  let txt;
  if (line.format === "ps") txt = fmt.num(Math.abs(v), 2);
  else if (line.format === "pct") txt = (Math.abs(v) * 100).toFixed(1) + "%";
  else txt = fmt.num(Math.abs(v) / DIV[units], units === "billions" ? 2 : units === "millions" ? 1 : 0);
  return v < 0 ? `<td class="neg ${fc}"${tip}>(${txt})</td>` : `<td class="${fc}"${tip}>${txt}</td>`;
}

function periodHead(p, freq) {
  if (p === "TTM") return `<th class="ttm">TTM<small>trailing 12m</small></th>`;
  const d = new Date(p + "T00:00:00");
  const mon = d.toLocaleString("en-US", { month: "short" });
  return freq === "quarterly"
    ? `<th>${mon} '${String(d.getFullYear()).slice(2)}<small>quarter end</small></th>`
    : `<th>FY${d.getFullYear()}<small>${mon} ${d.getDate()}</small></th>`;
}

function paintStatement() {
  const st = state.statements[`${state.stKind}|${state.stFreq}`];
  if (!st) return;
  if (st.error) { $("st-meta").textContent = ""; $("st-wrap").innerHTML = `<div class="err-box" style="margin:12px">${esc(st.error)}</div>`; return; }
  const units = state.stUnits;
  const check = state.checks[`${state.symbol}|${state.stKind}|${state.stFreq}`];
  const flagAt = {};
  ((check && check.flags) || []).forEach((f) => (flagAt[`${f.key}|${f.period}`] = f));
  $("st-meta").innerHTML = `${esc(`${st.title} · ${st.frequency === "quarterly" ? "Quarterly" : "Annual"} · ${st.currency} ${UNIT_LABEL[units]}, except per-share data · ${st.source || ""} · oldest → newest`)}${checkBadge(check)}`;
  const rows = st.lines.map((l) => {
    if (l.format === "header") return `<tr class="hdr"><td colspan="${st.periods.length + 1}">${esc(l.label)}</td></tr>`;
    const ind = l.indent ? ` class="i${l.indent}"` : "";
    return `<tr class="${l.emphasis ? "em" : ""}"><td${ind}>${esc(l.label)}</td>${l.values.map((v, i) => stCell(v, l, units, flagAt[`${l.key}|${st.periods[i]}`])).join("")}</tr>`;
  }).join("");
  $("st-wrap").innerHTML = `<table class="st"><thead><tr><th>${esc(st.currency)} ${esc(UNIT_LABEL[units])}</th>${st.periods.map((p) => periodHead(p, st.frequency)).join("")}</tr></thead><tbody>${rows}</tbody></table>`;
}

// What the cross-check found, next to the statement's caption. Click a red one to jump to the cells.
function checkBadge(c) {
  if (!c) return ` <span class="chk na">· cross-checking…</span>`;
  if (c.status === "unavailable") return ` <span class="chk na" title="${esc(c.note || "")}">· not cross-checked</span>`;
  const severe = c.flags.filter((f) => f.severity === "severe").length, warn = c.flags.length - severe;
  if (!c.flags.length) return ` <span class="chk ok" title="${c.compared} figures compared">✓ matches ${esc(c.against)}</span>`;
  const warnTxt = warn ? `${warn} small difference${warn === 1 ? "" : "s"} with ${esc(c.against)}` : "";
  return severe
    ? ` <button class="chk bad" data-jump-flag title="Click to show them. ${c.compared} figures compared${warn ? "; " + warnTxt + " (amber underline)" : ""}.">⚠ ${severe} figure${severe === 1 ? " disagrees" : "s disagree"} with ${esc(c.against)}</button>`
    : ` <span class="chk warn" title="Amber underline: the sources usually define these lines differently. Hover a cell for both figures.">${warnTxt}</span>`;
}
let jumpIdx = 0;
document.addEventListener("click", (e) => {
  if (!e.target.closest("[data-jump-flag]")) return;
  const cells = [...document.querySelectorAll("#st-wrap td.flag-severe")];
  if (!cells.length) return;
  const td = cells[jumpIdx++ % cells.length];
  td.scrollIntoView({ block: "center", inline: "center", behavior: "smooth" });
  td.classList.remove("flash"); void td.offsetWidth; td.classList.add("flash");
});

async function doExport() {
  if (!state.symbol) { toast("Pick a company first", "Search for a ticker above.", "warn"); return; }
  const statements = [...document.querySelectorAll('input[name="ex-st"]:checked')].map((i) => i.value);
  const extras = [...document.querySelectorAll('input[name="ex-x"]:checked')].map((i) => i.value);
  if (!statements.length && !extras.length) { toast("Nothing selected", "Tick at least one statement or extra.", "warn"); return; }

  const btn = $("btn-export");
  btn.disabled = true;
  $("pp-msg").textContent = "Choose where to save…"; $("pp-fill").style.width = "0%";
  $("progress-pop").classList.remove("hidden");
  try {
    const live = state.model && state.model.symbol === state.symbol;
    const football = live ? await footballForExport().catch(() => undefined) : undefined;
    const r = await call("export", {
      symbol: state.symbol, statements, extras, football,
      frequency: state.exFreq, units: state.exUnits, comps: state.compSet,
      // The Model tab's current case when it's open on this company; otherwise the saved case or defaults.
      assumptions: state.model && state.model.symbol === state.symbol ? state.model.a : undefined,
    }, { quiet: true });
    if (r.cancelled) return;
    const name = r.path.split(/[\\/]/).pop();
    const res = $("export-result");
    res.innerHTML = `Saved<div class="fn">${esc(name)}</div>
      <div class="row-gap"><button class="btn sm" id="ex-open">Open in Excel</button><button class="btn sm" id="ex-reveal">Show in folder</button></div>`;
    res.classList.remove("hidden");
    $("ex-open").onclick = () => call("openPath", { path: r.path }).catch((e) => toast("Couldn't open", e.message, "err"));
    $("ex-reveal").onclick = () => call("revealPath", { path: r.path }).catch((e) => toast("Couldn't open folder", e.message, "err"));
    toast("Workbook saved", name, "ok");
    if (r.skipped && r.skipped.length) toast("Some data wasn't available", r.skipped.join("\n"), "warn");
  } catch (e) {
    toast("Export failed", e.message, "err");
  } finally {
    btn.disabled = false;
    $("progress-pop").classList.add("hidden");
  }
}


// ================================================================ settings
// One window, three tabs: where the data comes from, the general DCF assumptions every new
// model starts from, and the per-company changes Funnel is keeping. Save writes the first two
// and the keep period; deleting saved changes happens on the spot.
let dcfDraft = null, srcSnapshot = "", keepDraft = 1;

function wireSources() {
  $("open-sources").addEventListener("click", () => openSettings("sources"));
  $("open-dcf").addEventListener("click", () => openSettings("dcf"));
  $("open-saved").addEventListener("click", () => openSettings("saved"));
  wireSaved();
  $("sources-close").addEventListener("click", closeSources);
  $("sources-cancel").addEventListener("click", closeSources);
  $("sources-modal").addEventListener("mousedown", (e) => { if (e.target.id === "sources-modal") closeSources(); });
  $("sources-save").addEventListener("click", saveSettings);
  $("src-yahoo").addEventListener("change", paintSections);
  $("src-keys").addEventListener("click", onKeyClick);
  onSeg("set-tabs", showSettingsTab);
  $("dcf-restore").addEventListener("click", () => { dcfDraft = clone(state.dcfDefaults); paintDcfForm(); });
  wireDcfForm();
  document.addEventListener("click", (e) => {
    if (e.target.closest("[data-open-sources]")) openSettings("sources");
    else if (e.target.closest("[data-open-dcf]")) openSettings("dcf");
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !$("sources-modal").classList.contains("hidden")) closeSources(); });
}

function providerName(id) {
  if (id === "none") return "No source";
  const p = state.sources && state.sources.providers.find((x) => x.id === id);
  return p ? p.name : id;
}

function paintSourceSummary() {
  if (!state.sources) return;
  const used = [...new Set(state.sources.sections.map((s) => s.selected))];
  const names = used.map(providerName);
  $("src-summary").textContent = names.length > 2 ? `${names[0]} + ${names.length - 1} more` : names.join(" + ");
  $("open-sources").title = "Settings → Data sources\n" + state.sources.sections.map((s) => `${s.label}: ${providerName(s.selected)}`).join("\n");
}

function paintDcfSummary() {
  const d = state.dcf; if (!d) return;
  $("dcf-summary").textContent = `DCF · ${fmt.pct(d.economyGrowth)} growth · ${d.years} years`;
}

function openSettings(tab) {
  const src = state.sources; if (!src) return;
  $("src-yahoo").checked = src.useYahoo !== false;
  $("src-sections").innerHTML = "";
  paintSections();
  paintKeys();
  $("src-email").value = src.contactEmail || "";
  $("src-macro").checked = !!src.macroOnBoard;
  srcSnapshot = JSON.stringify(sourcesPayload());
  dcfDraft = clone(state.dcf || state.dcfDefaults);
  paintDcfForm();
  keepDraft = state.keepSavedDays; paintKeepSaved(); loadSaved();
  setSeg("set-tabs", tab || "sources"); showSettingsTab(tab || "sources");
  $("sources-modal").classList.remove("hidden");
}

function showSettingsTab(tab) {
  $("pane-sources").classList.toggle("hidden", tab !== "sources");
  $("pane-dcf").classList.toggle("hidden", tab !== "dcf");
  $("pane-saved").classList.toggle("hidden", tab !== "saved");
  $("dcf-restore").classList.toggle("hidden", tab !== "dcf");
}

// Yahoo's options disappear while its box is unticked; a section left pointing at it moves to
// the first source that still serves it (the app re-checks the choice when it's saved).
function paintSections() {
  const src = state.sources;
  const yahoo = $("src-yahoo").checked;
  const current = {};
  document.querySelectorAll("#src-sections select[data-section]").forEach((s) => (current[s.dataset.section] = s.value));
  const needsKey = (id) => { const p = src.providers.find((x) => x.id === id); return p && p.keyed && !p.hasKey; };
  $("src-sections").innerHTML = src.sections.map((s) => {
    const opts = s.options.filter((o) => yahoo || o.id !== "yahoo");
    let sel = current[s.id] || s.selected;
    if (!opts.some((o) => o.id === sel)) sel = (opts.find((o) => !needsKey(o.id)) || opts[0] || {}).id;
    return `
    <div class="src-row">
      <span>${esc(s.label)}</span>
      ${opts.length > 1
        ? `<select data-section="${esc(s.id)}">${opts.map((o) => `<option value="${esc(o.id)}" ${o.id === sel ? "selected" : ""}>${esc(o.name)}${needsKey(o.id) ? " (needs a key)" : ""}</option>`).join("")}</select>`
        : `<span class="muted">${esc(opts[0] ? opts[0].name + (needsKey(opts[0].id) ? " (needs a key)" : "") : "No source")}</span>`}
    </div>`;
  }).join("");
}

function paintKeys() {
  $("src-keys").innerHTML = state.sources.providers.filter((p) => p.keyed).map((p) => `
    <div class="src-key">
      <div class="sk-head"><b>${esc(p.name)}</b><span class="muted">${esc(p.keyNote || "")}</span>
        <button class="card-link" data-url="${esc(p.keyUrl)}">Get a key ↗</button></div>
      <div class="sk-row">
        <input type="password" data-key="${esc(p.id)}" placeholder="${p.hasKey ? "•••••••••••• saved — paste to replace" : "Paste your API key"}" spellcheck="false" autocomplete="off" />
        <button class="btn sm" data-save-key="${esc(p.id)}">${p.hasKey ? "Test" : "Save & test"}</button>
        ${p.hasKey ? `<button class="btn sm ghost" data-clear-key="${esc(p.id)}">Remove</button>` : ""}
      </div>
      <div class="sk-status" id="sk-${esc(p.id)}">${p.hasKey ? "Key saved." : ""}</div>
    </div>`).join("");
  $("src-keys").querySelectorAll("input[data-key]").forEach((i) => i.addEventListener("input", () => {
    const b = $("src-keys").querySelector(`[data-save-key="${i.dataset.key}"]`); if (b) b.textContent = i.value.trim() ? "Save & test" : "Test";
  }));
}

async function onKeyClick(e) {
  const save = e.target.closest("[data-save-key]"), clear = e.target.closest("[data-clear-key]");
  if (!save && !clear) return;
  const id = (save || clear).dataset.saveKey || (save || clear).dataset.clearKey;
  const status = $("sk-" + id);
  try {
    if (clear) {
      applyInit(await call("saveKey", { provider: id, key: "" }));
      paintKeys(); $("sk-" + id).textContent = "Key removed.";
      return;
    }
    const input = $("src-keys").querySelector(`input[data-key="${id}"]`);
    if (input.value.trim()) { applyInit(await call("saveKey", { provider: id, key: input.value.trim() })); paintKeys(); }
    const st = $("sk-" + id);
    st.className = "sk-status"; st.textContent = "Testing…";
    await call("testKey", { provider: id });
    st.className = "sk-status ok"; st.textContent = "Key works ✓";
  } catch (err) {
    const st = $("sk-" + id) || status;
    st.className = "sk-status bad"; st.textContent = err.message;
  }
}

// ---------------------------------------------------------------- DCF assumptions tab
const pctIn = (k, disabled) =>
  `<input class="set-in" data-set="${k}" value="${esc((dcfDraft[k] * 100).toFixed(2))}%" spellcheck="false" ${disabled ? "disabled" : ""} />`;
const setSegHtml = (k, opts, cur) =>
  `<div class="seg" data-set-seg="${k}">${opts.map(([v, l]) => `<button data-sv="${v}" class="${String(cur) === v ? "on" : ""}">${l}</button>`).join("")}</div>`;
const setRow = (title, help, control) =>
  `<div class="set-row"><div class="set-txt"><b>${title}</b><small>${help}</small></div><div class="set-ctl">${control}</div></div>`;

function paintDcfForm() {
  const d = dcfDraft;
  $("dcf-form").innerHTML = `
    <div class="fld-label">Growth</div>
    ${setRow("Long-run economy growth", "Nominal GDP growth. It is the terminal growth rate, and the rate every revenue stream's growth fades to by the final forecast year.", pctIn("economyGrowth"))}
    ${setRow("Forecast horizon", "Years of explicit forecast. Growth fades in a straight line from year 1 to the economy rate.", setSegHtml("years", [["5", "5 years"], ["10", "10 years"]], d.years))}
    <div class="fade-demo" id="fade-demo"></div>
    ${setRow("Forecast each revenue stream", "When the 10-K splits revenue by product or segment, every stream gets its own growth path. Uses SEC EDGAR, so it needs the contact email on the Data sources tab (US listings only).",
      `<label class="check"><input type="checkbox" data-set-bool="splitRevenue" ${d.splitRevenue ? "checked" : ""} /> On</label>`)}

    <div class="fld-label">Industry &amp; analysts</div>
    ${setRow("Adapt to industry and analysts", "New models start from the company's history, then margins, reinvestment, beta and the exit multiple drift toward the industry's long-run norms, the first years lean on the analysts' EPS and revenue estimates, and a solver tilts them within realistic ranges toward the analysts' price target. Light keeps the history in charge; Strong leans harder.",
      setSegHtml("calibration", [["off", "Off"], ["light", "Light"], ["strong", "Strong"]], d.calibration))}

    <div class="fld-label">Cost of capital</div>
    ${setRow("Risk-free rate", "Today's 10-year US Treasury yield, or a rate you fix for every model.",
      setSegHtml("liveRiskFree", [["true", "Live 10-yr Treasury"], ["false", "Fixed"]], d.liveRiskFree) + pctIn("riskFree", d.liveRiskFree))}
    ${setRow("Equity risk premium", "Extra return investors want for owning stocks over Treasuries.", pctIn("erp"))}
    ${setRow("Size / other premium", "Added to every cost of equity, e.g. for small companies.", pctIn("sizePremium"))}

    <div class="fld-label">Taxes, timing &amp; terminal value</div>
    ${setRow("Tax rate", "Each company's 3-year average effective rate, or one rate for all.",
      setSegHtml("historicalTax", [["true", "Company's 3-yr rate"], ["false", "Fixed"]], d.historicalTax) + pctIn("taxRate", d.historicalTax))}
    ${setRow("Mid-year convention", "Discount each year's cash flow from mid-year, as if it arrives evenly.",
      `<label class="check"><input type="checkbox" data-set-bool="midYear" ${d.midYear ? "checked" : ""} /> On</label>`)}
    ${setRow("Terminal value method", "Perpetuity growth uses the economy rate above; exit multiple uses today's EV / EBITDA.",
      setSegHtml("tvMethod", [["gordon", "Perpetuity growth"], ["multiple", "Exit multiple"]], d.tvMethod))}`;
  paintFadeDemo();
}

// What the fade does, with numbers: a stream starting at 12% walks down to the economy rate.
function paintFadeDemo() {
  const d = dcfDraft, n = d.years, g1 = 0.12, e = d.economyGrowth;
  if (!isFinite(e)) return;
  const path = Array.from({ length: n }, (_, t) => (t >= n - 1 ? e : g1 + ((e - g1) * t) / (n - 1)));
  $("fade-demo").innerHTML = `<span class="muted">Example: a stream growing 12% in year 1</span>
    <div class="fade-steps">${path.map((g, t) => `<span class="${t === n - 1 ? "end" : ""}"><small>Y${t + 1}</small>${fmt.pct(g)}</span>`).join("")}</div>`;
}

function wireDcfForm() {
  const form = $("dcf-form");
  form.addEventListener("input", (e) => {
    const el = e.target;
    if (el.matches("input.set-in")) {
      const v = mnum(el.value) / 100;
      el.classList.toggle("bad", !isFinite(v));
      if (isFinite(v)) { dcfDraft[el.dataset.set] = v; if (el.dataset.set === "economyGrowth") paintFadeDemo(); }
    } else if (el.matches("[data-set-bool]")) dcfDraft[el.dataset.setBool] = el.checked;
  });
  form.addEventListener("focusin", (e) => { if (e.target.matches("input.set-in")) setTimeout(() => e.target.select(), 0); });
  form.addEventListener("focusout", (e) => {
    const el = e.target; if (!el.matches("input.set-in")) return;
    el.value = (dcfDraft[el.dataset.set] * 100).toFixed(2) + "%"; el.classList.remove("bad");
  });
  form.addEventListener("click", (e) => {
    const b = e.target.closest("[data-set-seg] button"); if (!b) return;
    const k = b.parentElement.dataset.setSeg, v = b.dataset.sv;
    dcfDraft[k] = k === "years" ? +v : k === "tvMethod" || k === "calibration" ? v : v === "true";
    paintDcfForm();
  });
}

function closeSources() { $("sources-modal").classList.add("hidden"); }

function sourcesPayload() {
  const sources = {};
  document.querySelectorAll("#src-sections select[data-section]").forEach((s) => (sources[s.dataset.section] = s.value));
  return { sources, useYahoo: $("src-yahoo").checked, contactEmail: $("src-email").value.trim(), macroOnBoard: $("src-macro").checked };
}

async function saveSettings() {
  const src = sourcesPayload();
  const srcChanged = JSON.stringify(src) !== srcSnapshot;
  const dcfChanged = JSON.stringify(dcfDraft) !== JSON.stringify(state.dcf);
  const keepChanged = keepDraft !== state.keepSavedDays;
  if ($("dcf-form").querySelector(".set-in.bad")) { setSeg("set-tabs", "dcf"); showSettingsTab("dcf"); toast("Check the DCF assumptions", "One of the rates isn't a number.", "warn"); return; }
  try {
    let r = srcChanged ? await call("saveSources", src) : null;
    if (dcfChanged) r = await call("saveDcfSettings", dcfDraft);
    if (keepChanged) r = await call("saveKeepSaved", { days: keepDraft });
    if (r) applyInit(r);
    closeSources();
    staleOthers();
    // A shorter keep period may have just dropped the open company's changes.
    if (keepChanged && !srcChanged && state.symbol) { state.model = null; openCompany(state.symbol); }
    if (srcChanged) {
      toast("Settings saved", "Reloading with the new sources.", "ok");
      loadMarket(false);
      if (state.symbol) { state.model = null; openCompany(state.symbol); }
    } else if (keepChanged && !dcfChanged) {
      toast("Settings saved", `Your changes to a company are now kept ${keepLabel(keepDraft, true)}.`, "ok");
    } else if (dcfChanged) {
      toast("DCF assumptions saved", "New models start from these. A model you've edited keeps its numbers until you reset it.", "ok");
      // Rebuild so an unedited model picks them up (an edited one reloads as saved).
      if (state.model) { state.model = null; if (state.tab === "model") renderModel(); }
    }
  } catch (e) { toast("Couldn't save settings", e.message, "err"); }
}

// ================================================================ saved data
function keepLabel(d, sentence) {
  const l = { 1: "1 day", 7: "1 week", 30: "1 month", 90: "3 months", 365: "1 year", 0: "Forever" }[d] || `${d} days`;
  return sentence ? (d === 0 ? "until you delete them" : "for " + l) : l;
}

// "in 5 hours", "3 days ago": the list only needs to be roughly right.
function relTime(iso) {
  const ms = new Date(iso) - Date.now(), past = ms < 0, a = Math.abs(ms);
  const [u, n] = [["day", 864e5], ["hour", 36e5], ["minute", 6e4]].find(([, n]) => a >= n) || ["minute", 6e4];
  const k = Math.max(1, Math.round(a / n));
  const txt = `${k} ${u}${k === 1 ? "" : "s"}`;
  return past ? `${txt} ago` : `in ${txt}`;
}

function paintSavedSummary() {
  const n = state.savedCount || 0;
  $("saved-summary").textContent = `${n || "None"} saved · keep ${keepLabel(state.keepSavedDays).toLowerCase()}`;
}

async function refreshSavedCount() {
  try { const r = await call("savedList", {}, { quiet: true }); state.savedCount = r.companies.length; paintSavedSummary(); } catch { }
}

function paintKeepSaved() {
  $("keep-saved").innerHTML = state.keepSavedChoices.map((d) =>
    `<button data-v="${d}" class="${d === keepDraft ? "on" : ""}">${keepLabel(d)}</button>`).join("");
}

async function loadSaved() {
  try { paintSaved(await call("savedList", {}, { quiet: true })); }
  catch (e) { $("saved-list").innerHTML = `<div class="saved-empty">Couldn't read saved data: ${esc(e.message)}</div>`; }
}

function paintSaved(r) {
  const list = r.companies || [];
  state.savedCount = list.length; paintSavedSummary();
  $("saved-n").textContent = list.length ? `· ${list.length}` : "";
  $("saved-clear-all").classList.toggle("hidden", list.length < 2);
  disarm($("saved-clear-all"));
  $("saved-list").innerHTML = list.length ? list.map((c) => `
    <div class="saved-row">
      <button class="saved-sym" data-saved-open="${esc(c.symbol)}" title="Open ${esc(c.symbol)}">${esc(c.symbol)}</button>
      <div class="saved-items">${c.items.map((i) => `<div>${esc(i.label)} <span class="muted">· changed ${relTime(i.savedAt)} · ${i.expiresAt ? "expires " + relTime(i.expiresAt) : "kept until deleted"}</span></div>`).join("")}</div>
      <button class="btn sm ghost" data-saved-del="${esc(c.symbol)}" title="Delete saved changes and go back to the latest numbers">Delete</button>
    </div>`).join("")
    : `<div class="saved-empty">Nothing saved. Edit a company's DCF assumptions or comp set and it shows up here.</div>`;
}

// "Delete all" takes two clicks, without a blocking confirm() dialog.
function disarm(btn) { btn.classList.remove("armed"); btn.textContent = "Delete all"; clearTimeout(btn._t); }

async function forgetSaved(payload, symbols) {
  try {
    const r = await call("forgetSaved", payload);
    applyInit(r.init); paintSaved(r.list);
    // The open company goes back to the latest numbers straight away.
    const cur = state.symbol;
    if (cur && (payload.all || symbols.includes(cur))) {
      if (state.model && state.model.symbol === cur) state.model = null;
      state.compSet = (state.company.peers || []).slice(0, 6); state.compRows = null;
      if (state.tab === "model") renderModel(); else if (state.tab === "comps") renderComps();
    }
    toast("Saved data deleted", payload.all ? "Every company is back on the latest numbers." : `${symbols.join(", ")} is back on the latest numbers.`, "ok");
  } catch (e) { toast("Couldn't delete saved data", e.message, "err"); }
}

function wireSaved() {
  $("keep-saved").addEventListener("click", (e) => {
    const b = e.target.closest("button[data-v]"); if (!b) return;
    keepDraft = +b.dataset.v; paintKeepSaved();
  });
  $("saved-list").addEventListener("click", (e) => {
    const del = e.target.closest("[data-saved-del]");
    if (del) { forgetSaved({ symbols: [del.dataset.savedDel] }, [del.dataset.savedDel]); return; }
    const open = e.target.closest("[data-saved-open]");
    if (open) { closeSources(); openCompany(open.dataset.savedOpen); }
  });
  $("saved-clear-all").addEventListener("click", (e) => {
    const b = e.currentTarget;
    if (!b.classList.contains("armed")) {
      b.classList.add("armed"); b.textContent = "Click again to delete all";
      b._t = setTimeout(() => disarm(b), 4000);
      return;
    }
    disarm(b);
    forgetSaved({ all: true }, []);
  });
}

// ================================================================ model (DCF)
// The engine below is the same maths as DcfEngine.cs and the formulas in the exported workbook.
function dcfCompute(a, baseRev) {
  const n = a.years;
  const ke = a.rf + a.beta * a.erp + a.sizePremium;
  const kdAfter = a.costOfDebt * (1 - a.taxRate[0]);
  const wacc = (1 - a.debtWeight) * ke + a.debtWeight * kdAfter;
  const r = { rev: [], growth: [], ebit: [], tax: [], nopat: [], da: [], ebitda: [], capex: [], dnwc: [], ufcf: [], period: [], df: [], pv: [], srev: [] };
  // Revenue streams: shares are weights (scaled to 100%), each grows on its own path, revenue is the sum.
  const streams = a.streams || [];
  const shareSum = streams.reduce((s, x) => s + (+x.share || 0), 0);
  const sv = streams.map((x) => (shareSum ? (baseRev * x.share) / shareSum : 0));
  r.srev = streams.map(() => []);
  let prev = baseRev, sum = 0;
  for (let t = 0; t < n; t++) {
    let rev;
    if (sv.length) {
      rev = 0;
      sv.forEach((v, i) => { sv[i] = v * (1 + streams[i].growth[t]); r.srev[i].push(sv[i]); rev += sv[i]; });
    } else rev = prev * (1 + a.growth[t]);
    r.growth.push(prev ? rev / prev - 1 : NaN);
    const ebit = rev * a.ebitMargin[t];
    const tax = -ebit * a.taxRate[t];
    const da = rev * a.daPct[t];
    const capex = -rev * a.capexPct[t];
    const dnwc = -(rev - prev) * a.nwcPct[t];
    const ufcf = ebit + tax + da + capex + dnwc;
    const period = t + 1 - (a.midYear ? 0.5 : 0);
    const df = 1 / Math.pow(1 + wacc, period);
    r.rev.push(rev); r.ebit.push(ebit); r.tax.push(tax); r.nopat.push(ebit + tax); r.da.push(da); r.ebitda.push(ebit + da); r.capex.push(capex);
    r.dnwc.push(dnwc); r.ufcf.push(ufcf); r.period.push(period); r.df.push(df); r.pv.push(ufcf * df);
    sum += ufcf * df;
    prev = rev;
  }
  const ebitdaN = r.ebit[n - 1] + r.da[n - 1], ufcfN = r.ufcf[n - 1];
  const tvGordon = ufcfN * (1 + a.tgr) / (wacc - a.tgr);
  const tvMultiple = ebitdaN * a.exitMultiple;
  const tv = a.tvMethod === "multiple" ? tvMultiple : tvGordon;
  const pvTv = tv / Math.pow(1 + wacc, n);
  const ev = sum + pvTv;
  const equity = ev - a.debt + a.cash - a.minority;
  return {
    ...r, ke, kdAfter, wacc, sumPv: sum, ebitdaN, tvGordon, tvMultiple, tv, pvTv, ev, equity,
    price: a.shares > 0 ? equity / a.shares : NaN,
    impliedMultiple: ebitdaN ? tvGordon / ebitdaN : NaN,
    impliedG: (tvMultiple * wacc - ufcfN) / (tvMultiple + ufcfN),
    gordonInvalid: a.tvMethod !== "multiple" && wacc <= a.tgr,
  };
}

// Price at another WACC / terminal assumption, holding the cash flows (they don't depend on WACC).
function dcfPriceAt(a, res, w, x, method) {
  const n = a.years;
  let sum = 0;
  for (let t = 0; t < n; t++) sum += res.ufcf[t] / Math.pow(1 + w, res.period[t]);
  const tv = method === "multiple" ? res.ebitdaN * x : res.ufcf[n - 1] * (1 + x) / (w - x);
  if (method !== "multiple" && w <= x) return NaN;
  return (sum + tv / Math.pow(1 + w, n) - a.debt + a.cash - a.minority) / a.shares;
}

const MKIND = {
  pct1: { fmt: (v) => (v * 100).toFixed(1) + "%", parse: (s) => mnum(s) / 100, step: 0.005 },
  pct2: { fmt: (v) => (v * 100).toFixed(2) + "%", parse: (s) => mnum(s) / 100, step: 0.0025 },
  num: { fmt: (v) => v.toFixed(2), parse: (s) => mnum(s), step: 0.05 },
  mult: { fmt: (v) => v.toFixed(1) + "x", parse: (s) => mnum(s), step: 0.5 },
  money: { fmt: (v) => fmt.num(v / 1e6, 1), parse: (s) => mnum(s) * 1e6, step: null },
};
function mnum(s) {
  let t = String(s).trim(), neg = false;
  if (/^\(.*\)$/.test(t)) { neg = true; t = t.slice(1, -1); }
  const v = parseFloat(t.replace(/[,%x\s$]/gi, ""));
  return neg ? -v : v;
}
// Millions; big figures drop the decimal so five forecast years fit on screen.
const mm = (v) => {
  if (v == null || !isFinite(v)) return "–";
  const m = Math.abs(v) / 1e6, txt = fmt.num(m, m >= 10000 ? 0 : 1);
  return v < 0 ? `(${txt})` : txt;
};
const clone = (o) => JSON.parse(JSON.stringify(o));

function wireModel() {
  const tab = $("tab-model");
  onSeg("md-years", (v) => { const m = state.model; if (!m) return; m.a.years = +v; renderModelTable(); recalc(); saveModelSoon(); });
  onSeg("md-sens", (v) => { if (state.model) { state.model.sens = v; paintSens(); } });
  $("md-mid").addEventListener("change", (e) => { if (!state.model) return; state.model.a.midYear = e.target.checked; recalc(); saveModelSoon(); });
  tab.addEventListener("focusin", (e) => { if (e.target.matches("input.in")) setTimeout(() => e.target.select(), 0); });
  tab.addEventListener("input", (e) => {
    const el = e.target;
    if (el.matches("input.sname")) {
      state.model.a.streams[+el.dataset.s].name = el.value;
      const lbl = tab.querySelector(`[data-sname="${el.dataset.s}"]`); if (lbl) lbl.textContent = el.value || `Stream ${+el.dataset.s + 1}`;
      saveModelSoon(); return;
    }
    if (!el.matches("input.in")) return;
    const v = MKIND[el.dataset.kind].parse(el.value);
    el.classList.toggle("bad", !isFinite(v));
    if (!isFinite(v)) return;
    setField(el, v); recalcFor(el); saveModelSoon();
  });
  tab.addEventListener("focusout", (e) => { if (e.target.matches("input.in")) e.target.value = MKIND[e.target.dataset.kind].fmt(getField(e.target)); e.target.classList.remove("bad"); });
  tab.addEventListener("keydown", (e) => {
    const el = e.target; if (!el.matches("input.in")) return;
    if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      e.preventDefault();
      const k = MKIND[el.dataset.kind], cur = getField(el), sign = e.key === "ArrowUp" ? 1 : -1;
      const v = k.step == null ? cur * (1 + 0.01 * sign) : Math.round((cur + sign * k.step) * 1e6) / 1e6;
      setField(el, v); el.value = k.fmt(v); recalcFor(el); saveModelSoon();
      setTimeout(() => el.select(), 0);
    } else if (e.key === "Enter") {
      e.preventDefault();
      const all = [...tab.querySelectorAll("input.in")];
      const next = all[all.indexOf(el) + (e.shiftKey ? -1 : 1)];
      if (next) next.focus(); else el.blur();
    } else if (e.key === "Escape") { el.blur(); }
  });
  tab.addEventListener("click", (e) => {
    if (state.model && onModelsClick(e)) return;
    if (state.model && onStreamClick(e)) return;
    if (state.model && onCalibClick(e)) return;
    if (state.model && onFitClick(e)) return;
    const fill = e.target.closest("[data-fill]");
    if (fill && state.model) {
      const arr = (fill.dataset.s != null ? state.model.a.streams[+fill.dataset.s] : state.model.a)[fill.dataset.fill];
      for (let i = 1; i < arr.length; i++) arr[i] = arr[0];
      renderModelTable(); recalc(); saveModelSoon();
      toast("Filled across", "Every forecast year now uses year 1's value.", "ok");
      return;
    }
    const tv = e.target.closest("[data-tv]");
    if (tv && state.model) { state.model.a.tvMethod = tv.dataset.tv; state.model.sens = tv.dataset.tv; renderModelSide(); recalc(); saveModelSoon(); return; }
    const peer = e.target.closest("[data-peer-multiple]");
    if (peer && state.model) { state.model.a.exitMultiple = +peer.dataset.peerMultiple; renderModelSide(); recalc(); saveModelSoon(); return; }
    const useShares = e.target.closest("[data-use-shares]");
    if (useShares && state.model) {
      state.model.a.shares = +useShares.dataset.useShares;
      renderModelSide(); recalc(); saveModelSoon();
      toast("Share count updated", useShares.dataset.useSharesNote ? `${fmt.big(state.model.a.shares)} shares. ${useShares.dataset.useSharesNote}`
        : `Using ${fmt.big(state.model.a.shares)} diluted shares: the second source's count plus dilutive securities.`, "ok");
      return;
    }
    if (e.target.closest("#md-reset")) { resetModel(); return; }
    if (e.target.closest("#md-export")) { exportModel(); return; }
  });
}

// Inputs with data-s belong to a revenue stream (a.streams[s]); the rest to the model itself.
const fieldOwner = (el) => (el.dataset.alt != null ? state.model.a.alt[el.dataset.alt] : el.dataset.s != null ? state.model.a.streams[+el.dataset.s] : state.model.a);
const recalcFor = (el) => (el.dataset.alt != null ? recalcAlt() : recalc());
function getField(el) {
  const o = fieldOwner(el), k = el.dataset.k;
  return el.dataset.i != null ? o[k][+el.dataset.i] : o[k];
}
function setField(el, v) {
  const o = fieldOwner(el), k = el.dataset.k;
  if (el.dataset.i != null) o[k][+el.dataset.i] = v; else o[k] = v;
}
function inp(k, kind, i, extra, s) {
  const o = s != null ? state.model.a.streams[s] : state.model.a;
  const v = i != null ? o[k][i] : o[k];
  return `<input class="in ${extra || ""}" data-k="${k}" ${i != null ? `data-i="${i}"` : ""} ${s != null ? `data-s="${s}"` : ""} data-kind="${kind}" value="${esc(MKIND[kind].fmt(v))}" spellcheck="false" />`;
}

let modelSaveTimer = 0;
function saveModelSoon() {
  const m = state.model; if (!m) return;
  clearTimeout(modelSaveTimer);
  modelSaveTimer = setTimeout(() => call("saveModel", { symbol: m.symbol, assumptions: m.a }, { quiet: true }).then(refreshSavedCount, () => {}), 600);
}

function modelFrom(r) {
  const a = clone(r.saved || r.defaults);
  a.streams = a.streams || [];
  if (r.base.kit) a.alt = altMerge(altDefaults(r.base.kit, clone(r.defaults), r.base), a.alt);
  return { symbol: r.base.symbol, base: r.base, defaults: r.defaults, a, sens: a.tvMethod, custom: !!r.saved };
}

async function renderModel() {
  const sym = state.symbol;
  if (state.model && state.model.symbol === sym) { showModel(); return; }
  $("md-body").classList.add("hidden"); $("md-error").classList.add("hidden");
  skelModel(); $("md-loading").classList.remove("hidden");
  try {
    const r = await call("model", { symbol: sym });
    if (sym !== state.symbol) return;
    state.model = modelFrom(r);
    showModel();
    const sf = r.base.sharesFlag;
    if (sf && sf.severity === "severe" && !shareWarned.has(r.base.symbol)) {
      shareWarned.add(r.base.symbol);
      toast(`${r.base.symbol}: share counts don't match`, `The model starts on ${fmt.big(sf.value)} shares, the larger count. See the note above the model.`, "warn");
    }
  } catch (e) {
    if (sym !== state.symbol) return;
    $("md-loading").classList.add("hidden");
    const el = $("md-error"); el.textContent = e.message; el.classList.remove("hidden");
  }
}

function showModel() {
  $("md-loading").classList.add("hidden"); $("md-error").classList.add("hidden");
  $("md-body").classList.remove("hidden");
  const m = state.model, b = m.base;
  $("md-bar").innerHTML = `<div class="md-res" id="md-res"></div>
    <div class="md-actions">
      <button class="btn ghost" data-open-dcf title="The general assumptions every new model starts from">Assumption defaults</button>
      <button class="btn ghost" id="md-reset" title="Back to the assumptions built from the data and your Settings">Reset to defaults</button>
      <button class="btn primary" id="md-export"><svg class="ic"><use href="#i-excel"/></svg>Export model</button>
    </div>
    <div class="md-saved hidden" id="md-saved"></div>`;
  const last = b.periods[b.periods.length - 1];
  $("md-sub").textContent = `${b.currency} millions · history from ${b.statementsSource || "the financial statements"}${last ? ` (FY${new Date(last + "T12:00:00").getFullYear()} is the last reported year)` : ""} · edits save automatically`;
  renderModelTable(); renderStreams(); renderCalib(); renderFit(); renderModelSide(); recalc();
  showModelView();
  ensureCompsForModel();
}

// ---------------------------------------------------------------- DCF fit warning
// Banks, REITs, oil and gas, miners, clinical-stage biotech…: a free-cash-flow DCF misreads them
// (DcfFit in IndustryProfiles.cs). Dismissing hides the banner for that company, remembered on this
// computer; a small link under the model title brings it back.
const FIT_KEY = "funnel.dcfFitDismissed";
function fitDismissed() { try { return JSON.parse(localStorage.getItem(FIT_KEY) || "{}"); } catch { return {}; } }
function setFitDismissed(symbol, kind) {
  const d = fitDismissed();
  if (kind) d[symbol] = kind; else delete d[symbol];
  try { localStorage.setItem(FIT_KEY, JSON.stringify(d)); } catch { /* private storage: dismissal lasts this session only */ }
  fitSession[symbol] = kind || null;
}
const fitSession = {};
function isFitDismissed(symbol, kind) {
  const s = fitSession[symbol];
  if (s !== undefined) return s === kind;
  return fitDismissed()[symbol] === kind;
}

function renderFit() {
  const el = $("md-fit"), b = state.model && state.model.base, f = b && b.fit;
  const sub = $("md-sub"); sub.querySelector(".md-fit-show")?.remove();
  if (!f) { el.classList.add("hidden"); el.innerHTML = ""; return; }
  if (isFitDismissed(b.symbol, f.kind)) {
    el.classList.add("hidden"); el.innerHTML = "";
    sub.insertAdjacentHTML("beforeend", ` · <button class="card-link md-fit-show" data-fit-show title="${esc(f.title)}">⚠ DCF fit note</button>`);
    return;
  }
  const by = f.industry ? `Classed as ${esc(f.industry.toLowerCase())}${f.matchedOn ? ` from “${esc(f.matchedOn)}”` : ""}.` : "";
  el.className = `md-fit ${f.severity === "weak" ? "weak" : "caution"}`;
  el.innerHTML = `<div class="md-fit-ic">⚠</div>
    <div class="md-fit-body">
      <div class="md-fit-title">${esc(f.title)}</div>
      <div class="md-fit-why">${esc(f.why)} The model still runs; treat its value as one input, not the answer.</div>
      <div class="md-fit-alt"><span>Better suited:</span><ul>${f.instead.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div>
      ${b.kit && b.kit.recommended !== "dcf" && fitOf(b.kit, b.kit.recommended).level !== "na" ? `<div class="md-fit-go"><button class="btn sm" data-model="${b.kit.recommended}">Open the ${esc(modelName(b.kit.recommended, b.kit))} model (recommended for ${esc(lc1(b.kit.industry) || "this industry")})</button></div>` : ""}
      ${by ? `<div class="md-fit-by">${by}</div>` : ""}
    </div>
    <button class="md-fit-x" data-fit-dismiss title="Hide this note for ${esc(b.symbol)}" aria-label="Dismiss">×</button>`;
}
function onFitClick(e) {
  const b = state.model && state.model.base; if (!b || !b.fit) return false;
  if (e.target.closest("[data-fit-dismiss]")) { setFitDismissed(b.symbol, b.fit.kind); renderFit(); return true; }
  if (e.target.closest("[data-fit-show]")) { setFitDismissed(b.symbol, null); renderFit(); return true; }
  return false;
}

function fyLabel(period, offset, est) { return `FY${new Date(period + "T12:00:00").getFullYear() + offset}${est ? "E" : "A"}`; }

function renderModelTable() {
  const m = state.model, a = m.a, b = m.base, h = b.periods.length, n = a.years;
  setSeg("md-years", String(n)); $("md-mid").checked = !!a.midYear;
  const last = b.periods[h - 1] || new Date().toISOString().slice(0, 10);
  // Every cell carries a data-x key (or data-o) that explain.js turns into a hover card.
  const hist = (vals, kind, src, xk) => b.periods.map((p, i) => {
    const v = vals ? vals[i] : null;
    const x = xk && v != null && isFinite(v) ? ` data-x="${xk}:${i}"` : "";
    if (kind === "link") return `<td class="hist link"${x} ${src ? `data-hk="${src}|${p}"` : ""}>${mm(v)}</td>`;
    if (kind === "amt") return `<td class="hist"${x}>${mm(v)}</td>`;
    return `<td class="hist"${x}>${v == null || !isFinite(v) ? "" : (v * 100).toFixed(1) + "%"}</td>`;
  }).join("");
  const ratio = (num, den, sign = 1) => b.periods.map((_, i) => num[i] != null && den[i] ? sign * num[i] / den[i] : null);
  const growth = b.revenue.map((v, i) => i > 0 && v != null && b.revenue[i - 1] ? v / b.revenue[i - 1] - 1 : null);
  const ebitda = b.ebit.map((v, i) => (v != null && b.da[i] != null ? v + b.da[i] : null));
  const out = (key) => Array.from({ length: n }, (_, t) => `<td data-o="${key}:${t}"></td>`).join("");
  const ins = (k, s) => Array.from({ length: n }, (_, t) => `<td>${inp(k, "pct1", t, "", s)}</td>`).join("");
  const fill = (k, s) => `<button class="fill" data-fill="${k}" ${s != null ? `data-s="${s}"` : ""} title="Copy year 1 to every year">⇥</button>`;
  const row = (label, histCells, fcCells, cls, rk) => `<tr class="${cls || ""}"><th${rk ? ` data-x="row:${rk}" class="has-note"` : ""}>${label}</th>${histCells}${fcCells}</tr>`;
  const blank = b.periods.map(() => `<td class="hist"></td>`).join("");

  // Revenue: one line with its own growth input, or a block per stream whose total is the revenue line.
  const revenueRows = !a.streams.length
    ? row("Revenue", hist(b.revenue, "link", "income:TotalRevenue", "h:revenue"), out("rev"), "em", "rev")
      + row(`% growth ${fill("growth")}`, hist(growth, null, null, "hr:growth"), ins("growth"), "drv", "drv:growth")
    : a.streams.map((st, s) => {
        const hv = streamHist(st);
        const hg = hv && hv.map((v, i) => (i > 0 && v != null && hv[i - 1] ? v / hv[i - 1] - 1 : null));
        return row(`<span data-sname="${s}">${esc(st.name || `Stream ${s + 1}`)}</span>`, hv ? hist(hv, "amt", null, `sh:${s}`) : blank, out(`srev${s}`), "stream", `srev:${s}`)
          + row(`% growth ${fill("growth", s)}`, hv ? hist(hg, null, null, `shr:${s}`) : blank, ins("growth", s), "drv", `drv:growth:${s}`);
      }).join("")
      + row("Revenue", hist(b.revenue, "link", "income:TotalRevenue", "h:revenue"), out("rev"), "em stream-total", "rev")
      + row("% growth", hist(growth, null, null, "hr:growth"), out("growth"), "drv", "growth");

  $("md-table").innerHTML = `
    <thead><tr><th>${esc(b.currency)} millions</th>${b.periods.map((p) => `<th class="hist">${fyLabel(p, 0, false)}</th>`).join("")}
      ${Array.from({ length: n }, (_, t) => `<th>${fyLabel(last, t + 1, true)}</th>`).join("")}</tr></thead>
    <tbody>
      ${revenueRows}
      ${row("EBIT", hist(b.ebit, "link", "income:OperatingIncome", "h:ebit"), out("ebit"), "em", "ebit")}
      ${row(`% margin ${fill("ebitMargin")}`, hist(ratio(b.ebit, b.revenue), null, null, "hr:ebitMargin"), ins("ebitMargin"), "drv", "drv:ebitMargin")}
      ${row("Taxes on EBIT", blank, out("tax"), "", "tax")}
      ${row(`% tax rate ${fill("taxRate")}`, hist(b.taxRate, null, null, "hr:taxRate"), ins("taxRate"), "drv", "drv:taxRate")}
      ${row("NOPAT", blank, out("nopat"), "em", "nopat")}
      ${row("D&amp;A", hist(b.da, "link", "cashflow:DepreciationAndAmortization", "h:da"), out("da"), "", "da")}
      ${row(`% of revenue ${fill("daPct")}`, hist(ratio(b.da, b.revenue), null, null, "hr:daPct"), ins("daPct"), "drv", "drv:daPct")}
      ${row("EBITDA", hist(ebitda, "amt", null, "h:ebitda"), out("ebitda"), "sub", "ebitda")}
      ${row("Capex", hist(b.capex, "link", "cashflow:CapitalExpenditure", "h:capex"), out("capex"), "", "capex")}
      ${row(`% of revenue ${fill("capexPct")}`, hist(ratio(b.capex, b.revenue, -1), null, null, "hr:capexPct"), ins("capexPct"), "drv", "drv:capexPct")}
      ${row("Change in NWC", blank, out("dnwc"), "", "dnwc")}
      ${row(`% of change in revenue ${fill("nwcPct")}`, blank, ins("nwcPct"), "drv", "drv:nwcPct")}
      ${row("Unlevered free cash flow", blank, out("ufcf"), "em total", "ufcf")}
      ${row("Discount factor", blank, out("df"), "sub", "df")}
      ${row("PV of free cash flow", blank, out("pv"), "sub", "pv")}
    </tbody>`;
  decorateModelFlags();
}

// ---------------------------------------------------------------- model cross-checks
// Bridge inputs and the statement lines they come from; a flag on the latest period marks the input.
const BRIDGE_SRC = {
  debt: ["balance:TotalDebt"], cash: ["balance:CashCashEquivalentsAndShortTermInvestments", "balance:CashAndCashEquivalents"],
  minority: ["balance:MinorityInterest"], shares: ["sharesOutstanding", "impliedShares"],
};
function modelFlagFor(k) {
  const m = state.model; if (!m) return null;
  const flags = state.modelFlags[m.symbol] || [];
  const last = m.base.periods[m.base.periods.length - 1] || "";
  const hits = flags.filter((f) => BRIDGE_SRC[k].includes(f.key) && (f.key === "sharesOutstanding" || f.period >= last));
  let f = hits.find((x) => x.severity === "severe") || hits[0] || null;
  // Once the input holds the other source's figure (or anything close to it), the doubt is resolved.
  // The share flag compares basic counts; the input is diluted.
  const other = f && (k === "shares" ? dilutedShares(f.other) : f.other);
  if (f && Math.abs(m.a[k] - other) <= 0.005 * Math.abs(other)) f = null;
  // Implied shares vs shares + dilutive is a standing heads-up, whichever count is in use.
  const imp = k === "shares" ? m.base.sharesFlag : null;
  return imp && (!f || imp.severity === "severe") ? imp : f;
}
function decorateModelFlags() {
  const m = state.model; if (!m) return;
  const flags = state.modelFlags[m.symbol] || [];
  document.querySelectorAll("#md-table [data-hk]").forEach((td) => {
    const f = flags.find((x) => `${x.key}|${x.period}` === td.dataset.hk);
    td.classList.remove("flag-severe", "flag-warn");
    if (f) td.classList.add(flagCls(f));
  });
  Object.keys(BRIDGE_SRC).forEach((k) => {
    const el = document.querySelector(`#md-bridge input.in[data-k="${k}"]`); if (!el) return;
    const line = el.closest(".md-line"), f = modelFlagFor(k);
    line.classList.remove("flag-severe", "flag-warn");
    if (f) line.classList.add(flagCls(f));
  });
}
// A basic share count plus the options, RSUs and convertibles the company reports as dilutive.
const dilutedShares = (basic) => basic + ((state.model && state.model.base.dilutiveShares) || 0);

// A red banner under the price when an input the model relies on is in doubt, with a fix for shares.
function modelWarning() {
  const m = state.model, flags = (m && state.modelFlags[m.symbol]) || [];
  const used = flags.filter((f) => f.severity === "severe" && document.querySelector(`#md-table [data-hk="${f.key}|${f.period}"]`))
    .concat(Object.keys(BRIDGE_SRC).map(modelFlagFor).filter((f) => f && f.severity === "severe" && f.key !== "impliedShares"));
  const sf = used.find((f) => f.key === "sharesOutstanding");
  const fix = sf ? ` <button class="card-link" data-use-shares="${dilutedShares(sf.other)}" title="${esc(sf.message)}">Use ${fmt.big(dilutedShares(sf.other))} diluted shares instead</button>` : "";
  const disagree = used.length ? `<div class="md-flag">⚠ Sources disagree on ${used.length === 1 ? "an input" : `${used.length} inputs`} this model uses (marked in red; hover for both figures).${fix}</div>` : "";
  return disagree + sharesWarning();
}

// Implied shares outstanding (market cap ÷ price, every share class) against shares outstanding
// + dilutive securities: the model starts on the larger, and says so when they're far apart.
function sharesWarning() {
  const m = state.model, f = m && m.base.sharesFlag; if (!f) return "";
  const onPick = Math.abs(m.a.shares - f.value) <= 0.005 * f.value;
  const alt = onPick ? f.other : f.value;
  const btn = `<button class="card-link" data-use-shares="${alt}" data-use-shares-note="${onPick ? "Using the smaller of the two share counts." : "Back to the larger of the two share counts."}">Use ${fmt.big(alt)} shares instead</button>`;
  return `<div class="md-flag ${f.severity === "severe" ? "" : "warn"}">⚠ Share count: ${esc(f.message)}${btn}</div>`;
}

// ---------------------------------------------------------------- industry & analyst calibration
// New models arrive adapted to the industry and leaned toward the analysts (Calibrator.cs). The card
// says what moved and why, and switches the inputs it touched between that and the history alone.
const CAL_KEYS = ["growth", "ebitMargin", "daPct", "capexPct", "nwcPct", "beta", "exitMultiple"];
const CAL_STAGE = { industry: "Industry norm", estimates: "Analyst estimates", target: "Price target" };

function calSame(x, y) {
  const near = (u, v) => Math.abs(u - v) <= 1e-9 * Math.max(1, Math.abs(u));
  return Array.isArray(x) ? Array.isArray(y) && x.every((v, i) => near(v, y[i])) : near(x, y);
}
// Which defaults the touched inputs hold: "cal" (calibrated), "raw" (history only) or null (edited).
function calInUse() {
  const m = state.model, c = m && m.base.calibration; if (!c) return null;
  const holds = (src) => CAL_KEYS.every((k) => calSame(m.a[k], src[k]))
    && m.a.streams.every((st) => { const d = (src.streams || []).find((x) => x.key && x.key === st.key); return !d || calSame(st.growth, d.growth); });
  return holds(m.defaults) ? "cal" : holds(c.uncalibrated) ? "raw" : null;
}
function applyCal(which) {
  const m = state.model, c = m.base.calibration, src = which === "raw" ? c.uncalibrated : m.defaults;
  CAL_KEYS.forEach((k) => (m.a[k] = clone(src[k])));
  m.a.streams.forEach((st) => { const d = (src.streams || []).find((x) => x.key && x.key === st.key); if (d) st.growth = clone(d.growth); });
  renderModelTable(); renderStreams(); renderModelSide(); recalc(); saveModelSoon();
  toast(which === "raw" ? "History-only defaults" : "Calibrated defaults",
    `Growth, margins, reinvestment, beta and the exit multiple ${which === "raw" ? "now come from the company's own history alone" : "are adapted to the industry and the analysts again"}. Everything else is as you left it.`, "ok");
}
function onCalibClick(e) {
  const b = e.target.closest("[data-cal]"); if (!b) return false;
  applyCal(b.dataset.cal); return true;
}

function renderCalib() {
  const el = $("md-calib"), c = state.model.base.calibration;
  el.classList.toggle("hidden", !c);
  if (!c) { el.innerHTML = ""; return; }
  const p = c.profile;
  const match = c.matchLevel === "industry" ? `Industry: <b>${esc(p.name)}</b> (from “${esc(c.matchedOn)}”)`
    : c.matchLevel === "sector" ? `Sector-level norms: <b>${esc(p.name)}</b> (from “${esc(c.matchedOn)}”)` : "<b>Broad-market norms</b> (no industry reported)";
  const norms = `${match} · margin ${fmt.pct(p.margin)} (${fmt.pct(p.marginLo)}–${fmt.pct(p.marginHi)} typical) · D&amp;A ${fmt.pct(p.daPct)} · capex ${fmt.pct(p.capexPct)} · unlevered beta ${p.betaU.toFixed(2)} · EV / EBITDA ${fmt.x(p.evEbitda)}`;
  const an = c.target == null ? "No analyst target or rating."
    : `${c.targetBasis === "rating" ? "Rating-implied price" : "Analysts"}: <b>${fmt.price(c.target)}</b>${c.targetBasis === "rating" ? "" : ` ${esc(c.targetBasis)}`}`
      + (c.targetLow != null && c.targetHigh != null ? ` (range ${fmt.price(c.targetLow)}–${fmt.price(c.targetHigh)})` : "")
      + (c.analysts ? ` · ${c.analysts} analysts` : "") + (c.recommendation ? ` · ${esc(c.recommendation.replace(/_/g, " "))}` : "")
      + (c.pull > 0 ? ` · pull ${fmt.pct(c.pull)} of the way` : "");
  const rows = c.steps.map((s) => `<tr><td>${esc(s.label)}<span class="cal-tag ${s.stage}">${CAL_STAGE[s.stage] || esc(s.stage)}</span></td>
    <td>${esc(s.from)}</td><td><b>${esc(s.to)}</b></td><td class="why">${esc(s.why)}</td></tr>`).join("");
  el.innerHTML = `<div class="card-head"><div><div class="card-title">Industry &amp; analyst calibration <span class="cal-mode">${esc(c.mode)}</span></div>
      <div class="card-sub">${esc(c.summary)}</div></div>
      <div class="row-gap" id="cal-actions"></div></div>
    <div class="cal-line" id="cal-line"></div>
    <div class="cal-meta">${norms}<br>${an}</div>
    ${rows ? `<div class="md-wrap"><table class="cal-steps"><thead><tr><th>Assumption</th><th>Before</th><th>After</th><th>Why</th></tr></thead><tbody>${rows}</tbody></table></div>` : ""}
    ${c.caveats.length ? `<ul class="cal-notes">${c.caveats.map((t) => `<li>${esc(t)}</li>`).join("")}</ul>` : ""}
    <div class="card-sub cal-foot">Industry norms are approximate long-run medians. Terminal growth and the cost-of-capital settings are never changed. <button class="card-link" data-open-dcf>Change the strength in Settings</button></div>`;
}

// The number line: where the history alone, the industry step and the calibration put the value,
// against today's price and the analysts' targets, with the model as it stands now.
function paintCalib(r) {
  const m = state.model, c = m && m.base.calibration, line = $("cal-line");
  if (!c || !line) return;
  const use = calInUse();
  $("cal-actions").innerHTML = use === "raw"
    ? `<span class="cal-state">History-only defaults in use</span><button class="btn ghost" data-cal="cal">Apply calibration</button>`
    : `<span class="cal-state ${use ? "on" : ""}">${use ? "Calibrated defaults in use" : "Edited by you"}</span>`
      + (use ? "" : `<button class="btn ghost" data-cal="cal" title="Put the calibrated growth, margins, reinvestment, beta and exit multiple back">Re-apply calibration</button>`)
      + `<button class="btn ghost" data-cal="raw" title="Growth, margins, reinvestment, beta and exit multiple from the company's history alone">Use history only</button>`;
  const live = r && !r.gordonInvalid && isFinite(r.price) ? r.price : null;
  const pts = [
    { k: "hist", label: "History only", v: c.valueHistory },
    { k: "ind", label: "Industry-adjusted", v: c.valueIndustry },
    { k: "cal", label: "Calibrated default", v: c.valueCalibrated },
    { k: "px", label: "Price", v: c.price },
    { k: "tgt", label: c.targetBasis === "rating" ? "Rating-implied" : "Analyst target", v: c.target },
    { k: "you", label: "Your model", v: live },
  ].filter((p) => p.v != null && isFinite(p.v));
  const ends = pts.map((p) => p.v).concat([c.targetLow, c.targetHigh].filter((v) => v != null && isFinite(v)));
  if (ends.length < 2) { line.innerHTML = ""; return; }
  let lo = Math.min(...ends), hi = Math.max(...ends);
  const pad = (hi - lo) * 0.05 || Math.abs(hi) * 0.1 || 1; lo -= pad; hi += pad;
  const x = (v) => ((v - lo) / (hi - lo)) * 100;
  const span = (a, b, cls, title) => `<div class="${cls}" style="left:${Math.min(x(a), x(b))}%;width:${Math.abs(x(b) - x(a))}%"${title ? ` title="${title}"` : ""}></div>`;
  const range = c.targetLow != null && c.targetHigh != null ? span(c.targetLow, c.targetHigh, "cal-range", `Analyst targets ${fmt.price(c.targetLow)}–${fmt.price(c.targetHigh)}`) : "";
  const move = c.valueHistory != null && c.valueCalibrated != null ? span(c.valueHistory, c.valueCalibrated, "cal-move " + (c.valueCalibrated >= c.valueHistory ? "up" : "down")) : "";
  line.innerHTML = `<div class="cal-track">${range}${move}${pts.map((p) => `<span class="cal-pt ${p.k}" style="left:${x(p.v)}%" title="${esc(p.label)}: ${fmt.price(p.v)}"></span>`).join("")}</div>
    <div class="cal-legend">${pts.map((p) => `<span><i class="cal-dot ${p.k}"></i>${esc(p.label)} <b>${fmt.price(p.v)}</b></span>`).join("")}${range ? `<span><i class="cal-dot rng"></i>Target range</span>` : ""}</div>`;
}

// ---------------------------------------------------------------- revenue streams
const streamHist = (st) => {
  const h = st.key && (state.model.base.streams || []).find((x) => x.key === st.key);
  return h ? h.values : null;
};

function renderStreams() {
  const m = state.model, a = m.a, b = m.base;
  const last = b.periods[b.periods.length - 1];
  const fy = last ? fyLabel(last, 0, false) : "base-year";
  // The default's note describes where the default streams came from; once edited, it still does.
  const note = (b.notes && b.notes.streams) || "Revenue is forecast as one line.";
  const head = (actions) => `<div class="card-head"><div><div class="card-title">Revenue streams</div><div class="card-sub">${esc(note)}</div></div>
    <div class="row-gap">${actions}</div></div>`;
  if (!a.streams.length) {
    $("md-streams").innerHTML = head(`<button class="btn ghost" id="st-split">Split into streams</button>`);
    return;
  }
  $("md-streams").innerHTML = head(`<button class="btn ghost" id="st-add">+ Add stream</button><button class="btn ghost" id="st-one">Use one revenue line</button>`) + `
    <table class="streams">
      <thead><tr><th>Stream</th><th>${esc(fy)} revenue</th><th>Share of ${esc(fy)}</th><th></th></tr></thead>
      <tbody>${a.streams.map((st, s) => {
        const hv = streamHist(st), lastV = hv ? hv[hv.length - 1] : null;
        return `<tr>
          <td><input class="sname" data-s="${s}" value="${esc(st.name)}" placeholder="Stream ${s + 1}" spellcheck="false" /></td>
          <td class="muted" ${lastV != null ? `data-x="sl:${s}"` : ""}>${lastV != null ? mm(lastV) : "–"}</td>
          <td>${inp("share", "pct1", null, "", s)}</td>
          <td><button class="icon-btn" data-st-rm="${s}" title="Remove this stream"><svg class="ic"><use href="#i-close"/></svg></button></td>
        </tr>`;
      }).join("")}</tbody>
      <tfoot><tr><td>Total</td><td></td><td id="st-total" data-x="stot"></td><td></td></tr></tfoot>
    </table>
    <div class="md-hint hidden" id="st-hint"></div>`;
  paintStreamTotal();
}

// Shares are weights: when they don't add to 100% the model scales them, and says so.
function paintStreamTotal() {
  const a = state.model && state.model.a; if (!a || !a.streams.length || !$("st-total")) return;
  const tot = a.streams.reduce((s, x) => s + (+x.share || 0), 0), off = Math.abs(tot - 1) > 0.0005;
  $("st-total").textContent = fmt.pct(tot);
  $("st-total").classList.toggle("warn", off);
  $("st-hint").classList.toggle("hidden", !off);
  $("st-hint").textContent = tot > 0
    ? `Shares add to ${fmt.pct(tot)}, so the model scales them to 100%: the streams always start from reported revenue.`
    : "Give at least one stream a share above 0%.";
}

function onStreamClick(e) {
  const m = state.model, a = m.a;
  const changed = () => { renderModelTable(); renderStreams(); recalc(); saveModelSoon(); return true; };
  const rm = e.target.closest("[data-st-rm]");
  if (rm) { a.streams.splice(+rm.dataset.stRm, 1); return changed(); }
  if (e.target.closest("#st-add")) {
    a.streams.push({ name: `Stream ${a.streams.length + 1}`, key: "", share: 0, growth: a.growth.slice() });
    return changed();
  }
  if (e.target.closest("#st-one")) {
    // Keep the revenue path the streams add up to, as the single line's growth.
    a.growth = a.growth.map((g, t) => (m.res && t < m.res.growth.length && isFinite(m.res.growth[t]) ? Math.round(m.res.growth[t] * 1e4) / 1e4 : g));
    a.streams = [];
    return changed();
  }
  if (e.target.closest("#st-split")) {
    a.streams = m.defaults.streams && m.defaults.streams.length ? clone(m.defaults.streams)
      : [{ name: "Stream 1", key: "", share: 0.5, growth: a.growth.slice() }, { name: "Stream 2", key: "", share: 0.5, growth: a.growth.slice() }];
    return changed();
  }
  return false;
}

function renderModelSide() {
  const m = state.model, a = m.a, b = m.base;
  const line = (label, k, kind) => `<div class="md-line"><span class="has-note">${label}</span>${inp(k, kind)}</div>`;
  const outLine = (label, id, strong) => `<div class="md-line out ${strong ? "strong" : ""}"><span>${label}</span><b id="${id}"></b></div>`;

  $("md-wacc").innerHTML = `<div class="card-title">Cost of capital</div><div class="card-sub">CAPM cost of equity, after-tax cost of debt</div>
    ${line("Risk-free rate", "rf", "pct2")}
    ${line("Equity risk premium", "erp", "pct2")}
    ${line("Beta", "beta", "num")}
    ${line("Size / other premium", "sizePremium", "pct2")}
    ${outLine("Cost of equity", "mo-ke")}
    ${line("Pre-tax cost of debt", "costOfDebt", "pct2")}
    ${outLine("After-tax cost of debt", "mo-kd")}
    ${line("Debt / (debt + equity)", "debtWeight", "pct1")}
    ${outLine("WACC", "mo-wacc", true)}`;

  const peers = (state.compRows || []).filter((r) => r.symbol !== state.symbol && !r.error).map((r) => r.metrics.evToEbitda).filter((v) => v != null && isFinite(v) && v > 0);
  const peerMed = peers.length >= 2 ? Math.round(median(peers) * 10) / 10 : null;
  $("md-tv").innerHTML = `<div class="card-title">Terminal value</div>
    <div class="seg full" style="margin:12px 0 4px">
      <button data-tv="gordon" class="${a.tvMethod !== "multiple" ? "on" : ""}">Perpetuity growth</button>
      <button data-tv="multiple" class="${a.tvMethod === "multiple" ? "on" : ""}">Exit multiple</button>
    </div>
    ${line("Terminal growth", "tgr", "pct2")}
    ${line("Exit EV / EBITDA", "exitMultiple", "mult")}
    ${peerMed != null ? `<div class="md-hint"><button class="card-link" data-peer-multiple="${peerMed}">Use peer median (${peerMed.toFixed(1)}x)</button></div>` : `<div class="md-hint muted">Load Comps to use the peer median multiple.</div>`}
    ${outLine("Terminal value", "mo-tv")}
    ${outLine("PV of terminal value", "mo-pvtv")}
    ${outLine("Terminal value % of EV", "mo-tvpct")}
    ${outLine(a.tvMethod === "multiple" ? "Implied perpetuity growth" : "Implied exit multiple", "mo-implied")}`;

  $("md-bridge").innerHTML = `<div class="card-title">Equity bridge</div><div class="card-sub">${esc(b.currency)} millions · shares in millions</div>
    ${outLine("PV of free cash flows", "mo-sumpv")}
    ${outLine("PV of terminal value", "mo-pvtv2")}
    ${outLine("Enterprise value", "mo-ev", true)}
    ${line("Less: total debt", "debt", "money")}
    ${line("Plus: cash &amp; investments", "cash", "money")}
    ${line("Less: minority interest", "minority", "money")}
    ${outLine("Equity value", "mo-eq", true)}
    ${line("Diluted shares", "shares", "money")}
    ${outLine("Implied share price", "mo-px", true)}`;
  decorateModelFlags();
}

function recalc() {
  const m = state.model; if (!m) return;
  const a = m.a, b = m.base;
  const baseRev = [...b.revenue].reverse().find((v) => v != null) || 0;
  const r = dcfCompute(a, baseRev);
  m.res = r;
  document.querySelectorAll("#md-table [data-o]").forEach((td) => {
    const [k, t] = td.dataset.o.split(":");
    const v = k.startsWith("srev") ? r.srev[+k.slice(4)][+t] : r[k][+t];
    td.textContent = k === "df" ? v.toFixed(3) : k === "growth" ? fmt.pct(v) : mm(v);
    td.classList.toggle("neg", v < 0 && k !== "df");
  });
  paintStreamTotal();
  const set = (id, txt) => { const el = $(id); if (el) el.textContent = txt; };
  set("mo-ke", fmt.pct(r.ke, 2)); set("mo-kd", fmt.pct(r.kdAfter, 2)); set("mo-wacc", fmt.pct(r.wacc, 2));
  set("mo-tv", mm(r.tv)); set("mo-pvtv", mm(r.pvTv)); set("mo-tvpct", fmt.pct(r.pvTv / r.ev));
  set("mo-implied", a.tvMethod === "multiple" ? fmt.pct(r.impliedG, 2) : fmt.x(r.impliedMultiple));
  set("mo-sumpv", mm(r.sumPv)); set("mo-pvtv2", mm(r.pvTv)); set("mo-ev", mm(r.ev)); set("mo-eq", mm(r.equity));
  set("mo-px", r.gordonInvalid ? "–" : fmt.price(r.price));

  const cur = b.price, up = cur && isFinite(r.price) ? r.price / cur - 1 : null;
  $("md-res").innerHTML = r.gordonInvalid
    ? `<div class="md-price bad"><small>Implied share price</small><b>–</b><span>WACC (${fmt.pct(r.wacc, 2)}) must be above terminal growth (${fmt.pct(a.tgr, 2)}).</span></div>`
    : `<div class="md-price" data-x="px"><small>Implied share price · ${esc(b.symbol)}</small><b>${fmt.price(r.price)}<em>${esc(b.currency)}</em></b></div>
      <div class="md-kv" data-x="cur">Current price<b>${fmt.price(cur)}</b></div>
      ${b.calibration && b.calibration.target != null ? `<div class="md-kv" title="${b.calibration.targetBasis === "rating" ? "No price target: the price the consensus rating implies" : `Analysts' ${esc(b.calibration.targetBasis)}${b.calibration.analysts ? `, ${b.calibration.analysts} analysts` : ""}`}">Analyst target<b>${fmt.price(b.calibration.target)}</b></div>` : ""}
      <div class="md-kv" data-x="up">Upside / (downside)<b class="${dir(up)}">${up == null ? "–" : (up >= 0 ? "+" : "") + fmt.pct(up)}</b></div>
      <div class="md-kv" data-x="ev">Enterprise value<b>${fmt.big(r.ev)}</b></div>
      <div class="md-kv" data-x="wacc">WACC<b>${fmt.pct(r.wacc, 2)}</b></div>
      <div class="md-kv" data-x="tvpct">Terminal value % of EV<b>${fmt.pct(r.pvTv / r.ev)}</b></div>${modelWarning()}`;
  paintCalib(r);
  paintSens();
  paintFootball();
  paintTabsSoon();
}

function paintSens() {
  const m = state.model; if (!m || !m.res) return;
  const a = m.a, r = m.res, method = m.sens || a.tvMethod;
  setSeg("md-sens", method);
  const ws = [-2, -1, 0, 1, 2].map((k) => r.wacc + k * 0.005);
  const xs = method === "multiple" ? [-2, -1, 0, 1, 2].map((k) => a.exitMultiple + k) : [-2, -1, 0, 1, 2].map((k) => a.tgr + k * 0.0025);
  const cur = m.base.price;
  const cell = (p, centre, i, j) => {
    if (!isFinite(p)) return `<td class="na" data-x="sens:${i}:${j}">–</td>`;
    const d = cur ? p / cur - 1 : 0, s = Math.min(1, Math.abs(d) / 0.5);
    const bg = d >= 0 ? `rgba(22,163,74,${0.08 + 0.32 * s})` : `rgba(220,38,38,${0.08 + 0.32 * s})`;
    return `<td class="${centre ? "centre" : ""}" style="background:${bg}" data-x="sens:${i}:${j}">${fmt.price(p)}</td>`;
  };
  $("md-sens-grid").innerHTML = `<table class="sens">
    <thead><tr><th>WACC ↓ · ${method === "multiple" ? "exit EV / EBITDA" : "terminal growth"} →</th>
      ${xs.map((x, j) => `<th class="${j === 2 ? "centre" : ""}" data-x="sensx:${j}">${method === "multiple" ? fmt.x(x) : fmt.pct(x, 2)}</th>`).join("")}</tr></thead>
    <tbody>${ws.map((w, i) => `<tr><th class="${i === 2 ? "centre" : ""}" data-x="sensw:${i}">${fmt.pct(w, 2)}</th>
      ${xs.map((x, j) => cell(dcfPriceAt(a, r, w, x, method), i === 2 && j === 2, i, j)).join("")}</tr>`).join("")}</tbody></table>
    <div class="card-sub" style="margin-top:8px">Today's price: <b>${fmt.price(cur)}</b> · steps of 0.50% WACC and ${method === "multiple" ? "1.0x" : "0.25%"}; the exported workbook has both grids as live formulas.</div>`;
}

// ---------------------------------------------------------------- football field
// Every way the app can put a price on the shares, side by side: the DCF over a sensible range of
// WACC and terminal assumptions, peer multiples applied to this company, the analysts' targets and
// the past year's trading range. Lines mark today's price and your model's value; it redraws as
// the model changes.
const FF_GROUPS = [["dcf", "Intrinsic · DCF"], ["alt", "Intrinsic · other models"], ["comps", "Relative · peer multiples"], ["market", "Market"]];
const ffFin = (v) => v != null && isFinite(v);
function ffQuantile(s, p) { const i = (s.length - 1) * p, f = Math.floor(i); return s[f] + (s[Math.ceil(i)] - s[f]) * (i - f); }

function ffRows() {
  const m = state.model, a = m.a, r = m.res, b = m.base, c = state.company || {};
  const rows = [], skipped = [];

  // DCF: the sensitivity grid's extremes, WACC ±1% against growth ±0.5% or the exit multiple ±2x.
  const grid = (method) => {
    const ws = [-2, -1, 0, 1, 2].map((k) => r.wacc + k * 0.005);
    const xs = [-2, -1, 0, 1, 2].map((k) => (method === "multiple" ? a.exitMultiple + k : a.tgr + k * 0.0025));
    const ps = ws.flatMap((w) => xs.map((x) => dcfPriceAt(a, r, w, x, method))).filter(ffFin);
    const mid = dcfPriceAt(a, r, r.wacc, method === "multiple" ? a.exitMultiple : a.tgr, method);
    return ps.length ? { lo: Math.min(...ps), hi: Math.max(...ps), mid: ffFin(mid) ? mid : null } : null;
  };
  const wRange = `WACC ${fmt.pct(r.wacc - 0.01, 1)}–${fmt.pct(r.wacc + 0.01, 1)}`;
  const g = grid("gordon"), x = grid("multiple");
  const dcfOn = !m.base.kit || altModel() === "dcf";
  if (g) rows.push({ group: "dcf", label: "DCF · perpetuity growth", mine: dcfOn && a.tvMethod !== "multiple", ...g,
    basis: `${wRange} · terminal growth ${fmt.pct(a.tgr - 0.005, 2)}–${fmt.pct(a.tgr + 0.005, 2)}` });
  if (x) rows.push({ group: "dcf", label: "DCF · exit multiple", mine: dcfOn && a.tvMethod === "multiple", ...x,
    basis: `${wRange} · exit EV / EBITDA ${fmt.x(a.exitMultiple - 2)}–${fmt.x(a.exitMultiple + 2)}` });

  // Peers: a peer multiple applied to this company's own metric. The metric is backed out of the
  // company's own multiple, so both are measured the same way, by the same source, on the same dates.
  let compsState = "ok";
  const all = state.compRows;
  const me = all && all.find((q) => q.symbol === b.symbol && !q.error);
  const peers = all ? all.filter((q) => q.symbol !== b.symbol && !q.error) : [];
  if (!all) compsState = "loading";
  else if (!me || !peers.length) compsState = "none";
  else {
    const mt = me.metrics || {}, p0 = me.price ?? c.price;
    const ev = mt.enterpriseValue, mc = mt.marketCap;
    const shares = mc > 0 && p0 > 0 ? mc / p0 : null, netDebt = ffFin(ev) && ffFin(mc) ? ev - mc : null;
    const toPrice = (key, mult, equity) => {
      const own = mt[key];
      if (equity) return p0 * mult / own;
      return (mult * (ev / own) - netDebt) / shares;
    };
    const defs = [["evToEbitda", "EV / EBITDA", false, "EBITDA"], ["forwardPE", "Forward P/E", true, "forecast earnings"],
      ["trailingPE", "Trailing P/E", true, "earnings"], ["evToRevenue", "EV / revenue", false, "revenue"]];
    if (b.fit && (b.fit.kind === "financial" || b.fit.kind === "reit")) defs.unshift(["priceToBook", "Price / book", true, "book value"]);
    for (const [key, label, equity, what] of defs) {
      const vals = peers.map((q) => q.metrics[key]).filter((v) => ffFin(v) && v > 0).sort((u, v) => u - v);
      if (vals.length < 2) { skipped.push(`${label}: fewer than two peers report it`); continue; }
      if (!(mt[key] > 0) || !(p0 > 0) || (!equity && (!ffFin(ev) || !(shares > 0) || netDebt == null))) {
        skipped.push(`${label}: ${b.symbol} has no positive ${what} to apply it to`); continue;
      }
      const wide = vals.length < 4;
      const lo = wide ? vals[0] : ffQuantile(vals, 0.25), hi = wide ? vals[vals.length - 1] : ffQuantile(vals, 0.75), med = median(vals);
      const pl = toPrice(key, lo, equity), ph = toPrice(key, hi, equity), pm = toPrice(key, med, equity);
      if (![pl, ph, pm].every(ffFin)) continue;
      rows.push({ group: "comps", label, lo: Math.min(pl, ph), hi: Math.max(pl, ph), mid: pm,
        basis: `${fmt.x(lo)}–${fmt.x(hi)}, the ${wide ? "peer low to high" : "middle half of the peers"} (${vals.length}); median ${fmt.x(med)} · ${b.symbol} trades at ${fmt.x(mt[key])}` });
    }
  }

  rows.push(...altFootballRows());

  // Market: what analysts expect and where the shares have actually traded.
  const an = c.analysts;
  if (an && ffFin(an.targetLow) && ffFin(an.targetHigh))
    rows.push({ group: "market", label: "Analyst price targets", lo: an.targetLow, hi: an.targetHigh, mid: ffFin(an.targetMean) ? an.targetMean : null,
      basis: `Lowest to highest${an.analystCount ? ` of ${an.analystCount} analysts` : ""}; tick = mean ${fmt.price(an.targetMean)}` });
  const mx = c.metrics || {};
  if (ffFin(mx.fiftyTwoWeekLow) && ffFin(mx.fiftyTwoWeekHigh))
    rows.push({ group: "market", label: "52-week trading range", lo: mx.fiftyTwoWeekLow, hi: mx.fiftyTwoWeekHigh, mid: null,
      basis: "Lowest to highest price over the past year" });
  return { rows, skipped, compsState };
}

function paintFootball() {
  const el = $("md-ff"), m = state.model; if (!el || !m || !m.res) return;
  const r = m.res, b = m.base, c = state.company || {}, cur = b.price;
  const { rows, skipped, compsState } = ffRows();
  // The dashed line is the model on screen: the DCF, or the NAV, LBO or excess-return value.
  const sel = m.base.kit && m.a.alt ? altModel() : "dcf";
  const you = sel === "dcf" ? (r.gordonInvalid ? NaN : r.price) : altValue(sel);
  const youLabel = { dcf: "Your DCF", nav: "Your NAV", lbo: "Your LBO", er: "Your excess return" }[sel];
  const head = `<div class="card-head"><div>
      <div class="card-title">Football field</div>
      <div class="card-sub">Implied value per share (${esc(b.currency)}) by method · bar = range · tick = midpoint (DCF base case, peer median, mean target) · redraws as you edit the model</div>
    </div></div>`;
  if (!rows.length) { el.innerHTML = head + `<div class="muted">Nothing to compare yet.</div>`; return; }

  // The axis covers every bar and both lines, but stays within −1× to 4× today's price so one wild
  // multiple can't squash everything else into a corner; a bar running past the edge gets an arrow.
  const ends = rows.flatMap((x) => [x.lo, x.hi]).concat([cur, you].filter(ffFin));
  let lo = Math.min(...ends), hi = Math.max(...ends);
  if (cur > 0) { lo = Math.max(lo, -cur); hi = Math.min(hi, 4 * cur); }
  if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
  const padv = (hi - lo) * 0.04; lo -= padv; hi += padv;
  // 8%–92% of the track, leaving room for the end labels.
  const pos = (v) => 8 + ((Math.max(lo, Math.min(hi, v)) - lo) / (hi - lo)) * 84;
  const ticks = niceTicks(lo, hi, 6);

  const bar = (x) => {
    const l = pos(x.lo), w = Math.max(0.6, pos(x.hi) - l);
    const cutL = x.lo < lo ? "◂ " : "", cutR = x.hi > hi ? " ▸" : "";
    const tip = `${x.label}: ${fmt.price(x.lo)} to ${fmt.price(x.hi)}${x.mid != null ? ` · midpoint ${fmt.price(x.mid)}` : ""}${cur > 0 && x.mid != null ? ` (${x.mid >= cur ? "+" : ""}${fmt.pct(x.mid / cur - 1)} vs today)` : ""}`;
    return `<div class="ff-row">
        <div class="ff-label"><b>${esc(x.label)}${x.mine ? ` <em class="ff-mine" title="The model you have open (for the DCF, its terminal value method)">your method</em>` : ""}</b><small>${esc(x.basis)}</small></div>
        <div class="ff-track" title="${esc(tip)}">
          <div class="ff-bar g-${x.group}" style="left:${l}%;width:${w}%">
            <span class="ff-lo">${cutL}${fmt.price(x.lo)}</span><span class="ff-hi">${fmt.price(x.hi)}${cutR}</span>
          </div>
          ${x.mid != null && x.mid >= lo && x.mid <= hi ? `<div class="ff-mid" style="left:${pos(x.mid)}%"></div>` : ""}
        </div>
      </div>`;
  };
  const groupHtml = FF_GROUPS.map(([k, name]) => {
    const list = rows.filter((x) => x.group === k);
    let body = list.map(bar).join("");
    if (k === "comps" && !list.length)
      body = `<div class="ff-row"><div class="ff-label"><small>${compsState === "loading" ? "Loading peer multiples…"
        : compsState === "none" ? "No peers yet: add tickers on the Comps tab to see multiples here." : "No peer multiple applies."}</small></div><div></div></div>`;
    return body ? `<div class="ff-group">${esc(name)}</div>${body}` : "";
  }).join("");

  const line = (v, cls) => (ffFin(v) && v >= lo && v <= hi ? `<div class="ff-line ${cls}" style="left:${pos(v)}%"></div>` : "");
  const tag = (v, cls, text) => (ffFin(v) ? `<div class="ff-tag ${cls}" style="left:${pos(v)}%">${text} ${fmt.price(v)}</div>` : "");
  const notes = [];
  if (c.currency && b.currency && c.currency !== b.currency)
    notes.push(`The DCF is in ${esc(b.currency)} (the statements' currency); the price, peers and targets are in ${esc(c.currency)}.`);
  if (skipped.length) notes.push(`Not shown: ${skipped.map(esc).join("; ")}.`);

  el.innerHTML = head + `<div class="ff">
      <div class="ff-lines">${ticks.map((t) => `<div class="ff-grid" style="left:${pos(t)}%"></div>`).join("")}${line(cur, "now")}${line(you, "you")}</div>
      <div class="ff-row ff-tags"><div></div><div class="ff-track">${tag(cur, "now", "Today")}</div></div>
      ${groupHtml}
      <div class="ff-row ff-axis"><div></div><div class="ff-track">${ticks.map((t) => `<span class="ff-tick" style="left:${pos(t)}%">${fmt.num(t, Math.abs(hi - lo) < 20 ? 1 : 0)}</span>`).join("")}</div></div>
      <div class="ff-row ff-tags"><div></div><div class="ff-track">${tag(you, "you", youLabel)}</div></div>
    </div>
    <div class="ff-legend"><span><i class="g-dcf"></i>DCF</span>${rows.some((x) => x.group === "alt") ? `<span><i class="g-alt"></i>NAV · LBO · excess return</span>` : ""}<span><i class="g-comps"></i>Peer multiples</span><span><i class="g-market"></i>Market</span>
      <span><i class="ln now"></i>Today's price</span><span><i class="ln you"></i>Your model</span></div>
    ${notes.length ? `<div class="ff-note">${notes.join(" ")}</div>` : ""}`;
}

// Peer multiples need the comp set; fetch it quietly if the Comps tab hasn't been opened yet.
async function ensureCompsForModel() {
  if (state.compRows || state._ffComps === state.symbol) return;
  const sym = state.symbol; state._ffComps = sym;
  try {
    const rows = await call("comps", { symbol: sym, symbols: state.compSet, save: "" });
    if (sym !== state.symbol) return;
    if (!state.compRows) state.compRows = rows;
  } catch { if (sym === state.symbol) state.compRows = []; }
  paintFootball();
}

// ---------------------------------------------------------------- other valuation models
// The DCF isn't the right tool for every company. Funnel picks the model the industry calls for
// (ValuationKit in Valuation.cs): NAV for miners, producers and property companies, the excess-
// return model for banks and insurers, the DCF for the rest, with an LBO as a floor for steady
// cash generators. Each model says how well it fits this company. Inputs live in a.alt and save
// with the DCF's; the operating case for the LBO is the DCF's own.
const ALT_MODELS = ["dcf", "nav", "lbo", "er"];
const FIT_LABEL = { good: "Good fit", caution: "Use with care", weak: "Weak fit", na: "Doesn't apply" };
function modelName(id, kit) {
  if (id === "nav") return kit && kit.nav.kind === "resource" ? "NAV · reserves" : kit && kit.nav.kind === "property" ? "NAV · properties" : "NAV";
  return { dcf: "DCF", lbo: "LBO", er: "Excess return" }[id];
}
const fitOf = (kit, id) => (kit ? kit.fits.find((f) => f.model === id) : null) || { level: "good", title: "", why: "", instead: [] };

// More input kinds: prices, big unit counts (production) and whole years.
MKIND.price = { fmt: (v) => fmt.num(v, 2), parse: (s) => mnum(s), step: null };
MKIND.big = {
  fmt: (v) => fmt.big(v),
  parse: (s) => { const t = String(s).trim().toUpperCase(), k = { K: 1e3, M: 1e6, B: 1e9, T: 1e12 }[t.slice(-1)] || 1; return mnum(k > 1 ? t.slice(0, -1) : t) * k; },
  step: null,
};
MKIND.yrs = { fmt: (v) => String(Math.round(v)), parse: (s) => Math.round(mnum(s)), step: 1 };

const clampN = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
function altDefaults(kit, a, b) {
  const { shares, cash, debt, minority } = a;
  const n = kit.nav, l = kit.lbo, e = kit.er;
  const nav = n.kind === "resource"
    ? { spot: n.spot || 0, ltPrice: n.spot || 0, production: n.production || 0, prodGrowth: 0, aisc: n.aisc || 0, life: n.life || 10, startYear: 1,
        initialCapex: 0, royalty: 0, taxRate: a.taxRate[0], discount: n.discountRate, closure: 0, otherAssets: 0, cash, debt, minority, shares }
    : n.kind === "property"
      ? { noi: n.noi || 0, noiGrowth: a.tgr, capRate: n.capRate, otherAssets: 0, cash, debt, preferred: n.preferred || 0, minority, shares }
      : null;
  const lbo = { ebitda: l.ebitda || 0, entryPremium: l.entryPremium, leverage: l.leverage, interestRate: l.interestRate, feesPct: l.feesPct,
    years: l.years, exitMultiple: 10, targetIrr: l.targetIrr, cash, debt, shares };
  const entry = lbo.ebitda > 0 && b.price > 0 ? (b.price * (1 + lbo.entryPremium) * shares + debt - cash) / lbo.ebitda : NaN;
  lbo.exitMultiple = isFinite(entry) && entry > 0 ? Math.round(clampN(entry, 4, 30) * 2) / 2 : 10;
  const coe = a.rf + a.beta * a.erp + a.sizePremium;
  const er = { bookValue: e.bookValue || 0, roe1: clampN(e.roeLatest ?? e.roeAverage ?? coe, -0.2, 0.4),
    roeLong: clampN(e.roeAverage ?? coe, coe - 0.06, coe + 0.06), payout: e.payout, rf: a.rf, beta: a.beta, erp: a.erp,
    sizePremium: a.sizePremium, g: a.tgr, years: 10, shares };
  return { model: null, nav, lbo, er };
}
// A saved set keeps its numbers; fields added since come from today's defaults.
function altMerge(def, saved) {
  if (!saved) return def;
  const out = { ...def, model: ALT_MODELS.includes(saved.model) ? saved.model : null };
  ["nav", "lbo", "er"].forEach((k) => { if (def[k] && saved[k]) out[k] = { ...def[k], ...saved[k] }; });
  return out;
}
const altModel = () => { const m = state.model; return (m.a.alt && m.a.alt.model) || (m.base.kit ? m.base.kit.recommended : "dcf"); };

// ---- engines
function navResource(n, deck = 1, rate = n.discount) {
  const start = Math.max(1, Math.round(n.startYear)), life = clampN(Math.round(n.life), 1, 50), total = start - 1 + life;
  const r = { year: [], price: [], prod: [], rev: [], roy: [], cost: [], margin: [], tax: [], capex: [], closure: [], fcf: [], df: [], pv: [] };
  let sum = 0;
  for (let t = 1; t <= total; t++) {
    const producing = t >= start;
    const price = deck * (t <= 1 ? n.spot : t >= 3 ? n.ltPrice : n.spot + (n.ltPrice - n.spot) * (t - 1) / 2);
    const prod = producing ? n.production * Math.pow(1 + n.prodGrowth, t - start) : 0;
    const rev = prod * price, roy = rev * n.royalty, cost = prod * n.aisc, margin = rev - roy - cost;
    const tax = Math.max(0, margin) * n.taxRate;
    // Development capex: spread over the years before production, or spent in year 1.
    const capex = start > 1 ? (t < start ? n.initialCapex / (start - 1) : 0) : t === 1 ? n.initialCapex : 0;
    const closure = t === total ? n.closure : 0;
    const fcf = margin - tax - capex - closure, df = 1 / Math.pow(1 + rate, t - 0.5);
    r.year.push(t); r.price.push(price); r.prod.push(prod); r.rev.push(rev); r.roy.push(-roy); r.cost.push(-cost); r.margin.push(margin);
    r.tax.push(-tax); r.capex.push(-capex); r.closure.push(-closure); r.fcf.push(fcf); r.df.push(df); r.pv.push(fcf * df);
    sum += fcf * df;
  }
  const nav = sum + n.otherAssets + n.cash - n.debt - n.minority;
  return { ...r, sumPv: sum, nav, ps: n.shares > 0 ? nav / n.shares : NaN };
}
function navProperty(p, noiMul = 1, cap = p.capRate) {
  const fwd = p.noi * noiMul * (1 + p.noiGrowth), value = cap > 0 ? fwd / cap : NaN;
  const gav = value + p.otherAssets + p.cash, nav = gav - p.debt - p.preferred - p.minority;
  return { fwd, value, gav, nav, ps: p.shares > 0 ? nav / p.shares : NaN };
}
function lboRun(l, prem = l.entryPremium, exitMult = l.exitMultiple) {
  const m = state.model, a = m.a, b = m.base, dr = m.res;
  const baseRev = [...b.revenue].reverse().find((v) => v != null) || 0, price = b.price;
  const n = clampN(Math.round(l.years), 1, 10);
  const offer = price * (1 + prem), equityPrice = offer * l.shares;
  const ev = equityPrice + l.debt - l.cash, fees = ev * l.feesPct, uses = ev + fees;
  const debt0 = Math.max(0, Math.min(l.leverage * l.ebitda, 0.75 * uses));
  const sponsor = uses - debt0;
  const r = { rev: [], ebitda: [], margin: [], da: [], interest: [], tax: [], capex: [], dnwc: [], fcf: [], debt: [], lev: [], cashB: [] };
  let rev = baseRev, debt = debt0, cashB = 0;
  for (let t = 0; t < n; t++) {
    const g = dr && t < dr.growth.length ? dr.growth[t] : a.growth[Math.min(t, a.growth.length - 1)];
    const prev = rev; rev = prev * (1 + (isFinite(g) ? g : 0));
    const k = Math.min(t, a.ebitMargin.length - 1);
    const ebitda = rev * (a.ebitMargin[k] + a.daPct[k]), da = rev * a.daPct[k];
    const interest = debt * l.interestRate, tax = Math.max(0, ebitda - da - interest) * a.taxRate[k];
    const capex = rev * a.capexPct[k], dnwc = (rev - prev) * a.nwcPct[k];
    const fcf = ebitda - interest - tax - capex - dnwc;
    // Every spare dollar repays debt; a shortfall is borrowed on the revolver.
    const repay = fcf >= 0 ? Math.min(debt, fcf) : fcf;
    debt -= repay; cashB += fcf - repay;
    r.rev.push(rev); r.ebitda.push(ebitda); r.margin.push(rev ? ebitda / rev : NaN); r.da.push(-da); r.interest.push(-interest); r.tax.push(-tax);
    r.capex.push(-capex); r.dnwc.push(-dnwc); r.fcf.push(fcf); r.debt.push(debt); r.lev.push(ebitda > 0 ? (debt - cashB) / ebitda : NaN); r.cashB.push(cashB);
  }
  const exitEbitda = r.ebitda[n - 1], exitEv = exitEbitda * exitMult, exitEq = exitEv - debt + cashB;
  const moic = sponsor > 0 ? exitEq / sponsor : NaN;
  const irr = !(sponsor > 0) ? NaN : exitEq <= 0 ? -1 : Math.pow(moic, 1 / n) - 1;
  return { ...r, n, offer, equityPrice, ev, fees, uses, debt0, sponsor, capped: debt0 < l.leverage * l.ebitda - 1, entryMult: l.ebitda > 0 ? ev / l.ebitda : NaN,
    exitEbitda, exitEv, exitDebt: debt, exitCash: cashB, exitEq, moic, irr };
}
// The highest price a sponsor could pay and still make the target return (IRR falls as the price rises).
function lboMaxPrice(l, target = l.targetIrr) {
  const irr = (p) => lboRun(l, p).irr;
  let lo = -0.95, hi = 10;
  if (!(irr(lo) >= target)) return NaN;
  if (irr(hi) >= target) return state.model.base.price * (1 + hi);
  for (let i = 0; i < 60; i++) { const mid = (lo + hi) / 2; if (irr(mid) >= target) lo = mid; else hi = mid; }
  return state.model.base.price * (1 + lo);
}
function erRun(e, coeAdj = 0, roeLong = e.roeLong) {
  const coe = e.rf + e.beta * e.erp + e.sizePremium + coeAdj, n = clampN(Math.round(e.years), 2, 30);
  const r = { bv: [], roe: [], ni: [], div: [], bvEnd: [], charge: [], ri: [], df: [], pv: [] };
  let bv = e.bookValue, sum = 0;
  for (let t = 0; t < n; t++) {
    const roe = e.roe1 + (roeLong - e.roe1) * t / (n - 1), ni = bv * roe, div = ni * e.payout, charge = bv * coe, ri = ni - charge;
    const df = 1 / Math.pow(1 + coe, t + 1);
    r.bv.push(bv); r.roe.push(roe); r.ni.push(ni); r.div.push(-div); r.charge.push(-charge); r.ri.push(ri); r.df.push(df); r.pv.push(ri * df);
    sum += ri * df; bv = bv + ni - div; r.bvEnd.push(bv);
  }
  const ri1 = (roeLong - coe) * bv, tv = ri1 / (coe - e.g), pvTv = tv / Math.pow(1 + coe, n);
  const value = e.bookValue + sum + pvTv;
  // Dividend discount cross-check: next year's dividend growing at the sustainable rate ROE × retention.
  const gSus = roeLong * (1 - e.payout), d1 = r.ni[0] * e.payout;
  const ddm = coe > gSus ? d1 / (coe - gSus) : NaN;
  return { ...r, n, coe, sumPv: sum, tv, pvTv, value, ps: e.shares > 0 ? value / e.shares : NaN, invalid: coe <= e.g,
    ddmPs: e.shares > 0 ? ddm / e.shares : NaN, gSus };
}

// ---- rendering
function altInp(model, k, kind) {
  const o = state.model.a.alt[model];
  return `<input class="in" data-alt="${model}" data-k="${k}" data-kind="${kind}" data-x="alt:${model}:${k}" value="${esc(MKIND[kind].fmt(o[k]))}" spellcheck="false" />`;
}
const altLine = (model, label, k, kind) => `<div class="md-line" data-x="alt:${model}:${k}"><span class="has-note">${label}</span>${altInp(model, k, kind)}</div>`;
const altOut = (label, id, strong, x) => `<div class="md-line out ${strong ? "strong" : ""}" ${x ? `data-x="${x}"` : ""}><span>${label}</span><b id="${id}"></b></div>`;

function renderModels() {
  const m = state.model, kit = m.base.kit, el = $("md-models");
  if (!kit) { el.classList.add("hidden"); return; }
  const cur = altModel();
  el.classList.remove("hidden");
  el.innerHTML = `<div class="mm-row">
      <span class="mm-label">Valuation model</span>
      <div class="mm-tabs">${ALT_MODELS.map((id) => {
        const f = fitOf(kit, id);
        return `<button class="mm-tab ${id === cur ? "on" : ""}" data-model="${id}" title="${esc(FIT_LABEL[f.level])}: ${esc(f.title)}">
          <i class="mm-dot ${f.level}"></i>${esc(modelName(id, kit))}${id === kit.recommended ? `<em>Recommended</em>` : ""}</button>`;
      }).join("")}</div>
    </div>
    <div class="mm-why"><b>${kit.industry ? `For ${esc(lc1(kit.industry))}:` : "Recommended:"}</b> ${esc(modelName(kit.recommended, kit))}. ${esc(kit.recommendedWhy)}</div>`;
}

function setAltModel(id) {
  const m = state.model; if (!m || !m.a.alt) return;
  m.a.alt.model = id;
  saveModelSoon();
  showModelView();
}

// Show the DCF or another model. The football field moves to whichever view is on screen.
function showModelView() {
  const m = state.model, id = altModel();
  renderModels();
  const isDcf = id === "dcf" || !m.base.kit;
  $("md-dcf").classList.toggle("hidden", !isDcf);
  $("md-alt").classList.toggle("hidden", isDcf);
  const ff = $("md-ff");
  if (isDcf) { $("md-streams").after(ff); paintFootball(); return; }
  renderAlt(id);
}

const MA_DISMISS = "funnel.modelFitDismissed";
function maDismissed(sym, id, level) { try { return (JSON.parse(localStorage.getItem(MA_DISMISS) || "{}")[sym] || {})[id] === level; } catch { return false; } }
function maDismiss(sym, id, level) {
  try { const d = JSON.parse(localStorage.getItem(MA_DISMISS) || "{}"); (d[sym] = d[sym] || {})[id] = level; localStorage.setItem(MA_DISMISS, JSON.stringify(d)); } catch { /* lasts until the view repaints */ }
}
function fitBanner(id, kit, sym, dismissable) {
  const f = fitOf(kit, id);
  if (f.level === "good") return `<div class="ma-ok"><i class="mm-dot good"></i><b>${esc(f.title)}.</b> ${esc(f.why)}</div>`;
  if (dismissable && maDismissed(sym, id, f.level)) return "";
  const alts = (f.instead || []).filter((x) => x !== id && fitOf(kit, x).level !== "na");
  return `<div class="md-fit ${f.level === "caution" ? "caution" : "weak"}">
    <div class="md-fit-ic">⚠</div>
    <div class="md-fit-body">
      <div class="md-fit-title">${esc(f.title)}</div>
      <div class="md-fit-why">${esc(f.why)}${f.level === "weak" ? " The model still runs; treat its answer as one input, not the answer." : ""}</div>
      ${alts.length ? `<div class="md-fit-go">${alts.map((x) => `<button class="btn sm" data-model="${x}">Open the ${esc(modelName(x, kit))} model${x === kit.recommended ? " (recommended)" : ""}</button>`).join("")}</div>` : ""}
    </div>
    ${dismissable && f.level !== "na" ? `<button class="md-fit-x" data-ma-dismiss="${id}:${f.level}" title="Hide this note for ${esc(sym)}" aria-label="Dismiss">×</button>` : ""}
  </div>`;
}

function renderAlt(id) {
  const m = state.model, b = m.base, kit = b.kit, f = fitOf(kit, id), el = $("md-alt");
  // The football field may be sitting in this view from last time; park it before repainting.
  if (el.contains($("md-ff"))) $("md-streams").after($("md-ff"));
  const canRun = f.level !== "na" && !(id === "nav" && !m.a.alt.nav);
  const resetBtn = canRun ? `<button class="btn ghost" data-ma-reset="${id}" title="Back to the inputs built from the data">Reset this model</button>` : "";
  el.innerHTML = `
    <div class="card md-bar"><div class="md-res" id="ma-res"></div><div class="md-actions">${resetBtn}</div></div>
    ${fitBanner(id, kit, b.symbol, true)}
    ${canRun ? `<div class="md-layout">
      <div class="md-main">
        <div class="card" id="ma-main"></div>
        <div class="card" id="ma-sens"></div>
        <div id="ma-ff-slot"></div>
      </div>
      <aside class="md-side" id="ma-side"></aside>
    </div>` : `<div class="card ma-na"><div class="card-title">${esc(modelName(id, kit))} isn't run for ${esc(b.symbol)}</div><div class="card-sub">${esc(f.why)}</div></div>`}`;
  if (!canRun) { $("ma-res").innerHTML = `<div class="md-price bad"><small>${esc(modelName(id, kit))}</small><b>–</b><span>${esc(f.title)}</span></div>`; return; }
  $("ma-ff-slot").replaceWith($("md-ff"));
  ({ nav: renderNav, lbo: renderLbo, er: renderEr })[id]();
  recalcAlt();
}

function renderNav() {
  const m = state.model, kit = m.base.kit, n = kit.nav, cur = esc(m.base.currency);
  const L = (label, k, kind) => altLine("nav", label, k, kind);
  if (n.kind === "property") {
    $("ma-side").innerHTML = `
      <div class="card"><div class="card-title">Property value</div><div class="card-sub">${cur} millions</div>
        ${L("Net operating income (last year)", "noi", "money")}
        ${L("NOI growth, next year", "noiGrowth", "pct1")}
        ${altOut("Forward NOI", "ma-fwd", false, "alt:nav:fwd")}
        ${L("Cap rate", "capRate", "pct2")}
        ${altOut("Property value", "ma-pv", true, "alt:nav:value")}</div>
      <div class="card"><div class="card-title">Net asset value</div><div class="card-sub">${cur} millions · shares in millions</div>
        ${L("Plus: development &amp; other assets", "otherAssets", "money")}
        ${L("Plus: cash", "cash", "money")}
        ${altOut("Gross asset value", "ma-gav", false, "alt:nav:gav")}
        ${L("Less: debt", "debt", "money")}
        ${L("Less: preferred shares", "preferred", "money")}
        ${L("Less: minority interest", "minority", "money")}
        ${altOut("Net asset value", "ma-nav", true, "alt:nav:nav")}
        ${L("Diluted shares", "shares", "money")}
        ${altOut("NAV per share", "ma-ps", true, "alt:nav:ps")}</div>`;
    $("ma-main").innerHTML = `<div class="card-title">How the market prices the buildings</div>
      <div class="card-sub">The cap rate is NOI ÷ property value. Comparing the cap rate today's share price implies with private-market cap rates shows whether the stock trades above or below what the buildings would sell for.</div>
      <div id="ma-capviz"></div>`;
    return;
  }
  const unit = n.unit === "index" ? "unit" : n.unit;
  $("ma-side").innerHTML = `
    <div class="card"><div class="card-title">Price deck</div><div class="card-sub">${esc(n.commodity)}${n.unit === "index" ? " · index (1.00 = last year's realised price)" : ` · ${cur} per ${esc(unit)}`}</div>
      ${L("Price today", "spot", "price")}
      ${L("Long-term price (year 3 on)", "ltPrice", "price")}</div>
    <div class="card"><div class="card-title">Production plan</div><div class="card-sub">${n.preProduction ? "Pre-production: enter the plan from the technical study" : `Implied from the statements; ${esc(unit)} a year`}</div>
      ${L(`Annual production (${esc(unit)})`, "production", "big")}
      ${L("Production growth a year", "prodGrowth", "pct1")}
      ${L(`All-in sustaining cost / ${esc(unit)}`, "aisc", "price")}
      ${L("Reserve life (years)", "life", "yrs")}
      ${L("Production starts in year", "startYear", "yrs")}
      ${L(`Development capex (${cur} M)`, "initialCapex", "money")}
      ${L(`Closure cost (${cur} M)`, "closure", "money")}
      ${L("Royalties, % of revenue", "royalty", "pct1")}
      ${L("Tax rate on cash margin", "taxRate", "pct1")}
      ${L("Real discount rate", "discount", "pct1")}</div>
    <div class="card"><div class="card-title">Net asset value</div><div class="card-sub">${cur} millions · shares in millions</div>
      ${altOut("PV of the mine plan", "ma-sumpv", false, "alt:nav:sumPv")}
      ${L("Plus: exploration &amp; other assets", "otherAssets", "money")}
      ${L("Plus: cash", "cash", "money")}
      ${L("Less: debt", "debt", "money")}
      ${L("Less: minority interest", "minority", "money")}
      ${altOut("Net asset value", "ma-nav", true, "alt:nav:nav")}
      ${L("Diluted shares", "shares", "money")}
      ${altOut("NAV per share", "ma-ps", true, "alt:nav:ps")}</div>`;
  $("ma-main").innerHTML = `<div class="card-head"><div><div class="card-title">Life-of-reserves cash flow</div>
      <div class="card-sub">${cur} millions · today's money · mid-year discounting · taxed on the cash margin (a simplification that ignores depreciation shields)</div></div></div>
    <div class="md-wrap"><table class="md" id="ma-table"></table></div>`;
}

function renderLbo() {
  const cur = esc(state.model.base.currency);
  const L = (label, k, kind) => altLine("lbo", label, k, kind);
  $("ma-side").innerHTML = `
    <div class="card"><div class="card-title">Entry</div><div class="card-sub">${cur} millions · shares in millions</div>
      ${L("EBITDA, last year", "ebitda", "money")}
      ${L("Premium to today's price", "entryPremium", "pct1")}
      ${altOut("Offer price per share", "ma-offer", false, "alt:lbo:offer")}
      ${L("Diluted shares", "shares", "money")}
      ${altOut("Equity purchase price", "ma-eqp", false, "alt:lbo:equityPrice")}
      ${L("Plus: refinance debt", "debt", "money")}
      ${L("Less: cash", "cash", "money")}
      ${altOut("Entry enterprise value", "ma-ev", true, "alt:lbo:ev")}
      ${altOut("Entry EV / EBITDA", "ma-emult", false, "alt:lbo:entryMult")}</div>
    <div class="card"><div class="card-title">Financing</div><div class="card-sub">Sources and uses</div>
      ${L("Debt / EBITDA", "leverage", "mult")}
      ${L("Interest rate", "interestRate", "pct2")}
      ${L("Fees, % of EV", "feesPct", "pct1")}
      ${altOut("New debt", "ma-debt0", false, "alt:lbo:debt0")}
      ${altOut("Sponsor equity", "ma-spon", true, "alt:lbo:sponsor")}
      ${altOut("Equity % of the price", "ma-sponpct", false, "alt:lbo:sponsorPct")}</div>
    <div class="card"><div class="card-title">Exit</div><div class="card-sub">Sale at the end of the holding period</div>
      ${L("Holding period (years)", "years", "yrs")}
      ${L("Exit EV / EBITDA", "exitMultiple", "mult")}
      ${altOut("Exit enterprise value", "ma-xev", false, "alt:lbo:exitEv")}
      ${altOut("Less: net debt at exit", "ma-xnd", false, "alt:lbo:exitNd")}
      ${altOut("Exit equity", "ma-xeq", true, "alt:lbo:exitEq")}
      ${altOut("Money multiple (MOIC)", "ma-moic", false, "alt:lbo:moic")}
      ${altOut("IRR", "ma-irr", true, "alt:lbo:irr")}
      ${L("Target IRR", "targetIrr", "pct1")}</div>`;
  $("ma-main").innerHTML = `<div class="card-head"><div><div class="card-title">Debt paydown</div>
      <div class="card-sub">${cur} millions · operating case from the DCF tab (growth, margins, D&amp;A, capex, working capital, tax) · all spare cash repays debt</div></div></div>
    <div class="md-wrap"><table class="md" id="ma-table"></table></div>`;
}

function renderEr() {
  const cur = esc(state.model.base.currency);
  const L = (label, k, kind) => altLine("er", label, k, kind);
  $("ma-side").innerHTML = `
    <div class="card"><div class="card-title">Cost of equity</div><div class="card-sub">CAPM: what shareholders require</div>
      ${L("Risk-free rate", "rf", "pct2")}
      ${L("Beta", "beta", "num")}
      ${L("Equity risk premium", "erp", "pct2")}
      ${L("Size / other premium", "sizePremium", "pct2")}
      ${altOut("Cost of equity", "ma-coe", true, "alt:er:coe")}</div>
    <div class="card"><div class="card-title">Returns</div><div class="card-sub">Return on equity fades from year 1 to the long run</div>
      ${L("ROE, year 1", "roe1", "pct1")}
      ${L("Long-run ROE", "roeLong", "pct1")}
      ${L("Payout ratio", "payout", "pct1")}
      ${L("Forecast years", "years", "yrs")}
      ${L("Growth after the forecast", "g", "pct2")}</div>
    <div class="card"><div class="card-title">Equity value</div><div class="card-sub">${cur} millions · shares in millions</div>
      ${L("Book value today", "bookValue", "money")}
      ${altOut("PV of excess returns", "ma-sumpv", false, "alt:er:sumPv")}
      ${altOut("PV of terminal value", "ma-pvtv", false, "alt:er:pvTv")}
      ${altOut("Equity value", "ma-val", true, "alt:er:value")}
      ${L("Diluted shares", "shares", "money")}
      ${altOut("Value per share", "ma-ps", true, "alt:er:ps")}
      ${altOut("Dividend discount check", "ma-ddm", false, "alt:er:ddm")}</div>`;
  $("ma-main").innerHTML = `<div class="card-head"><div><div class="card-title">Excess returns</div>
      <div class="card-sub">${cur} millions · value = book value + the present value of profit above the cost of equity (residual income)</div></div></div>
    <div class="md-wrap"><table class="md" id="ma-table"></table></div>`;
}

// Columns of a forecast table: label, values, how to format, row class.
function altTable(heads, rows) {
  $("ma-table").innerHTML = `<thead><tr><th>${heads[0]}</th>${heads.slice(1).map((h) => `<th>${h}</th>`).join("")}</tr></thead>
    <tbody>${rows.map(([label, vals, f, cls]) => `<tr class="${cls || ""}"><th>${label}</th>${vals.map((v) => {
      const txt = f === "pct" ? fmt.pct(v) : f === "df" ? (isFinite(v) ? v.toFixed(3) : "–") : f === "x" ? fmt.x(v) : f === "price" ? fmt.price(v) : f === "big" ? fmt.big(v) : mm(v);
      return `<td class="${f !== "df" && v < 0 ? "neg" : ""}">${txt}</td>`;
    }).join("")}</tr>`).join("")}</tbody>`;
}

// A 5×5 grid, coloured against today's price (or against a target, for IRRs).
function altSens(title, sub, rowHead, colHead, rows, cols, rowFmt, colFmt, cell, opts = {}) {
  const ref = opts.ref ?? state.model.base.price, isIrr = !!opts.irr;
  const td = (v, centre) => {
    if (!isFinite(v)) return `<td class="na">–</td>`;
    const d = isIrr ? (v - ref) / 0.15 : ref ? v / ref - 1 : 0, s = Math.min(1, Math.abs(d) / 0.5);
    const bg = d >= 0 ? `rgba(22,163,74,${0.08 + 0.32 * s})` : `rgba(220,38,38,${0.08 + 0.32 * s})`;
    return `<td class="${centre ? "centre" : ""}" style="background:${bg}">${isIrr ? fmt.pct(v) : fmt.price(v)}</td>`;
  };
  $("ma-sens").innerHTML = `<div class="card-title">${title}</div><div class="card-sub">${sub}</div>
    <table class="sens" style="margin-top:12px"><thead><tr><th>${rowHead} ↓ · ${colHead} →</th>${cols.map((c, j) => `<th class="${j === 2 ? "centre" : ""}">${colFmt(c)}</th>`).join("")}</tr></thead>
    <tbody>${rows.map((rv, i) => `<tr><th class="${i === 2 ? "centre" : ""}">${rowFmt(rv)}</th>${cols.map((c, j) => td(cell(rv, c), i === 2 && j === 2)).join("")}</tr>`).join("")}</tbody></table>`;
}

function maSet(id, txt) { const el = $(id); if (el) el.textContent = txt; }
function resBar(main, sub, kvs) {
  $("ma-res").innerHTML = `<div class="md-price" data-x="alt:res:main"><small>${main}</small><b>${sub}</b></div>` +
    kvs.map(([label, val, cls, x]) => `<div class="md-kv" ${x ? `data-x="${x}"` : ""}>${label}<b class="${cls || ""}">${val}</b></div>`).join("");
}

function recalcAlt() {
  const m = state.model; if (!m || !m.a.alt) return;
  const id = altModel(), b = m.base, cur = b.price, ccy = `<em>${esc(b.currency)}</em>`;
  const up = (v) => (cur && isFinite(v) ? v / cur - 1 : null);
  const upTxt = (v) => { const u = up(v); return u == null ? "–" : (u >= 0 ? "+" : "") + fmt.pct(u); };
  m.altRes = m.altRes || {};
  m.sumAlt = id !== "dcf" ? { id, v: altValue(id) } : null;   // for the tab's summary
  paintTabsSoon();
  if (id === "nav" && m.a.alt.nav && b.kit.nav.kind === "resource") {
    const n = m.a.alt.nav, r = navResource(n); m.altRes.nav = r;
    maSet("ma-sumpv", mm(r.sumPv)); maSet("ma-nav", mm(r.nav)); maSet("ma-ps", fmt.price(r.ps));
    const yrs = r.year.map((t) => `Y${t}`);
    altTable([`${esc(b.currency)} millions`, ...yrs], [
      [`Price (${esc(b.kit.nav.unit === "index" ? "index" : b.currency + "/" + b.kit.nav.unit)})`, r.price, "price", "sub"],
      ["Production", r.prod, "big", "sub"],
      ["Revenue", r.rev, "money", "em"], ["Royalties", r.roy], ["All-in sustaining cost", r.cost],
      ["Cash margin", r.margin, "money", "em"], ["Tax", r.tax], ["Development capex", r.capex], ["Closure", r.closure],
      ["Free cash flow", r.fcf, "money", "em total"], ["Discount factor", r.df, "df", "sub"], ["Present value", r.pv, "money", "sub"],
    ]);
    // The long-term price at which NAV equals today's share price: what the market is paying for.
    let impl = NaN;
    if (cur > 0 && n.shares > 0) {
      let lo = 0.01, hi = 20;
      if (navResource(n, lo).ps < cur && navResource(n, hi).ps > cur) {
        for (let i = 0; i < 50; i++) { const mid = (lo + hi) / 2; if (navResource(n, mid).ps < cur) lo = mid; else hi = mid; }
        impl = n.ltPrice * lo;
      }
    }
    m.altRes.navImplied = impl;
    const pnav = r.ps > 0 && cur ? cur / r.ps : NaN;
    resBar(`NAV per share · ${esc(b.symbol)}`, `${fmt.price(r.ps)}${ccy}`, [
      ["Current price", fmt.price(cur)], ["P / NAV", isFinite(pnav) ? fmt.x(pnav).replace("x", "x") : "–", "", "alt:nav:pnav"],
      ["Upside / (downside)", upTxt(r.ps), dir(up(r.ps))],
      [`Long-term price the market implies`, isFinite(impl) ? fmt.price(impl) : "–", "", "alt:nav:implied"],
      ["Net asset value", fmt.big(r.nav)],
    ]);
    const ds = [-2, -1, 0, 1, 2].map((k) => n.discount + k * 0.01), ps = [-0.3, -0.15, 0, 0.15, 0.3];
    altSens("Sensitivity", `NAV per share; green is above today's price (${fmt.price(cur)}). Price moves apply to the whole deck.`, "Discount rate", `${esc(b.kit.nav.commodity)} price`,
      ds, ps, (d) => fmt.pct(d, 1), (p) => (p === 0 ? "Base" : (p > 0 ? "+" : "") + fmt.pct(p, 0)), (d, p) => navResource(n, 1 + p, d).ps);
  } else if (id === "nav" && m.a.alt.nav) {
    const p = m.a.alt.nav, r = navProperty(p); m.altRes.nav = r;
    maSet("ma-fwd", mm(r.fwd)); maSet("ma-pv", mm(r.value)); maSet("ma-gav", mm(r.gav)); maSet("ma-nav", mm(r.nav)); maSet("ma-ps", fmt.price(r.ps));
    const mc = cur * p.shares, impliedCap = r.fwd / (mc + p.debt + p.preferred + p.minority - p.cash - p.otherAssets);
    m.altRes.navImplied = impliedCap;
    const pnav = r.ps > 0 && cur ? cur / r.ps : NaN;
    resBar(`NAV per share · ${esc(b.symbol)}`, `${fmt.price(r.ps)}${ccy}`, [
      ["Current price", fmt.price(cur)], ["P / NAV", isFinite(pnav) ? fmt.x(pnav) : "–", "", "alt:nav:pnav"],
      ["Upside / (downside)", upTxt(r.ps), dir(up(r.ps))],
      ["Cap rate the price implies", isFinite(impliedCap) && impliedCap > 0 ? fmt.pct(impliedCap, 2) : "–", "", "alt:nav:implied"],
      ["Your cap rate", fmt.pct(p.capRate, 2)],
    ]);
    const pos = (v) => clampN((v - 0.03) / 0.07, 0, 1) * 100;
    $("ma-capviz").innerHTML = `<div class="capviz">
        <div class="capviz-track">${isFinite(impliedCap) && impliedCap > 0 ? `<span class="capviz-m imp" style="left:${pos(impliedCap)}%"><b>${fmt.pct(impliedCap, 2)}</b>Share price</span>` : ""}
          <span class="capviz-m you" style="left:${pos(p.capRate)}%"><b>${fmt.pct(p.capRate, 2)}</b>Your cap rate</span></div>
        <div class="capviz-axis">${[0.03, 0.05, 0.07, 0.1].map((v) => `<span style="left:${pos(v)}%">${fmt.pct(v, 0)}</span>`).join("")}</div>
      </div>
      <div class="card-sub" style="margin-top:10px">${isFinite(impliedCap) && impliedCap > 0 ? (impliedCap > p.capRate
        ? `The share price implies a higher cap rate than yours, i.e. the stock values the buildings below what they'd sell for at ${fmt.pct(p.capRate, 2)}.`
        : `The share price implies a lower cap rate than yours, i.e. the stock values the buildings above what they'd sell for at ${fmt.pct(p.capRate, 2)}.`) : ""}</div>`;
    const caps = [-2, -1, 0, 1, 2].map((k) => p.capRate + k * 0.005), nois = [-0.1, -0.05, 0, 0.05, 0.1];
    altSens("Sensitivity", `NAV per share; green is above today's price (${fmt.price(cur)}).`, "Cap rate", "NOI",
      caps, nois, (c) => fmt.pct(c, 2), (x) => (x === 0 ? "Base" : (x > 0 ? "+" : "") + fmt.pct(x, 0)), (c, x) => navProperty(p, 1 + x, c).ps);
  } else if (id === "lbo") {
    const l = m.a.alt.lbo, r = lboRun(l), maxPx = lboMaxPrice(l); m.altRes.lbo = { ...r, maxPx };
    maSet("ma-offer", fmt.price(r.offer)); maSet("ma-eqp", mm(r.equityPrice)); maSet("ma-ev", mm(r.ev)); maSet("ma-emult", fmt.x(r.entryMult));
    maSet("ma-debt0", mm(r.debt0) + (r.capped ? " *" : "")); maSet("ma-spon", mm(r.sponsor)); maSet("ma-sponpct", fmt.pct(r.sponsor / r.uses));
    maSet("ma-xev", mm(r.exitEv)); maSet("ma-xnd", mm(-(r.exitDebt - r.exitCash))); maSet("ma-xeq", mm(r.exitEq));
    maSet("ma-moic", isFinite(r.moic) ? r.moic.toFixed(2) + "x" : "–"); maSet("ma-irr", isFinite(r.irr) ? fmt.pct(r.irr) : "–");
    const yrs = Array.from({ length: r.n }, (_, t) => `Y${t + 1}`);
    altTable([`${esc(b.currency)} millions`, ...yrs], [
      ["Revenue", r.rev, "money", "em"], ["EBITDA", r.ebitda, "money", "em"], ["% margin", r.margin, "pct", "sub"],
      ["Interest", r.interest], ["Taxes", r.tax], ["Capex", r.capex], ["Change in NWC", r.dnwc],
      ["Cash to repay debt", r.fcf, "money", "em total"], ["Debt, end of year", r.debt, "money", "em"],
      ["Net debt / EBITDA", r.lev, "x", "sub"],
    ]);
    resBar(`Max price for a ${fmt.pct(l.targetIrr, 0)} IRR · ${esc(b.symbol)}`, `${isFinite(maxPx) ? fmt.price(maxPx) : "–"}${ccy}`, [
      ["Current price", fmt.price(cur)], ["Upside / (downside)", upTxt(maxPx), dir(up(maxPx)), "alt:lbo:maxUp"],
      [`IRR at a ${fmt.pct(l.entryPremium, 0)} premium`, isFinite(r.irr) ? fmt.pct(r.irr) : "–", r.irr >= l.targetIrr ? "up" : "down", "alt:lbo:irr"],
      ["MOIC", isFinite(r.moic) ? r.moic.toFixed(2) + "x" : "–"], ["Entry EV / EBITDA", fmt.x(r.entryMult)],
    ]);
    if (r.capped) $("ma-res").insertAdjacentHTML("beforeend", `<div class="md-flag warn">* Debt capped at 75% of the price: ${fmt.x(l.leverage)} EBITDA would leave the sponsor almost no equity.</div>`);
    const prems = [0, 0.15, 0.3, 0.45, 0.6], xs = [-2, -1, 0, 1, 2].map((k) => l.exitMultiple + k);
    altSens("Sponsor returns", `IRR; green beats the ${fmt.pct(l.targetIrr, 0)} target.`, "Premium paid", "exit EV / EBITDA",
      prems, xs, (p) => fmt.pct(p, 0), (x) => fmt.x(x), (p, x) => lboRun(l, p, x).irr, { irr: true, ref: l.targetIrr });
  } else if (id === "er") {
    const e = m.a.alt.er, r = erRun(e); m.altRes.er = r;
    maSet("ma-coe", fmt.pct(r.coe, 2)); maSet("ma-sumpv", mm(r.sumPv)); maSet("ma-pvtv", mm(r.pvTv)); maSet("ma-val", mm(r.value));
    maSet("ma-ps", r.invalid ? "–" : fmt.price(r.ps)); maSet("ma-ddm", fmt.price(r.ddmPs));
    const yrs = Array.from({ length: r.n }, (_, t) => `Y${t + 1}`);
    altTable([`${esc(b.currency)} millions`, ...yrs], [
      ["Book value, start", r.bv, "money", "em"], ["Return on equity", r.roe, "pct", "sub"], ["Net income", r.ni], ["Dividends", r.div],
      ["Equity charge (book × cost of equity)", r.charge], ["Excess return", r.ri, "money", "em total"], ["Discount factor", r.df, "df", "sub"], ["Present value", r.pv, "money", "sub"],
    ]);
    const pb = e.bookValue > 0 ? cur * e.shares / e.bookValue : NaN, jpb = e.bookValue > 0 ? r.value / e.bookValue : NaN;
    if (r.invalid) $("ma-res").innerHTML = `<div class="md-price bad"><small>Value per share</small><b>–</b><span>The cost of equity (${fmt.pct(r.coe, 2)}) must be above growth (${fmt.pct(e.g, 2)}).</span></div>`;
    else resBar(`Value per share · ${esc(b.symbol)}`, `${fmt.price(r.ps)}${ccy}`, [
      ["Current price", fmt.price(cur)], ["Upside / (downside)", upTxt(r.ps), dir(up(r.ps))],
      ["Justified P / B", fmt.x(jpb), "", "alt:er:jpb"], ["Current P / B", fmt.x(pb)], ["Cost of equity", fmt.pct(r.coe, 2)],
    ]);
    const cs = [-0.02, -0.01, 0, 0.01, 0.02], roes = [-0.04, -0.02, 0, 0.02, 0.04].map((k) => e.roeLong + k);
    altSens("Sensitivity", `Value per share; green is above today's price (${fmt.price(cur)}).`, "Long-run ROE", "cost of equity",
      roes, cs, (x) => fmt.pct(x, 1), (c) => fmt.pct(r.coe + c, 2), (roe, c) => { const q = erRun(e, c, roe); return q.invalid ? NaN : q.ps; });
  }
  paintFootball();
}

// The open model's value per share (NaN when it doesn't run here).
function altValue(id) {
  const m = state.model, kit = m.base.kit, alt = m.a.alt;
  if (!kit || !alt || fitOf(kit, id).level === "na") return NaN;
  if (id === "nav") return !alt.nav ? NaN : kit.nav.kind === "resource" ? navResource(alt.nav).ps : navProperty(alt.nav).ps;
  if (id === "lbo") return lboMaxPrice(alt.lbo);
  if (id === "er") { const q = erRun(alt.er); return q.invalid ? NaN : q.ps; }
  return NaN;
}
// "Gold & precious metals" → "gold & precious metals", but "REITs" stays.
const lc1 = (s) => (!s ? s : /^[A-Z]{2}/.test(s) ? s : s[0].toLowerCase() + s.slice(1));

function altFootballRows() {
  const m = state.model, kit = m.base.kit; if (!kit || !m.a.alt) return [];
  const rows = [], sel = altModel(), ok = (id) => fitOf(kit, id).level !== "na";
  const span = (vals, mid) => { const v = vals.filter(ffFin); return v.length ? { lo: Math.min(...v), hi: Math.max(...v), mid: ffFin(mid) ? mid : null } : null; };
  const n = m.a.alt.nav;
  if (n && ok("nav")) {
    if (kit.nav.kind === "resource") {
      const s = span([-0.15, 0, 0.15].flatMap((p) => [-0.01, 0, 0.01].map((d) => navResource(n, 1 + p, n.discount + d).ps)), navResource(n).ps);
      if (s) rows.push({ group: "alt", label: "NAV · reserves", mine: sel === "nav", ...s, basis: `${kit.nav.commodity} ±15%, discount rate ${fmt.pct(n.discount - 0.01, 0)}–${fmt.pct(n.discount + 0.01, 0)}; tick = base case` });
    } else {
      const s = span([-0.005, 0, 0.005].flatMap((c) => [-0.05, 0, 0.05].map((x) => navProperty(n, 1 + x, n.capRate + c).ps)), navProperty(n).ps);
      if (s) rows.push({ group: "alt", label: "NAV · properties", mine: sel === "nav", ...s, basis: `Cap rate ${fmt.pct(n.capRate - 0.005, 2)}–${fmt.pct(n.capRate + 0.005, 2)}, NOI ±5%; tick = base case` });
    }
  }
  const l = m.a.alt.lbo;
  if (l && ok("lbo") && m.base.price > 0) {
    const s = span([l.targetIrr + 0.05, l.targetIrr - 0.05].map((t) => lboMaxPrice(l, t)), lboMaxPrice(l));
    if (s) rows.push({ group: "alt", label: "LBO · what a sponsor could pay", mine: sel === "lbo", ...s, basis: `Max price for a ${fmt.pct(l.targetIrr + 0.05, 0)}–${fmt.pct(l.targetIrr - 0.05, 0)} IRR at ${fmt.x(l.leverage)} debt; tick = ${fmt.pct(l.targetIrr, 0)}` });
  }
  const e = m.a.alt.er;
  if (e && ok("er")) {
    const base = erRun(e);
    const s = span([-0.01, 0, 0.01].flatMap((c) => [-0.02, 0, 0.02].map((x) => { const q = erRun(e, c, e.roeLong + x); return q.invalid ? NaN : q.ps; })), base.invalid ? NaN : base.ps);
    if (s) rows.push({ group: "alt", label: "Excess return", mine: sel === "er", ...s, basis: `Cost of equity ±1%, long-run ROE ±2 points; tick = base case` });
  }
  return rows;
}

function onModelsClick(e) {
  const m = state.model; if (!m) return false;
  const go = e.target.closest("[data-model]");
  if (go) { setAltModel(go.dataset.model); return true; }
  const dis = e.target.closest("[data-ma-dismiss]");
  if (dis) { const [id, level] = dis.dataset.maDismiss.split(":"); maDismiss(m.base.symbol, id, level); renderAlt(id); return true; }
  const rs = e.target.closest("[data-ma-reset]");
  if (rs) {
    const id = rs.dataset.maReset, def = altDefaults(m.base.kit, m.a, m.base);
    m.a.alt[id] = def[id]; saveModelSoon(); renderAlt(id);
    toast("Back to defaults", `${modelName(id, m.base.kit)} inputs rebuilt from the latest data.`, "ok");
    return true;
  }
  return false;
}

// Hover cards for the other models' inputs and results: what it is, and where the default came from.
const ALT_HELP = {
  nav: {
    spot: ["Price today", "The commodity's price now. Year 1 uses it; the deck then fades to the long-term price by year 3.", "price"],
    ltPrice: ["Long-term price", "The price from year 3 on. Analysts usually use a consensus long-term price below spot for gold and copper.", "price"],
    production: ["Annual production", "Units produced a year.", "production"],
    prodGrowth: ["Production growth", "Yearly growth in production while the reserves last (expansions, new mines)."],
    aisc: ["All-in sustaining cost", "Cash cost per unit including sustaining capex, royalties paid in cost and corporate overhead.", "aisc"],
    life: ["Reserve life", "Years of production left: reserves ÷ annual production.", "life"],
    startYear: ["Production start", "1 for a producer; later for a developer still building its mine."],
    initialCapex: ["Development capex", "Spending to build or expand the mine, spread over the years before production starts."],
    closure: ["Closure cost", "Reclamation and closure spending in the final year."],
    royalty: ["Royalties", "Royalties and streams paid as a share of revenue, if not already in the cost per unit."],
    taxRate: ["Tax rate", "Applied to the cash margin. Defaults to the DCF's tax rate; mining taxes often add 5–15 points."],
    discount: ["Real discount rate", "Prices and costs are in today's money, so the rate is real.", "discount"],
    otherAssets: ["Other assets", "Exploration ground, equity stakes and other assets not in the mine plan, at your estimate."],
    noi: ["Net operating income", "Rental income less property costs, before interest and depreciation.", "noi"],
    noiGrowth: ["NOI growth", "Growth from last year to the next twelve months: rent increases and lease-up."],
    capRate: ["Cap rate", "Forward NOI ÷ property value: the yield a buyer would accept.", "capRate"],
    preferred: ["Preferred shares", "Ranks ahead of common shareholders, so it comes off the NAV."],
    pnav: ["P / NAV", "Share price ÷ NAV per share. Producers often trade at 1.0–1.5x, developers and explorers at 0.3–0.8x; REITs swing around 1.0x."],
    implied: ["What the price implies", "For a producer, the long-term commodity price at which NAV per share equals today's share price. For a REIT, the cap rate today's share price puts on the buildings."],
  },
  lbo: {
    ebitda: ["EBITDA, last year", "EBIT + D&A in the last reported year: what lenders lend against."],
    entryPremium: ["Premium", "Buyouts usually pay 20–40% above the undisturbed share price."],
    leverage: ["Debt / EBITDA", "How much the buyout borrows.", "leverage"],
    interestRate: ["Interest rate", "", "rate"],
    feesPct: ["Fees", "Advisory and financing fees, paid by the sponsor."],
    years: ["Holding period", "Sponsors usually sell after 3–7 years."],
    exitMultiple: ["Exit multiple", "EV / EBITDA at the sale. Defaults to the entry multiple: no credit for multiple expansion."],
    targetIrr: ["Target IRR", "The annual return buyout funds aim for, usually 20–25%."],
    irr: ["IRR", "The sponsor's annual return: (exit equity ÷ equity invested)^(1 ÷ years) − 1."],
    maxUp: ["Max price vs today", "Above today's price means a sponsor could pay a premium and still hit its target: a floor under the shares. Below means a buyout doesn't work at today's price."],
  },
  er: {
    bookValue: ["Book value", "", "book"],
    roe1: ["ROE, year 1", "", "roe"],
    roeLong: ["Long-run ROE", "", "roeLong"],
    payout: ["Payout ratio", "", "payout"],
    g: ["Growth after the forecast", "Growth of the excess return after the final year; defaults to economy growth."],
    years: ["Forecast years", "ROE fades over this many years."],
    jpb: ["Justified P / B", "Value ÷ book value. Above 1x when the company earns more than its cost of equity, below when it earns less."],
    ddm: ["Dividend discount check", "Next year's dividend ÷ (cost of equity − sustainable growth), where sustainable growth = long-run ROE × the share of profit retained."],
  },
};
const ALT_COMMON = {
  cash: ["Cash", "Cash and short-term investments on the latest balance sheet; starts from the DCF's equity bridge."],
  debt: ["Debt", "Total debt on the latest balance sheet; starts from the DCF's equity bridge."],
  minority: ["Minority interest", "The share of subsidiaries other shareholders own; it comes off the value."],
  shares: ["Diluted shares", "The DCF's share count: shares outstanding plus options, RSUs and convertibles (or implied shares, if larger)."],
  rf: ["Risk-free rate", "Starts from the DCF's risk-free rate (the 10-year Treasury yield)."],
  beta: ["Beta", "Starts from the DCF's beta: how much the shares move with the market."],
  erp: ["Equity risk premium", "Starts from the DCF's equity risk premium (Settings → DCF assumptions)."],
  sizePremium: ["Size / other premium", "Any extra return you require on top of CAPM."],
};
function altExplain(model, key) {
  const m = state.model, kit = m.base.kit;
  if (model === "res") return null;
  const h = (ALT_HELP[model] || {})[key] || ALT_COMMON[key];
  const o = m.a.alt && m.a.alt[model];
  const notes = model === "nav" ? kit.nav.notes : model === "lbo" ? kit.lbo.notes : kit.er.notes;
  if (!h) return null;
  const note = h[2] && notes ? notes[h[2]] : null;
  const isIn = o && key in o;
  return { title: h[0], sub: modelName(model, kit), value: null,
    sources: [h[1] ? { src: isIn ? "in" : "calc", label: "", text: h[1] } : null, note ? { src: "data", label: "Default:", text: note } : null].filter(Boolean) };
}

async function resetModel() {
  const m = state.model; if (!m) return;
  const alt = m.a.alt;
  m.a = clone(m.defaults); m.a.streams = m.a.streams || []; m.sens = m.a.tvMethod;
  if (alt) m.a.alt = alt;
  clearTimeout(modelSaveTimer);
  call("saveModel", { symbol: m.symbol }, { quiet: true }).then(refreshSavedCount, () => {});
  renderModelTable(); renderStreams(); renderModelSide(); recalc();
  toast("Back to defaults", "Assumptions rebuilt from the latest data.", "ok");
}

// What the workbook's football field draws: the DCF's two sensitivity ranges and the (at most two)
// peer multiples that matter most for this kind of company, with the peers' figures so Excel can
// work the ranges out itself. Banks, insurers and REITs go on book value and earnings; everyone
// else on EV / EBITDA and forward earnings, falling back to trailing earnings and revenue.
const FF_MULT = {
  evToEbitda: { label: "EV / EBITDA", equity: false, metric: "EBITDA" },
  forwardPE: { label: "Forward P/E", equity: true, metric: "Forward EPS" },
  trailingPE: { label: "Trailing P/E", equity: true, metric: "EPS" },
  evToRevenue: { label: "EV / revenue", equity: false, metric: "Revenue" },
  priceToBook: { label: "Price / book", equity: true, metric: "Book value" },
};
async function footballForExport() {
  const m = state.model; if (!m || !m.res) return undefined;
  const b = m.base, c = state.company || {};
  if (!state.compRows) await ensureCompsForModel();
  if (state.model !== m) return undefined;
  const fin = (v) => (ffFin(v) ? v : null);
  const dcf = ffRows().rows.filter((r) => r.group === "dcf")
    .map((r) => ({ method: /exit/.test(r.label) ? "multiple" : "gordon", lo: fin(r.lo), mid: fin(r.mid), hi: fin(r.hi), inUse: !!r.mine }));
  const all = state.compRows || [], me = all.find((q) => q.symbol === b.symbol && !q.error);
  const peers = all.filter((q) => q.symbol !== b.symbol && !q.error).slice(0, 8);
  const multiples = [];
  if (me && peers.length >= 2) {
    const mt = me.metrics || {}, p0 = me.price ?? c.price, ev = mt.enterpriseValue, mc = mt.marketCap;
    const shares = mc > 0 && p0 > 0 ? mc / p0 : null, netDebt = ffFin(ev) && ffFin(mc) ? ev - mc : null;
    const order = b.fit && (b.fit.kind === "financial" || b.fit.kind === "reit")
      ? ["priceToBook", "trailingPE", "forwardPE"] : ["evToEbitda", "forwardPE", "trailingPE", "evToRevenue"];
    for (const key of order) {
      if (multiples.length >= 2) break;
      const d = FF_MULT[key];
      if (/PE$/.test(key) && multiples.some((x) => /PE$/.test(x.key))) continue;   // one earnings multiple is enough
      if (peers.filter((q) => ffFin(q.metrics[key]) && q.metrics[key] > 0).length < 2 || !(mt[key] > 0) || !(p0 > 0)) continue;
      if (!d.equity && (!ffFin(ev) || !(shares > 0) || netDebt == null)) continue;
      multiples.push({ key, label: d.label, equity: d.equity, own: mt[key], metricLabel: d.metric,
        metric: d.equity ? p0 / mt[key] : ev / mt[key], netDebt: d.equity ? 0 : netDebt, shares: d.equity ? 1 : shares });
    }
  }
  return {
    price: fin(b.price), dcf, multiples,
    peers: multiples.length ? peers.map((q) => ({ symbol: q.symbol, name: q.name || q.symbol,
      multiples: Object.fromEntries(multiples.map((x) => [x.key, ffFin(q.metrics[x.key]) && q.metrics[x.key] > 0 ? q.metrics[x.key] : null])) })) : [],
  };
}

async function exportModel() {
  const m = state.model; if (!m) return;
  const btn = $("md-export"); btn.disabled = true;
  $("pp-msg").textContent = "Choose where to save…"; $("pp-fill").style.width = "0%";
  $("progress-pop").classList.remove("hidden");
  try {
    const football = await footballForExport().catch(() => undefined);
    const r = await call("exportModel", { symbol: m.symbol, assumptions: m.a, units: "millions", football }, { quiet: true });
    if (r.cancelled) return;
    const name = r.path.split(/[\\/]/).pop();
    const saved = $("md-saved");
    saved.innerHTML = `Saved <b>${esc(name)}</b> <button class="card-link" id="md-open">Open in Excel</button> · <button class="card-link" id="md-reveal">Show in folder</button>`;
    saved.classList.remove("hidden");
    $("md-open").onclick = () => call("openPath", { path: r.path }).catch((e) => toast("Couldn't open", e.message, "err"));
    $("md-reveal").onclick = () => call("revealPath", { path: r.path }).catch((e) => toast("Couldn't open folder", e.message, "err"));
    toast("Model saved", name, "ok");
  } catch (e) {
    toast("Export failed", e.message, "err");
  } finally {
    btn.disabled = false;
    $("progress-pop").classList.add("hidden");
  }
}

init();
