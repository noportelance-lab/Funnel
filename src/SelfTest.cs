using ClosedXML.Excel;
using Funnel.Engine;
using Funnel.Providers;

namespace Funnel;

/// <summary>
/// `Funnel.exe --selftest [SYMBOL] [provider]` — exercises every call a provider serves (calls it
/// doesn't serve show as N/A), the Bank of Canada and FRED feeds, builds the default DCF, and
/// writes a full workbook with the live model to %TEMP%. Without a provider it tests the sources
/// picked in the app. Exit code 0 = everything passed.
/// For this run only: FUNNEL_SEC_CONTACT sets the EDGAR contact email, FUNNEL_NO_YAHOO=1 switches
/// Yahoo Finance off, and FUNNEL_TWELVEDATA_KEY sets a Twelve Data key (try "demo" with AAPL).
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var symbol = (args.Length > 1 ? args[1] : "AAPL").ToUpperInvariant();
        if (symbol == "YOLO") return RunYolo();
        if (symbol.StartsWith("DEBT:")) return RunDebt(symbol[5..]);
        var config = AppConfig.Load();
        if (Environment.GetEnvironmentVariable("FUNNEL_SEC_CONTACT") is { Length: > 0 } email) config.ContactEmail = email;
        if (Environment.GetEnvironmentVariable("FUNNEL_NO_YAHOO") == "1") config.UseYahoo = false;
        if (Environment.GetEnvironmentVariable("FUNNEL_TWELVEDATA_KEY") is { Length: > 0 } tdKey) config.KeyOverrides["twelvedata"] = tdKey;
        ProviderRegistry.Configure(config);
        var composite = new CompositeProvider(config, m => Console.WriteLine($"        note: {m}"));
        IFinanceProvider provider = composite;
        if (args.Length > 2)
        {
            if (ProviderRegistry.Get(args[2]) is not { } one) { Console.Error.WriteLine($"Unknown or disabled source \"{args[2]}\"."); return 2; }
            provider = one;
        }
        if (provider == composite)
            Console.WriteLine("  sources: " + string.Join(", ", Section.All.Select(s => $"{s.Id}={composite.SourceFor(s.Id).Id}")) +
                (ProviderRegistry.YahooEnabled ? "" : "  (Yahoo off)"));
        return RunAsync(provider, composite, symbol, config.Dcf).GetAwaiter().GetResult();
    }

    /// <summary>`--selftest debt:ORCL,F`: issuer matching, bonds, 10-K face amounts and the Treasury curve for each.</summary>
    private static int RunDebt(string symbols)
    {
        var config = AppConfig.Load();
        ProviderRegistry.Configure(config);
        var composite = new CompositeProvider(config, _ => { });
        var finra = new Funnel.Providers.Bonds.FinraBondSource();
        var edgar = ProviderRegistry.All.OfType<Funnel.Providers.Edgar.EdgarProvider>().First();
        foreach (var sym in symbols.Split(','))
        {
            try
            {
                var co = composite.CompanyAsync(sym, CancellationToken.None).GetAwaiter().GetResult();
                var prefix = DebtProfile.Prefix(co.Name);
                var issuers = finra.IssuersAsync(prefix, CancellationToken.None).GetAwaiter().GetResult();
                var chosen = issuers.Where(i => DebtProfile.Matches(co.Name, i.Name)).ToList();
                Console.WriteLine($"{sym} \"{co.Name}\" prefix \"{prefix}\": {issuers.Count} issuers; picked {string.Join(" | ", chosen.Select(c => $"{c.Name} ({c.Bonds})"))}");
                Console.WriteLine("    others: " + string.Join(" | ", issuers.Except(chosen).Take(8).Select(c => $"{c.Name} ({c.Bonds})")));
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                var bonds = chosen.SelectMany(c => finra.BondsAsync(c.Name, CancellationToken.None).GetAwaiter().GetResult())
                    .Where(b => b.Maturity == null || string.CompareOrdinal(b.Maturity, today) > 0).GroupBy(b => b.Cusip).Select(g => g.First()).ToList();
                var traded = bonds.Count(b => b.LastTrade != null && string.CompareOrdinal(b.LastTrade, DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd")) >= 0);
                Console.WriteLine($"    {bonds.Count} outstanding, {traded} traded in 30 days; types {string.Join(",", bonds.GroupBy(b => b.CouponType).Select(g => $"{g.Key}:{g.Count()}"))}");
                foreach (var b in bonds.Take(4)) Console.WriteLine($"      {b.Cusip} {b.Coupon}% {b.Maturity} px {b.Price} y {b.Yield} {b.LastTrade} {b.SandP}/{b.Moodys}");
                try
                {
                    var (notes, form) = edgar.DebtInstrumentsAsync(co.Symbol, CancellationToken.None).GetAwaiter().GetResult();
                    var faces = DebtProfile.MatchFaces(bonds, notes);
                    if (Environment.GetEnvironmentVariable("FUNNEL_DEBT_DIR") is { Length: > 0 } dir)
                    {
                        var curveNow = ProviderRegistry.Treasury.CurvePointsAsync(CancellationToken.None).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(dir, $"{sym}.json"), System.Text.Json.JsonSerializer.Serialize(new
                        {
                            symbol = co.Symbol, name = co.Name, currency = co.Currency, asOf = DateTime.Now,
                            issuers = issuers.Select(i => new { name = i.Name, bonds = i.Bonds, selected = chosen.Contains(i) }),
                            bonds = bonds.OrderBy(b => b.Maturity ?? "9999").Select(b => new { b.Cusip, b.Symbol, b.Issuer, b.Coupon, b.CouponType, b.Maturity, b.Price, b.Yield, b.LastTrade, b.PriceChangePct, b.Moodys, b.SandP, b.Grade,
                                face = faces.TryGetValue(b.Cusip, out var f) ? f.Face : (double?)null, faceNote = faces.TryGetValue(b.Cusip, out var f2) ? f2.Note : null }),
                            facesForm = form, curve = curveNow is { } cn ? new { date = cn.Date, points = cn.Points } : null, notes = new List<string>(), source = "FINRA TRACE",
                        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                    }
                    Console.WriteLine($"    {form}: {notes.Count} notes ({notes.Count(n => n.Rate != null && n.MaturityYear != null)} with rate+maturity), matched {faces.Count} bonds, {faces.Values.Sum(f => f.Face) / 1e9:0.0}B");
                    foreach (var n in notes.Take(3)) Console.WriteLine($"      note {n.Name}: {n.Face / 1e6:0}M {n.Rate} {n.MaturityYear}-{n.MaturityMonth}");
                }
                catch (Exception ex) { Console.WriteLine("    edgar: " + ex.Message); }
            }
            catch (Exception ex) { Console.WriteLine($"{sym}: FAIL {ex.Message}"); }
        }
        var curve = ProviderRegistry.Treasury.CurvePointsAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"curve {curve?.Date}: {string.Join(" ", curve?.Points.Select(p => $"{p[0]:0.##}y={p[1]}") ?? Array.Empty<string>())}");
        return 0;
    }

    /// <summary>`--selftest yolo`: runs the YOLO screen and prints every pick with its reasons.</summary>
    private static int RunYolo()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last = -10;
        var r = YoloScreen.RunAsync(ProviderRegistry.Yahoo, (pct, msg) =>
        {
            if (pct - last >= 10 || pct == 100) { last = pct; Console.WriteLine($"  [{pct,3}%] {msg}"); }
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"YOLO screen in {sw.Elapsed.TotalSeconds:0.0}s");
        if (Environment.GetEnvironmentVariable("FUNNEL_YOLO_JSON") is { Length: > 0 } dump)
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
            File.WriteAllText(dump, System.Text.Json.JsonSerializer.Serialize(r, opts));
            var back = System.Text.Json.JsonSerializer.Deserialize<YoloResult>(File.ReadAllText(dump), opts)!;
            Console.WriteLine($"  round-trip: {back.Picks.Count} picks, {back.Picks.Sum(p => p.Factors.Count)} factors, first {back.Picks[0].Symbol} L{back.Picks[0].Level}, cuts {back.LevelCuts.Length}, as of {back.AsOf:t}");
        }
        foreach (var g in r.Groups) Console.WriteLine($"  {g.Label,-10} matched {g.Matched}, passed filters {g.Screened}, reviewed {g.Reviewed}");
        foreach (var n in r.Notes) Console.WriteLine($"  note: {n}");
        Console.WriteLine("  levels: " + string.Join(" ", Enumerable.Range(1, 10).Select(l => $"{l}:{r.Picks.Count(p => p.Level == l)}")));
        foreach (var p in r.Picks.Take(int.TryParse(Environment.GetEnvironmentVariable("FUNNEL_YOLO_SHOW"), out var n2) ? n2 : 25))
        {
            Console.WriteLine($"  L{p.Level,-2} {p.Score,5:0.0}  {p.Symbol,-9} {p.GroupLabel,-9} {p.Industry,-32} {YoloScreen.Money(p.MarketCap, p.Currency),8}  {p.Headline}");
            foreach (var w in p.Why) Console.WriteLine($"          + {w}");
            foreach (var k in p.Risks) Console.WriteLine($"          - {k}");
        }
        return r.Picks.Count > 0 ? 0 : 1;
    }

    private static async Task<int> RunAsync(IFinanceProvider p, CompositeProvider composite, string symbol, DcfSettings settings)
    {
        var ct = CancellationToken.None;
        int failures = 0;
        Console.WriteLine($"Funnel {Bridge.AppVersion} self-test · {(p is CompositeProvider ? "configured sources" : p.Name)} · {symbol}");

        async Task<T?> Check<T>(string name, Func<Task<T>> f, Func<T, string> describe) where T : class
        {
            try
            {
                var r = await f();
                Console.WriteLine($"  PASS  {name,-22} {describe(r)}");
                return r;
            }
            catch (NotSupportedException ex)
            {
                Console.WriteLine($"  N/A   {name,-22} {(ex.Message.StartsWith("Specified") ? "not served by this source" : ex.Message)}");
                return null;
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  FAIL  {name,-22} {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        await Check("search", () => p.SearchAsync(symbol, ct), r => $"{r.Count} hits, first {r.FirstOrDefault()?.Symbol} ({r.FirstOrDefault()?.Name})");
        await Check("market board", () => p.MarketOverviewAsync(ct),
            r => $"{r.Count(q => q.Price != null)}/{r.Count} priced, S&P {r.FirstOrDefault()?.Price:0.00} ({r.FirstOrDefault()?.ChangePct:+0.00;-0.00}%)");
        foreach (var m in ProviderRegistry.Macro)
            await Check($"{m.Id} rates", () => m.BoardAsync(ct),
                r => string.Join("  ", r.Select(q => $"{q.Name} {q.Price:0.####}")) + $" (as of {r.FirstOrDefault()?.AsOf})");
        await Check("movers", () => p.MoversAsync("gainers", ct), r => $"{r.Count} gainers, top {r.FirstOrDefault()?.Symbol} {r.FirstOrDefault()?.ChangePct:+0.0}%");
        var co = await Check("company", () => p.CompanyAsync(symbol, ct),
            r => $"{r.Name} · {r.Price:0.00} {r.Currency} ({r.ChangePct:+0.00;-0.00}%) · {r.Metrics.Count(m => m.Value != null)}/{r.Metrics.Count} metrics · peers {string.Join(",", r.Peers)} [{r.PeersBasis}]");
        if (co != null)
        {
            var a = co.Analysts;
            var t = a.Trend.FirstOrDefault();
            Console.WriteLine($"        analysts: {a.RecommendationKey} mean {a.RecommendationMean:0.00}, n={a.AnalystCount}, " +
                $"SB/B/H/S/SS {t?.StrongBuy}/{t?.Buy}/{t?.Hold}/{t?.Sell}/{t?.StrongSell}, target {a.TargetLow}-{a.TargetMean:0.0}-{a.TargetHigh}, " +
                $"{a.Firms.Count} firms (buy {a.Firms.Count(f => f.Bucket == "Buy")}, hold {a.Firms.Count(f => f.Bucket == "Hold")}, sell {a.Firms.Count(f => f.Bucket == "Sell")})");
            Console.WriteLine($"        P/E {co.Metrics.GetValueOrDefault("trailingPE"):0.0}x  EV/EBITDA {co.Metrics.GetValueOrDefault("evToEbitda"):0.0}x  mkt cap {co.Metrics.GetValueOrDefault("marketCap") / 1e9:0.0}B");
        }
        if (p is IPeerSource ps)
            await Check("peers", () => ps.PeersAsync(symbol, ct), r => $"{string.Join(",", r.Symbols)} — {r.Basis} ({r.Source})");
        if (p is IFilingSource fs)
            await Check("filings", () => fs.FilingsAsync(symbol, ct), r => $"{r.Count} filings, latest {r.FirstOrDefault()?.Form} {r.FirstOrDefault()?.Filed:yyyy-MM-dd}");
        if (p is INewsSource ns)
            await Check("news", () => ns.NewsAsync(symbol, ct), r => $"{r.Count} stories, latest \"{r.FirstOrDefault()?.Headline}\"");
        foreach (var range in new[] { "1d", "1y", "max" })
            await Check($"chart {range}", () => p.ChartAsync(symbol, range, ct), r => $"{r.Points.Count} points ({r.Interval}), last {r.Points.LastOrDefault()?.C:0.00}");
        var comp = await Check("comp row", () => p.CompAsync("MSFT", ct), r => r.Error ?? $"{r.Name} EV/EBITDA {r.Metrics.GetValueOrDefault("evToEbitda"):0.0}x");

        var statements = new List<FinancialStatement>();
        foreach (var freq in new[] { "annual", "quarterly" })
            foreach (var kind in new[] { StatementKind.Income, StatementKind.Balance, StatementKind.CashFlow })
            {
                // Through the same extras the app adds, when testing one source on its own.
                var s = await Check($"{kind} {freq}", async () => StatementExtras.WithNwcChange(await p.StatementAsync(symbol, kind, freq, ct)),
                    r => $"{r.Lines.Count(l => l.Format != "header")} lines × {r.Periods.Count} periods [{string.Join(", ", r.Periods)}] {r.Currency} · {r.Source}");
                if (s != null) statements.Add(s);
                if (s != null && kind != StatementKind.CashFlow && (freq == "annual" || kind == StatementKind.Balance))
                    foreach (var key in kind == StatementKind.Income ? new[] { "TotalRevenue", "OperatingIncome", "NetIncome", "DilutedEPS" }
                                                                     : new[] { "CurrentAssets", "CurrentLiabilities", "NetWorkingCapital", "ChangeInNetWorkingCapital" })
                        Console.WriteLine($"        {key,-26} {string.Join("  ", s.Lines.FirstOrDefault(l => l.Key == key)?.Values.Select(v => v is { } x && Math.Abs(x) > 1000 ? (x / 1e6).ToString("#,0") : v?.ToString("0.00") ?? "–") ?? new[] { "missing" })}");
            }

        // ---- cross-checks: each statement against a second source, and the share count against the SEC cover.
        foreach (var st in statements)
        {
            var label = $"check {st.Kind} {(st.Frequency == "annual" ? "ann" : "qtr")}";
            await Check(label, async () => st.Check = await composite.CheckStatementAsync(symbol, st.Kind, st.Frequency, st, ct), c =>
                c.Status == "unavailable" ? $"not checked: {c.Note}"
                : $"{c.Compared} values vs {c.Against}: {c.Flags.Count(f => f.Severity == "severe")} severe, {c.Flags.Count(f => f.Severity == "warn")} warn" +
                  string.Concat(c.Flags.Select(f => $"\n          [{f.Severity}] {f.Message}")));
        }
        DataCheck? sharesCheck = null;
        if (co != null)
            sharesCheck = await Check("check shares", () => composite.CheckSharesAsync(co, ct), c =>
                c.Status == "unavailable" ? $"not checked: {c.Note}" : c.Flags.Count == 0 ? $"agrees with {c.Against}" : $"[{c.Flags[0].Severity}] {c.Flags[0].Message}");

        var prices = await Check("price history 5y", () => p.ChartAsync(symbol, "5y-daily", ct), r => $"{r.Points.Count} daily bars");
        var segments = await Check("revenue streams",
            () => p is CompositeProvider cp ? cp.RevenueSegmentsAsync(symbol, ct) : p is ISegmentSource ss ? ss.RevenueSegmentsAsync(symbol, ct) : throw new NotSupportedException(),
            r => $"by {r.Basis}, {r.Form} [{string.Join(", ", r.Periods)}]: " +
                 string.Join(" · ", r.Segments.Select(x => $"{x.Name} {x.Values.LastOrDefault() / 1e9:0.0}B")));

        // ---- DCF: defaults, the C# engine, and the same numbers out of the Excel formulas.
        DcfBase? mb = null; DcfAssumptions? model = null;
        var annual = statements.Where(s => s.Frequency == "annual").ToList();
        var income = annual.FirstOrDefault(s => s.Kind == StatementKind.Income);
        if (co != null && income != null)
        {
            await Check("dcf defaults", async () =>
            {
                var rf = await Bridge.RiskFreeAsync(ct);
                ConsensusEstimates? g = null;
                if (p is IEstimateSource est) { try { if (await est.ConsensusAsync(symbol, ct) is { } v) g = new ConsensusEstimates { Years = v.Years, Source = p.Name }; } catch (Exception ex) when (ex is ProviderException or NotSupportedException) { } }
                else g = await composite.ConsensusAsync(symbol, ct);
                (mb, model) = DcfDefaults.Build(co, income, annual.FirstOrDefault(s => s.Kind == StatementKind.Balance),
                    annual.FirstOrDefault(s => s.Kind == StatementKind.CashFlow), rf, null, settings, segments,
                    quarterlyIncome: statements.FirstOrDefault(s => s.Frequency == "quarterly" && s.Kind == StatementKind.Income),
                    impliedShares: await composite.ImpliedSharesAsync(co, ct), estimates: g);
                var r = DcfEngine.Compute(model, mb.Revenue.LastOrDefault(v => v != null) ?? 0);
                var streams = model.Streams.Count == 0 ? "one revenue line"
                    : string.Join(", ", model.Streams.Select(x => $"{x.Name} {x.Share:P0} g1 {x.Growth[0]:P1}"));
                return $"g1 {r.Revenue[0] / (mb.Revenue.LastOrDefault(v => v != null) ?? 1) - 1:P1} → {model.Growth[model.Years - 1]:P1} in year {model.Years}, margin {model.EbitMargin[0]:P1}, " +
                    $"WACC {r.Wacc:P2} (rf {model.Rf:P2} {rf?.Source}), EV {r.EnterpriseValue / 1e9:0.0}B → {r.SharePrice:0.00} vs {mb.Price:0.00}\n        streams: {streams}" +
                    $"\n        shares: {mb.BasicShares / 1e6:#,0.0}M basic + {mb.DilutiveShares / 1e6:#,0.0}M dilutive; implied {(mb.ImpliedShares ?? 0) / 1e6:#,0.0}M ({mb.ImpliedSharesSource}) → {model.Shares / 1e6:#,0.0}M used" +
                    (mb.SharesFlag is { } sf ? $"\n        {sf.Severity}: {sf.Message}" : "") +
                    (g != null ? $"\n        consensus ({g.Source}): " + string.Join(" · ", g.Years.Select(y => $"{y.Period} to {y.EndDate}: rev {y.RevenueAvg / 1e9:0.00}B ({y.RevenueGrowth:P1}), EPS {y.EpsAvg:0.00}")) : "") +
                    (mb.Fit is { } fit ? $"\n        DCF fit ({fit.Severity}, {fit.Kind}): {fit.Title}" : "") +
                    (mb.Calibration is { } cal ? $"\n        calibration: {cal.Summary}" +
                        $"\n          pull {cal.Pull:P0}, tilt {cal.Tilt:0.00}{(cal.LeversExhausted ? " (levers exhausted)" : "")}, industry weight {cal.IndustryWeight:P0}" +
                        string.Concat(cal.Steps.Select(st => $"\n          [{st.Stage}] {st.Label}: {st.From} → {st.To}  ({st.Why})")) +
                        string.Concat(cal.Caveats.Select(cv => $"\n          note: {cv}")) : "");
            }, r => r);
        }
        if (mb != null && model != null && co != null && income != null)
            await Check("valuation models", async () =>
            {
                var kit = await Valuation.BuildAsync(co, mb, model, income, annual.FirstOrDefault(s => s.Kind == StatementKind.Balance),
                    annual.FirstOrDefault(s => s.Kind == StatementKind.CashFlow), ct);
                var n = kit.Nav; var e = kit.Er;
                mb.Kit = kit;
                if (Environment.GetEnvironmentVariable("FUNNEL_MODEL_JSON") is { Length: > 0 } dumpTo)
                    File.WriteAllText(dumpTo, System.Text.Json.JsonSerializer.Serialize(new { @base = mb, defaults = model, saved = (object?)null },
                        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) } }));
                return $"recommended {kit.Recommended} ({kit.Industry}): {kit.RecommendedWhy}" +
                    string.Concat(kit.Fits.Select(f => $"\n        {f.Model,-4} {f.Level,-8} {f.Title}")) +
                    (n.Kind == "resource" ? $"\n        NAV: {n.Commodity} {n.Unit} spot {n.Spot:#,0.00} FY avg {n.FyAvgPrice:#,0.00}, production {n.Production:#,0}, AISC {n.Aisc:#,0.00}, r {n.DiscountRate:P0}{(n.PreProduction ? " (pre-production)" : "")}"
                     : n.Kind == "property" ? $"\n        NAV: NOI {n.Noi / 1e6:#,0}M at {n.CapRate:P2} ({n.PropertyType}), preferred {n.Preferred / 1e6:#,0}M" : "") +
                    $"\n        LBO: EBITDA {kit.Lbo.Ebitda / 1e6:#,0}M, {kit.Lbo.Leverage:0.0}x at {kit.Lbo.InterestRate:P2}" +
                    $"\n        ER: book {e.BookValue / 1e6:#,0}M, ROE latest {e.RoeLatest:P1} avg {e.RoeAverage:P1}, payout {e.Payout:P0}";
            }, r => r);

        var path = Path.Combine(Path.GetTempPath(), $"Funnel selftest {symbol}.xlsx");
        await Check("excel export", () =>
        {
            ExcelExport.Write(path, new ExportBundle
            {
                Symbol = symbol,
                Company = co,
                Statements = statements,
                Comps = comp != null ? new List<CompRow> { new() { Symbol = symbol, Name = co?.Name ?? symbol, Metrics = co?.Metrics ?? new() }, comp } : null,
                Prices = prices,
                Units = "millions",
                Source = income?.Source ?? p.Name,
                Model = model,
                ModelBase = mb,
                SharesFlag = sharesCheck?.Flags.FirstOrDefault(),
                // A football field to draw, as the Model tab would send it (FUNNEL_FOOTBALL_JSON = a file of it).
                Football = Environment.GetEnvironmentVariable("FUNNEL_FOOTBALL_JSON") is { Length: > 0 } fj
                    ? System.Text.Json.JsonSerializer.Deserialize<FootballData>(File.ReadAllText(fj), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    : null,
            });
            using var wb = new XLWorkbook(path);
            return Task.FromResult(string.Join(", ", wb.Worksheets.Select(w => $"{w.Name}({w.LastRowUsed()?.RowNumber()})")));
        }, r => $"{path} → {r}");

        if (File.Exists(path))
        {
            await Check("excel layout", () =>
            {
                using var wb = new XLWorkbook(path);
                foreach (var ws in wb.Worksheets)
                {
                    if (Math.Abs(ws.Column(1).Width - 3) > 0.01) throw new Exception($"{ws.Name}: column A width {ws.Column(1).Width}, expected 3");
                    if (ws.Column(1).CellsUsed().Any()) throw new Exception($"{ws.Name}: gutter column A is not empty");
                    if (ws.SheetView.SplitRow > 0 || ws.SheetView.SplitColumn > 0) throw new Exception($"{ws.Name}: panes are frozen");
                }
                return Task.FromResult($"{wb.Worksheets.Count} sheets indented, none frozen");
            }, r => r);
        }

        if (File.Exists(path))
        {
            await Check("excel flags", () =>
            {
                // Every flag raised must land on a cell with a note, in the sheet for its statement.
                var expected = statements.Sum(st => st.Check?.Flags.Count ?? 0) + (sharesCheck?.Flags.Count ?? 0);
                using var wb = new XLWorkbook(path);
                var noted = wb.Worksheets.Sum(w => w.CellsUsed(c => c.HasComment).Count());
                if (noted != expected) throw new Exception($"{expected} flags but {noted} cells carry a note");
                return Task.FromResult($"{noted} flagged cells carry a note");
            }, r => r);
        }

        if (model != null && mb != null && File.Exists(path))
        {
            await Check("model formulas", () =>
            {
                var expect = DcfEngine.Compute(model, mb.Revenue.LastOrDefault(v => v != null) ?? 0).SharePrice;
                using var wb = new XLWorkbook(path);
                wb.RecalculateAllFormulas();
                var got = wb.Worksheet("DCF").Cell("C44").Value.GetNumber();
                if (Math.Abs(got - expect) > 0.01) throw new Exception($"Excel model says {got:0.0000}, engine says {expect:0.0000}");
                // Bump WACC via the risk-free input: the price should fall.
                wb.Worksheet("Assumptions").Cell("C14").Value = model.Rf + 0.01;
                wb.RecalculateAllFormulas();
                var bumped = wb.Worksheet("DCF").Cell("C44").Value.GetNumber();
                if (!(bumped < got)) throw new Exception($"Raising the risk-free rate didn't lower the price ({got:0.00} → {bumped:0.00})");
                return Task.FromResult($"Excel {got:0.00} = engine {expect:0.00}; rf +1pp → {bumped:0.00}");
            }, r => r);
        }

        // The revenue-stream workbook: made-up streams when the filings gave none, so the formulas are always checked.
        if (model != null && mb != null && co != null)
        {
            await Check("stream formulas", () =>
            {
                var sm = System.Text.Json.JsonSerializer.Deserialize<DcfAssumptions>(System.Text.Json.JsonSerializer.Serialize(model))!.Normalised();
                if (sm.Streams.Count == 0)
                    sm.Streams = new()
                    {
                        new() { Name = "Test A", Share = 0.62, Growth = Enumerable.Repeat(0.09, DcfAssumptions.MaxYears).ToArray() },
                        new() { Name = "Test B", Share = 0.41, Growth = Enumerable.Repeat(-0.02, DcfAssumptions.MaxYears).ToArray() },
                    };
                var baseRev = mb.Revenue.LastOrDefault(v => v != null) ?? 0;
                var expect = DcfEngine.Compute(sm, baseRev);
                var spath = Path.Combine(Path.GetTempPath(), $"Funnel selftest {symbol} streams.xlsx");
                ExcelExport.Write(spath, new ExportBundle { Symbol = symbol, Company = co, IncludeAnalysts = false, Statements = annual, Units = "millions", Source = income?.Source ?? p.Name, Model = sm, ModelBase = mb });
                using var wb = new XLWorkbook(spath);
                wb.RecalculateAllFormulas();
                var got = wb.Worksheet("DCF").Cell("C44").Value.GetNumber();
                if (Math.Abs(got - expect.SharePrice) > 0.01) throw new Exception($"Excel model says {got:0.0000}, engine says {expect.SharePrice:0.0000}");
                return Task.FromResult($"{sm.Streams.Count} streams · Excel {got:0.00} = engine {expect.SharePrice:0.00} · year-1 revenue {expect.Revenue[0] / 1e9:0.00}B");
            }, r => r);
        }

        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
