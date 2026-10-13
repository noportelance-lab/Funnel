using System.Text.Json;
using System.Text.Json.Nodes;

namespace Funnel.Providers.Yahoo;

/// <summary>Yahoo Finance, via the public JSON endpoints yfinance uses. No API key needed.</summary>
public sealed class YahooProvider : IFinanceProvider, IPeerSource, IEstimateSource, INewsSource
{
    public string Id => "yahoo";
    public string Name => "Yahoo Finance";
    public IReadOnlyCollection<string> Sections { get; } = new[]
    {
        Section.Search, Section.Market, Section.Movers, Section.Company, Section.Chart,
        Section.Comps, Section.Statements, Section.Peers, Section.News,
    };

    private const string Q1 = "https://query1.finance.yahoo.com";
    private const string Q2 = "https://query2.finance.yahoo.com";
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Medium = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Long = TimeSpan.FromHours(6);

    private readonly YahooSession _yh = new();

    // ---------------------------------------------------------------- search

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();
        var url = $"{Q1}/v1/finance/search?q={Uri.EscapeDataString(query.Trim())}&quotesCount=8&newsCount=0&listsCount=0&enableFuzzyQuery=false";
        var root = await _yh.GetJsonAsync(url, needsCrumb: false, Medium, ct);
        var hits = new List<SearchHit>();
        foreach (var q in Arr(root["quotes"]))
        {
            var sym = Str(q?["symbol"]);
            if (string.IsNullOrEmpty(sym)) continue;
            hits.Add(new SearchHit(sym,
                Str(q?["longname"]) ?? Str(q?["shortname"]) ?? sym,
                Str(q?["exchDisp"]) ?? Str(q?["exchange"]) ?? "",
                Str(q?["typeDisp"]) ?? Str(q?["quoteType"]) ?? ""));
        }
        return hits;
    }

    // ---------------------------------------------------------------- market board

    private static readonly (string Symbol, string Name, string Group)[] Board =
    {
        ("^GSPC", "S&P 500", "US equities"),
        ("^IXIC", "Nasdaq Composite", "US equities"),
        ("^DJI", "Dow Jones", "US equities"),
        ("^RUT", "Russell 2000", "US equities"),
        ("^GSPTSE", "S&P/TSX Composite", "Global equities"),
        ("^FTSE", "FTSE 100", "Global equities"),
        ("^STOXX50E", "Euro Stoxx 50", "Global equities"),
        ("^N225", "Nikkei 225", "Global equities"),
        ("^TNX", "US 10Y yield", "Rates & volatility"),
        ("^IRX", "US 3M T-bill", "Rates & volatility"),
        ("^VIX", "VIX", "Rates & volatility"),
        ("CL=F", "WTI crude", "Commodities & FX"),
        ("GC=F", "Gold", "Commodities & FX"),
        ("USDCAD=X", "USD / CAD", "Commodities & FX"),
        ("EURUSD=X", "EUR / USD", "Commodities & FX"),
        ("BTC-USD", "Bitcoin", "Commodities & FX"),
    };

    public async Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct)
    {
        var quotes = await QuotesAsync(Board.Select(b => b.Symbol), ct);
        return Board.Select(b =>
        {
            quotes.TryGetValue(b.Symbol, out var q);
            return new MarketQuote(b.Symbol, b.Name,
                Num(q?["regularMarketPrice"]), Num(q?["regularMarketChange"]),
                Num(q?["regularMarketChangePercent"]), Str(q?["currency"]), b.Group,
                b.Symbol is "^TNX" or "^IRX" ? "pct" : b.Symbol.EndsWith("=X") ? "fx" : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct)
    {
        var scr = kind switch { "losers" => "day_losers", "actives" => "most_actives", _ => "day_gainers" };
        var root = await _yh.GetJsonAsync($"{Q1}/v1/finance/screener/predefined/saved?scrIds={scr}&count=8", true, Short, ct);
        return Arr(root["finance"]?["result"]?[0]?["quotes"]).Select(q => new MarketQuote(
            Str(q?["symbol"]) ?? "", Str(q?["shortName"]) ?? Str(q?["longName"]) ?? "",
            Num(q?["regularMarketPrice"]), Num(q?["regularMarketChange"]),
            Num(q?["regularMarketChangePercent"]), Str(q?["currency"]), kind)).ToList();
    }

    private async Task<Dictionary<string, JsonNode?>> QuotesAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        var list = string.Join(",", symbols.Select(Uri.EscapeDataString));
        var root = await _yh.GetJsonAsync($"{Q1}/v7/finance/quote?symbols={list}", true, Short, ct);
        var map = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in Arr(root["quoteResponse"]?["result"]))
            if (Str(q?["symbol"]) is { } s) map[s] = q;
        return map;
    }

    // ---------------------------------------------------------------- YOLO screen

    /// <summary>
    /// One page of Yahoo's custom equity screener: <paramref name="query"/> is the screener's
    /// operator tree ({ operator, operands }). Returns the total match count and the raw quotes.
    /// </summary>
    internal async Task<(int Total, List<JsonNode> Quotes)> ScreenAsync(JsonObject query, string sortField, int offset, int size, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["size"] = size, ["offset"] = offset, ["sortField"] = sortField, ["sortType"] = "DESC",
            ["quoteType"] = "EQUITY", ["query"] = query, ["userId"] = "", ["userIdType"] = "guid",
        };
        var root = await _yh.PostJsonAsync($"{Q1}/v1/finance/screener?lang=en-US&region=US&formatted=false", body.ToJsonString(), Medium, ct);
        var res = root["finance"]?["result"]?[0];
        if (res == null)
            throw new ProviderException(Str(root["finance"]?["error"]?["description"]) ?? "Yahoo Finance's screener sent nothing back.");
        return ((int)(Num(res["total"]) ?? 0), Arr(res["quotes"]).OfType<JsonNode>().ToList());
    }

    /// <summary>Profile, balance-sheet figures, ownership and analyst targets for one screened company.</summary>
    internal Task<JsonNode> ScreenDetailAsync(string symbol, CancellationToken ct) =>
        SummaryAsync(symbol, "assetProfile,financialData,defaultKeyStatistics", ct);

    /// <summary>Raw v7 quotes by symbol (FX pairs, commodity futures).</summary>
    internal Task<Dictionary<string, JsonNode?>> RawQuotesAsync(IEnumerable<string> symbols, CancellationToken ct) =>
        QuotesAsync(symbols, ct);

    // ---------------------------------------------------------------- company

    private const string CompanyModules =
        "assetProfile,price,quoteType,summaryDetail,defaultKeyStatistics,financialData,recommendationTrend,upgradeDowngradeHistory";
    private const string CompModules = "price,summaryDetail,defaultKeyStatistics,financialData";

    private async Task<JsonNode> SummaryAsync(string symbol, string modules, CancellationToken ct)
    {
        var url = $"{Q2}/v10/finance/quoteSummary/{Uri.EscapeDataString(symbol)}?modules={modules}&corsDomain=finance.yahoo.com&formatted=false";
        var root = await _yh.GetJsonAsync(url, true, Medium, ct);
        return root["quoteSummary"]?["result"]?[0]
            ?? throw new ProviderException($"No data for \"{symbol}\" on Yahoo Finance.");
    }

    public async Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var peersTask = PeersAsync(symbol, ct);
        var r = await SummaryAsync(symbol, CompanyModules, ct);

        var price = r["price"];
        var profile = r["assetProfile"];
        var fd = r["financialData"];
        var metrics = ExtractMetrics(r);

        return new CompanySnapshot
        {
            Symbol = Str(price?["symbol"]) ?? symbol,
            Name = Str(price?["longName"]) ?? Str(price?["shortName"]) ?? Str(r["quoteType"]?["longName"]) ?? symbol,
            Exchange = Str(price?["exchangeName"]) ?? "",
            Currency = Str(price?["currency"]) ?? "",
            FinancialCurrency = Str(fd?["financialCurrency"]),
            QuoteType = Str(price?["quoteType"]) ?? Str(r["quoteType"]?["quoteType"]),
            Sector = Str(profile?["sector"]),
            Industry = Str(profile?["industry"]),
            Country = Str(profile?["country"]),
            City = Str(profile?["city"]),
            Website = Str(profile?["website"]),
            Employees = (long?)Num(profile?["fullTimeEmployees"]),
            Summary = Str(profile?["longBusinessSummary"]),
            Price = Num(price?["regularMarketPrice"]),
            Change = Num(price?["regularMarketChange"]),
            // quoteSummary sends a fraction (0.0037); the rest of the app speaks percent (0.37).
            ChangePct = Num(price?["regularMarketChangePercent"]) * 100,
            MarketState = Str(price?["marketState"]),
            Metrics = metrics,
            Analysts = ExtractAnalysts(r, Num(price?["regularMarketPrice"])),
            Peers = await peersTask,
            PeersBasis = PeersBasis,
            Source = Name,
        };
    }

    private const string PeersBasis = "Yahoo: people also watch";

    async Task<PeerSet> IPeerSource.PeersAsync(string symbol, CancellationToken ct) =>
        new(await PeersAsync(symbol.Trim().ToUpperInvariant(), ct), PeersBasis, Name);

    private async Task<List<string>> PeersAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var root = await _yh.GetJsonAsync($"{Q2}/v6/finance/recommendationsbysymbol/{Uri.EscapeDataString(symbol)}", false, Long, ct);
            return Arr(root["finance"]?["result"]?[0]?["recommendedSymbols"])
                .Select(n => Str(n?["symbol"])).OfType<string>()
                .Where(s => !s.Equals(symbol, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        }
        catch { return new List<string>(); }   // peers are a suggestion; the company page doesn't need them
    }

    public async Task<CompRow> CompAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        try
        {
            var r = await SummaryAsync(symbol, CompModules, ct);
            var price = r["price"];
            return new CompRow
            {
                Symbol = Str(price?["symbol"]) ?? symbol,
                Name = Str(price?["shortName"]) ?? Str(price?["longName"]) ?? symbol,
                Currency = Str(price?["currency"]) ?? "",
                Price = Num(price?["regularMarketPrice"]),
                ChangePct = Num(price?["regularMarketChangePercent"]) * 100,
                Metrics = ExtractMetrics(r),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompRow { Symbol = symbol, Name = symbol, Error = ex.Message };
        }
    }

    private static Dictionary<string, double?> ExtractMetrics(JsonNode r)
    {
        var sd = r["summaryDetail"];
        var ks = r["defaultKeyStatistics"];
        var fd = r["financialData"];
        var pr = r["price"];

        var marketCap = Num(pr?["marketCap"]) ?? Num(sd?["marketCap"]);
        var fcf = Num(fd?["freeCashflow"]);
        var de = Num(fd?["debtToEquity"]);

        return new Dictionary<string, double?>
        {
            ["marketCap"] = marketCap,
            ["enterpriseValue"] = Num(ks?["enterpriseValue"]),
            ["trailingPE"] = Num(sd?["trailingPE"]),
            ["forwardPE"] = Num(sd?["forwardPE"]) ?? Num(ks?["forwardPE"]),
            ["pegRatio"] = Num(ks?["pegRatio"]),
            ["priceToBook"] = Num(ks?["priceToBook"]),
            ["priceToSales"] = Num(sd?["priceToSalesTrailing12Months"]),
            ["evToRevenue"] = Num(ks?["enterpriseToRevenue"]),
            ["evToEbitda"] = Num(ks?["enterpriseToEbitda"]),
            ["fcfYield"] = fcf != null && marketCap is > 0 ? fcf / marketCap : null,
            ["dividendYield"] = Num(sd?["dividendYield"]),

            ["grossMargin"] = Num(fd?["grossMargins"]),
            ["ebitdaMargin"] = Num(fd?["ebitdaMargins"]),
            ["operatingMargin"] = Num(fd?["operatingMargins"]),
            ["profitMargin"] = Num(fd?["profitMargins"]) ?? Num(ks?["profitMargins"]),
            ["returnOnEquity"] = Num(fd?["returnOnEquity"]),
            ["returnOnAssets"] = Num(fd?["returnOnAssets"]),

            ["totalRevenue"] = Num(fd?["totalRevenue"]),
            ["revenueGrowth"] = Num(fd?["revenueGrowth"]),
            ["ebitda"] = Num(fd?["ebitda"]),
            ["earningsGrowth"] = Num(fd?["earningsGrowth"]),
            ["freeCashflow"] = fcf,
            ["totalCash"] = Num(fd?["totalCash"]),
            ["totalDebt"] = Num(fd?["totalDebt"]),
            // Yahoo reports D/E as a percentage (152.4 = 1.52x).
            ["debtToEquity"] = de / 100,

            ["fiftyTwoWeekLow"] = Num(sd?["fiftyTwoWeekLow"]),
            ["fiftyTwoWeekHigh"] = Num(sd?["fiftyTwoWeekHigh"]),
            ["beta"] = Num(sd?["beta"]) ?? Num(ks?["beta"]),
            ["averageVolume"] = Num(sd?["averageVolume"]),
            ["sharesOutstanding"] = Num(ks?["sharesOutstanding"]) ?? Num(ks?["impliedSharesOutstanding"]),
            // Market cap ÷ price: counts every share class, where sharesOutstanding can cover only one.
            ["impliedSharesOutstanding"] = Num(ks?["impliedSharesOutstanding"]),
            ["trailingEps"] = Num(ks?["trailingEps"]),
            ["forwardEps"] = Num(ks?["forwardEps"]),
        };
    }

    // ---------------------------------------------------------------- analysts

    private static AnalystView ExtractAnalysts(JsonNode r, double? currentPrice)
    {
        var fd = r["financialData"];

        var trend = Arr(r["recommendationTrend"]?["trend"]).Select(t => new RatingTrend(
            PeriodLabel(Str(t?["period"])),
            (int)(Num(t?["strongBuy"]) ?? 0), (int)(Num(t?["buy"]) ?? 0), (int)(Num(t?["hold"]) ?? 0),
            (int)(Num(t?["sell"]) ?? 0), (int)(Num(t?["strongSell"]) ?? 0))).ToList();

        // Latest action per firm, last 18 months. Older calls are usually from analysts who've
        // since moved or dropped coverage, and would overstate who is behind each rating.
        var cutoff = DateTime.UtcNow.AddMonths(-18);
        var firms = Arr(r["upgradeDowngradeHistory"]?["history"])
            .Select(h => new
            {
                Firm = Str(h?["firm"]) ?? "",
                To = Str(h?["toGrade"]) ?? "",
                From = Str(h?["fromGrade"]),
                Action = Str(h?["action"]) ?? "",
                When = DateTimeOffset.FromUnixTimeSeconds((long)(Num(h?["epochGradeDate"]) ?? 0)).UtcDateTime,
                Pt = Num(h?["currentPriceTarget"]),
                PriorPt = Num(h?["priorPriceTarget"]),
            })
            .Where(h => h.Firm.Length > 0 && h.To.Length > 0 && h.When >= cutoff)
            .OrderByDescending(h => h.When)
            .GroupBy(h => h.Firm, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(h => new FirmRating(h.Firm, h.To, string.IsNullOrEmpty(h.From) ? null : h.From,
                Bucket(h.To), ActionLabel(h.Action), h.When,
                h.Pt is > 0 ? h.Pt : null, h.PriorPt is > 0 ? h.PriorPt : null))
            .ToList();

        return new AnalystView
        {
            RecommendationKey = Str(fd?["recommendationKey"]),
            RecommendationMean = Num(fd?["recommendationMean"]),
            AnalystCount = (int?)Num(fd?["numberOfAnalystOpinions"]) ?? trend.FirstOrDefault()?.Total,
            CurrentPrice = Num(fd?["currentPrice"]) ?? currentPrice,
            TargetLow = Num(fd?["targetLowPrice"]),
            TargetMean = Num(fd?["targetMeanPrice"]),
            TargetMedian = Num(fd?["targetMedianPrice"]),
            TargetHigh = Num(fd?["targetHighPrice"]),
            Trend = trend,
            Firms = firms,
        };
    }

    private static string PeriodLabel(string? p) => p switch
    {
        "0m" => "Current",
        "-1m" => "1 month ago",
        "-2m" => "2 months ago",
        "-3m" => "3 months ago",
        _ => p ?? "",
    };

    private static string ActionLabel(string a) => a switch
    {
        "up" => "Upgrade",
        "down" => "Downgrade",
        "init" => "Initiated",
        "reit" => "Reiterated",
        "main" => "Maintained",
        _ => a,
    };

    /// <summary>Map a broker's house rating onto Buy / Hold / Sell.</summary>
    internal static string Bucket(string grade)
    {
        var g = grade.Trim().ToLowerInvariant();
        string[] sell = { "sell", "underperform", "underweight", "reduce", "negative", "strong sell", "market underperform", "sector underperform", "below average" };
        string[] buy = { "buy", "outperform", "overweight", "strong buy", "positive", "accumulate", "add", "market outperform", "sector outperform", "top pick", "conviction buy", "long-term buy", "speculative buy", "above average" };
        if (sell.Contains(g) || g.Contains("sell") || g.Contains("underperform") || g.Contains("underweight")) return "Sell";
        if (buy.Contains(g) || g.Contains("buy") || g.Contains("outperform") || g.Contains("overweight")) return "Buy";
        return "Hold";
    }

    // ---------------------------------------------------------------- chart

    public async Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct)
    {
        var (rng, interval) = range switch
        {
            "1d" => ("1d", "5m"),
            "5d" => ("5d", "15m"),
            "1mo" => ("1mo", "1d"),
            "6mo" => ("6mo", "1d"),
            "ytd" => ("ytd", "1d"),
            "5y" => ("5y", "1wk"),
            "max" => ("max", "1mo"),
            "5y-daily" => ("5y", "1d"),   // full daily history, for the Excel price sheet
            _ => ("1y", "1d"),
        };
        var url = $"{Q1}/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range={rng}&interval={interval}&includePrePost=false&events=div%2Csplits";
        var root = await _yh.GetJsonAsync(url, false, range == "1d" ? Short : Medium, ct);
        var res = root["chart"]?["result"]?[0] ?? throw new ProviderException($"No price history for \"{symbol}\".");
        var meta = res["meta"];
        var ts = Arr(res["timestamp"]).ToList();
        var q = res["indicators"]?["quote"]?[0];
        var o = Arr(q?["open"]).ToList(); var h = Arr(q?["high"]).ToList(); var l = Arr(q?["low"]).ToList();
        var c = Arr(q?["close"]).ToList(); var v = Arr(q?["volume"]).ToList();

        var pts = new List<PricePoint>(ts.Count);
        for (int i = 0; i < ts.Count; i++)
        {
            var close = Num(At(c, i));
            if (close == null) continue;   // Yahoo pads halted / not-yet-traded bars with nulls
            pts.Add(new PricePoint((long)(Num(ts[i]) ?? 0), Num(At(o, i)), Num(At(h, i)), Num(At(l, i)), close, (long?)Num(At(v, i))));
        }
        return new PriceSeries
        {
            Symbol = Str(meta?["symbol"]) ?? symbol,
            Range = range,
            Interval = interval,
            Currency = Str(meta?["currency"]) ?? "",
            PreviousClose = Num(meta?["chartPreviousClose"]) ?? Num(meta?["previousClose"]),
            Points = pts,
        };
    }

    // ---------------------------------------------------------------- statements

    public async Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var items = YahooStatements.For(kind);
        var keys = items.Where(i => i.Format != "header").Select(i => i.Key).ToList();
        var prefix = frequency == "quarterly" ? "quarterly" : "annual";
        // Annual income & cash-flow statements get a trailing-twelve-months column, like Yahoo's site.
        var withTtm = prefix == "annual" && kind != StatementKind.Balance;

        var types = keys.Select(k => prefix + k);
        if (withTtm) types = types.Concat(keys.Select(k => "trailing" + k));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = $"{Q2}/ws/fundamentals-timeseries/v1/finance/timeseries/{Uri.EscapeDataString(symbol)}" +
                  $"?symbol={Uri.EscapeDataString(symbol)}&type={string.Join(",", types)}&period1=493590046&period2={now}";
        var root = await _yh.GetJsonAsync(url, true, Medium, ct);

        // key -> asOfDate -> value, separately for the period columns and the TTM column.
        var periodic = new Dictionary<string, Dictionary<string, double>>();
        var trailing = new Dictionary<string, (string Date, double Val)>();
        string currency = "";

        foreach (var series in Arr(root["timeseries"]?["result"]))
        {
            var type = Str(series?["meta"]?["type"]?[0]);
            if (type == null) continue;
            var isTtm = type.StartsWith("trailing", StringComparison.Ordinal);
            var key = isTtm ? type["trailing".Length..] : type[prefix.Length..];
            foreach (var pt in Arr(series?[type]))
            {
                var date = Str(pt?["asOfDate"]);
                var val = Num(pt?["reportedValue"]);
                if (date == null || val == null) continue;
                if (currency.Length == 0) currency = Str(pt?["currencyCode"]) ?? "";
                if (isTtm)
                {
                    if (!trailing.TryGetValue(key, out var cur) || string.CompareOrdinal(date, cur.Date) > 0)
                        trailing[key] = (date, val.Value);
                }
                else
                {
                    if (!periodic.TryGetValue(key, out var byDate)) periodic[key] = byDate = new();
                    byDate[date] = val.Value;
                }
            }
        }

        if (periodic.Count == 0)
            throw new ProviderException($"Yahoo Finance has no {frequency} {YahooStatements.Title(kind).ToLowerInvariant()} for {symbol}.");

        var dates = periodic.Values.SelectMany(d => d.Keys).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        // The TTM column only when it's newer than the last fiscal year — otherwise it's a duplicate.
        var ttmDate = trailing.Values.Select(t => t.Date).DefaultIfEmpty("").Max(StringComparer.Ordinal) ?? "";
        var ttm = withTtm && trailing.Count > 0 && string.CompareOrdinal(ttmDate, dates.Last()) > 0;

        var periods = new List<string>(dates);
        if (ttm) periods.Add("TTM");

        var lines = new List<StatementLine>();
        foreach (var item in items)
        {
            if (item.Format == "header")
            {
                lines.Add(new StatementLine(item.Key, item.Label, "header", true, 0, new()));
                continue;
            }
            periodic.TryGetValue(item.Key, out var byDate);
            var values = dates.Select(d => byDate != null && byDate.TryGetValue(d, out var v) ? v : (double?)null).ToList();
            if (ttm) values.Add(trailing.TryGetValue(item.Key, out var t) && t.Date == ttmDate ? t.Val : null);
            if (values.All(v => v == null)) continue;
            lines.Add(new StatementLine(item.Key, item.Label, item.Format, item.Emphasis, item.Indent, values));
        }
        // Drop section headers left with nothing under them.
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Format == "header" && (i == lines.Count - 1 || lines[i + 1].Format == "header"))
                lines.RemoveAt(i);

        return new FinancialStatement
        {
            Kind = kind,
            Title = YahooStatements.Title(kind),
            Frequency = prefix,
            Currency = currency,
            Periods = periods,
            Lines = lines,
            Source = Name,
        };
    }

    // ---------------------------------------------------------------- estimates & news

    /// <summary>
    /// Consensus revenue growth for the fiscal year in progress ("0y"), or next year's when the
    /// current one has no estimate. Feeds the DCF's first-year growth default.
    /// </summary>
    public async Task<double?> RevenueGrowthEstimateAsync(string symbol, CancellationToken ct)
    {
        var r = await SummaryAsync(symbol.Trim().ToUpperInvariant(), "earningsTrend", ct);
        var trend = Arr(r["earningsTrend"]?["trend"]).ToList();
        foreach (var period in new[] { "0y", "+1y" })
        {
            var t = trend.FirstOrDefault(x => Str(x?["period"]) == period);
            if (Num(t?["revenueEstimate"]?["growth"]) is { } g) return g;
        }
        return null;
    }

    /// <summary>Revenue and EPS consensus for this fiscal year and next (earningsTrend "0y" and "+1y").</summary>
    public async Task<ConsensusEstimates?> ConsensusAsync(string symbol, CancellationToken ct)
    {
        var r = await SummaryAsync(symbol.Trim().ToUpperInvariant(), "earningsTrend", ct);
        var years = Arr(r["earningsTrend"]?["trend"])
            .Where(t => Str(t?["period"]) is "0y" or "+1y")
            .Select(t => new ConsensusYear(Str(t!["period"])!, Str(t["endDate"]),
                Num(t["revenueEstimate"]?["avg"]), Num(t["revenueEstimate"]?["growth"]),
                Num(t["earningsEstimate"]?["avg"]), Num(t["earningsEstimate"]?["growth"]),
                (int?)(Num(t["revenueEstimate"]?["numberOfAnalysts"]) ?? Num(t["earningsEstimate"]?["numberOfAnalysts"]))))
            .Where(y => y.RevenueGrowth != null || y.EpsAvg != null || y.RevenueAvg != null)
            .ToList();
        return years.Count > 0 ? new ConsensusEstimates { Years = years, Source = Name } : null;
    }

    public async Task<IReadOnlyList<NewsItem>> NewsAsync(string symbol, CancellationToken ct)
    {
        var url = $"{Q1}/v1/finance/search?q={Uri.EscapeDataString(symbol.Trim())}&quotesCount=0&newsCount=10&listsCount=0";
        var root = await _yh.GetJsonAsync(url, needsCrumb: false, Medium, ct);
        return Arr(root["news"]).Select(n => new NewsItem(
                Str(n?["title"]) ?? "", Str(n?["publisher"]) ?? "",
                DateTimeOffset.FromUnixTimeSeconds((long)(Num(n?["providerPublishTime"]) ?? 0)).UtcDateTime,
                Str(n?["link"]) ?? "", null))
            .Where(n => n.Headline.Length > 0 && n.Url.StartsWith("http")).ToList();
    }

    // ---------------------------------------------------------------- json helpers

    private static IEnumerable<JsonNode?> Arr(JsonNode? n) => n is JsonArray a ? a : Enumerable.Empty<JsonNode?>();
    private static JsonNode? At(List<JsonNode?> list, int i) => i < list.Count ? list[i] : null;

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A number, whether Yahoo sent it bare or wrapped as { raw, fmt }.</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is JsonObject o) n = o["raw"];
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d))
            return double.IsFinite(d) ? d : null;
        return null;
    }
}
