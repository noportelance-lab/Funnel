using System.Globalization;
using System.Text.Json.Nodes;
using Funnel.Engine;
using static Funnel.Providers.J;

namespace Funnel.Providers.Edgar;

/// <summary>
/// SEC EDGAR: financial statements exactly as companies filed them (XBRL "company facts") — US
/// GAAP for domestic filers, IFRS for foreign ones filing 20-F / 40-F (most US-listed Canadian
/// companies) — plus a company profile and fundamentals-only comp metrics built from those
/// filings, industry peers from the SEC's SIC codes, the filing list, and 8-K / 6-K current
/// reports as company news. SEC content may be redistributed freely; the SEC only asks every
/// client to send a contact email in its User-Agent, so the user enters one in Sources.
/// EDGAR has no market prices, so anything that needs one (market cap, P/E, EV multiples) stays empty.
/// </summary>
public sealed partial class EdgarProvider : IFinanceProvider, IPeerSource, IFilingSource, INewsSource
{
    public string Id => "edgar";
    public string Name => "SEC EDGAR";
    public IReadOnlyCollection<string> Sections { get; } = new[]
    {
        Section.Search, Section.Company, Section.Comps, Section.Statements, Section.Peers, Section.Filings, Section.News, Section.Segments,
    };

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly Func<string> _contact;
    // At most 10 requests a second, per the SEC's fair-access policy.
    private readonly HttpSource _http = new("SEC EDGAR", "Funnel", TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(40));
    private readonly SemaphoreSlim _tickerLock = new(1, 1);
    private List<Listing>? _listings;
    private DateTime _listingsAt;

    public EdgarProvider(Func<string> contactEmail) { _contact = contactEmail; }

    private sealed record Listing(int Cik, string Name, string Ticker, string Exchange, int Rank);

    private Task<JsonNode> GetAsync(string url, TimeSpan cacheFor, CancellationToken ct)
    {
        var email = _contact().Trim();
        if (!email.Contains('@'))
            throw new ProviderException("SEC EDGAR needs a contact email. Add yours under Sources — the SEC asks every app to identify itself.");
        return _http.GetJsonAsync(url, cacheFor, ct, req =>
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd($"Funnel/{Bridge.AppVersion}");
            req.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue($"({email})"));
        });
    }

    // ---------------------------------------------------------------- tickers

    /// <summary>
    /// Every listed SEC filer with its ticker and exchange. The SEC orders the file by market
    /// value, so a row's position doubles as a size rank — handy for picking similar-sized peers.
    /// </summary>
    private async Task<List<Listing>> ListingsAsync(CancellationToken ct)
    {
        if (_listings != null && DateTime.UtcNow - _listingsAt < Day) return _listings;
        await _tickerLock.WaitAsync(ct);
        try
        {
            if (_listings != null && DateTime.UtcNow - _listingsAt < Day) return _listings;
            var root = await GetAsync("https://www.sec.gov/files/company_tickers_exchange.json", Day, ct);
            var list = new List<Listing>();
            int rank = 0;
            foreach (var row in Arr(root["data"]))
            {
                if (row is not JsonArray a || a.Count < 4) continue;
                var cik = (int)(Num(a[0]) ?? 0);
                var ticker = Str(a[2]);
                if (cik == 0 || string.IsNullOrEmpty(ticker)) continue;
                list.Add(new Listing(cik, Str(a[1]) ?? ticker, ticker.ToUpperInvariant(), Str(a[3]) ?? "", rank++));
            }
            _listingsAt = DateTime.UtcNow;
            return _listings = list;
        }
        finally { _tickerLock.Release(); }
    }

    private async Task<Listing> FindAsync(string symbol, CancellationToken ct)
    {
        var sym = symbol.Trim().ToUpperInvariant().Replace('.', '-');
        return (await ListingsAsync(ct)).FirstOrDefault(l => l.Ticker == sym)
            ?? throw new ProviderException($"SEC EDGAR only covers companies listed in the US, and {symbol.ToUpperInvariant()} isn't one of them.");
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length == 0) return Array.Empty<SearchHit>();
        var all = await ListingsAsync(ct);
        var qu = q.ToUpperInvariant();
        return all.Where(l => l.Ticker == qu)
            .Concat(all.Where(l => l.Ticker.StartsWith(qu, StringComparison.Ordinal)))
            .Concat(all.Where(l => l.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(l => l.Ticker).Take(8)
            .Select(l => new SearchHit(l.Ticker, l.Name, l.Exchange, "Equity"))
            .ToList();
    }

    // ---------------------------------------------------------------- statements

    private sealed record Fact(DateOnly? Start, DateOnly End, double Val, string Filed)
    {
        public int Days => Start is { } s ? End.DayNumber - s.DayNumber : 0;
    }

    /// <summary>All facts for one company, by tag and unit, from its periodic reports only.</summary>
    private sealed class Facts
    {
        public Dictionary<string, Dictionary<string, List<Fact>>> ByTag { get; } = new();
        public string Currency { get; set; } = "USD";
        /// <summary>Reports under IFRS (a foreign filer) rather than US GAAP.</summary>
        public bool Ifrs { get; set; }
        /// <summary>Shares outstanding from the cover page of each report (dei).</summary>
        public List<Fact> CoverShares { get; set; } = new();

        public List<Fact> Get(string tag, string unit) =>
            ByTag.TryGetValue(tag, out var units) && units.TryGetValue(unit, out var f) ? f : new List<Fact>();
    }

    // Annual and quarterly reports, domestic (10-K / 10-Q) and foreign (20-F / 40-F annual, 6-K interim).
    private static bool Periodic(string form) =>
        form.StartsWith("10-K") || form.StartsWith("10-Q") || form.StartsWith("20-F") || form.StartsWith("40-F") || form.StartsWith("6-K");

    private static List<Fact> ReadFacts(JsonNode? arr, bool periodicOnly)
    {
        var list = new List<Fact>();
        foreach (var f in Arr(arr))
        {
            if (periodicOnly && !Periodic(Str(f?["form"]) ?? "")) continue;
            if (Num(f?["val"]) is not { } val || !DateOnly.TryParse(Str(f?["end"]), CultureInfo.InvariantCulture, out var end)) continue;
            DateOnly? start = DateOnly.TryParse(Str(f?["start"]), CultureInfo.InvariantCulture, out var s) ? s : null;
            list.Add(new Fact(start, end, val, Str(f?["filed"]) ?? ""));
        }
        return list;
    }

    private async Task<Facts> FactsAsync(int cik, string symbol, CancellationToken ct)
    {
        var root = await GetAsync($"https://data.sec.gov/api/xbrl/companyfacts/CIK{cik:D10}.json", Hour, ct);
        var gaap = root["facts"]?["us-gaap"] as JsonObject;
        var ifrs = root["facts"]?["ifrs-full"] as JsonObject;
        // A company files under one standard; a stray handful of tags from the other doesn't count.
        var useIfrs = (ifrs?.Count ?? 0) > (gaap?.Count ?? 0);
        var taxonomy = useIfrs ? ifrs : gaap;
        if (taxonomy == null || taxonomy.Count == 0)
            throw new ProviderException($"SEC EDGAR has no machine-readable financials for {symbol}.");

        var facts = new Facts { Ifrs = useIfrs };
        var currencyVotes = new Dictionary<string, int>();
        foreach (var (tag, node) in taxonomy)
        {
            if (node?["units"] is not JsonObject units) continue;
            var byUnit = new Dictionary<string, List<Fact>>();
            foreach (var (unit, arr) in units)
            {
                var list = ReadFacts(arr, periodicOnly: true);
                if (list.Count == 0) continue;
                byUnit[unit] = list;
                if (unit.Length == 3 && unit.All(char.IsUpper)) currencyVotes[unit] = currencyVotes.GetValueOrDefault(unit) + list.Count;
            }
            if (byUnit.Count > 0) facts.ByTag[tag] = byUnit;
        }
        if (currencyVotes.Count > 0) facts.Currency = currencyVotes.MaxBy(kv => kv.Value).Key;
        facts.CoverShares = ReadFacts(root["facts"]?["dei"]?["EntityCommonStockSharesOutstanding"]?["units"]?["shares"], periodicOnly: false);
        return facts;
    }

    private static string UnitFor(EdgarConcepts.Line line, string currency) => line.Unit switch
    {
        "perShare" => currency + "/shares",
        "shares" => "shares",
        _ => currency,
    };

    // The anchors that tell us which period ends exist: almost every filer reports one of these.
    private static readonly string[] Anchors =
    {
        "NetIncomeLoss", "ProfitLoss", "Revenues", "RevenueFromContractWithCustomerExcludingAssessedTax", "OperatingIncomeLoss", "Assets",
        "Revenue", "ProfitLossFromOperatingActivities",
    };

    private static bool IsYear(Fact f) => f.Days is >= 350 and <= 380;
    private static bool IsQuarter(Fact f) => f.Days is >= 80 and <= 100;

    /// <summary>Newest filing wins, so restated figures replace the originals.</summary>
    private static Fact? Latest(IEnumerable<Fact> fs) => fs.OrderByDescending(f => f.Filed, StringComparer.Ordinal).FirstOrDefault();

    /// <param name="additive">False for share counts and per-share figures: a weighted average or
    /// EPS for the year minus nine months is meaningless, so a missing quarter stays missing.</param>
    private static double? Value(List<Fact> facts, DateOnly end, bool instant, bool quarterly, bool additive = true)
    {
        if (instant) return Latest(facts.Where(f => f.Start == null && f.End == end))?.Val;
        if (!quarterly) return Latest(facts.Where(f => f.End == end && IsYear(f)))?.Val;

        if (Latest(facts.Where(f => f.End == end && IsQuarter(f))) is { } q) return q.Val;
        if (!additive) return null;
        // 10-Q cash-flow statements (and Q4 in general) are only reported year-to-date, so the
        // quarter is the difference between two YTD figures that share a start date.
        foreach (var ytd in facts.Where(f => f.End == end && f.Days > 100 && f.Days <= 380).OrderByDescending(f => f.Filed, StringComparer.Ordinal))
        {
            var prior = Latest(facts.Where(f => f.Start == ytd.Start && f.End < end && end.DayNumber - f.End.DayNumber is >= 80 and <= 100));
            if (prior != null) return ytd.Val - prior.Val;
        }
        return null;
    }

    private static List<DateOnly> PeriodEnds(Facts facts, bool quarterly)
    {
        var ends = new HashSet<DateOnly>();
        foreach (var tag in Anchors)
            foreach (var f in facts.Get(tag, facts.Currency))
            {
                if (f.Start == null) continue;
                if (IsYear(f)) ends.Add(f.End);
                else if (quarterly && IsQuarter(f)) ends.Add(f.End);
            }
        return ends.OrderBy(e => e).TakeLast(quarterly ? 9 : 5).ToList();
    }

    public async Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct)
    {
        var listing = await FindAsync(symbol, ct);
        var facts = await FactsAsync(listing.Cik, listing.Ticker, ct);
        return BuildStatement(facts, listing.Ticker, kind, frequency == "quarterly");
    }

    private FinancialStatement BuildStatement(Facts facts, string ticker, StatementKind kind, bool quarterly)
    {
        var frequency = quarterly ? "quarterly" : "annual";
        var ends = PeriodEnds(facts, quarterly);
        if (ends.Count == 0) throw new ProviderException($"SEC EDGAR has no {frequency} figures for {ticker}.");

        var spec = EdgarConcepts.For(kind, facts.Ifrs);
        // values[key][periodIndex]
        var values = new Dictionary<string, double?[]>();
        foreach (var line in spec.Where(l => l.Format != "header"))
        {
            var row = new double?[ends.Count];
            for (int i = 0; i < ends.Count; i++)
                foreach (var t in line.Tags)
                {
                    // "!tag" runs the other way from the line's usual sign (see EdgarConcepts.Ifrs).
                    var flip = t.StartsWith('!');
                    var v = Value(facts.Get(flip ? t[1..] : t, UnitFor(line, facts.Currency)), ends[i], line.Instant, quarterly,
                        additive: line.Unit is not ("shares" or "perShare"));
                    if (v != null) { row[i] = line.Negate != flip ? -v : v; break; }
                }
            values[line.Key] = row;
        }
        // Derived lines, in list order, so later derivations can use earlier ones.
        foreach (var line in spec.Where(l => l.Derive != null))
            for (int i = 0; i < ends.Count; i++)
            {
                if (values[line.Key][i] != null) continue;
                var idx = i;
                values[line.Key][i] = line.Derive!(k => values.TryGetValue(k, out var r) ? r[idx] : null);
            }

        // Quarterly views keep the last 8 quarters that actually have revenue or net income.
        var keep = Enumerable.Range(0, ends.Count).ToList();
        if (quarterly)
        {
            var anchor = kind == StatementKind.Balance ? "TotalAssets" : kind == StatementKind.Income ? "NetIncome" : "OperatingCashFlow";
            keep = keep.Where(i => values.TryGetValue(anchor, out var r) && r[i] != null).TakeLast(8).ToList();
        }

        var lines = new List<StatementLine>();
        foreach (var line in spec)
        {
            if (line.Format == "header") { lines.Add(new StatementLine(line.Key, line.Label, "header", true, 0, new())); continue; }
            var vals = keep.Select(i => values[line.Key][i]).ToList();
            if (vals.All(v => v == null)) continue;
            lines.Add(new StatementLine(line.Key, line.Label, line.Format, line.Emphasis, line.Indent, vals));
        }
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Format == "header" && (i == lines.Count - 1 || lines[i + 1].Format == "header"))
                lines.RemoveAt(i);
        if (lines.Count(l => l.Format != "header") == 0)
            throw new ProviderException($"SEC EDGAR has no {frequency} {kind.ToString().ToLowerInvariant()} figures for {ticker}.");

        return new FinancialStatement
        {
            Kind = kind,
            Title = Yahoo.YahooStatements.Title(kind),
            Frequency = frequency,
            Currency = facts.Currency,
            Periods = keep.Select(i => ends[i].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(),
            Lines = lines,
            Source = Name,
        };
    }

    // ---------------------------------------------------------------- fundamentals (company & comps)

    /// <summary>The company's annual and quarterly statements, built once from one download.</summary>
    private sealed class Book
    {
        public Dictionary<(StatementKind, bool), FinancialStatement> Sheets { get; } = new();
        public FinancialStatement? Get(StatementKind k, bool quarterly) => Sheets.GetValueOrDefault((k, quarterly));
    }

    private Book BuildBook(Facts facts, string ticker)
    {
        var book = new Book();
        foreach (var k in new[] { StatementKind.Income, StatementKind.Balance, StatementKind.CashFlow })
            foreach (var q in new[] { false, true })
            {
                try { book.Sheets[(k, q)] = BuildStatement(facts, ticker, k, q); }
                catch (ProviderException) { /* foreign filers have no quarterlies; fine */ }
            }
        return book;
    }

    private static List<double?> Row(FinancialStatement? s, string key) =>
        s?.Lines.FirstOrDefault(l => l.Key == key)?.Values ?? new List<double?>();

    private static DateOnly? End(FinancialStatement s, int i) =>
        DateOnly.TryParse(s.Periods[i], CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>
    /// Trailing twelve months of a flow, and the twelve months before that (for growth): the sum
    /// of four consecutive quarters when the company files quarterlies, else the last two fiscal years.
    /// </summary>
    private static (double? Now, double? Before) Ttm(Book b, StatementKind kind, string key)
    {
        if (b.Get(kind, true) is { } q && Row(q, key) is { Count: >= 4 } qv)
        {
            double? Sum(int last)
            {
                if (last < 3) return null;
                var span = End(q, last)?.DayNumber - End(q, last - 3)?.DayNumber;
                if (span is not (>= 250 and <= 300)) return null;
                var four = qv.Skip(last - 3).Take(4).ToList();
                return four.All(v => v != null) ? four.Sum() : null;
            }
            if (Sum(qv.Count - 1) is { } now) return (now, Sum(qv.Count - 5));
        }
        var av = Row(b.Get(kind, false), key);
        return (av.LastOrDefault(), av.Count >= 2 ? av[^2] : null);
    }

    /// <summary>The latest balance-sheet figure, from whichever report is newest.</summary>
    private static double? Latest(Book b, string key)
    {
        foreach (var s in new[] { b.Get(StatementKind.Balance, true), b.Get(StatementKind.Balance, false) })
            if (s != null && Row(s, key).LastOrDefault() is { } v) return v;
        return null;
    }

    private static double? Ratio(double? a, double? b) => a != null && b is { } d && d != 0 ? a / d : null;
    private static double? Growth((double? Now, double? Before) x) => x.Now != null && x.Before is { } p && p != 0 ? x.Now / Math.Abs(p) - Math.Sign(p) : null;

    private static Dictionary<string, double?> Fundamentals(Book b, Facts facts)
    {
        var rev = Ttm(b, StatementKind.Income, "TotalRevenue");
        var gross = Ttm(b, StatementKind.Income, "GrossProfit").Now;
        var op = Ttm(b, StatementKind.Income, "OperatingIncome").Now;
        var ni = Ttm(b, StatementKind.Income, "NetIncome");
        var ebitda = Ttm(b, StatementKind.Income, "EBITDA").Now;
        var fcf = Ttm(b, StatementKind.CashFlow, "FreeCashFlow").Now;
        var eps = Ttm(b, StatementKind.Income, "DilutedEPS").Now;
        var cash = Latest(b, "CashCashEquivalentsAndShortTermInvestments") ?? Latest(b, "CashAndCashEquivalents");
        var debt = Latest(b, "TotalDebt");
        var equity = Latest(b, "StockholdersEquity") ?? Latest(b, "TotalEquityGrossMinorityInterest");
        var assets = Latest(b, "TotalAssets");
        var shares = CoverShares(facts)?.Shares ?? Latest(b, "OrdinarySharesNumber")
                     ?? Row(b.Get(StatementKind.Income, false), "DilutedAverageShares").LastOrDefault();

        // Everything that needs a share price stays null — EDGAR has no market data.
        var m = Metrics.All.ToDictionary(x => x.Key, _ => (double?)null);
        m["grossMargin"] = Ratio(gross, rev.Now);
        m["ebitdaMargin"] = Ratio(ebitda, rev.Now);
        m["operatingMargin"] = Ratio(op, rev.Now);
        m["profitMargin"] = Ratio(ni.Now, rev.Now);
        m["returnOnEquity"] = Ratio(ni.Now, equity);
        m["returnOnAssets"] = Ratio(ni.Now, assets);
        m["totalRevenue"] = rev.Now;
        m["revenueGrowth"] = Growth(rev);
        m["ebitda"] = ebitda;
        m["earningsGrowth"] = Growth(ni);
        m["freeCashflow"] = fcf;
        m["totalCash"] = cash;
        m["totalDebt"] = debt;
        m["debtToEquity"] = Ratio(debt, equity);
        m["sharesOutstanding"] = shares;
        m["trailingEps"] = eps;
        return m;
    }

    private static Fact? Newest(List<Fact> fs) =>
        fs.OrderByDescending(f => f.End).ThenByDescending(f => f.Filed, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Shares outstanding from the cover of the newest report. Companies with several share
    /// classes (Alphabet, Meta) report one figure per class on the same cover, so those are added up.
    /// </summary>
    private static (double Shares, DateOnly AsOf)? CoverShares(Facts facts)
    {
        if (Newest(facts.CoverShares) is not { } top) return null;
        var sameCover = facts.CoverShares.Where(f => f.End == top.End && f.Filed == top.Filed).Select(f => f.Val).ToList();
        return (sameCover.Sum(), top.End);
    }

    /// <summary>The latest cover-page share count, for checking another source's figure.</summary>
    public async Task<(double Shares, DateOnly AsOf)> CoverSharesAsync(string symbol, CancellationToken ct)
    {
        var me = await FindAsync(symbol, ct);
        return CoverShares(await FactsAsync(me.Cik, me.Ticker, ct))
            ?? throw new ProviderException($"SEC EDGAR has no cover-page share count for {me.Ticker}.");
    }

    /// <summary>The SEC's SIC divisions, as a sector label.</summary>
    private static string? Sector(string? sic) => int.TryParse(sic, out var n) ? (n / 100) switch
    {
        < 10 => "Agriculture, Forestry & Fishing",
        < 15 => "Mining & Energy",
        < 18 => "Construction",
        < 40 => "Manufacturing",
        < 50 => "Transportation, Communications & Utilities",
        < 52 => "Wholesale Trade",
        < 60 => "Retail Trade",
        < 68 => "Finance, Insurance & Real Estate",
        < 90 => "Services",
        _ => "Public Administration",
    } : null;

    private static string Title(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());

    public async Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct)
    {
        var me = await FindAsync(symbol, ct);
        var subTask = SubmissionsAsync(me.Cik, ct);
        var factsTask = FactsAsync(me.Cik, me.Ticker, ct);
        var peersTask = PeersAsync(me.Ticker, ct);
        try { await Task.WhenAll(subTask, factsTask, peersTask); } catch { /* inspected one by one */ }
        var sub = await subTask;
        var facts = await factsTask;
        var peers = peersTask.IsCompletedSuccessfully ? peersTask.Result : null;

        var sic = Str(sub["sic"]);
        var addr = sub["addresses"]?["business"];
        // US addresses carry a two-letter state; foreign ones read "ONTARIO, CANADA" or just "CANADA".
        var where = Str(addr?["stateOrCountryDescription"]) ?? "";
        var country = where.Length == 2 ? "United States" : Title(where.Contains(',') ? where[(where.LastIndexOf(',') + 1)..] : where);
        var fye = Str(sub["fiscalYearEnd"]) is { Length: 4 } f && DateTime.TryParseExact(f, "MMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fy)
            ? fy.ToString("MMMM d", CultureInfo.InvariantCulture) : null;
        var name = Str(sub["name"]) ?? me.Name;
        var summary = $"{name} files with the SEC under industry code {sic} ({Title(Str(sub["sicDescription"]))})" +
                      (Str(sub["stateOfIncorporationDescription"]) is { Length: > 0 } inc ? $", incorporated in {Title(inc)}" : "") +
                      (fye != null ? $", with a fiscal year ending {fye}" : "") +
                      $". Figures are from its {(facts.Ifrs ? "IFRS" : "US GAAP")} filings; the business description is in the latest annual report under Filings.";

        return new CompanySnapshot
        {
            Symbol = me.Ticker,
            Name = name,
            Exchange = me.Exchange,
            Currency = facts.Currency,
            FinancialCurrency = facts.Currency,
            QuoteType = "EQUITY",
            Sector = Sector(sic),
            Industry = Title(Str(sub["sicDescription"])),
            Country = country,
            City = Title(Str(addr?["city"])),
            Website = Str(sub["website"]) is { Length: > 0 } w ? (w.StartsWith("http") ? w : "https://" + w) : null,
            Summary = summary,
            Metrics = Fundamentals(BuildBook(facts, me.Ticker), facts),
            Peers = peers?.Symbols ?? new(),
            PeersBasis = peers?.Basis,
            Source = Name,
        };
    }

    public async Task<CompRow> CompAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        try
        {
            var me = await FindAsync(symbol, ct);
            var facts = await FactsAsync(me.Cik, me.Ticker, ct);
            return new CompRow { Symbol = me.Ticker, Name = me.Name, Currency = facts.Currency, Metrics = Fundamentals(BuildBook(facts, me.Ticker), facts) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompRow { Symbol = symbol, Name = symbol, Error = ex.Message };
        }
    }

    // ---------------------------------------------------------------- submissions: SIC, filings, news

    private async Task<JsonNode> SubmissionsAsync(int cik, CancellationToken ct) =>
        await GetAsync($"https://data.sec.gov/submissions/CIK{cik:D10}.json", Hour, ct);

    private static readonly HashSet<string> MajorExchanges = new(StringComparer.OrdinalIgnoreCase) { "Nasdaq", "NYSE", "CBOE" };

    /// <summary>
    /// Companies with the same SIC code that filed an annual report in the last 13 months (from
    /// EDGAR full-text search), listed on a major exchange, closest in size to the subject.
    /// </summary>
    public async Task<PeerSet> PeersAsync(string symbol, CancellationToken ct)
    {
        var me = await FindAsync(symbol, ct);
        var sub = await SubmissionsAsync(me.Cik, ct);
        var sic = Str(sub["sic"]);
        if (string.IsNullOrEmpty(sic)) throw new ProviderException($"SEC EDGAR has no industry code for {me.Ticker}.");
        var sicName = Str(sub["sicDescription"]) ?? "";

        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-400);
        var ciks = new HashSet<int>();
        for (int page = 0; page < 8; page++)
        {
            var url = $"https://efts.sec.gov/LATEST/search-index?forms=10-K,20-F,40-F&sics={sic}&startdt={from:yyyy-MM-dd}&enddt={to:yyyy-MM-dd}&from={page * 100}";
            var root = await GetAsync(url, Day, ct);
            var hits = Arr(root["hits"]?["hits"]).ToList();
            foreach (var h in hits)
                foreach (var c in Arr(h?["_source"]?["ciks"]))
                    if (int.TryParse(Str(c), out var id)) ciks.Add(id);
            var total = Num(root["hits"]?["total"]?["value"]) ?? 0;
            if (hits.Count < 100 || (page + 1) * 100 >= total) break;
        }

        var all = await ListingsAsync(ct);
        double LogRank(Listing l) => Math.Log(l.Rank + 10);
        var peers = all.Where(l => ciks.Contains(l.Cik) && l.Cik != me.Cik && MajorExchanges.Contains(l.Exchange))
            .GroupBy(l => l.Cik).Select(g => g.OrderBy(l => l.Rank).First())   // one ticker per company
            .OrderBy(l => Math.Abs(LogRank(l) - LogRank(me)))
            .Take(8).Select(l => l.Ticker).ToList();
        var label = string.IsNullOrEmpty(sicName) ? $"SIC {sic}" : $"SIC {sic} · {Title(sicName)}";
        return new PeerSet(peers, $"Same industry ({label}), closest in size", Name);
    }

    private static readonly HashSet<string> Interesting = new(StringComparer.OrdinalIgnoreCase)
    {
        "10-K", "10-Q", "8-K", "10-K/A", "10-Q/A", "20-F", "40-F", "6-K", "DEF 14A", "S-1", "S-4", "424B4",
    };

    private sealed record Recent(string Form, DateTime Filed, string? Period, string Url, string? Description, string? Items);

    private async Task<(Listing Me, string Name, List<Recent> Rows)> RecentAsync(string symbol, CancellationToken ct)
    {
        var me = await FindAsync(symbol, ct);
        var sub = await SubmissionsAsync(me.Cik, ct);
        var r = sub["filings"]?["recent"];
        List<string?> Col(string k) => Arr(r?[k]).Select(Str).ToList();
        var forms = Col("form"); var dates = Col("filingDate"); var periods = Col("reportDate"); var accns = Col("accessionNumber");
        var docs = Col("primaryDocument"); var descs = Col("primaryDocDescription"); var items = Col("items");

        var rows = new List<Recent>();
        for (int i = 0; i < forms.Count; i++)
        {
            if (i >= accns.Count || i >= docs.Count) break;
            if (!DateTime.TryParse(dates.ElementAtOrDefault(i), CultureInfo.InvariantCulture, DateTimeStyles.None, out var filed)) continue;
            var url = $"https://www.sec.gov/Archives/edgar/data/{me.Cik}/{(accns[i] ?? "").Replace("-", "")}/{docs[i]}";
            rows.Add(new Recent(forms[i] ?? "", filed, periods.ElementAtOrDefault(i) is { Length: > 0 } p ? p : null, url,
                descs.ElementAtOrDefault(i), items.ElementAtOrDefault(i)));
        }
        return (me, Str(sub["name"]) ?? me.Name, rows);
    }

    public async Task<IReadOnlyList<Filing>> FilingsAsync(string symbol, CancellationToken ct)
    {
        var (_, _, rows) = await RecentAsync(symbol, ct);
        return rows.Where(x => Interesting.Contains(x.Form)).Take(15)
            .Select(x => new Filing(x.Form, x.Filed, x.Period,
                string.IsNullOrWhiteSpace(x.Description) || x.Description == x.Form ? FormName(x.Form) : x.Description, x.Url))
            .ToList();
    }

    /// <summary>What each 8-K item number announces (Form 8-K, General Instructions B).</summary>
    private static readonly Dictionary<string, string> Items = new()
    {
        ["1.01"] = "Material agreement signed", ["1.02"] = "Material agreement ended", ["1.03"] = "Bankruptcy or receivership",
        ["1.05"] = "Cybersecurity incident", ["2.01"] = "Acquisition or disposal completed", ["2.02"] = "Quarterly results released",
        ["2.03"] = "New debt or financial obligation", ["2.04"] = "Debt acceleration triggered", ["2.05"] = "Restructuring or exit costs",
        ["2.06"] = "Impairment charge", ["3.01"] = "Listing standards notice or delisting", ["3.02"] = "Unregistered share sale",
        ["3.03"] = "Shareholder rights changed", ["4.01"] = "Auditor changed", ["4.02"] = "Earlier financials no longer reliable",
        ["5.01"] = "Change in control", ["5.02"] = "Director or officer change", ["5.03"] = "Bylaws or fiscal year changed",
        ["5.07"] = "Shareholder vote results", ["5.08"] = "Shareholder nominations", ["7.01"] = "Investor disclosure (Reg FD)",
        ["8.01"] = "Other company announcement",
    };

    /// <summary>
    /// Company news as filed: each 8-K (or a foreign filer's 6-K) with what it announces. Not
    /// third-party journalism, but it covers results, deals, management changes and votes.
    /// </summary>
    public async Task<IReadOnlyList<NewsItem>> NewsAsync(string symbol, CancellationToken ct)
    {
        var (_, name, rows) = await RecentAsync(symbol, ct);
        return rows.Where(x => x.Form is "8-K" or "6-K").Take(10).Select(x =>
        {
            var what = (x.Items ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(i => Items.GetValueOrDefault(i)).OfType<string>().ToList();
            var headline = what.Count > 0 ? string.Join(" · ", what)
                : x.Form == "6-K" ? (string.IsNullOrWhiteSpace(x.Description) || x.Description == "6-K" ? "Report of foreign issuer" : Title(x.Description))
                : "Current report";
            return new NewsItem($"{name}: {headline}", $"SEC EDGAR · {x.Form}", x.Filed, x.Url, null);
        }).ToList();
    }

    private static string FormName(string form) => form switch
    {
        "10-K" => "Annual report",
        "10-Q" => "Quarterly report",
        "8-K" => "Current report",
        "DEF 14A" => "Proxy statement",
        "20-F" or "40-F" => "Annual report (foreign issuer)",
        "6-K" => "Report of foreign issuer",
        _ => form,
    };

    // ---------------------------------------------------------------- not served

    public Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct) => throw new NotSupportedException();
    public Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct) => throw new NotSupportedException();
}
