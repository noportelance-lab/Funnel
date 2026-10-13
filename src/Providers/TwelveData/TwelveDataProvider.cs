using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Funnel.Providers.Yahoo;
using static Funnel.Providers.J;

namespace Funnel.Providers.TwelveData;

/// <summary>
/// Twelve Data (api.twelvedata.com). A licensed replacement for everything Yahoo serves except
/// news and peers: search, quotes, intraday and daily charts, profile, valuation statistics,
/// statements, analyst recommendations, price targets and firm-by-firm ratings, revenue estimates,
/// and the movers lists. Showing its data to other people needs a business plan (Venture or
/// above); analyst data needs Enterprise. Calls a plan doesn't include answer 403 and become
/// <see cref="NotSupportedException"/>, so the section falls back to the next source.
///
/// Symbols: Funnel uses Yahoo-style tickers (SHOP.TO, BRK-B); Twelve Data wants SHOP:TSX and
/// BRK.B, so every call translates on the way in and search results on the way out.
/// </summary>
public sealed class TwelveDataProvider : IFinanceProvider, IKeyedSource, IEstimateSource
{
    public string Id => "twelvedata";
    public string Name => "Twelve Data";
    public IReadOnlyCollection<string> Sections { get; } = new[]
    {
        Section.Search, Section.Market, Section.Movers, Section.Company, Section.Chart, Section.Comps, Section.Statements,
    };
    public string KeyUrl => "https://twelvedata.com/pricing-business";
    public string KeyNote => "Venture plan or above to show data to customers · Enterprise for analyst data";

    private const string Base = "https://api.twelvedata.com";
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Medium = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Long = TimeSpan.FromHours(6);

    private readonly Func<string?> _key;
    private readonly HttpSource _http;

    public TwelveDataProvider(Func<string?> key)
    {
        _key = key;
        _http = new HttpSource("Twelve Data", "Funnel") { Classify = Classify };
    }

    /// <summary>Twelve Data often answers 200 with { "code": 4xx, "status": "error" }; read those.</summary>
    private static Exception? Classify(HttpStatusCode status, string body)
    {
        if (!body.Contains("\"status\":\"error\"", StringComparison.Ordinal) && status == HttpStatusCode.OK) return null;
        JsonNode? n = null;
        try { n = JsonNode.Parse(body); } catch { }
        var code = (int?)Num(n?["code"]) ?? (int)status;
        var msg = (Str(n?["message"]) ?? "").Replace("**", "");
        if (msg.Contains("demo", StringComparison.OrdinalIgnoreCase)) return new NotSupportedException("Not available with Twelve Data's demo key.");
        return code switch
        {
            401 => new ProviderException("Twelve Data rejected the API key. Check it under Sources."),
            403 => new NotSupportedException("Not in your Twelve Data plan."),
            429 => new ProviderException("Twelve Data's per-minute credit limit is used up. Wait a minute and try again."),
            400 or 404 => new ProviderException("Twelve Data: " + (msg.Split(". ")[0] is { Length: > 0 } m ? m.TrimEnd('.') + "." : "not found.")),
            _ when status == HttpStatusCode.OK => new ProviderException("Twelve Data: " + (msg.Length > 0 ? msg : "request failed.")),
            _ => null,
        };
    }

    private Task<JsonNode> GetAsync(string path, TimeSpan cacheFor, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Add a Twelve Data API key under Sources.");
        return _http.GetJsonAsync($"{Base}{path}{(path.Contains('?') ? "&" : "?")}apikey={Uri.EscapeDataString(key.Trim())}", cacheFor, ct);
    }

    /// <summary>Calls that only add colour: a plan-gated or failed answer just leaves those fields empty.</summary>
    private async Task<JsonNode?> OptionalAsync(string path, TimeSpan cacheFor, CancellationToken ct)
    {
        try { return await GetAsync(path, cacheFor, ct); }
        catch (Exception ex) when (ex is NotSupportedException or ProviderException) { return null; }
    }

    public async Task TestKeyAsync(CancellationToken ct)
    {
        var q = await GetAsync("/quote?symbol=AAPL", TimeSpan.Zero, ct);
        if (Num(q["close"]) is not > 0) throw new ProviderException("Twelve Data answered, but without a price.");
    }

    // ---------------------------------------------------------------- symbols

    private static readonly Dictionary<string, string> SuffixToExchange = new(StringComparer.OrdinalIgnoreCase)
    {
        [".TO"] = "TSX", [".V"] = "TSXV", [".NE"] = "NEO", [".CN"] = "CSE", [".L"] = "LSE",
    };

    private static readonly HashSet<string> UsExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "NASDAQ", "NYSE", "AMEX", "NYSE American", "NYSE ARCA", "NYSEARCA", "BATS", "CBOE", "IEX", "OTC",
    };

    /// <summary>Funnel's Yahoo-style ticker → Twelve Data's symbol (with exchange when it isn't a US listing).</summary>
    internal static string ToTd(string symbol)
    {
        var s = symbol.Trim().ToUpperInvariant();
        if (s.Contains(':')) return s;
        foreach (var (suffix, exch) in SuffixToExchange)
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return s[..^suffix.Length].Replace('-', '.') + ":" + exch;
        return s.Replace('-', '.');
    }

    /// <summary>Twelve Data symbol + exchange → the ticker the rest of Funnel uses.</summary>
    internal static string FromTd(string symbol, string? exchange)
    {
        var ex = exchange ?? "";
        if (ex.Length == 0 || UsExchanges.Contains(ex)) return symbol.Replace('.', '-');
        var suffix = SuffixToExchange.FirstOrDefault(kv => kv.Value.Equals(ex, StringComparison.OrdinalIgnoreCase)).Key;
        return suffix != null ? symbol.Replace('.', '-') + suffix : symbol + ":" + ex;
    }

    private static string Q(string symbol) => Uri.EscapeDataString(ToTd(symbol));

    // ---------------------------------------------------------------- search, board, movers

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();
        var root = await GetAsync($"/symbol_search?symbol={Uri.EscapeDataString(query.Trim())}&outputsize=30", Medium, ct);
        static int Rank(string? country) => country switch { "United States" => 0, "Canada" => 1, _ => 2 };
        return Arr(root["data"])
            .Where(r => Str(r?["instrument_type"]) is { } t && (t.Contains("Stock") || t == "ETF" || t.Contains("Depositary")))
            .OrderBy(r => Rank(Str(r?["country"])))
            .Select(r => new SearchHit(FromTd(Str(r?["symbol"]) ?? "", Str(r?["exchange"])), Str(r?["instrument_name"]) ?? "",
                Str(r?["exchange"]) ?? "", Str(r?["instrument_type"]) == "ETF" ? "ETF" : "Equity"))
            .Where(h => h.Symbol.Length > 0).DistinctBy(h => h.Symbol).Take(8).ToList();
    }

    // Twelve Data doesn't carry the US benchmark indices (or the VIX), so those tiles are the ETFs
    // that track them, labelled as such. Rates come from the US Treasury group on the same page.
    private static readonly (string Symbol, string Name, string Group, string? Unit)[] Board =
    {
        ("SPY", "S&P 500 · SPY ETF", "US equities", null),
        ("QQQ", "Nasdaq-100 · QQQ ETF", "US equities", null),
        ("DIA", "Dow Jones · DIA ETF", "US equities", null),
        ("IWM", "Russell 2000 · IWM ETF", "US equities", null),
        ("GSPTSE", "S&P/TSX Composite", "Global equities", null),
        ("FTSE", "FTSE 100", "Global equities", null),
        ("FEZ", "Euro Stoxx 50 · FEZ ETF", "Global equities", null),
        ("N225", "Nikkei 225", "Global equities", null),
        ("WTI/USD", "WTI crude", "Commodities & FX", null),
        ("XAU/USD", "Gold", "Commodities & FX", null),
        ("USD/CAD", "USD / CAD", "Commodities & FX", "fx"),
        ("EUR/USD", "EUR / USD", "Commodities & FX", "fx"),
        ("BTC/USD", "Bitcoin", "Commodities & FX", null),
    };

    public async Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct)
    {
        var list = string.Join(",", Board.Select(b => b.Symbol));
        var root = await GetAsync($"/quote?symbol={Uri.EscapeDataString(list)}", Short, ct);
        var tiles = Board.Select(b =>
        {
            var q = root[b.Symbol];
            if (q == null || Str(q["status"]) == "error" || Num(q["close"]) == null) return null;
            return new MarketQuote(b.Symbol, b.Name, Num(q["close"]), Num(q["change"]), Num(q["percent_change"]), Str(q["currency"]), b.Group, b.Unit);
        }).OfType<MarketQuote>().ToList();
        if (tiles.Count == 0) throw new ProviderException("Twelve Data returned no prices for the market board.");
        return tiles;
    }

    public async Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct)
    {
        var direction = kind == "losers" ? "losers" : "gainers";
        if (kind == "actives") throw new NotSupportedException("Twelve Data has no most-active list.");
        var root = await GetAsync($"/market_movers/stocks?direction={direction}&outputsize=8", Short, ct);
        return Arr(root["values"]).Select(q => new MarketQuote(
            FromTd(Str(q?["symbol"]) ?? "", Str(q?["exchange"])), Str(q?["name"]) ?? "",
            Num(q?["last"]), Num(q?["change"]), Num(q?["percent_change"]), "USD", kind)).ToList();
    }

    // ---------------------------------------------------------------- company & comps

    private sealed record Core(JsonNode Quote, JsonNode? Profile, JsonNode? StatsRoot)
    {
        public JsonNode? Stats => StatsRoot?["statistics"];
    }

    private async Task<Core> CoreAsync(string symbol, bool withProfile, CancellationToken ct)
    {
        var s = Q(symbol);
        var quote = GetAsync($"/quote?symbol={s}", Short, ct);
        var profile = withProfile ? OptionalAsync($"/profile?symbol={s}", Long, ct) : Task.FromResult<JsonNode?>(null);
        var stats = OptionalAsync($"/statistics?symbol={s}", Long, ct);
        await Task.WhenAll((Task)quote, profile, stats);
        return new Core(quote.Result, profile.Result, stats.Result);
    }

    private static Dictionary<string, double?> Metrics(Core c)
    {
        var st = c.Stats;
        var v = st?["valuations_metrics"];
        var f = st?["financials"];
        var inc = f?["income_statement"];
        var bal = f?["balance_sheet"];
        var ss = st?["stock_statistics"];
        var ps = st?["stock_price_summary"];
        var div = st?["dividends_and_splits"];
        var price = Num(c.Quote["close"]);
        var mcap = Num(v?["market_capitalization"]);
        var fcf = Num(f?["cash_flow"]?["levered_free_cash_flow_ttm"]);
        var revenue = Num(inc?["revenue_ttm"]);
        var ebitda = Num(inc?["ebitda"]);
        var fwdPe = Num(v?["forward_pe"]);
        return new Dictionary<string, double?>
        {
            ["marketCap"] = mcap,
            ["enterpriseValue"] = Num(v?["enterprise_value"]),
            ["trailingPE"] = Num(v?["trailing_pe"]),
            ["forwardPE"] = fwdPe,
            ["pegRatio"] = Num(v?["peg_ratio"]),
            ["priceToBook"] = Num(v?["price_to_book_mrq"]),
            ["priceToSales"] = Num(v?["price_to_sales_ttm"]),
            ["evToRevenue"] = Num(v?["enterprise_to_revenue"]),
            ["evToEbitda"] = Num(v?["enterprise_to_ebitda"]),
            ["fcfYield"] = fcf != null && mcap is > 0 ? fcf / mcap : null,
            ["dividendYield"] = Num(div?["trailing_annual_dividend_yield"]) ?? Num(div?["forward_annual_dividend_yield"]),
            ["grossMargin"] = Num(f?["gross_margin"]),
            ["ebitdaMargin"] = ebitda != null && revenue is > 0 ? ebitda / revenue : null,
            ["operatingMargin"] = Num(f?["operating_margin"]),
            ["profitMargin"] = Num(f?["profit_margin"]),
            ["returnOnEquity"] = Num(f?["return_on_equity_ttm"]),
            ["returnOnAssets"] = Num(f?["return_on_assets_ttm"]),
            ["totalRevenue"] = revenue,
            ["revenueGrowth"] = Num(inc?["quarterly_revenue_growth"]),
            ["ebitda"] = ebitda,
            ["earningsGrowth"] = Num(inc?["quarterly_earnings_growth_yoy"]),
            ["freeCashflow"] = fcf,
            ["totalCash"] = Num(bal?["total_cash_mrq"]),
            ["totalDebt"] = Num(bal?["total_debt_mrq"]),
            // Sent as a percentage (78.4 = 0.78x), like Yahoo's.
            ["debtToEquity"] = Num(bal?["total_debt_to_equity_mrq"]) / 100,
            ["fiftyTwoWeekLow"] = Num(ps?["fifty_two_week_low"]) ?? Num(c.Quote["fifty_two_week"]?["low"]),
            ["fiftyTwoWeekHigh"] = Num(ps?["fifty_two_week_high"]) ?? Num(c.Quote["fifty_two_week"]?["high"]),
            ["beta"] = Num(ps?["beta"]),
            ["averageVolume"] = Num(ss?["avg_90_volume"]) ?? Num(c.Quote["average_volume"]),
            ["sharesOutstanding"] = Num(ss?["shares_outstanding"]),
            ["trailingEps"] = Num(inc?["diluted_eps_ttm"]),
            ["forwardEps"] = price != null && fwdPe is > 0 ? price / fwdPe : null,
        };
    }

    public async Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var s = Q(symbol);
        var coreTask = CoreAsync(symbol, withProfile: true, ct);
        var recsTask = OptionalAsync($"/recommendations?symbol={s}", Long, ct);
        var targetTask = OptionalAsync($"/price_target?symbol={s}", Long, ct);
        var ratingsTask = OptionalAsync($"/analyst_ratings/us_equities?symbol={s}&outputsize=120", Long, ct);
        await Task.WhenAll(coreTask, recsTask, targetTask, ratingsTask);
        var c = coreTask.Result;
        var q = c.Quote;
        var p = c.Profile;
        var price = Num(q["close"]);

        return new CompanySnapshot
        {
            Symbol = symbol,
            Name = Str(p?["name"]) ?? Str(q["name"]) ?? symbol,
            Exchange = Str(q["exchange"]) ?? "",
            Currency = Str(q["currency"]) ?? "",
            FinancialCurrency = Str(c.StatsRoot?["meta"]?["currency"]),
            QuoteType = Str(p?["type"]) == "ETF" ? "ETF" : "EQUITY",
            Sector = Str(p?["sector"]),
            Industry = Str(p?["industry"]),
            Country = Str(p?["country"]),
            City = Str(p?["city"]),
            Website = Str(p?["website"]),
            Employees = (long?)Num(p?["employees"]),
            Summary = Str(p?["description"]),
            Price = price,
            Change = Num(q["change"]),
            ChangePct = Num(q["percent_change"]),
            MarketState = q["is_market_open"] is JsonValue open && open.TryGetValue<bool>(out var o) ? (o ? "REGULAR" : "CLOSED") : null,
            Metrics = Metrics(c),
            Analysts = Analysts(recsTask.Result, targetTask.Result, ratingsTask.Result, price),
            Source = Name,
        };
    }

    public async Task<CompRow> CompAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        try
        {
            var c = await CoreAsync(symbol, withProfile: false, ct);
            return new CompRow
            {
                Symbol = symbol,
                Name = Str(c.Quote["name"]) ?? symbol,
                Currency = Str(c.Quote["currency"]) ?? "",
                Price = Num(c.Quote["close"]),
                ChangePct = Num(c.Quote["percent_change"]),
                Metrics = Metrics(c),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompRow { Symbol = symbol, Name = symbol, Error = ex.Message };
        }
    }

    // ---------------------------------------------------------------- analysts

    private static AnalystView Analysts(JsonNode? recs, JsonNode? target, JsonNode? ratings, double? price)
    {
        var trends = recs?["trends"];
        var trend = new[] { ("current_month", "Current"), ("previous_month", "1 month ago"), ("2_months_ago", "2 months ago"), ("3_months_ago", "3 months ago") }
            .Select(x => (Node: trends?[x.Item1], Label: x.Item2))
            .Where(x => x.Node != null)
            .Select(x => new RatingTrend(x.Label, (int)(Num(x.Node!["strong_buy"]) ?? 0), (int)(Num(x.Node!["buy"]) ?? 0),
                (int)(Num(x.Node!["hold"]) ?? 0), (int)(Num(x.Node!["sell"]) ?? 0), (int)(Num(x.Node!["strong_sell"]) ?? 0)))
            .ToList();
        double? mean = trend.FirstOrDefault() is { Total: > 0 } t
            ? (1.0 * t.StrongBuy + 2 * t.Buy + 3 * t.Hold + 4 * t.Sell + 5 * t.StrongSell) / t.Total : null;
        // Same words Yahoo uses, so the UI's labels and colours carry over.
        var key = mean switch { null => null, <= 1.5 => "strong_buy", <= 2.5 => "buy", <= 3.5 => "hold", <= 4.5 => "underperform", _ => "sell" };

        // Latest action per firm, last 18 months (older calls overstate who's behind each rating).
        var cutoff = DateTime.UtcNow.AddMonths(-18);
        var firms = Arr(ratings?["ratings"])
            .Select(r => new
            {
                Firm = Str(r?["firm"]) ?? "",
                To = Str(r?["rating_current"]) ?? "",
                From = Str(r?["rating_prior"]),
                Action = Str(r?["rating_change"]) ?? "",
                When = DateTime.TryParse(Str(r?["date"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : DateTime.MinValue,
                Pt = Num(r?["price_target_current"]),
                PriorPt = Num(r?["price_target_prior"]),
            })
            .Where(r => r.Firm.Length > 0 && r.To.Length > 0 && r.When >= cutoff)
            .OrderByDescending(r => r.When)
            .GroupBy(r => r.Firm, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(r => new FirmRating(r.Firm, r.To, string.IsNullOrEmpty(r.From) ? null : r.From, YahooProvider.Bucket(r.To),
                r.Action.ToLowerInvariant() switch
                {
                    "upgrades" or "upgrade" => "Upgrade", "downgrades" or "downgrade" => "Downgrade",
                    "initiates" or "initiate" or "initiated" => "Initiated", "reiterates" or "reiterated" => "Reiterated", _ => "Maintained",
                }, r.When, r.Pt is > 0 ? r.Pt : null, r.PriorPt is > 0 ? r.PriorPt : null))
            .ToList();

        var pt = target?["price_target"];
        return new AnalystView
        {
            RecommendationKey = key,
            RecommendationMean = mean,
            AnalystCount = trend.FirstOrDefault()?.Total,
            CurrentPrice = Num(pt?["current"]) ?? price,
            TargetLow = Num(pt?["low"]),
            TargetMean = Num(pt?["average"]),
            TargetMedian = Num(pt?["median"]),
            TargetHigh = Num(pt?["high"]),
            Trend = trend,
            Firms = firms,
        };
    }

    /// <summary>Consensus revenue growth for the fiscal year in progress, else next year's.</summary>
    public async Task<double?> RevenueGrowthEstimateAsync(string symbol, CancellationToken ct)
    {
        var root = await GetAsync($"/revenue_estimate?symbol={Q(symbol)}", Long, ct);
        var rows = Arr(root["revenue_estimate"]).ToList();
        foreach (var period in new[] { "current_year", "next_year" })
            if (rows.FirstOrDefault(r => Str(r?["period"]) == period) is { } r && Num(r["sales_growth"]) is { } g) return g;
        return null;
    }

    // ---------------------------------------------------------------- chart

    private static TimeZoneInfo Zone(string? id)
    {
        try { return id is { Length: > 0 } ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Utc; }
        catch { return TimeZoneInfo.Utc; }
    }

    public async Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var (interval, query, label) = range switch
        {
            "1d" => ("5min", "outputsize=200", "5m"),
            "5d" => ("15min", "outputsize=200", "15m"),
            "1mo" => ("1day", $"start_date={today.AddMonths(-1):yyyy-MM-dd}", "1d"),
            "6mo" => ("1day", $"start_date={today.AddMonths(-6):yyyy-MM-dd}", "1d"),
            "ytd" => ("1day", $"start_date={today.Year}-01-01", "1d"),
            "5y" => ("1week", $"start_date={today.AddYears(-5):yyyy-MM-dd}", "1wk"),
            "max" => ("1month", "outputsize=5000", "1mo"),
            "5y-daily" => ("1day", $"start_date={today.AddYears(-5):yyyy-MM-dd}&outputsize=5000", "1d"),
            _ => ("1day", $"start_date={today.AddYears(-1):yyyy-MM-dd}", "1d"),
        };
        var root = await GetAsync($"/time_series?symbol={Q(symbol)}&interval={interval}&{query}&order=asc", range == "1d" ? Short : Medium, ct);
        var meta = root["meta"];
        var intraday = interval.EndsWith("min");
        var zone = Zone(Str(meta?["exchange_timezone"]));

        var pts = new List<(DateTime Local, PricePoint P)>();
        foreach (var b in Arr(root["values"]))
        {
            if (!DateTime.TryParse(Str(b?["datetime"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) || Num(b?["close"]) is not { } close) continue;
            // Intraday bars are exchange-local times; daily and longer bars are dates (kept at midnight UTC, like the other sources).
            var utc = intraday ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone) : DateTime.SpecifyKind(local.Date, DateTimeKind.Utc);
            pts.Add((local, new PricePoint(new DateTimeOffset(utc).ToUnixTimeSeconds(), Num(b?["open"]), Num(b?["high"]), Num(b?["low"]), close, (long?)Num(b?["volume"]))));
        }
        if (pts.Count == 0) throw new ProviderException($"No price history for \"{symbol}\" on Twelve Data.");

        // Match Yahoo: 1D is the latest session only, 5D the last five sessions.
        if (range is "1d" or "5d")
        {
            var days = pts.Select(x => x.Local.Date).Distinct().OrderBy(d => d).TakeLast(range == "1d" ? 1 : 5).ToHashSet();
            pts = pts.Where(x => days.Contains(x.Local.Date)).ToList();
        }

        double? prevClose = null;
        if (range == "1d")
        {
            var q = await OptionalAsync($"/quote?symbol={Q(symbol)}", Short, ct);
            prevClose = Num(q?["previous_close"]);
        }
        return new PriceSeries
        {
            Symbol = symbol.Trim().ToUpperInvariant(),
            Range = range,
            Interval = label,
            Currency = Str(meta?["currency"]) ?? "",
            PreviousClose = prevClose,
            Points = pts.Select(x => x.P).ToList(),
        };
    }

    // ---------------------------------------------------------------- statements

    /// <summary>Shared line key → path into a Twelve Data statement row.</summary>
    private static readonly Dictionary<StatementKind, (string Key, string Path)[]> FieldMap = new()
    {
        [StatementKind.Income] = new[]
        {
            ("TotalRevenue", "sales"), ("CostOfRevenue", "cost_of_goods"), ("GrossProfit", "gross_profit"),
            ("ResearchAndDevelopment", "operating_expense.research_and_development"),
            ("SellingGeneralAndAdministration", "operating_expense.selling_general_and_administrative"),
            ("OtherOperatingExpenses", "operating_expense.other_operating_expenses"), ("OperatingIncome", "operating_income"),
            ("InterestIncomeNonOperating", "non_operating_interest.income"), ("InterestExpenseNonOperating", "non_operating_interest.expense"),
            ("OtherIncomeExpense", "other_income_expense"), ("PretaxIncome", "pretax_income"), ("TaxProvision", "income_tax"),
            ("NetIncomeContinuousOperations", "net_income_continuous_operations"), ("MinorityInterests", "minority_interests"),
            ("NetIncome", "net_income"), ("BasicEPS", "eps_basic"), ("DilutedEPS", "eps_diluted"),
            ("BasicAverageShares", "basic_shares_outstanding"), ("DilutedAverageShares", "diluted_shares_outstanding"),
            ("EBIT", "ebit"), ("EBITDA", "ebitda"),
        },
        [StatementKind.Balance] = new[]
        {
            ("CashAndCashEquivalents", "assets.current_assets.cash_and_cash_equivalents"),
            ("OtherShortTermInvestments", "assets.current_assets.other_short_term_investments"),
            ("AccountsReceivable", "assets.current_assets.accounts_receivable"), ("OtherReceivables", "assets.current_assets.other_receivables"),
            ("Inventory", "assets.current_assets.inventory"), ("PrepaidAssets", "assets.current_assets.prepaid_assets"),
            ("OtherCurrentAssets", "assets.current_assets.other_current_assets"), ("CurrentAssets", "assets.current_assets.total_current_assets"),
            ("AccumulatedDepreciation", "assets.non_current_assets.accumulated_depreciation"), ("Goodwill", "assets.non_current_assets.goodwill"),
            ("OtherIntangibleAssets", "assets.non_current_assets.intangible_assets"),
            ("InvestmentsAndAdvances", "assets.non_current_assets.investments_and_advances"),
            ("OtherNonCurrentAssets", "assets.non_current_assets.other_non_current_assets"),
            ("TotalNonCurrentAssets", "assets.non_current_assets.total_non_current_assets"), ("TotalAssets", "assets.total_assets"),
            ("AccountsPayable", "liabilities.current_liabilities.accounts_payable"),
            ("CurrentAccruedExpenses", "liabilities.current_liabilities.accrued_expenses"),
            ("CurrentDebt", "liabilities.current_liabilities.short_term_debt"),
            ("CurrentDeferredRevenue", "liabilities.current_liabilities.deferred_revenue"),
            ("OtherCurrentLiabilities", "liabilities.current_liabilities.other_current_liabilities"),
            ("CurrentLiabilities", "liabilities.current_liabilities.total_current_liabilities"),
            ("LongTermDebt", "liabilities.non_current_liabilities.long_term_debt"),
            ("OtherNonCurrentLiabilities", "liabilities.non_current_liabilities.other_non_current_liabilities"),
            ("TotalNonCurrentLiabilitiesNetMinorityInterest", "liabilities.non_current_liabilities.total_non_current_liabilities"),
            ("TotalLiabilitiesNetMinorityInterest", "liabilities.total_liabilities"),
            ("CommonStock", "shareholders_equity.common_stock"), ("AdditionalPaidInCapital", "shareholders_equity.additional_paid_in_capital"),
            ("RetainedEarnings", "shareholders_equity.retained_earnings"), ("TreasuryStock", "shareholders_equity.treasury_stock"),
            ("StockholdersEquity", "shareholders_equity.total_shareholders_equity"), ("MinorityInterest", "shareholders_equity.minority_interest"),
        },
        [StatementKind.CashFlow] = new[]
        {
            ("NetIncomeFromContinuingOperations", "operating_activities.net_income"),
            ("DepreciationAndAmortization", "operating_activities.depreciation"), ("DeferredIncomeTax", "operating_activities.deferred_taxes"),
            ("StockBasedCompensation", "operating_activities.stock_based_compensation"), ("OtherNonCashItems", "operating_activities.other_non_cash_items"),
            ("ChangeInReceivables", "operating_activities.accounts_receivable"),
            ("ChangeInPayablesAndAccruedExpense", "operating_activities.accounts_payable"),
            ("OperatingCashFlow", "operating_activities.operating_cash_flow"),
            ("CapitalExpenditure", "investing_activities.capital_expenditures"), ("NetBusinessPurchaseAndSale", "investing_activities.net_acquisitions"),
            ("PurchaseOfInvestment", "investing_activities.purchase_of_investments"), ("SaleOfInvestment", "investing_activities.sale_of_investments"),
            ("NetOtherInvestingChanges", "investing_activities.other_investing_activity"), ("InvestingCashFlow", "investing_activities.investing_cash_flow"),
            ("IssuanceOfDebt", "financing_activities.long_term_debt_issuance"), ("RepaymentOfDebt", "financing_activities.long_term_debt_payments"),
            ("IssuanceOfCapitalStock", "financing_activities.common_stock_issuance"),
            ("RepurchaseOfCapitalStock", "financing_activities.common_stock_repurchase"),
            ("CashDividendsPaid", "financing_activities.common_dividends"), ("NetOtherFinancingCharges", "financing_activities.other_financing_charges"),
            ("FinancingCashFlow", "financing_activities.financing_cash_flow"), ("EndCashPosition", "end_cash_position"),
            ("FreeCashFlow", "free_cash_flow"), ("IncomeTaxPaidSupplementalData", "income_tax_paid"), ("InterestPaidSupplementalData", "interest_paid"),
        },
    };

    private static double? At(JsonNode? row, string path)
    {
        foreach (var part in path.Split('.')) row = row?[part];
        return Num(row);
    }

    private static double? Sum(params double?[] v) => v.All(x => x == null) ? null : v.Sum(x => x ?? 0);

    /// <summary>Lines Twelve Data doesn't send but the sheets (and the DCF) expect, from the ones it does.</summary>
    private static readonly Dictionary<StatementKind, (string Key, Func<Func<string, double?>, double?> Derive)[]> Derived = new()
    {
        [StatementKind.Income] = new (string, Func<Func<string, double?>, double?>)[]
        {
            ("ReconciledDepreciation", v => v("EBITDA") is { } e && v("EBIT") is { } b ? e - b : null),
            ("TaxRateForCalcs", v => v("TaxProvision") is { } t && v("PretaxIncome") is { } p && p != 0 ? t / p : null),
        },
        [StatementKind.Balance] = new (string, Func<Func<string, double?>, double?>)[]
        {
            ("CashCashEquivalentsAndShortTermInvestments", v => Sum(v("CashAndCashEquivalents"), v("OtherShortTermInvestments"))),
            ("TotalEquityGrossMinorityInterest", v => Sum(v("StockholdersEquity"), v("MinorityInterest"))),
            ("TotalDebt", v => Sum(v("CurrentDebt"), v("LongTermDebt"))),
            ("NetDebt", v => v("TotalDebt") is { } d ? d - (v("CashCashEquivalentsAndShortTermInvestments") ?? 0) : null),
            ("WorkingCapital", v => v("CurrentAssets") is { } a && v("CurrentLiabilities") is { } l ? a - l : null),
        },
        [StatementKind.CashFlow] = new (string, Func<Func<string, double?>, double?>)[]
        {
            // Some filers' capex only shows up inside free cash flow.
            ("CapitalExpenditure", v => v("FreeCashFlow") is { } f && v("OperatingCashFlow") is { } o ? f - o : null),
        },
    };

    public async Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct)
    {
        var quarterly = frequency == "quarterly";
        var (endpoint, field) = kind switch
        {
            StatementKind.Income => ("income_statement", "income_statement"),
            StatementKind.Balance => ("balance_sheet", "balance_sheet"),
            _ => ("cash_flow", "cash_flow"),
        };
        var root = await GetAsync($"/{endpoint}?symbol={Q(symbol)}&period={(quarterly ? "quarterly" : "annual")}", Long, ct);
        var rows = Arr(root[field]).Where(r => Str(r?["fiscal_date"]) != null)
            .OrderBy(r => Str(r?["fiscal_date"]), StringComparer.Ordinal).TakeLast(quarterly ? 8 : 5).ToList();
        if (rows.Count == 0) throw new ProviderException($"Twelve Data has no {frequency} figures for {symbol.ToUpperInvariant()}.");

        var values = FieldMap[kind].ToDictionary(m => m.Key, m => rows.Select(r => At(r, m.Path)).ToArray());
        foreach (var (key, derive) in Derived[kind])
        {
            var row = values.TryGetValue(key, out var existing) ? existing : new double?[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                if (row[i] == null) { var idx = i; row[i] = derive(k => values.TryGetValue(k, out var r) ? r[idx] : null); }
            values[key] = row;
        }

        var lines = new List<StatementLine>();
        // Presentation order and labels come from the shared template, so every source looks alike.
        foreach (var item in YahooStatements.For(kind))
        {
            if (item.Format == "header") { lines.Add(new StatementLine(item.Key, item.Label, "header", true, 0, new())); continue; }
            if (!values.TryGetValue(item.Key, out var vals) || vals.All(v => v == null)) continue;
            lines.Add(new StatementLine(item.Key, item.Label, item.Format, item.Emphasis, item.Indent, vals.ToList()));
        }
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Format == "header" && (i == lines.Count - 1 || lines[i + 1].Format == "header"))
                lines.RemoveAt(i);

        return new FinancialStatement
        {
            Kind = kind,
            Title = YahooStatements.Title(kind),
            Frequency = quarterly ? "quarterly" : "annual",
            Currency = Str(root["meta"]?["currency"]) ?? "",
            Periods = rows.Select(r => Str(r?["fiscal_date"])!).ToList(),
            Lines = lines,
            Source = Name,
        };
    }
}
