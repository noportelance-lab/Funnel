# Funnel

Company research, quick comps, the analyst view, and IS / BS / CFS straight into Excel.
Same stack as ARDesk: a .NET 8 WinForms shell hosting a WebView2 UI (HTML/CSS/vanilla JS),
a JSON bridge between the two, and ClosedXML for the workbooks. Ships as one self-contained `.exe`.

## Build & run

Double-click **`Build Funnel.bat`** (needs the .NET 8 SDK) → `dist\Funnel.exe`.

Headless check of every data call + a full export to `%TEMP%`:

```
dist\Funnel.exe --selftest AAPL
```

### macOS

Same app, same UI, on the system WebKit view ([Photino](https://www.tryphotino.io/)) instead of WebView2.
Double-click **`Build Funnel (Mac).command`**, or run `./build-mac.sh` (`./build-mac.sh x64` for Intel Macs),
with the .NET 8 SDK installed → `ios version/Funnel.app`. Drag it into Applications.

```
"ios version/Funnel.app/Contents/MacOS/Funnel" --selftest AAPL
```

Differences from Windows: one window (tabs can't be dragged out into windows of their own), no
native loading screen (the start screen shows straight away), and settings live in
`~/Library/Application Support/Funnel` with API keys encrypted under a key kept in the login Keychain.
The app is signed ad hoc, not notarised: on another Mac, open it the first time with right-click → Open.
Windows-only code (`Program.cs`, `MainForm.cs`, `AppWindows.cs`, `LoadingView.cs`) is left out of the
Mac build; the Mac shell is in `src/Mac/`.

## Pages

| Page | What it shows |
|---|---|
| **Markets** | Indices, rates, commodities, FX; top gainers / losers / most active. Auto-refreshes every minute. |
| **Overview** | Price chart (1D → Max) with volume, street view, key multiples, full key-stats grid, company profile. |
| **Comps** | Editable peer set (remembered per ticker), multiples table with peer median/mean, sortable columns, where the company sits in the peer range. |
| **Analysts** | Consensus, rating counts, price-target range, 4-month trend, and the latest call from each named firm grouped Buy / Hold / Sell. |
| **Financials & export** | Income statement, balance sheet, cash flow (annual + TTM, or quarterly) in actual / K / M / B, and the Excel export. |
| **Debt & cost of debt** | The company's bonds trading in the US (FINRA TRACE last trades: price, yield, coupon, maturity, ratings), with the market cost of debt: average yield to maturity, weighted by each note's amount outstanding when the 10-K tags it (else equally), after-tax figure, spread over the Treasury curve, average coupon, maturity, duration and price. Yield-curve and maturity-wall charts; tick bonds in or out; one click puts the figure into the DCF. Prices come from the public endpoint behind finra.org's bond pages (FINRA's official API needs an account), so a change on finra.org can break it. |
| **Model (DCF)** | A live discounted-cash-flow model pre-loaded from the statements: every assumption editable, share price recalculates instantly, 5×5 sensitivity, edits saved per ticker. The share price is per diluted share: shares outstanding plus the options, RSUs and convertibles the company counts as dilutive (diluted minus basic weighted shares, latest quarter). Exports as a formula-driven workbook (blue inputs, black formulas, green links to the statement sheets). When the 10-K splits revenue by product or segment, each revenue stream gets its own growth path (and its own block on a *Revenue build* sheet in the export). |

Markets also shows Bank of Canada rates/FX and the US Treasury curve; Overview lists the latest
SEC filings and news.

The Excel export writes one workbook: a sheet per statement (annual, quarterly or both), plus
optional Summary, Comps (median / mean are live formulas), Analysts and 5-year daily prices.

## Settings

**Settings** (bottom of the sidebar) has two tabs.

- **Data sources**: pick a source per section and paste API keys (below).
- **DCF assumptions**: the general assumptions every new model starts from. *Long-run economy growth*
  is the terminal growth rate and the rate every revenue stream's growth fades to (in a straight
  line) by the final forecast year. Also: forecast horizon (5 or 10 years), revenue streams on/off,
  risk-free rate (live 10-year Treasury or fixed), equity risk premium, size premium, tax rate
  (company's 3-year effective rate or fixed), mid-year convention and terminal value method.
  A model you've edited keeps its numbers; **Reset to defaults** on the Model tab rebuilds it from these.

Revenue streams come from the XBRL in the latest 10-K / 20-F / 40-F on SEC EDGAR (products and
services first, else business segments; the split must add up to total revenue). That needs the
SEC contact email and a US listing. Otherwise revenue is one line, and you can still split it by hand
on the Model tab.

## Cross-checks

Every statement is compared, figure by figure, with a second source in the background: SEC EDGAR
(the filings themselves), or Yahoo when the statements came from EDGAR. The company's share count is
checked against the SEC cover page (for multi-class companies like Alphabet, the latest quarter's
weighted shares). Nothing waits on the check; flags appear when it lands.

- **Red** (sources disagree sharply): share counts and per-share figures more than 15% apart, headline
  totals (revenue, net income, assets, equity, cash, operating cash flow) more than 35% apart, or any
  line 200x+ apart (unit errors). Share-count disagreements also raise a one-time warning, and the
  Model tab offers to use the second source's count.
- **Amber underline** (sources differ): smaller gaps, and lines the sources define differently (net
  debt, investments, working-capital moves), only when big enough to matter.
- Hover any flagged figure for both values. Exports carry the same highlighting, with a note on each cell.

Tolerances live in `src/Providers/CrossCheck.cs`.

## Data sources

Pick a source per section under Settings → Data sources. Anything a source
can't answer falls back to the next source that can, with a one-time notice.

| Source | Key | Serves | Licence |
|---|---|---|---|
| Yahoo Finance | none | everything except filings (default) | personal use only |
| SEC EDGAR | none — needs a contact email (SEC rule) | statements as filed (US GAAP, and IFRS for 20-F / 40-F filers such as US-listed Canadian companies), company profile and fundamentals-only comps (no prices), industry (SIC) peers, filings, 8-K / 6-K reports as news | public, free to redistribute |
| Twelve Data | paid key | search, quotes, charts (intraday to max), profile, multiples, statements, analyst ratings & targets, revenue estimates, markets board, movers | Venture plan+ to show data to others; Enterprise for analyst data |
| Financial Modeling Prep | free key, 250 calls/day | company, charts, statements, comps, peers, movers | free key is personal use |
| Finnhub | free key, 60 calls/min | company, comps, industry peers, news | free key is personal use |
| Bank of Canada · US Treasury | none | rates & FX on Markets; 10-year yield = DCF risk-free rate | public (Bank of Canada: credit it) |

**Running without Yahoo.** Untick *Use Yahoo Finance* in Data sources and Funnel never calls it —
not as a source, not as a fallback. Do this for any copy you sell or share. With a Twelve Data key
everything keeps working except news (EDGAR's 8-K feed stands in) and suggested peers (EDGAR's
industry peers); without one, EDGAR, the Treasury and the Bank of Canada still give statements,
filings, peers, fundamentals and rates, but no prices. Twelve Data doesn't carry the S&P 500,
Nasdaq, Dow, Russell or VIX, so its markets board shows the matching ETFs (SPY, QQQ, DIA, IWM).

Self-test without Yahoo: `set FUNNEL_NO_YAHOO=1` (plus `FUNNEL_TWELVEDATA_KEY=...` and
`FUNNEL_SEC_CONTACT=you@example.com` as needed) before `Funnel.exe --selftest AAPL`. These apply to
that run only and are never saved.

API keys are encrypted with Windows DPAPI for the current user. FRED isn't used directly: its
download site stalls Windows HTTP clients, and its Treasury series come from the Treasury anyway.

## Adding a data source

All data goes through `IFinanceProvider` (`src/Providers/IFinanceProvider.cs`) and lands in
provider-neutral models (`Models.cs`), so the UI and the Excel export never know which API a number
came from.

1. Add `src/Providers/<Name>/<Name>Provider.cs` implementing `IFinanceProvider`, listing the
   `Sections` it serves (others throw `NotSupportedException`). Optional extras: `IPeerSource`,
   `IFilingSource`, `INewsSource`, `IKeyedSource` (for an API key).
2. Register it in `ProviderRegistry.cs`. It appears in the Sources panel for its sections.
3. `CompositeProvider` handles routing and the Yahoo fallback. Use `HttpSource` for HTTP.

Check a source headlessly: `Funnel.exe --selftest AAPL edgar` (N/A = not served by that source).

## Where things are kept

- Settings, sources, encrypted keys, recent tickers, saved comp sets and DCF cases: `%LOCALAPPDATA%\Funnel\config.json`
- Exports default to `Documents\Funnel` (then wherever you last saved)
- Start-screen video: `src/Assets/McGill-Desautels-The-SnapYT.App.webm` (embedded in the exe)
