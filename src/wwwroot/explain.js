// ================================================================ model: explain on hover
// Hover any number on the Model tab for a card that takes it apart: the formula as chips (blue =
// an input, green = from the statements or the market, grey = another formula), a picture of how
// the pieces combine, and where every piece came from. Built from the live model, so it always
// matches what's on screen. Loaded after app.js and uses its helpers (state, fmt, mm, esc…).

const XF = {
  money: (v) => mm(v),
  pct: (v) => fmt.pct(v, 1),
  pct2: (v) => fmt.pct(v, 2),
  x: (v) => fmt.x(v),
  price: (v) => fmt.price(v),
  num: (v) => (v != null && isFinite(v) ? v.toFixed(2) : "–"),
  df: (v) => (v != null && isFinite(v) ? v.toFixed(3) : "–"),
  yrs: (v) => (v != null && isFinite(v) ? `${+v.toFixed(1)} yrs` : "–"),
  shares: (v) => (v != null && isFinite(v) ? `${mm(v)}M` : "–"),
};
const X_SRC = { in: "Input", data: "Reported", mkt: "Market", calc: "Formula" };

// A term in a formula: what it is, its value, how to show it, where it comes from, and one line on how it's made.
const T = (label, v, f, src, how) => ({ label, v, f: f || "money", src: src || "calc", how });

// ---------------------------------------------------------------- context
function xctx() {
  const m = state.model, a = m.a, b = m.base, r = m.res, n = a.years;
  let baseIdx = -1;
  b.revenue.forEach((v, i) => { if (v != null) baseIdx = i; });
  const lastP = b.periods[b.periods.length - 1] || new Date().toISOString().slice(0, 10);
  return {
    m, a, b, r, n, baseIdx,
    baseRev: baseIdx >= 0 ? b.revenue[baseIdx] : 0,
    fyE: (t) => fyLabel(lastP, t + 1, true),
    fyA: (i) => fyLabel(b.periods[i], 0, false),
    src: b.statementsSource || "the financial statements",
    unit: `${b.currency} millions`,
  };
}
const shortFy = (s) => s.replace(/^FY20/, "’");

// Where an input's default came from, and whether it's still the default.
const X_NOTE = {
  growth: "growth", ebitMargin: "ebitMargin", taxRate: "taxRate", daPct: "daPct", capexPct: "capexPct", nwcPct: "nwcPct",
  rf: "rf", erp: "erp", beta: "beta", costOfDebt: "costOfDebt", debtWeight: "debtWeight", tgr: "tgr", exitMultiple: "exitMultiple",
  debt: "bridge", cash: "bridge", minority: "bridge", shares: "shares",
};
function xOrigin(c, k, i, s, f) {
  const own = s != null ? c.a.streams[s] : c.a;
  const st = s != null ? own : null;
  const def = s != null ? (c.m.defaults.streams || []).find((x) => st.key && x.key === st.key) : c.m.defaults;
  const cur = i != null ? own[k][i] : own[k];
  const dv = def ? (i != null ? (def[k] || [])[i] : def[k]) : null;
  const note = s != null ? c.b.notes.streams : k === "sizePremium" ? "Size / other premium from Settings → DCF assumptions." : c.b.notes[X_NOTE[k]];
  const status = dv == null || !isFinite(dv) ? "You added this."
    : Math.abs(cur - dv) <= 1e-9 * Math.max(1, Math.abs(dv)) ? "Unchanged from the default."
    : `You changed it; the default was ${XF[f](dv)}.`;
  return `${note ? note + " " : ""}${status}`;
}
const tIn = (c, label, k, i, f, s) => {
  const own = s != null ? c.a.streams[s] : c.a;
  return T(label, i != null ? own[k][i] : own[k], f || "pct", "in", xOrigin(c, k, i, s, f || "pct"));
};

// ---------------------------------------------------------------- terms for each forecast line
const tRev = (c, t) => t < 0
  ? T(`Revenue ${c.fyA(c.baseIdx)}`, c.baseRev, "money", "data", `Reported revenue, ${c.src}.`)
  : T(`Revenue ${c.fyE(t)}`, c.r.rev[t], "money", "calc", c.a.streams.length ? "The sum of the revenue streams."
    : `Revenue ${t ? c.fyE(t - 1) : c.fyA(c.baseIdx)} × (1 + ${fmt.pct(c.a.growth[t])} growth).`);
const tEbit = (c, t) => T(`EBIT ${c.fyE(t)}`, c.r.ebit[t], "money", "calc", `Revenue × ${fmt.pct(c.a.ebitMargin[t])} EBIT margin.`);
const tDa = (c, t) => T(`D&A ${c.fyE(t)}`, c.r.da[t], "money", "calc", `Revenue × ${fmt.pct(c.a.daPct[t])} of revenue.`);
const tTaxes = (c, t) => T(`Taxes ${c.fyE(t)}`, -c.r.tax[t], "money", "calc", `EBIT × ${fmt.pct(c.a.taxRate[t])} tax rate.`);
const tCapex = (c, t) => T(`Capex ${c.fyE(t)}`, -c.r.capex[t], "money", "calc", `Revenue × ${fmt.pct(c.a.capexPct[t])} of revenue.`);
const tNopat = (c, t) => T(`NOPAT ${c.fyE(t)}`, c.r.nopat[t], "money", "calc", "EBIT − taxes on EBIT.");
const tUfcf = (c, t) => T(`Free cash flow ${c.fyE(t)}`, c.r.ufcf[t], "money", "calc", "EBIT − taxes + D&A − capex − change in NWC.");
const tWacc = (c) => T("WACC", c.r.wacc, "pct2", "calc",
  `${fmt.pct(1 - c.a.debtWeight)} × ${fmt.pct(c.r.ke, 2)} cost of equity + ${fmt.pct(c.a.debtWeight)} × ${fmt.pct(c.r.kdAfter, 2)} after-tax cost of debt.`);
const tDf = (c, t) => T(`Discount factor ${c.fyE(t)}`, c.r.df[t], "df", "calc", `1 ÷ (1 + ${fmt.pct(c.r.wacc, 2)} WACC)^${+c.r.period[t].toFixed(1)}.`);
const tPv = (c, t) => T(`PV ${c.fyE(t)}`, c.r.pv[t], "money", "calc", `Free cash flow × ${c.r.df[t].toFixed(3)} discount factor.`);
const tEbitdaN = (c) => T(`EBITDA ${c.fyE(c.n - 1)}`, c.r.ebitdaN, "money", "calc", "Final-year EBIT + D&A.");
const tTv = (c) => T("Terminal value", c.r.tv, "money", "calc", c.a.tvMethod === "multiple"
  ? `Final-year EBITDA × ${fmt.x(c.a.exitMultiple)}.` : `Final-year free cash flow × (1 + g) ÷ (WACC − g).`);
const tSumPv = (c) => T("PV of free cash flows", c.r.sumPv, "money", "calc", `Sum of the ${c.n} discounted forecast years.`);
const tPvTv = (c) => T("PV of terminal value", c.r.pvTv, "money", "calc", `Terminal value ÷ (1 + WACC)^${c.n}.`);
const tEv = (c) => T("Enterprise value", c.r.ev, "money", "calc", "PV of free cash flows + PV of terminal value.");
const tEquity = (c) => T("Equity value", c.r.equity, "money", "calc", "Enterprise value − debt + cash − minority interest.");

// Base-year slice of a stream: reported revenue × its share (shares are weights scaled to 100%).
function streamBase(c, s) {
  const tot = c.a.streams.reduce((x, y) => x + (+y.share || 0), 0);
  return tot ? c.baseRev * c.a.streams[s].share / tot : 0;
}
const sName = (c, s) => c.a.streams[s].name || `Stream ${s + 1}`;
const tSrev = (c, s, t) => t < 0
  ? T(`${sName(c, s)} ${c.fyA(c.baseIdx)}`, streamBase(c, s), "money", "calc",
    `Reported revenue × ${fmt.pct(c.a.streams[s].share)} share${Math.abs(c.a.streams.reduce((x, y) => x + (+y.share || 0), 0) - 1) > 0.0005 ? " (shares scaled to 100%)" : ""}.`)
  : T(`${sName(c, s)} ${c.fyE(t)}`, c.r.srev[s][t], "money", "calc", `Last year × (1 + ${fmt.pct(c.a.streams[s].growth[t])} growth).`);

// ---------------------------------------------------------------- one step per forecast line
const X_STEP = {
  rev(c, t) {
    if (c.a.streams.length) {
      const parts = c.a.streams.map((_, s) => tSrev(c, s, t));
      return { eq: parts.flatMap((p, i) => (i ? ["+", p] : [p])), res: tRev(c, t),
        viz: { type: "split", items: parts.map((p) => ({ label: p.label, v: p.v })) } };
    }
    const prev = tRev(c, t - 1);
    return { eq: [prev, "×", "(1 +", tIn(c, `Growth ${c.fyE(t)}`, "growth", t), ")"], res: tRev(c, t),
      viz: { type: "bars", items: [{ label: prev.label, v: prev.v }, { label: `Revenue ${c.fyE(t)}`, v: c.r.rev[t], hl: true }] } };
  },
  srev(c, t, s) {
    const prev = tSrev(c, s, t - 1);
    return { eq: [prev, "×", "(1 +", tIn(c, `Growth ${c.fyE(t)}`, "growth", t, "pct", s), ")"], res: tSrev(c, s, t),
      viz: { type: "bars", items: [{ label: prev.label, v: prev.v }, { label: `${sName(c, s)} ${c.fyE(t)}`, v: c.r.srev[s][t], hl: true }] } };
  },
  growth(c, t) {
    return { eq: [tRev(c, t), "÷", tRev(c, t - 1), "− 1"], res: T(`Growth ${c.fyE(t)}`, c.r.growth[t], "pct"),
      viz: driverCols(c, "growth", null, { fc: t }), caption: "With revenue streams, total growth follows from the streams' own growth and their mix." };
  },
  ebit(c, t) {
    return { eq: [tRev(c, t), "×", tIn(c, `EBIT margin ${c.fyE(t)}`, "ebitMargin", t)], res: tEbit(c, t),
      viz: { type: "bars", items: [{ label: "Revenue", v: c.r.rev[t] }, { label: "EBIT", v: c.r.ebit[t], hl: true }] } };
  },
  tax(c, t) {
    return { eq: ["−", tEbit(c, t), "×", tIn(c, `Tax rate ${c.fyE(t)}`, "taxRate", t)], res: T(`Taxes on EBIT ${c.fyE(t)}`, c.r.tax[t]),
      viz: { type: "bars", items: [{ label: "EBIT", v: c.r.ebit[t] }, { label: "Taxes", v: c.r.tax[t], hl: true }] },
      caption: "Taxed as if the company had no debt: interest tax shields are in the WACC, not here." };
  },
  nopat(c, t) {
    return { eq: [tEbit(c, t), "−", tTaxes(c, t)], res: tNopat(c, t),
      viz: { type: "wf", items: [{ label: "EBIT", v: c.r.ebit[t] }, { label: "Taxes on EBIT", v: c.r.tax[t] }], total: { label: "NOPAT", v: c.r.nopat[t] } } };
  },
  da(c, t) {
    return { eq: [tRev(c, t), "×", tIn(c, `D&A % ${c.fyE(t)}`, "daPct", t)], res: tDa(c, t),
      viz: { type: "bars", items: [{ label: "Revenue", v: c.r.rev[t] }, { label: "D&A", v: c.r.da[t], hl: true }] } };
  },
  ebitda(c, t) {
    return { eq: [tEbit(c, t), "+", tDa(c, t)], res: T(`EBITDA ${c.fyE(t)}`, c.r.ebitda[t]),
      viz: { type: "wf", items: [{ label: "EBIT (operating income)", v: c.r.ebit[t] }, { label: "D&A", v: c.r.da[t] }], total: { label: "EBITDA", v: c.r.ebitda[t] } },
      caption: t === c.n - 1 ? "The final year's EBITDA is what the exit multiple applies to." : "" };
  },
  capex(c, t) {
    return { eq: ["−", tRev(c, t), "×", tIn(c, `Capex % ${c.fyE(t)}`, "capexPct", t)], res: T(`Capex ${c.fyE(t)}`, c.r.capex[t]),
      viz: { type: "bars", items: [{ label: "Revenue", v: c.r.rev[t] }, { label: "Capex", v: c.r.capex[t], hl: true }] } };
  },
  dnwc(c, t) {
    const d = c.r.rev[t] - (t ? c.r.rev[t - 1] : c.baseRev);
    return { eq: ["−(", tRev(c, t), "−", tRev(c, t - 1), ")", "×", tIn(c, `NWC % ${c.fyE(t)}`, "nwcPct", t)], res: T(`Change in NWC ${c.fyE(t)}`, c.r.dnwc[t]),
      viz: { type: "bars", items: [{ label: "Revenue added", v: d }, { label: "Cash into working capital", v: c.r.dnwc[t], hl: true }] },
      caption: "More revenue ties up cash in receivables and inventory, so growth costs cash and a shrinking business releases it." };
  },
  ufcf(c, t) {
    const r = c.r, dn = r.dnwc[t];
    return { eq: [tEbit(c, t), "−", tTaxes(c, t), "+", tDa(c, t), "−", tCapex(c, t), dn >= 0 ? "+" : "−",
        T(`Change in NWC ${c.fyE(t)}`, Math.abs(dn), "money", "calc", `${fmt.pct(c.a.nwcPct[t])} of the change in revenue.`)],
      res: tUfcf(c, t),
      viz: { type: "wf", items: [{ label: "EBIT", v: r.ebit[t] }, { label: "Taxes on EBIT", v: r.tax[t] }, { label: "D&A (non-cash)", v: r.da[t] },
        { label: "Capex", v: r.capex[t] }, { label: "Change in NWC", v: dn }], total: { label: "Free cash flow", v: r.ufcf[t] } } };
  },
  df(c, t) {
    const per = c.r.period[t];
    return { eq: ["1 ÷ (1 +", tWacc(c), ")^", T("Discount period", per, "yrs", "calc",
        c.a.midYear ? `Year ${t + 1} − 0.5: mid-year convention, the cash arrives through the year.` : `Year ${t + 1}: cash at the end of the year.`)],
      res: T(`Discount factor ${c.fyE(t)}`, c.r.df[t], "df"),
      viz: { type: "bars", f: "df", items: [{ label: `1.00 in ${c.fyE(t)}`, v: 1 }, { label: "is worth today", v: c.r.df[t], hl: true }] } };
  },
  pv(c, t) {
    return { eq: [tUfcf(c, t), "×", tDf(c, t)], res: tPv(c, t),
      viz: { type: "bars", items: [{ label: "Free cash flow", v: c.r.ufcf[t] }, { label: "Worth today", v: c.r.pv[t], hl: true }] } };
  },
};
const X_ROW = {
  rev: "Revenue", srev: "Revenue", growth: "Revenue growth", ebit: "EBIT", tax: "Taxes on EBIT", nopat: "NOPAT", da: "D&A",
  ebitda: "EBITDA", capex: "Capex", dnwc: "Change in NWC", ufcf: "Unlevered free cash flow", df: "Discount factor", pv: "PV of free cash flow",
};

// ---------------------------------------------------------------- drivers: history next to the forecast
const histRatio = (num, den, sign = 1) => num.map((x, i) => (x != null && den[i] ? sign * x / den[i] : null));
const histGrowth = (v) => v.map((x, i) => (i > 0 && x != null && v[i - 1] ? x / v[i - 1] - 1 : null));
const X_DRV = {
  growth: { label: "Revenue growth", hist: (b) => histGrowth(b.revenue), use: "rev" },
  ebitMargin: { label: "EBIT margin", hist: (b) => histRatio(b.ebit, b.revenue), use: "ebit" },
  taxRate: { label: "Tax rate", hist: (b) => b.taxRate, use: "tax" },
  daPct: { label: "D&A % of revenue", hist: (b) => histRatio(b.da, b.revenue), use: "da" },
  capexPct: { label: "Capex % of revenue", hist: (b) => histRatio(b.capex, b.revenue, -1), use: "capex" },
  nwcPct: { label: "NWC % of change in revenue", hist: (b) => b.periods.map(() => null), use: "dnwc" },
};
function driverCols(c, k, s, hl) {
  const st = s != null ? c.a.streams[s] : null;
  const hv = st ? (() => { const h = streamHist(st); return h ? histGrowth(h) : c.b.periods.map(() => null); })() : X_DRV[k].hist(c.b);
  const calc = k === "growth" && !st && c.a.streams.length > 0, fc = calc ? c.r.growth : (st || c.a)[k];
  return {
    type: "cols", f: "pct",
    items: [
      ...c.b.periods.map((_, i) => ({ label: shortFy(c.fyA(i)), v: hv[i], kind: "hist", hl: hl && hl.hist === i })),
      ...Array.from({ length: c.n }, (_, t) => ({ label: shortFy(c.fyE(t)), v: fc[t], kind: calc ? "fc calc" : "fc", hl: hl && hl.fc === t })),
    ],
  };
}

// ---------------------------------------------------------------- reported history
const X_HIST = {
  revenue: { label: "Revenue", vals: (b) => b.revenue, st: "income statement", hk: "income:TotalRevenue" },
  ebit: { label: "EBIT (operating income)", vals: (b) => b.ebit, st: "income statement", hk: "income:OperatingIncome" },
  da: { label: "D&A", vals: (b) => b.da, st: "cash flow statement", hk: "cashflow:DepreciationAndAmortization" },
  capex: { label: "Capex", vals: (b) => b.capex, st: "cash flow statement", hk: "cashflow:CapitalExpenditure" },
};
const flagAt = (c, hk, i) => ((state.modelFlags[c.m.symbol] || []).find((f) => f.key === hk && f.period === c.b.periods[i]) || null);
const tHist = (c, key, i) => {
  const h = X_HIST[key];
  return T(`${h.label} ${c.fyA(i)}`, h.vals(c.b)[i], "money", "data", `Reported in the ${c.fyA(i)} ${h.st}, ${c.src}.`);
};
const histCols = (c, vals, hl, f) => ({ type: "cols", f: f || "money", items: c.b.periods.map((_, i) => ({ label: shortFy(c.fyA(i)), v: vals[i], kind: "hist", hl: i === hl })) });

// ---------------------------------------------------------------- the explanations
function explain(key) {
  const c = xctx(), p = key.split(":"), r = c.r, a = c.a, b = c.b;
  const num = (i) => (p[i] === "" || p[i] == null ? null : +p[i]);
  switch (p[0]) {
    // Forecast cells: o:<line>:<year>, o:srev<s>:<year>
    case "o": {
      const t = +p[2];
      if (p[1].startsWith("srev")) {
        const s = +p[1].slice(4);
        return { title: `${sName(c, s)} revenue`, sub: `${c.fyE(t)} · ${c.unit}`, value: mm(r.srev[s][t]), steps: [X_STEP.srev(c, t, s)] };
      }
      const k = p[1], v = r[k][t];
      const value = k === "df" ? XF.df(v) : k === "growth" ? fmt.pct(v) : mm(v);
      return { title: X_ROW[k], sub: `${c.fyE(t)}${k === "df" || k === "growth" ? "" : " · " + c.unit}`, value, steps: [X_STEP[k](c, t)] };
    }
    // Inputs: in:<field>:<year>:<stream>
    case "in": return explainInput(c, p[1], num(2), num(3));
    // The NAV, LBO and excess-return models: alt:<model>:<field>
    case "alt": return altExplain(p[1], p[2]);
    // Reported history: h:<line>:<i>; ratios from it: hr:<driver>:<i>
    case "h": {
      const i = +p[2];
      if (p[1] === "ebitda") {
        const e = b.ebit[i], d = b.da[i];
        return { title: "EBITDA", sub: `${c.fyA(i)} · ${c.unit}`, value: mm(e != null && d != null ? e + d : null),
          steps: [{ eq: [tHist(c, "ebit", i), "+", tHist(c, "da", i)], res: T(`EBITDA ${c.fyA(i)}`, e + d),
            viz: { type: "wf", items: [{ label: "EBIT (operating income)", v: e }, { label: "D&A", v: d }], total: { label: "EBITDA", v: e + d } } }] };
      }
      const h = X_HIST[p[1]], vals = h.vals(b);
      return { title: h.label, sub: `${c.fyA(i)} · ${c.unit} · as reported`, value: mm(vals[i]), flag: flagAt(c, h.hk, i),
        steps: [{ eq: [tHist(c, p[1], i)], viz: histCols(c, vals, i) }] };
    }
    case "hr": return explainHistRatio(c, p[1], +p[2]);
    // Revenue streams' reported history: sh:<s>:<i>, growth: shr:<s>:<i>
    case "sh": case "shr": {
      const s = +p[1], i = +p[2], hv = streamHist(a.streams[s]) || [];
      const tv = (j) => T(`${sName(c, s)} ${c.fyA(j)}`, hv[j], "money", "data", b.notes.streams || "Reported by the company.");
      if (p[0] === "sh")
        return { title: `${sName(c, s)} revenue`, sub: `${c.fyA(i)} · ${c.unit} · as reported`, value: mm(hv[i]), steps: [{ eq: [tv(i)], viz: histCols(c, hv, i) }] };
      const g = i > 0 && hv[i] != null && hv[i - 1] ? hv[i] / hv[i - 1] - 1 : null;
      return { title: `${sName(c, s)} growth`, sub: c.fyA(i), value: fmt.pct(g),
        steps: [{ eq: [tv(i), "÷", tv(i - 1), "− 1"], res: T(`Growth ${c.fyA(i)}`, g, "pct"), viz: driverCols(c, "growth", s, { hist: i }) }] };
    }
    // Row labels: row:<line> (or row:drv:<driver>:<stream>)
    case "row": return explainRow(c, p[1], p[2], num(3));
    // Side panels and the headline.
    case "ke": return { title: "Cost of equity", sub: "CAPM", value: fmt.pct(r.ke, 2), steps: [stepKe(c)] };
    case "kd": return { title: "After-tax cost of debt", value: fmt.pct(r.kdAfter, 2), steps: [stepKd(c)] };
    case "wacc": return { title: "WACC", sub: "Weighted average cost of capital", value: fmt.pct(r.wacc, 2), steps: [stepWacc(c)] };
    case "tv": return { title: "Terminal value", sub: `End of ${c.fyE(c.n - 1)} · ${c.unit}`, value: mm(r.tv), steps: [stepTv(c)] };
    case "pvtv": return { title: "PV of terminal value", sub: c.unit, value: mm(r.pvTv), steps: [stepPvTv(c)] };
    case "tvpct": return { title: "Terminal value % of EV", value: fmt.pct(r.pvTv / r.ev),
      steps: [{ eq: [tPvTv(c), "÷", tEv(c)], res: T("Share of EV", r.pvTv / r.ev, "pct"),
        viz: { type: "split", items: [{ label: "PV of free cash flows", v: r.sumPv }, { label: "PV of terminal value", v: r.pvTv }] },
        caption: "Above about 75%, the value rests mostly on what happens after the forecast." }] };
    case "implied": return explainImplied(c);
    case "sumpv": return { title: "PV of free cash flows", sub: c.unit, value: mm(r.sumPv), steps: [stepSumPv(c)] };
    case "ev": return { title: "Enterprise value", sub: c.unit, value: mm(r.ev), steps: [stepEv(c)] };
    case "eq": return { title: "Equity value", sub: c.unit, value: mm(r.equity), steps: [stepEquity(c)] };
    case "px": return explainPrice(c);
    case "cur": return { title: "Current share price", sub: b.currency, value: fmt.price(b.price),
      steps: [{ eq: [T("Last price", b.price, "price", "mkt", `${(state.company && state.company.source) || "Market data"}, when the company was loaded.`)] }] };
    case "up": {
      const up = b.price && isFinite(r.price) ? r.price / b.price - 1 : null;
      return { title: "Upside / (downside)", value: up == null ? "–" : (up >= 0 ? "+" : "") + fmt.pct(up),
        steps: [{ eq: [T("Implied share price", r.price, "price"), "÷", T("Current price", b.price, "price", "mkt", (state.company && state.company.source) || "Market data."), "− 1"],
          res: T("Upside", up, "pct"),
          viz: { type: "bars", f: "price", items: [{ label: "Implied by the DCF", v: r.price, hl: true }, { label: "Market price", v: b.price }] } }] };
    }
    case "sens": return explainSens(c, +p[1], +p[2]);
    case "sensw": case "sensx": return explainSensHead(c, p[0], +p[1]);
    case "sl": {
      const s = +p[1], hv = streamHist(a.streams[s]) || [];
      return { title: `${sName(c, s)} revenue`, sub: `${c.fyA(b.periods.length - 1)} · ${c.unit} · as reported`, value: mm(hv[hv.length - 1]),
        steps: [{ eq: [T(sName(c, s), hv[hv.length - 1], "money", "data", b.notes.streams || "Reported by the company.")],
          viz: { type: "split", items: a.streams.map((st) => { const h = streamHist(st); return { label: st.name, v: h ? h[h.length - 1] || 0 : 0 }; }) } }] };
    }
    case "stot": {
      const parts = a.streams.map((st, s) => T(sName(c, s), +st.share || 0, "pct", "in", xOrigin(c, "share", null, s, "pct")));
      return { title: "Stream shares", sub: "Should add to 100%", value: fmt.pct(parts.reduce((x, y) => x + y.v, 0)),
        steps: [{ eq: parts.flatMap((q, i) => (i ? ["+", q] : [q])), res: T("Total", parts.reduce((x, y) => x + y.v, 0), "pct"),
          caption: "The shares are weights: if they don't add to 100%, the model scales them so the streams start from reported revenue." }] };
    }
  }
  return null;
}

function explainInput(c, k, i, s) {
  const a = c.a, b = c.b, r = c.r;
  if (s != null && k === "share") {
    const st = a.streams[s], tot = a.streams.reduce((x, y) => x + (+y.share || 0), 0);
    return { title: `${sName(c, s)} · share of revenue`, sub: c.fyA(c.baseIdx), value: fmt.pct(st.share),
      steps: [{ eq: [tRev(c, -1), "×", tIn(c, "Share", "share", null, "pct", s), ...(Math.abs(tot - 1) > 0.0005 ? ["÷", T("All shares", tot, "pct", "calc", "Shares are scaled to add to 100%.")] : [])],
        res: tSrev(c, s, -1), viz: { type: "split", items: a.streams.map((x, j) => ({ label: sName(c, j), v: +x.share || 0, hl: j === s })) },
        caption: "This stream's starting slice; each forecast year grows it at the stream's own rate." }] };
  }
  if (i != null) {
    // A per-year driver: its history beside the forecast, then the line it drives.
    const d = X_DRV[k], used = s != null ? X_STEP.srev(c, i, s) : X_STEP[d.use](c, i);
    return { title: `${s != null ? sName(c, s) + " growth" : d.label}`, sub: `${c.fyE(i)} · input`, value: fmt.pct((s != null ? a.streams[s] : a)[k][i]),
      steps: [{ viz: driverCols(c, k, s, { fc: i }), caption: "History (green) beside your forecast (blue)." }, { ...used, label: "Used in" }],
      sources: [{ src: "in", label: "This input", text: xOrigin(c, k, i, s, "pct") }] };
  }
  const f = { beta: "num", exitMultiple: "x", debt: "money", cash: "money", minority: "money", shares: "shares", rf: "pct2", erp: "pct2", sizePremium: "pct2", costOfDebt: "pct2", tgr: "pct2", debtWeight: "pct" }[k] || "pct";
  const label = { rf: "Risk-free rate", erp: "Equity risk premium", beta: "Beta", sizePremium: "Size / other premium", costOfDebt: "Pre-tax cost of debt",
    debtWeight: "Debt / (debt + equity)", tgr: "Terminal growth", exitMultiple: "Exit EV / EBITDA", debt: "Total debt", cash: "Cash & investments",
    minority: "Minority interest", shares: "Diluted shares" }[k] || k;
  const head = { title: label, sub: `Input${f === "money" ? " · " + c.unit : f === "shares" ? " · millions" : ""}`, value: XF[f](a[k]),
    flag: ["debt", "cash", "minority", "shares"].includes(k) ? modelFlagFor(k) : null,
    sources: [{ src: "in", label: "This input", text: xOrigin(c, k, null, null, f) }] };
  const use = (st) => ({ ...st, label: "Used in" });
  switch (k) {
    case "rf": case "erp": case "beta": case "sizePremium": return { ...head, steps: [use(stepKe(c))] };
    case "costOfDebt": return { ...head, steps: [use(stepKd(c))] };
    case "debtWeight": return { ...head, steps: [use(stepWacc(c))] };
    case "tgr": case "exitMultiple": {
      const used = (k === "tgr") === (a.tvMethod !== "multiple");
      const extra = k === "exitMultiple" && b.currentEvToEbitda ? ` Today's EV / EBITDA is ${fmt.x(b.currentEvToEbitda)}.` : "";
      return { ...head, steps: [use({ ...stepTv(c, k === "tgr" ? "gordon" : "multiple"),
        caption: (used ? "" : "Not the method in use, so this only feeds the cross-check and the sensitivity grid.") + extra })] };
    }
    case "debt": case "cash": case "minority": return { ...head, steps: [use(stepEquity(c))] };
    case "shares": {
      const steps = [];
      if (b.basicShares > 0) steps.push({ label: "Default", eq: [
        T("Shares outstanding", b.basicShares, "shares", "data", "Today's count, before dilution."), "+",
        T("Dilutive securities", b.dilutiveShares || 0, "shares", "data", "Options, RSUs and convertibles: diluted − basic weighted shares in the latest period.")],
        res: T("Diluted shares", b.basicShares + (b.dilutiveShares || 0), "shares"),
        viz: { type: "split", f: "shares", items: [{ label: "Shares outstanding", v: b.basicShares }, { label: "Dilutive securities", v: b.dilutiveShares || 0 }] } });
      if (b.impliedShares > 0) {
        const dil = b.basicShares + (b.dilutiveShares || 0), pick = Math.max(dil, b.impliedShares);
        steps.push({ label: "Against implied shares outstanding", eq: ["larger of",
          T("Shares + dilutive", dil, "shares"), "and",
          T("Implied shares outstanding", b.impliedShares, "shares", "mkt", `Market cap ÷ price (${b.impliedSharesSource}). Counts every share class.`)],
          res: T("Default share count", pick, "shares"),
          caption: "A reported share count can cover one share class only; market cap ÷ price covers them all. The model starts on the larger." });
      }
      steps.push(use({ eq: [tEquity(c), "÷", T("Diluted shares", a.shares, "shares", "in")], res: T("Implied share price", r.price, "price") }));
      return { ...head, steps };
    }
  }
  return { ...head, steps: [] };
}

function explainHistRatio(c, k, i) {
  const b = c.b, d = X_DRV[k];
  let eq = [], v = d.hist(b)[i];
  if (k === "growth") eq = [tHist(c, "revenue", i), "÷", tHist(c, "revenue", i - 1), "− 1"];
  else if (k === "ebitMargin") eq = [tHist(c, "ebit", i), "÷", tHist(c, "revenue", i)];
  else if (k === "daPct") eq = [tHist(c, "da", i), "÷", tHist(c, "revenue", i)];
  else if (k === "capexPct") eq = ["−", tHist(c, "capex", i), "÷", tHist(c, "revenue", i)];
  else if (k === "taxRate") eq = [T(`Effective tax rate ${c.fyA(i)}`, v, "pct", "data", `Tax provision ÷ pre-tax income, as reported, ${c.src}.`)];
  return { title: d.label, sub: `${c.fyA(i)} · from the statements`, value: fmt.pct(v),
    steps: [{ eq, res: k === "taxRate" ? null : T(`${d.label} ${c.fyA(i)}`, v, "pct"), viz: driverCols(c, k, null, { hist: i }) }] };
}

// row:<line>, row:srev:<stream>, row:drv:<driver>[:<stream>]
function explainRow(c, k, arg, stream) {
  const a = c.a, b = c.b, r = c.r;
  if (k === "drv") {
    const dk = arg, st = stream;
    return { title: st != null ? `${sName(c, st)} growth` : X_DRV[dk].label, sub: "History and forecast",
      steps: [{ viz: driverCols(c, dk, st, null), caption: "History (green) beside your forecast (blue). Hover a year for its detail." }],
      sources: [{ src: "in", label: "Default", text: st != null ? b.notes.streams || "" : b.notes[X_NOTE[dk]] || "" }] };
  }
  if (k === "srev") {
    const s = +arg, hv = streamHist(a.streams[s]) || b.periods.map(() => null);
    return { title: `${sName(c, s)} revenue`, sub: c.unit, steps: [{ viz: lineCols(c, hv, r.srev[s]), caption: "Each year: last year × (1 + this stream's growth)." }] };
  }
  const H = { rev: b.revenue, ebit: b.ebit, da: b.da, capex: b.capex, ebitda: b.ebit.map((e, i) => (e != null && b.da[i] != null ? e + b.da[i] : null)) };
  const how = {
    rev: a.streams.length ? "The sum of the revenue streams." : "Last year's revenue × (1 + growth).",
    ebit: "Revenue × EBIT margin.", tax: "EBIT × tax rate, as a deduction.", nopat: "EBIT − taxes on EBIT: operating profit after tax.",
    da: "Revenue × D&A %. Added back because it isn't cash.", ebitda: "EBIT + D&A.", capex: "Revenue × capex %, as a cash outflow.",
    dnwc: "Change in revenue × NWC %, as a cash outflow when revenue grows.", ufcf: "EBIT − taxes + D&A − capex − change in NWC.",
    df: "1 ÷ (1 + WACC)^discount period.", pv: "Free cash flow × discount factor.", growth: "Revenue ÷ last year's revenue − 1.",
  }[k];
  const fc = r[k] || [];
  return { title: X_ROW[k], sub: k === "df" || k === "growth" ? "" : c.unit,
    steps: [{ viz: k === "growth" ? driverCols(c, "growth", null, null) : lineCols(c, H[k] || b.periods.map(() => null), fc, k === "df" ? "df" : "money"), caption: how }] };
}
const lineCols = (c, hist, fc, f) => ({ type: "cols", f: f || "money", items: [
  ...c.b.periods.map((_, i) => ({ label: shortFy(c.fyA(i)), v: hist[i], kind: "hist" })),
  ...Array.from({ length: c.n }, (_, t) => ({ label: shortFy(c.fyE(t)), v: fc[t], kind: "fc calc" })),
] });

// ---------------------------------------------------------------- cost of capital, terminal value, bridge
function stepKe(c) {
  const a = c.a;
  return { eq: [tIn(c, "Risk-free rate", "rf", null, "pct2"), "+", tIn(c, "Beta", "beta", null, "num"), "×", tIn(c, "Equity risk premium", "erp", null, "pct2"), "+", tIn(c, "Size / other premium", "sizePremium", null, "pct2")],
    res: T("Cost of equity", c.r.ke, "pct2"),
    viz: { type: "wf", f: "pct2", items: [{ label: "Risk-free rate", v: a.rf }, { label: `Beta × ERP (${a.beta.toFixed(2)} × ${fmt.pct(a.erp, 2)})`, v: a.beta * a.erp }, { label: "Size / other premium", v: a.sizePremium }],
      total: { label: "Cost of equity", v: c.r.ke } } };
}
function stepKd(c) {
  return { eq: [tIn(c, "Pre-tax cost of debt", "costOfDebt", null, "pct2"), "×", "(1 −", tIn(c, `Tax rate ${c.fyE(0)}`, "taxRate", 0), ")"],
    res: T("After-tax cost of debt", c.r.kdAfter, "pct2"), caption: "Interest is tax-deductible, so debt costs less than its coupon." };
}
function stepWacc(c) {
  const a = c.a, r = c.r, we = 1 - a.debtWeight;
  return { eq: ["(", T("Equity weight", we, "pct", "calc", "1 − debt weight."), "×", T("Cost of equity", r.ke, "pct2", "calc", "Risk-free + beta × ERP + size premium."), ")", "+",
      "(", tIn(c, "Debt weight", "debtWeight"), "×", T("After-tax cost of debt", r.kdAfter, "pct2", "calc", "Pre-tax cost × (1 − tax rate)."), ")"],
    res: T("WACC", r.wacc, "pct2"),
    viz: { type: "wf", f: "pct2", items: [{ label: `Equity: ${fmt.pct(we)} × ${fmt.pct(r.ke, 2)}`, v: we * r.ke }, { label: `Debt: ${fmt.pct(a.debtWeight)} × ${fmt.pct(r.kdAfter, 2)}`, v: a.debtWeight * r.kdAfter }],
      total: { label: "WACC", v: r.wacc } } };
}
function stepTv(c, method) {
  const a = c.a, r = c.r, n = c.n, m = method || a.tvMethod;
  const other = { type: "bars", items: [{ label: "Perpetuity growth", v: r.tvGordon, hl: a.tvMethod !== "multiple" }, { label: "Exit multiple", v: r.tvMultiple, hl: a.tvMethod === "multiple" }] };
  if (m === "multiple")
    return { eq: [tEbitdaN(c), "×", tIn(c, "Exit EV / EBITDA", "exitMultiple", null, "x")], res: T("Terminal value (multiple)", r.tvMultiple), viz: other,
      caption: `The perpetuity-growth method gives ${mm(r.tvGordon)}, an implied ${fmt.x(r.impliedMultiple)} EBITDA.` };
  return { eq: [T(`Free cash flow ${c.fyE(n - 1)}`, r.ufcf[n - 1], "money", "calc", "The final forecast year."), "×", "(1 +", tIn(c, "Terminal growth", "tgr", null, "pct2"), ")", "÷",
      "(", tWacc(c), "−", tIn(c, "Terminal growth", "tgr", null, "pct2"), ")"],
    res: T("Terminal value (perpetuity)", r.tvGordon), viz: other,
    caption: r.gordonInvalid ? "WACC must be above terminal growth, or the value is infinite." : `The exit-multiple method gives ${mm(r.tvMultiple)} (EBITDA × ${fmt.x(a.exitMultiple)}).` };
}
function stepPvTv(c) {
  return { eq: [tTv(c), "÷", "(1 +", tWacc(c), ")^", T("Years", c.n, "yrs", "calc", `Discounted from the end of ${c.fyE(c.n - 1)}.`)], res: tPvTv(c),
    viz: { type: "bars", items: [{ label: `Terminal value, end of ${c.fyE(c.n - 1)}`, v: c.r.tv }, { label: "Worth today", v: c.r.pvTv, hl: true }] } };
}
function stepSumPv(c) {
  const terms = Array.from({ length: c.n }, (_, t) => tPv(c, t));
  return { eq: terms.flatMap((q, i) => (i ? ["+", q] : [q])), res: tSumPv(c),
    viz: { type: "wf", items: terms.map((q, t) => ({ label: c.fyE(t), v: q.v })), total: { label: "PV of free cash flows", v: c.r.sumPv } } };
}
function stepEv(c) {
  return { eq: [tSumPv(c), "+", tPvTv(c)], res: tEv(c),
    viz: { type: "split", items: [{ label: "PV of free cash flows", v: c.r.sumPv }, { label: "PV of terminal value", v: c.r.pvTv }] } };
}
function stepEquity(c) {
  const a = c.a, r = c.r;
  return { eq: [tEv(c), "−", tIn(c, "Total debt", "debt", null, "money"), "+", tIn(c, "Cash & investments", "cash", null, "money"), "−", tIn(c, "Minority interest", "minority", null, "money")],
    res: tEquity(c),
    viz: { type: "wf", items: [{ label: "Enterprise value", v: r.ev }, { label: "Debt", v: -a.debt }, { label: "Cash & investments", v: a.cash }, { label: "Minority interest", v: -a.minority }],
      total: { label: "Equity value", v: r.equity } } };
}

function explainImplied(c) {
  const a = c.a, r = c.r, n = c.n;
  if (a.tvMethod === "multiple")
    return { title: "Implied perpetuity growth", sub: "What the exit multiple assumes", value: fmt.pct(r.impliedG, 2),
      steps: [{ eq: ["(", T("Terminal value", r.tvMultiple, "money", "calc", "EBITDA × exit multiple."), "×", tWacc(c), "−", T(`Free cash flow ${c.fyE(n - 1)}`, r.ufcf[n - 1]), ")", "÷",
          "(", T("Terminal value", r.tvMultiple), "+", T(`Free cash flow ${c.fyE(n - 1)}`, r.ufcf[n - 1]), ")"],
        res: T("Implied growth", r.impliedG, "pct2"), caption: "The growth rate forever that would make the perpetuity formula give the same terminal value. Compare it with long-run economy growth." }] };
  return { title: "Implied exit multiple", sub: "What the perpetuity growth assumes", value: fmt.x(r.impliedMultiple),
    steps: [{ eq: [T("Terminal value", r.tvGordon, "money", "calc", "Perpetuity growth method."), "÷", tEbitdaN(c)], res: T("Implied EV / EBITDA", r.impliedMultiple, "x"),
      caption: curMultipleNote(c) }] };
}
const curMultipleNote = (c) => (c.b.currentEvToEbitda ? `The company trades at ${fmt.x(c.b.currentEvToEbitda)} EBITDA today.` : "");

function explainPrice(c) {
  const a = c.a, r = c.r, b = c.b;
  if (r.gordonInvalid) return { title: "Implied share price", value: "–", steps: [{ caption: `WACC (${fmt.pct(r.wacc, 2)}) must be above terminal growth (${fmt.pct(a.tgr, 2)}).` }] };
  const up = b.price && isFinite(r.price) ? r.price / b.price - 1 : null;
  return { title: "Implied share price", sub: `${b.symbol} · ${b.currency}`, value: fmt.price(r.price), flag: modelFlagFor("shares"),
    steps: [
      { label: "From cash flows to equity", eq: [tSumPv(c), "+", tPvTv(c), "−", tIn(c, "Debt", "debt", null, "money"), "+", tIn(c, "Cash", "cash", null, "money"), "−", tIn(c, "Minority", "minority", null, "money")],
        res: tEquity(c),
        viz: { type: "wf", items: [{ label: "PV of free cash flows", v: r.sumPv }, { label: "PV of terminal value", v: r.pvTv }, { label: "Debt", v: -a.debt },
          { label: "Cash & investments", v: a.cash }, { label: "Minority interest", v: -a.minority }], total: { label: "Equity value", v: r.equity } } },
      { label: "Per share", eq: [tEquity(c), "÷", tIn(c, "Diluted shares", "shares", null, "shares")], res: T("Implied share price", r.price, "price"),
        viz: { type: "bars", f: "price", items: [{ label: "Implied by the DCF", v: r.price, hl: true }, { label: "Market price", v: b.price }] },
        caption: up == null ? "" : `${up >= 0 ? "Upside" : "Downside"} of ${fmt.pct(Math.abs(up))} to today's price.` },
    ] };
}

// Sensitivity: the same equity bridge at another WACC and terminal assumption. The cash flows don't move.
function sensAxes(c) {
  const method = c.m.sens || c.a.tvMethod;
  return { method, ws: [-2, -1, 0, 1, 2].map((k) => c.r.wacc + k * 0.005),
    xs: method === "multiple" ? [-2, -1, 0, 1, 2].map((k) => c.a.exitMultiple + k) : [-2, -1, 0, 1, 2].map((k) => c.a.tgr + k * 0.0025) };
}
function explainSens(c, i, j) {
  const a = c.a, r = c.r, n = c.n, { method, ws, xs } = sensAxes(c), w = ws[i], x = xs[j];
  const pvs = r.ufcf.map((u, t) => u / Math.pow(1 + w, r.period[t]));
  const sum = pvs.reduce((s, v) => s + v, 0);
  const tv = method === "multiple" ? r.ebitdaN * x : r.ufcf[n - 1] * (1 + x) / (w - x);
  const bad = method !== "multiple" && w <= x;
  const pvTv = tv / Math.pow(1 + w, n), ev = sum + pvTv, eq = ev - a.debt + a.cash - a.minority, px = bad ? NaN : eq / a.shares;
  const xl = method === "multiple" ? `${fmt.x(x)} exit multiple` : `${fmt.pct(x, 2)} terminal growth`;
  const tvT = method === "multiple"
    ? T("Terminal value", tv, "money", "calc", `Final-year EBITDA ${mm(r.ebitdaN)} × ${fmt.x(x)}.`)
    : T("Terminal value", tv, "money", "calc", `Final-year free cash flow ${mm(r.ufcf[n - 1])} × (1 + ${fmt.pct(x, 2)}) ÷ (${fmt.pct(w, 2)} − ${fmt.pct(x, 2)}).`);
  if (bad) return { title: "Sensitivity", sub: `${fmt.pct(w, 2)} WACC · ${xl}`, value: "–", steps: [{ caption: "WACC is at or below terminal growth here, so the perpetuity value has no finite answer." }] };
  return { title: "Sensitivity", sub: `${fmt.pct(w, 2)} WACC · ${xl}`, value: fmt.price(px),
    steps: [
      { label: "Enterprise value at these rates", eq: [T(`PV of free cash flows at ${fmt.pct(w, 2)}`, sum, "money", "calc", "The same forecast cash flows, discounted at this WACC."), "+",
          T("PV of terminal value", pvTv, "money", "calc", `Terminal value ${mm(tv)} ÷ (1 + ${fmt.pct(w, 2)})^${n}.`)],
        res: T("Enterprise value", ev),
        viz: { type: "split", items: [{ label: "PV of free cash flows", v: sum }, { label: "PV of terminal value", v: pvTv }] } },
      { label: "Per share", eq: [T("Enterprise value", ev), "−", T("Debt", a.debt, "money", "in"), "+", T("Cash", a.cash, "money", "in"), "−", T("Minority", a.minority, "money", "in"), "÷", T("Diluted shares", a.shares, "shares", "in")],
        res: T("Share price", px, "price"),
        viz: { type: "bars", f: "price", items: [{ label: "This cell", v: px, hl: true }, { label: "Your case (centre)", v: r.price }, { label: "Market price", v: c.b.price }] },
        caption: `WACC ${fmtStep(w - r.wacc, "pct2")} and ${method === "multiple" ? "multiple " + fmtStep(x - a.exitMultiple, "x") : "growth " + fmtStep(x - a.tgr, "pct2")} vs your case. Only the discounting and the terminal value change.` },
    ],
    sources: [{ src: "calc", label: "Terminal value", text: tvT.how }] };
}
const fmtStep = (d, f) => (Math.abs(d) < 1e-9 ? "unchanged" : (d > 0 ? "+" : "−") + XF[f](Math.abs(d)));
function explainSensHead(c, kind, i) {
  const { method, ws, xs } = sensAxes(c);
  if (kind === "sensw")
    return { title: "WACC", sub: "Sensitivity row", value: fmt.pct(ws[i], 2),
      steps: [{ eq: [tWacc(c), ws[i] >= c.r.wacc ? "+" : "−", T("Step", Math.abs(ws[i] - c.r.wacc), "pct2", "calc", "Rows step by 0.50%.")], res: T("WACC in this row", ws[i], "pct2") }] };
  const base = method === "multiple" ? tIn(c, "Exit EV / EBITDA", "exitMultiple", null, "x") : tIn(c, "Terminal growth", "tgr", null, "pct2");
  const f = method === "multiple" ? "x" : "pct2";
  return { title: method === "multiple" ? "Exit multiple" : "Terminal growth", sub: "Sensitivity column", value: XF[f](xs[i]),
    steps: [{ eq: [base, xs[i] >= base.v ? "+" : "−", T("Step", Math.abs(xs[i] - base.v), f, "calc", `Columns step by ${method === "multiple" ? "1.0x" : "0.25%"}.`)], res: T("In this column", xs[i], f) }] };
}

// ---------------------------------------------------------------- drawing
function xChip(t, res) {
  return `<span class="xc ${res ? "res" : t.src}"><small>${esc(t.label)}</small><b>${XF[t.f](t.v)}</b></span>`;
}
function xEq(st) {
  if (!st.eq || !st.eq.length) return "";
  const parts = st.eq.map((p) => (typeof p === "string" ? `<i class="xo">${esc(p)}</i>` : xChip(p))).join("");
  return `<div class="xeq">${parts}${st.res ? `<span class="xres"><i class="xo eqs">=</i>${xChip(st.res, true)}</span>` : ""}</div>`;
}
const pctW = (v) => `${Math.max(0, Math.min(100, v)).toFixed(2)}%`;

// Waterfall: each piece starts where the last one ended; the total is drawn from zero.
function xWaterfall(v) {
  const f = XF[v.f || "money"];
  let run = 0;
  const rows = v.items.map((it) => { const from = run; run += it.v || 0; return { ...it, from, to: run }; });
  const tot = v.total ? v.total.v : run;
  const pts = [0, tot, ...rows.flatMap((q) => [q.from, q.to])].filter((x) => isFinite(x));
  const lo = Math.min(...pts), hi = Math.max(...pts), span = hi - lo || 1;
  const bar = (a, b, cls) => `<i class="${cls}" style="left:${pctW(((Math.min(a, b) - lo) / span) * 100)};width:${pctW(Math.max(0.8, (Math.abs(b - a) / span) * 100))}"></i>`;
  const zero = lo < 0 ? `<u style="left:${pctW((-lo / span) * 100)}"></u>` : "";
  const row = (label, a, b, cls, val) => `<div class="xr"><span>${esc(label)}</span><div class="xt">${zero}${bar(a, b, cls)}</div><b>${val}</b></div>`;
  return `<div class="xviz">${rows.map((q, i) => row(q.label, q.from, q.to, q.v >= 0 ? "pos" : "neg", (i ? (q.v >= 0 ? "+" : "−") : q.v < 0 ? "−" : "") + f(Math.abs(q.v)))).join("")}
    ${row(v.total ? v.total.label : "Total", 0, tot, "tot", f(tot))}</div>`;
}
function xBars(v) {
  const max = Math.max(1e-12, ...v.items.map((i) => Math.abs(i.v)).filter((x) => isFinite(x)));
  return `<div class="xviz">${v.items.map((i) => `<div class="xr ${i.hl ? "hl" : ""}"><span>${esc(i.label)}</span>
    <div class="xt"><i class="${i.hl ? "tot" : i.v < 0 ? "neg" : "pos"}" style="left:0;width:${pctW(isFinite(i.v) ? (Math.abs(i.v) / max) * 100 : 0)}"></i></div>
    <b>${XF[i.f || v.f || "money"](i.v)}</b></div>`).join("")}</div>`;
}
// One bar split into its parts, with a legend.
function xSplit(v) {
  const f = XF[v.f || "money"], items = v.items.filter((i) => isFinite(i.v) && i.v > 0), tot = items.reduce((s, i) => s + i.v, 0) || 1;
  return `<div class="xviz"><div class="xsplit">${items.map((i, k) => `<i class="c${k % 6} ${i.hl ? "hl" : ""}" style="width:${pctW((i.v / tot) * 100)}"></i>`).join("")}</div>
    <div class="xleg">${items.map((i, k) => `<span class="${i.hl ? "hl" : ""}"><i class="c${k % 6}"></i>${esc(i.label)} <b>${f(i.v)}</b> <em>${fmt.pct(i.v / tot)}</em></span>`).join("")}</div></div>`;
}
// Columns over time: history in green, forecast inputs in blue, formulas in grey; the hovered year outlined.
function xCols(v) {
  const f = XF[v.f || "pct"], vals = v.items.map((i) => i.v).filter((x) => x != null && isFinite(x));
  if (!vals.length) return "";
  const lo = Math.min(0, ...vals), hi = Math.max(0, ...vals), span = hi - lo || 1;
  return `<div class="xviz"><div class="xcols">${v.items.map((i) => {
    const ok = i.v != null && isFinite(i.v), top = ok ? ((hi - Math.max(i.v, 0)) / span) * 100 : 0, h = ok ? (Math.abs(i.v) / span) * 100 : 0;
    return `<div class="xcol ${i.kind} ${i.hl ? "hl" : ""}"><em>${ok ? f(i.v) : ""}</em><div class="xca">${lo < 0 ? `<u style="top:${pctW((hi / span) * 100)}"></u>` : ""}<i class="${ok && i.v < 0 ? "neg" : ""}" style="top:${pctW(top)};height:${pctW(Math.max(h, ok ? 2 : 0))}"></i></div><small>${esc(i.label)}</small></div>`;
  }).join("")}</div></div>`;
}
const X_VIZ = { wf: xWaterfall, bars: xBars, split: xSplit, cols: xCols };

function xRender(x) {
  const steps = (x.steps || []).map((st) => `<div class="xstep">${st.label ? `<div class="xlbl">${esc(st.label)}</div>` : ""}${xEq(st)}${st.viz ? X_VIZ[st.viz.type](st.viz) : ""}${st.caption ? `<div class="xcap">${esc(st.caption)}</div>` : ""}</div>`).join("");
  // Every input and reported figure in the formulas, once each, with where it came from.
  // (An input already described by the card's own source line isn't repeated.)
  const src = [...(x.sources || [])], seen = new Set(src.map((s) => s.label)), said = new Set(src.map((s) => s.text));
  (x.steps || []).forEach((st) => (st.eq || []).forEach((q) => {
    if (typeof q === "string" || !q.how || seen.has(q.label) || (q.src === "in" && said.has(q.how))) return;
    seen.add(q.label); src.push({ src: q.src, label: q.label, text: q.how });
  }));
  const f = x.flag;
  return `<div class="xhead"><div><div class="xtitle">${esc(x.title)}</div>${x.sub ? `<div class="xsub">${esc(x.sub)}</div>` : ""}</div>${x.value != null ? `<div class="xval">${esc(x.value)}</div>` : ""}</div>
    ${f ? `<div class="xflag ${f.severity === "severe" ? "severe" : ""}">${esc(flagTip(f))}</div>` : ""}
    ${steps}
    ${src.length ? `<div class="xsrc">${src.map((s) => `<div><span class="xtag ${s.src}">${X_SRC[s.src] || ""}</span><span><b>${esc(s.label)}</b> ${esc(s.text || "")}</span></div>`).join("")}</div>` : ""}`;
}

// ---------------------------------------------------------------- hover wiring
// What a hovered element explains: an explicit data-x, a forecast cell, an input, or a side-panel line.
function xKeyOf(el) {
  if (el.dataset.x) return el.dataset.x;
  if (el.dataset.o) return `o:${el.dataset.o}`;
  if (el.matches("input.in")) return `in:${el.dataset.k}:${el.dataset.i ?? ""}:${el.dataset.s ?? ""}`;
  if (el.classList.contains("md-line")) {
    const inner = el.querySelector("input.in, b[id]");
    return inner ? (inner.id ? inner.id.replace(/^mo-/, "").replace(/2$/, "") : xKeyOf(inner)) : null;
  }
  return null;
}

function wireExplain() {
  const tab = $("tab-model");
  const card = document.createElement("div");
  card.className = "xcard hidden";
  document.body.appendChild(card);
  // The card stays up while the cursor is on the number or on the card itself, so it can be read
  // and scrolled; it goes once the cursor leaves the card (or the number, without heading for
  // the card).
  let cur = null, timer = 0, leaveTimer = 0, onCard = false;
  const hide = () => { clearTimeout(timer); clearTimeout(leaveTimer); cur = null; card.classList.add("hidden"); };
  const hideSoon = () => { clearTimeout(leaveTimer); leaveTimer = setTimeout(() => { if (!onCard) hide(); }, 220); };
  card.addEventListener("mouseenter", () => { onCard = true; clearTimeout(leaveTimer); clearTimeout(timer); });
  card.addEventListener("mouseleave", () => { onCard = false; hide(); });
  const show = (el) => {
    if (!state.model || !state.model.res || !document.body.contains(el)) return;
    let x = null;
    try { x = explain(xKeyOf(el)); } catch (e) { console.error(e); }
    if (!x) { card.classList.add("hidden"); return; }
    card.innerHTML = xRender(x);
    card.classList.remove("hidden");
    // Below the element if it fits, else above; kept inside the window.
    const r = el.getBoundingClientRect(), cw = card.offsetWidth, ch = card.offsetHeight, pad = 10;
    let top = r.bottom + 8;
    if (top + ch > innerHeight - pad) top = Math.max(pad, r.top - ch - 8);
    const left = Math.min(Math.max(pad, r.left + r.width / 2 - cw / 2), innerWidth - cw - pad);
    card.style.top = `${top}px`; card.style.left = `${left}px`;
  };
  tab.addEventListener("mouseover", (e) => {
    const el = e.target.closest("[data-x], [data-o], input.in, .md-line");
    const target = el && xKeyOf(el) ? el : null;
    if (target === cur) { clearTimeout(leaveTimer); return; }
    clearTimeout(timer);
    // Off a number (on the way to the card, say): give the cursor a moment to reach the card.
    if (!target) { if (!card.classList.contains("hidden")) hideSoon(); cur = null; return; }
    clearTimeout(leaveTimer);
    cur = target;
    timer = setTimeout(() => show(target), card.classList.contains("hidden") ? 280 : 60);
  });
  tab.addEventListener("mouseleave", () => { if (!card.classList.contains("hidden")) hideSoon(); else hide(); });
  tab.addEventListener("input", hide);
  tab.addEventListener("click", hide);
  window.addEventListener("scroll", (e) => { if (!card.contains(e.target)) hide(); }, true);
}
wireExplain();
