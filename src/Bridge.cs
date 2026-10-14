using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Funnel.Engine;
using Funnel.Providers.Bonds;
using Funnel.Providers;

namespace Funnel;

/// <summary>
/// Routes JSON messages between the web UI (JS) and the C# engine.
/// JS -> C#: { cmd, reqId, payload }.  C# -> JS: response / event objects.
/// Network work runs on the thread pool; dialogs run on the UI thread.
/// </summary>
public sealed class Bridge
{
    public const string AppVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IAppWindow _window;
    private readonly AppConfig _config;

    // One set of data sources (and their caches) for every window.
    private static CompositeProvider? s_provider;
    private readonly CompositeProvider _provider;
    private IFinanceProvider Provider => _provider;

    public Bridge(IAppWindow window, AppConfig config)
    {
        _window = window; _config = config;
        if (s_provider == null) ProviderRegistry.Configure(config);
        // A source falling back to another is said in the window the user is looking at.
        _provider = s_provider ??= new CompositeProvider(config, msg => (AppWindows.Active?.Bridge ?? this).Emit("notice", new { message = msg }));
    }

    /// <summary>A dragged-out tab's page (JSON), which this window opens on; null for the first window.</summary>
    public string? Seed { get; init; }
    public int WindowId { get; init; }

    // ---------- outbound ----------
    private void PostToJs(object obj)
    {
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        if (_window.IsClosed) return;
        try { _window.PostJson(json); }
        catch { /* window closing */ }
    }

    private void Respond(int reqId, object? result) =>
        PostToJs(new { type = "response", reqId, ok = true, result });

    private void Fail(int reqId, string error) =>
        PostToJs(new { type = "response", reqId, ok = false, error });

    private void Emit(string name, object data) =>
        PostToJs(new { type = "event", name, data });

    /// <summary>Send the page an event from outside the Bridge (the window's loading screen).</summary>
    public void Notify(string name, object data) => Emit(name, data);

    /// <summary>The page reporting a startup step to the window's loading screen. Called on the UI thread.</summary>
    public Action<string>? LoadStep { get; init; }

    private void Progress(int percent, string message) =>
        Emit("progress", new { percent = Math.Clamp(percent, 0, 100), message });

    /// <summary>Run a command off the UI thread and answer the request when it finishes.</summary>
    private void Run(int reqId, Func<Task<object?>> work)
    {
        _ = Task.Run(async () =>
        {
            try { Respond(reqId, await work()); }
            catch (Exception ex) { Fail(reqId, Friendly(ex)); }
        });
    }

    private string Friendly(Exception ex) => ex switch
    {
        ProviderException => ex.Message,
        TaskCanceledException => "The data source took too long to answer. Try again.",
        HttpRequestException => $"Couldn't reach the data source. Check your internet connection. ({ex.Message})",
        IOException when ex.Message.Contains("being used by another process") =>
            "That file is open in Excel. Close it and export again.",
        NotSupportedException => ex.Message.StartsWith("Specified method") ? "The selected source doesn't provide this data." : ex.Message,
        _ => ex.Message,
    };

    // ---------- inbound ----------
    public void Handle(string raw)
    {
        int reqId = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var cmd = root.GetProperty("cmd").GetString() ?? "";
            reqId = root.TryGetProperty("reqId", out var rid) ? rid.GetInt32() : 0;
            // Clone: the payload outlives this JsonDocument once the work moves to the thread pool.
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

            switch (cmd)
            {
                case "init":
                    Respond(reqId, BuildInitWindow());
#if WINDOWS
                    if (_window is MainForm { WindowKind: MainForm.Kind.Spare } sp) AppWindows.SpareLoaded(sp);
                    else AppWindows.WantSpare(6000);   // a window ready for the first tab dragged out
#endif
                    break;
                case "strip":
#if WINDOWS
                    if (_window is MainForm sf) sf.StripCss = new System.Drawing.RectangleF(Num(payload, "x"), Num(payload, "y"), Num(payload, "w"), Num(payload, "h"));
#endif
                    Respond(reqId, null);
                    break;
                case "loadStep": LoadStep?.Invoke(Str(payload, "id")); Respond(reqId, null); break;
                case "search": Run(reqId, async () => await Provider.SearchAsync(Str(payload, "q"), CancellationToken.None)); break;
                case "market": Run(reqId, Market); break;
                case "company": Run(reqId, () => Company(Str(payload, "symbol"))); break;
                case "quote": Run(reqId, () => Quote(Str(payload, "symbol"))); break;
                case "chart": Run(reqId, async () => await Provider.ChartAsync(Str(payload, "symbol"), Str(payload, "range"), CancellationToken.None)); break;
                case "comps": Run(reqId, () => Comps(Str(payload, "symbol"), StrList(payload, "symbols"), Str(payload, "save"))); break;
                case "statement": Run(reqId, () => Statement(Str(payload, "symbol"), Str(payload, "kind"), Str(payload, "frequency"))); break;
                case "export": Run(reqId, () => Export(payload)); break;
                case "savePrefs": SavePrefs(reqId, payload); break;
                case "saveSources": SaveSources(reqId, payload); break;
                case "saveDcfSettings": SaveDcfSettings(reqId, payload); break;
                case "saveKey": SaveKey(reqId, payload); break;
                case "testKey": Run(reqId, () => TestKey(Str(payload, "provider"))); break;
                case "filings": Run(reqId, async () => await _provider.FilingsAsync(Str(payload, "symbol"), CancellationToken.None)); break;
                case "news": Run(reqId, async () => await _provider.NewsAsync(Str(payload, "symbol"), CancellationToken.None)); break;
                case "model": Run(reqId, () => Model(Str(payload, "symbol"))); break;
                case "saveModel": SaveModel(reqId, payload); break;
                case "savedList": _config.PruneExpired(); Respond(reqId, SavedList()); break;
                case "forgetSaved": ForgetSaved(reqId, payload); break;
                case "saveKeepSaved": SaveKeepSaved(reqId, payload); break;
                case "newDay": NewDay(reqId); break;
                case "exportModel": Run(reqId, () => ExportModel(payload)); break;
                case "openPath": OpenPath(reqId, Str(payload, "path"), reveal: false); break;
                case "revealPath": OpenPath(reqId, Str(payload, "path"), reveal: true); break;
                case "debt": Run(reqId, () => Debt(Str(payload, "symbol"), StrList(payload, "issuers"), payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("issuers", out var di) && di.ValueKind == JsonValueKind.Array)); break;
                case "yoloScore": Run(reqId, () => YoloScore(Str(payload, "symbol"))); break;
                case "yolo": Run(reqId, () => Yolo(payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("refresh", out var rf) && rf.ValueKind == JsonValueKind.True)); break;
                case "openUrl": OpenExternal(Str(payload, "url")); Respond(reqId, null); break;
                case "winTitle": _window.SetTitle(Str(payload, "title") is { Length: > 0 } wt ? $"{wt} — Funnel" : $"Funnel  v{AppVersion}"); Respond(reqId, null); break;
#if WINDOWS
                case "tabDrag":
                    if (_window is MainForm from)
                        AppWindows.DragStarted(this, from, Str(payload, "id"), Str(payload, "tab"), Str(payload, "title"), Str(payload, "sub"),
                            new System.Drawing.PointF(Num(payload, "gx"), Num(payload, "gy")), Bool(payload, "single"));
                    Respond(reqId, null);
                    break;
                case "tabDragEnd": AppWindows.Ended(this, Str(payload, "id"), Bool(payload, "dropped")); Respond(reqId, null); break;
                case "tabTake":
                    var took = AppWindows.Take(this, Str(payload, "id"));
                    if (took != null) _window.Raise();   // the window a tab was dropped on comes to the front
                    Respond(reqId, new { tab = took });
                    break;
#else
                // One window on macOS: a tab dragged off the strip stays in it.
                case "tabDrag": case "tabDragEnd": Respond(reqId, null); break;
                case "tabTake": Respond(reqId, new { tab = (string?)null }); break;
#endif
                case "closeWindow": _window.CloseWindow(); Respond(reqId, null); break;
                default: Fail(reqId, $"Unknown command: {cmd}"); break;
            }
        }
        catch (Exception ex) { Fail(reqId, ex.Message); }
    }

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static float Num(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0;

    private static int Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : 0;

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> StrList(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                .Where(s => s.Length > 0).ToList()
            : new List<string>();

    private static StatementKind ParseKind(string k) => k switch
    {
        "balance" => StatementKind.Balance,
        "cashflow" or "cashFlow" => StatementKind.CashFlow,
        _ => StatementKind.Income,
    };

    // ---------- commands ----------

    private object BuildInit() => new
    {
        version = AppVersion,
        sources = SourcesInfo(),
        metrics = Metrics.All,
        compKeys = Metrics.CompKeys,
        recent = _config.Recent,
        units = _config.Units,
        frequency = _config.Frequency,
        exportDir = _config.ResolvedExportDir(),
        dcf = _config.Dcf,
        dcfDefaults = new DcfSettings(),
        keepSavedDays = _config.KeepSavedDays,
        keepSavedChoices = AppConfig.KeepSavedChoices,
        savedCount = _config.Models.Keys.Union(_config.CompSets.Keys, StringComparer.OrdinalIgnoreCase).Count(),
    };

    // What the page starts on: the settings, plus the tab it was dragged out as (if it was).
    private object BuildInitWindow() => new { init = BuildInit(), seed = Seed, windowId = WindowId };

    /// <summary>The spare window takes on a dragged tab (with today's settings: it's been waiting).</summary>
    public void Adopt(string tab, string hosting) => Emit("adopt", new { tab, hosting, init = BuildInit() });

    /// <summary>Settings saved in one window reach the others; reload = the sources changed, so data has to be fetched again.</summary>
    private void Changed(bool reload = false) => AppWindows.Broadcast(this, "initChanged", new { init = BuildInit(), reload });

    /// <summary>The board and the three mover lists, each allowed to fail on its own.</summary>
    private async Task<object?> Market()
    {
        var p = Provider;
        var ct = CancellationToken.None;
        async Task<object> Safe<T>(Func<Task<T>> f)
        {
            try { return new { ok = true, data = (object?)await f() }; }
            catch (Exception ex) { return new { ok = false, error = Friendly(ex) }; }
        }
        var board = Safe(() => p.MarketOverviewAsync(ct));
        var macro = Safe(() => _provider.MacroBoardAsync(ct));
        var gainers = Safe(() => p.MoversAsync("gainers", ct));
        var losers = Safe(() => p.MoversAsync("losers", ct));
        var actives = Safe(() => p.MoversAsync("actives", ct));
        await Task.WhenAll(board, macro, gainers, losers, actives);
        return new
        {
            board = board.Result, macro = macro.Result, gainers = gainers.Result, losers = losers.Result, actives = actives.Result,
            asOf = DateTime.Now, source = _provider.SourceFor(Section.Market).Name,
        };
    }

    private async Task<object?> Company(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ProviderException("Enter a ticker.");
        var co = await Provider.CompanyAsync(symbol, CancellationToken.None);
        if (_config.PruneExpired()) _config.Save();
        _config.Touch(co.Symbol);
        Background(async () => Emit("companyCheck", new { symbol = co.Symbol, check = await _provider.CheckSharesAsync(co, CancellationToken.None) }));
        // A comp set the user built for this ticker beats the provider's suggestion.
        var compSet = _config.CompSets.TryGetValue(co.Symbol, out var saved) && saved.Count > 0
            ? saved : co.Peers.Take(6).ToList();
        return new { company = co, compSet, recent = _config.Recent };
    }

    // ---------- YOLO mode ----------
    // The screen takes a few seconds and a few hundred requests, so it runs once a day: the result
    // is kept in memory and in yolo.json, and only "Refresh" (or a new day) runs it again. Two
    // callers asking at once share one run.

    private static YoloResult? _yolo;
    private static Task<YoloResult>? _yoloRun;
    private static readonly object _yoloLock = new();
    private static string YoloFile => Path.Combine(AppPaths.Home, "yolo.json");

    private async Task<object?> Yolo(bool refresh)
    {
        if (!ProviderRegistry.YahooEnabled)
            throw new ProviderException("YOLO mode screens the market with Yahoo Finance, which is switched off in Settings → Data sources.");
        if (!refresh)
        {
            _yolo ??= LoadYolo();
            // A run that came back incomplete is retried after ten minutes rather than kept all day.
            if (_yolo != null && _yolo.AsOf.Date == DateTime.Today && !(_yolo.Partial && DateTime.Now - _yolo.AsOf > TimeSpan.FromMinutes(10)))
                return _yolo;
        }
        Task<YoloResult> run;
        lock (_yoloLock) run = _yoloRun ??= RunYolo();
        try { return await run; }
        finally { lock (_yoloLock) if (_yoloRun == run && run.IsCompleted) _yoloRun = null; }
    }

    private async Task<YoloResult> RunYolo()
    {
        var r = await YoloScreen.RunAsync(ProviderRegistry.Yahoo,
            (pct, msg) => AppWindows.Broadcast(null, "yoloProgress", new { percent = pct, message = msg }), CancellationToken.None);
        _yolo = r;
        if (!r.Partial)
            try { File.WriteAllText(YoloFile, JsonSerializer.Serialize(r, JsonOpts)); } catch { /* next start just runs it again */ }
        return r;
    }

    // ---------- Debt tab ----------
    // The company's bonds trading in the US (FINRA TRACE: last price and yield of each), the face
    // amounts its 10-K gives for each note (to weight them), and the Treasury curve (for spreads).
    // The averages are worked out in the page, so excluding a bond or switching weights is instant.
    private static readonly FinraBondSource _finra = new();

    private async Task<object?> Debt(string symbol, List<string> picked, bool pickedGiven)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ProviderException("Pick a company first.");
        var co = await Provider.CompanyAsync(symbol, CancellationToken.None);
        var notes = new List<string>();

        // Issuers FINRA files this company's bonds under.
        var prefix = DebtProfile.Prefix(co.Name);
        var issuers = await _finra.IssuersAsync(prefix, CancellationToken.None);
        if (issuers.Count == 0 && prefix.Contains(' ')) issuers = await _finra.IssuersAsync(prefix.Split(' ')[0], CancellationToken.None);
        var chosen = pickedGiven ? picked.Where(p => issuers.Any(i => i.Name == p)).ToList()
            : issuers.Where(i => DebtProfile.Matches(co.Name, i.Name)).Select(i => i.Name).ToList();

        var curveTask = ProviderRegistry.Treasury.CurvePointsAsync(CancellationToken.None);
        var edgar = ProviderRegistry.All.OfType<Providers.Edgar.EdgarProvider>().FirstOrDefault();
        var notesTask = edgar != null && chosen.Count > 0 ? edgar.DebtInstrumentsAsync(co.Symbol, CancellationToken.None) : null;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var bonds = new List<FinraBond>();
        foreach (var name in chosen)
            bonds.AddRange((await _finra.BondsAsync(name, CancellationToken.None)).Where(b => b.Maturity == null || string.CompareOrdinal(b.Maturity, today) > 0));
        bonds = bonds.GroupBy(b => b.Cusip).Select(g => g.First()).OrderBy(b => b.Maturity ?? "9999").ToList();

        Dictionary<string, (double Face, string Note)> faces = new();
        string? facesForm = null;
        if (notesTask != null)
        {
            try
            {
                var (instruments, form) = await notesTask;
                faces = DebtProfile.MatchFaces(bonds, instruments);
                facesForm = form;
                if (instruments.Count == 0) notes.Add($"The {form} doesn't tag its notes one by one, so bonds can't be weighted by amount.");
            }
            catch (Exception ex) { notes.Add("Face amounts from the 10-K aren't available: " + Friendly(ex)); }
        }
        object? curve = null;
        try { if (await curveTask is { } c) curve = new { date = c.Date, points = c.Points }; }
        catch (Exception ex) { notes.Add("No Treasury curve for spreads: " + Friendly(ex)); }

        return new
        {
            symbol = co.Symbol, name = co.Name, currency = co.Currency, asOf = DateTime.Now,
            issuers = issuers.Select(i => new { name = i.Name, bonds = i.Bonds, selected = chosen.Contains(i.Name) }),
            bonds = bonds.Select(b => new
            {
                b.Cusip, b.Symbol, b.Issuer, b.Coupon, b.CouponType, b.Maturity, b.Price, b.Yield, b.LastTrade, b.PriceChangePct,
                b.Moodys, b.SandP, b.Grade,
                face = faces.TryGetValue(b.Cusip, out var f) ? f.Face : (double?)null,
                faceNote = faces.TryGetValue(b.Cusip, out var f2) ? f2.Note : null,
            }),
            facesForm, curve, notes, source = "FINRA TRACE",
        };
    }

    // One company through the YOLO review, for its YOLO tab: today's board if it's there, else
    // scored on its own (kept for the day).
    private static readonly Dictionary<string, YoloSingle> _yoloOne = new(StringComparer.OrdinalIgnoreCase);
    private async Task<object?> YoloScore(string symbol)
    {
        if (!ProviderRegistry.YahooEnabled)
            throw new ProviderException("YOLO mode scores companies with Yahoo Finance, which is switched off in Settings → Data sources.");
        _yolo ??= LoadYolo();
        if (_yolo != null && _yolo.AsOf.Date == DateTime.Today && _yolo.Picks.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) is { } on)
            return new YoloSingle { Pick = on, OnBoard = true, AsOf = _yolo.AsOf };
        lock (_yoloOne)
            if (_yoloOne.TryGetValue(symbol, out var hit) && hit.AsOf.Date == DateTime.Today) return hit;
        var r = await YoloScreen.ScoreOneAsync(ProviderRegistry.Yahoo, symbol, CancellationToken.None);
        lock (_yoloOne) _yoloOne[symbol] = r;
        return r;
    }

    private static YoloResult? LoadYolo()
    {
        try { return File.Exists(YoloFile) ? JsonSerializer.Deserialize<YoloResult>(File.ReadAllText(YoloFile), JsonOpts) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Just the live price of the open company, for the once-a-minute refresh. Everything else in
    /// the snapshot comes out of the response caches, so this costs one quote request.
    /// </summary>
    private async Task<object?> Quote(string symbol)
    {
        var co = await Provider.CompanyAsync(symbol, CancellationToken.None);
        return new { symbol = co.Symbol, price = co.Price, change = co.Change, changePct = co.ChangePct, marketState = co.MarketState };
    }

    // ---------- cross-checks ----------
    // Figures are checked against a second source in the background, so nothing waits on it; the
    // UI paints the result when it lands. Exports wait, since the workbook has to carry the flags.

    private static void Background(Func<Task> work) =>
        _ = Task.Run(async () => { try { await work(); } catch { /* a check that can't run just doesn't show */ } });

    private static string KindId(StatementKind k) => k switch { StatementKind.Balance => "balance", StatementKind.CashFlow => "cashflow", _ => "income" };

    private async Task<object?> Statement(string symbol, string kind, string frequency)
    {
        var st = await Provider.StatementAsync(symbol, ParseKind(kind), frequency, CancellationToken.None);
        Background(async () => Emit("statementCheck", new
        {
            symbol, kind, frequency,
            check = await _provider.CheckStatementAsync(symbol, st.Kind, st.Frequency, st, CancellationToken.None),
        }));
        return st;
    }

    /// <summary>Checks every statement that hasn't been, in parallel.</summary>
    private Task CheckAll(string symbol, IEnumerable<FinancialStatement> statements) =>
        Task.WhenAll(statements.Where(s => s.Check == null).Select(async s =>
            s.Check = await _provider.CheckStatementAsync(symbol, s.Kind, s.Frequency, s, CancellationToken.None)));

    /// <summary>The model's flags: every statement it reads plus the share count it divides by.</summary>
    private async Task<List<DataFlag>> ModelFlags(ModelInputs m)
    {
        var shares = _provider.CheckSharesAsync(m.Company, CancellationToken.None);
        await CheckAll(m.Base.Symbol, m.Statements);
        return m.Statements.SelectMany(s => (s.Check?.Flags ?? new()).Select(f => f with { Key = $"{KindId(s.Kind)}:{f.Key}" }))
            .Concat((await shares).Flags).ToList();
    }

    private async Task<List<CompRow>> FetchComps(string symbol, IEnumerable<string> peers)
    {
        var all = new[] { symbol }.Concat(peers)
            .Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0)
            .Distinct().Take(16).ToList();
        // A handful at a time — fanning out sixteen at once is how you meet a rate limiter.
        using var gate = new SemaphoreSlim(4);
        var tasks = all.Select(async s =>
        {
            await gate.WaitAsync();
            try { return await Provider.CompAsync(s, CancellationToken.None); }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <param name="save">"set" when the user edited the peers, "clear" when they went back to the
    /// suggested ones; anything else just fetches and leaves what's saved alone.</param>
    private async Task<object?> Comps(string symbol, List<string> peers, string save)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (save == "set") _config.SetCompSet(symbol, peers.Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList());
        else if (save == "clear") _config.SetCompSet(symbol, null);
        return await FetchComps(symbol, peers);
    }

    // ---------- saved inputs ----------
    // Edited DCF assumptions and comp sets are kept per company for KeepSavedDays after their last
    // change (Settings → Saved data), then dropped so the company reopens on fresh numbers.

    private object SavedList()
    {
        object? Item(string kind, string label, Dictionary<string, DateTime> at, string sym)
        {
            if (!at.TryGetValue(sym, out var t)) return null;
            return new { kind, label, savedAt = t, expiresAt = _config.ExpiresAt(t) };
        }
        var symbols = _config.Models.Keys.Union(_config.CompSets.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.ToUpperInvariant()).Distinct().OrderBy(s => s);
        return new
        {
            keepSavedDays = _config.KeepSavedDays,
            companies = symbols.Select(sym => new
            {
                symbol = sym,
                items = new[]
                {
                    _config.Models.ContainsKey(sym) ? Item("model", "DCF assumptions", _config.ModelsSavedAt, sym) : null,
                    _config.CompSets.TryGetValue(sym, out var peers) ? Item("comps", $"Comp set ({peers.Count} peers)", _config.CompSetsSavedAt, sym) : null,
                }.Where(x => x != null),
            }).ToList(),
        };
    }

    private void ForgetSaved(int reqId, JsonElement p)
    {
        var all = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
        _config.ForgetCompanies(all ? null : StrList(p, "symbols").Select(s => s.Trim().ToUpperInvariant()));
        Respond(reqId, new { list = SavedList(), init = BuildInit() });
        Changed();
    }

    private void SaveKeepSaved(int reqId, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("days", out var d) || !d.TryGetInt32(out var days)
            || !AppConfig.KeepSavedChoices.Contains(days)) { Fail(reqId, "Pick one of the listed periods."); return; }
        _config.KeepSavedDays = days;
        _config.PruneExpired();
        _config.Save();
        Respond(reqId, BuildInit());
        Changed();
    }

    /// <summary>
    /// The UI calls this when the local date rolls over. Cached responses already go stale at
    /// midnight (see HttpSource / YahooSession); this drops inputs that expired overnight.
    /// </summary>
    private void NewDay(int reqId)
    {
        if (_config.PruneExpired()) _config.Save();
        Respond(reqId, BuildInit());
    }

    private void SavePrefs(int reqId, JsonElement p)
    {
        if (Str(p, "units") is { Length: > 0 } u) _config.Units = u;
        if (Str(p, "frequency") is { Length: > 0 } f) _config.Frequency = f;
        _config.Save();
        Respond(reqId, BuildInit());
        Changed();
    }

    /// <summary>
    /// Ask where to save, then pull everything the user ticked and write one workbook.
    /// The path is asked for first so nobody waits on the download only to be asked a question.
    /// </summary>
    private async Task<object?> Export(JsonElement p)
    {
        var symbol = Str(p, "symbol").Trim().ToUpperInvariant();
        if (symbol.Length == 0) throw new ProviderException("Pick a company first.");
        var kinds = StrList(p, "statements").Select(ParseKind).Distinct().ToList();
        var freq = Str(p, "frequency") is { Length: > 0 } fq ? fq : "annual";
        var units = Str(p, "units") is { Length: > 0 } un ? un : "millions";
        var extras = StrList(p, "extras").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var peers = StrList(p, "comps");
        if (kinds.Count == 0 && extras.Count == 0) throw new ProviderException("Tick at least one thing to export.");

        var path = AskSavePath($"{symbol} {(extras.Contains("model") ? "model" : "financials")} {DateTime.Now:yyyy-MM-dd}.xlsx");
        if (path == null) return new { cancelled = true };
        _config.ExportDir = Path.GetDirectoryName(path) ?? "";
        _config.Units = units;
        _config.Frequency = freq;
        _config.Save();

        var ct = CancellationToken.None;
        var prov = Provider;
        var freqs = freq == "both" ? new[] { "annual", "quarterly" } : new[] { freq };

        Progress(5, $"Pulling {symbol}…");
        var wantModel = extras.Contains("model");
        var needCompany = extras.Contains("summary") || extras.Contains("analysts");
        var modelTask = wantModel ? ModelInputsAsync(symbol, ct) : Task.FromResult<ModelInputs>(null!);
        var companyTask = needCompany ? prov.CompanyAsync(symbol, ct) : Task.FromResult<CompanySnapshot>(null!);
        var statementTasks = freqs.SelectMany(f => kinds.Select(k => prov.StatementAsync(symbol, k, f, ct))).ToList();
        var compsTask = extras.Contains("comps") ? FetchComps(symbol, peers) : Task.FromResult<List<CompRow>>(null!);
        var pricesTask = extras.Contains("prices") ? prov.ChartAsync(symbol, "5y-daily", ct) : Task.FromResult<PriceSeries>(null!);

        // Report progress as each piece lands rather than one jump at the end. Failures are
        // inspected task-by-task below, so one missing statement doesn't sink the whole file.
        var all = new List<Task> { companyTask, compsTask, pricesTask, modelTask };
        all.AddRange(statementTasks);
        int done = 0;
        async Task Track(Task t)
        {
            try { await t; } catch { }
            var n = Interlocked.Increment(ref done);
            Progress(5 + 80 * n / all.Count, $"Downloaded {n} of {all.Count}…");
        }
        await Task.WhenAll(all.Select(Track));

        var statements = new List<FinancialStatement>();
        var skipped = new List<string>();
        foreach (var t in statementTasks)
        {
            if (t.IsCompletedSuccessfully) statements.Add(t.Result);
            else skipped.Add(t.Exception?.InnerException?.Message ?? "A statement could not be pulled.");
        }
        CompanySnapshot? company = null;
        if (needCompany)
        {
            if (companyTask.IsCompletedSuccessfully) company = companyTask.Result;
            else skipped.Add("Summary / analysts: " + Friendly(companyTask.Exception!.InnerException!));
        }
        List<CompRow>? comps = compsTask.IsCompletedSuccessfully ? compsTask.Result : null;
        PriceSeries? prices = pricesTask.IsCompletedSuccessfully ? pricesTask.Result : null;
        if (extras.Contains("prices") && prices == null) skipped.Add("Price history: " + Friendly(pricesTask.Exception!.InnerException!));
        ModelInputs? model = null;
        if (wantModel)
        {
            if (modelTask.IsCompletedSuccessfully)
            {
                model = modelTask.Result;
                // The model links into the annual statements, so make sure each one is in the file.
                foreach (var st in model.Statements)
                    if (!statements.Any(x => x.Kind == st.Kind && x.Frequency == "annual")) statements.Add(st);
            }
            else skipped.Add("DCF model: " + Friendly(modelTask.Exception!.InnerException!));
        }

        var bundle = new ExportBundle
        {
            Symbol = symbol,
            Company = company,
            // Both sheets come from the one company snapshot; each is written only if ticked.
            IncludeSummary = extras.Contains("summary"),
            IncludeAnalysts = extras.Contains("analysts"),
            Statements = statements,
            Comps = comps,
            Prices = prices,
            Units = units,
            Source = statements.FirstOrDefault()?.Source is { Length: > 0 } ss ? ss : _provider.SourceFor(Section.Statements).Name,
            CompsSource = _provider.SourceFor(Section.Comps).Name,
            PricesSource = _provider.SourceFor(Section.Chart).Name,
            Model = model == null ? null : ParseAssumptions(p) ?? SavedModel(model) ?? model.Defaults,
            ModelBase = model?.Base,
            Football = model == null ? null : ParseFootball(p),
        };

        if (statements.Count == 0 && company == null && comps == null && prices == null)
            throw new ProviderException(skipped.FirstOrDefault() ?? "Nothing could be pulled for that export.");

        Progress(88, "Cross-checking figures…");
        await CheckAll(symbol, statements);
        var flags = model != null ? await ModelFlags(model) : null;
        bundle = bundle with { SharesFlag = model != null ? SharesFlagFor(model, flags) : null };
        var severe = statements.Sum(s => s.Check?.Flags.Count(f => f.Severity == "severe") ?? 0) + (bundle.SharesFlag?.Severity == "severe" ? 1 : 0);
        if (severe > 0) skipped.Add($"{severe} figure{(severe == 1 ? "" : "s")} disagree sharply with a second source; they're highlighted in red in the workbook, with a note on each.");

        Progress(92, "Writing workbook…");
        ExcelExport.Write(path, bundle);
        Progress(100, "Saved.");
        return new { path, skipped };
    }

    // ---------- sources ----------

    private object SourcesInfo() => new
    {
        // Sections nobody serves yet are left out. Options list Yahoo even when it's off, so the
        // panel can switch it back on without a round trip; the registry ignores it while off.
        sections = Section.All.Where(sec => ProviderRegistry.Everything.Any(p => p.Sections.Contains(sec.Id))).Select(sec => new
        {
            id = sec.Id,
            label = sec.Label,
            selected = _provider.SourceFor(sec.Id).Id,
            options = ProviderRegistry.Everything.Where(p => p.Sections.Contains(sec.Id)).Select(p => new { id = p.Id, name = p.Name }),
        }),
        providers = ProviderRegistry.Everything.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            keyed = p is IKeyedSource,
            hasKey = p is IKeyedSource && !string.IsNullOrEmpty(_config.GetKey(p.Id)),
            keyUrl = (p as IKeyedSource)?.KeyUrl,
            keyNote = (p as IKeyedSource)?.KeyNote,
        }),
        contactEmail = _config.ContactEmail,
        macroOnBoard = _config.MacroOnBoard,
        useYahoo = _config.UseYahoo,
    };

    private void SaveSources(int reqId, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) { Fail(reqId, "Nothing to save."); return; }
        // First, so the per-section picks below are checked against the right list of sources.
        if (p.TryGetProperty("useYahoo", out var uy) && uy.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _config.UseYahoo = uy.GetBoolean();
        if (p.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Object)
            foreach (var kv in src.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String && Section.All.Any(x => x.Id == kv.Name))
                    _config.Sources[kv.Name] = ProviderRegistry.For(kv.Name, kv.Value.GetString()).Id;
        if (p.TryGetProperty("contactEmail", out var em) && em.ValueKind == JsonValueKind.String)
            _config.ContactEmail = em.GetString()!.Trim();
        if (p.TryGetProperty("macroOnBoard", out var mb) && mb.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _config.MacroOnBoard = mb.GetBoolean();
        _config.Save();
        Respond(reqId, BuildInit());
        Changed(reload: true);
    }

    /// <summary>Settings → DCF assumptions. Only seeds new models; edited ones keep their numbers.</summary>
    private void SaveDcfSettings(int reqId, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) { Fail(reqId, "Nothing to save."); return; }
        try { _config.Dcf = (p.Deserialize<DcfSettings>(JsonOpts) ?? new DcfSettings()).Normalised(); }
        catch (JsonException) { Fail(reqId, "Those assumptions couldn't be read."); return; }
        _config.Save();
        Respond(reqId, BuildInit());
        Changed();
    }

    private void SaveKey(int reqId, JsonElement p)
    {
        var id = Str(p, "provider");
        if (ProviderRegistry.Everything.FirstOrDefault(x => x.Id == id) is not IKeyedSource) { Fail(reqId, "That source doesn't take a key."); return; }
        _config.SetKey(id, Str(p, "key"));
        Respond(reqId, BuildInit());
        Changed(reload: true);
    }

    private async Task<object?> TestKey(string id)
    {
        if (ProviderRegistry.Everything.FirstOrDefault(x => x.Id == id) is not IKeyedSource k) throw new ProviderException("That source doesn't take a key.");
        await k.TestKeyAsync(CancellationToken.None);
        return new { ok = true };
    }

    // ---------- DCF model ----------

    private sealed record ModelInputs(DcfBase Base, DcfAssumptions Defaults, CompanySnapshot Company, List<FinancialStatement> Statements);

    /// <summary>
    /// Annual statements, market data, the 10-year Treasury, the growth consensus and the revenue
    /// streams → default assumptions, shaped by Settings → DCF assumptions.
    /// </summary>
    private async Task<ModelInputs> ModelInputsAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Length == 0) throw new ProviderException("Pick a company first.");
        var settings = _config.Dcf;
        var coTask = Provider.CompanyAsync(symbol, ct);
        var isTask = Provider.StatementAsync(symbol, StatementKind.Income, "annual", ct);
        var bsTask = Provider.StatementAsync(symbol, StatementKind.Balance, "annual", ct);
        var cfTask = Provider.StatementAsync(symbol, StatementKind.CashFlow, "annual", ct);
        // Only for the latest quarter's dilution; the annual statement stands in without it.
        var qTask = Provider.StatementAsync(symbol, StatementKind.Income, "quarterly", ct);
        var rfTask = settings.LiveRiskFree ? RiskFreeAsync(ct) : Task.FromResult<(double, string)?>(null);
        var gTask = _provider.ConsensusAsync(symbol, ct);
        var segTask = settings.SplitRevenue ? _provider.RevenueSegmentsAsync(symbol, ct) : Task.FromResult<RevenueSegments>(null!);
        try { await Task.WhenAll(coTask, isTask, bsTask, cfTask, qTask, rfTask, gTask, segTask); } catch { /* inspected one by one below */ }

        if (!coTask.IsCompletedSuccessfully) throw coTask.Exception!.InnerException!;
        // Every source's implied share count, to check the reported count + dilution against.
        var implied = await _provider.ImpliedSharesAsync(coTask.Result, ct);
        if (!isTask.IsCompletedSuccessfully) throw new ProviderException("The DCF needs an annual income statement: " + Friendly(isTask.Exception!.InnerException!));
        var statements = new List<FinancialStatement> { isTask.Result };
        var bs = bsTask.IsCompletedSuccessfully ? bsTask.Result : null;
        var cf = cfTask.IsCompletedSuccessfully ? cfTask.Result : null;
        if (bs != null) statements.Add(bs);
        if (cf != null) statements.Add(cf);
        // No breakdown is normal (one-business companies, non-US listings); say why in the model.
        var segNote = segTask.IsCompletedSuccessfully ? null
            : "Revenue is forecast as one line: " + Friendly(segTask.Exception!.InnerException!).TrimEnd('.') + ".";
        var (mb, a) = DcfDefaults.Build(coTask.Result, isTask.Result, bs, cf,
            rfTask.IsCompletedSuccessfully ? rfTask.Result : null, null,
            settings, segTask.IsCompletedSuccessfully ? segTask.Result : null, segNote,
            qTask.IsCompletedSuccessfully ? qTask.Result : null, implied,
            gTask.IsCompletedSuccessfully ? gTask.Result : null);
        // The NAV, LBO and excess-return inputs; the DCF stands on its own if they can't be built.
        try { mb.Kit = await Valuation.BuildAsync(coTask.Result, mb, a, isTask.Result, bs, cf, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return new ModelInputs(mb, a, coTask.Result, statements);
    }

    internal static async Task<(double Value, string Source)?> RiskFreeAsync(CancellationToken ct)
    {
        try
        {
            if (await ProviderRegistry.Treasury.TenYearAsync(ct) is { } ust)
                return (ust.Value / 100, $"US Treasury 10-year par yield, {ust.Date}");
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException) { }
        if (!ProviderRegistry.YahooEnabled) return null;
        try
        {
            var tnx = (await ProviderRegistry.Yahoo.MarketOverviewAsync(ct)).FirstOrDefault(q => q.Symbol == "^TNX");
            if (tnx?.Price is { } y) return (y / 100, "Yahoo Finance ^TNX");
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException) { }
        return null;
    }

    private DcfAssumptions? SavedModel(string symbol)
    {
        if (!_config.Models.TryGetValue(symbol, out var el)) return null;
        try { return el.Deserialize<DcfAssumptions>(JsonOpts)?.Normalised(); } catch { return null; }
    }

    /// <summary>
    /// The saved case, with a share count still at a figure older versions defaulted to (the basic
    /// count, or basic + dilutive before implied shares were checked) moved to today's default.
    /// A count the user typed in is left alone.
    /// </summary>
    private DcfAssumptions? SavedModel(ModelInputs m)
    {
        var saved = SavedModel(m.Base.Symbol);
        static bool Near(double x, double y) => y > 0 && Math.Abs(x - y) <= 0.005 * y;
        if (saved != null && (Near(saved.Shares, m.Base.BasicShares) || Near(saved.Shares, m.Base.BasicShares + m.Base.DilutiveShares)))
            saved.Shares = m.Defaults.Shares;
        return saved;
    }

    /// <summary>The flag for the workbook's share-count cell: the implied-shares check or the second-source check, worst first.</summary>
    private static DataFlag? SharesFlagFor(ModelInputs m, List<DataFlag>? flags) =>
        new[] { m.Base.SharesFlag, flags?.FirstOrDefault(f => f.Key == "sharesOutstanding") }
            .Where(f => f != null).OrderBy(f => f!.Severity == "severe" ? 0 : 1).FirstOrDefault();

    private async Task<object?> Model(string symbol)
    {
        if (_config.PruneExpired()) _config.Save();
        var m = await ModelInputsAsync(symbol, CancellationToken.None);
        Background(async () => Emit("modelCheck", new { symbol = m.Base.Symbol, flags = await ModelFlags(m) }));
        return new { @base = m.Base, defaults = m.Defaults, saved = SavedModel(m) };
    }

    private void SaveModel(int reqId, JsonElement p)
    {
        var symbol = Str(p, "symbol").Trim().ToUpperInvariant();
        if (symbol.Length == 0) { Fail(reqId, "No symbol."); return; }
        // null = back to defaults
        _config.SetModel(symbol, p.TryGetProperty("assumptions", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : null);
        Respond(reqId, null);
    }

    private static FootballData? ParseFootball(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("football", out var f) || f.ValueKind != JsonValueKind.Object) return null;
        try { return f.Deserialize<FootballData>(JsonOpts); } catch { return null; }
    }

    private static DcfAssumptions? ParseAssumptions(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("assumptions", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        try { return a.Deserialize<DcfAssumptions>(JsonOpts)?.Normalised(); } catch { return null; }
    }

    /// <summary>The model on its own: DCF, assumptions, WACC, sensitivity, and the three annual statements it links to.</summary>
    private async Task<object?> ExportModel(JsonElement p)
    {
        var symbol = Str(p, "symbol").Trim().ToUpperInvariant();
        if (symbol.Length == 0) throw new ProviderException("Pick a company first.");
        var units = Str(p, "units") is { Length: > 0 } un ? un : "millions";
        var path = AskSavePath($"{symbol} DCF {DateTime.Now:yyyy-MM-dd}.xlsx");
        if (path == null) return new { cancelled = true };
        _config.ExportDir = Path.GetDirectoryName(path) ?? "";
        _config.Save();

        Progress(10, $"Pulling {symbol} statements…");
        var m = await ModelInputsAsync(symbol, CancellationToken.None);
        var assumptions = ParseAssumptions(p) ?? SavedModel(m) ?? m.Defaults;
        Progress(70, "Cross-checking figures…");
        var flags = await ModelFlags(m);
        Progress(85, "Writing model…");
        ExcelExport.Write(path, new ExportBundle
        {
            SharesFlag = SharesFlagFor(m, flags),
            Symbol = m.Base.Symbol,
            Company = m.Company,
            IncludeSummary = true,
            IncludeAnalysts = false,
            Statements = m.Statements,
            Units = units,
            Source = m.Base.StatementsSource,
            Model = assumptions,
            ModelBase = m.Base,
            Football = ParseFootball(p),
        });
        Progress(100, "Saved.");
        var severe = flags.Count(f => f.Severity == "severe");
        return new { path, skipped = severe == 0 ? Array.Empty<string>()
            : new[] { $"{severe} figure{(severe == 1 ? "" : "s")} disagree sharply with a second source; they're highlighted in red in the workbook, with a note on each." } };
    }

    private string? AskSavePath(string defaultName)
    {
        var dir = _config.ResolvedExportDir();
        try { Directory.CreateDirectory(dir); } catch { }
        return _window.AskSavePath("Export to Excel", defaultName, dir);
    }

    private void OpenPath(int reqId, string path, bool reveal)
    {
        try
        {
            if (!File.Exists(path)) { Fail(reqId, "That file has moved or been deleted."); return; }
            if (reveal && OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", new[] { "-R", path });
            else if (reveal) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Respond(reqId, null);
        }
        catch (Exception ex) { Fail(reqId, ex.Message); }
    }

    /// <summary>Open a web link in the user's browser. Only http(s) — never a file or a protocol handler.</summary>
    public static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
