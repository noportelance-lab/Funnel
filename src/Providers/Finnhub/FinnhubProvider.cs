using System.Net;
using System.Text.Json.Nodes;
using static Funnel.Providers.J;

namespace Funnel.Providers.Finnhub;

/// <summary>
/// Finnhub (free key, 60 calls a minute): industry-based peers, monthly recommendation counts,
/// ~100 TTM ratios and company news. No price history or price targets on the free plan, so
/// charts and the firm-by-firm analyst list stay with Yahoo.
/// </summary>
public sealed class FinnhubProvider : IFinanceProvider, IPeerSource, INewsSource, IKeyedSource
{
    public string Id => "finnhub";
    public string Name => "Finnhub";
    public IReadOnlyCollection<string> Sections { get; } = new[] { Section.Search, Section.Company, Section.Comps, Section.Peers, Section.News };
    public string KeyUrl => "https://finnhub.io/register";
    public string KeyNote => "Free key · 60 calls/min · personal use";

    private const string Base = "https://finnhub.io/api/v1";
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Long = TimeSpan.FromHours(6);

    private readonly Func<string?> _key;
    private readonly HttpSource _http;

    public FinnhubProvider(Func<string?> key)
    {
        _key = key;
        _http = new HttpSource("Finnhub", "Funnel")
        {
            Classify = (status, body) => status switch
            {
                HttpStatusCode.Unauthorized => new ProviderException("Finnhub rejected the API key. Check it under Sources."),
                HttpStatusCode.Forbidden => new NotSupportedException("Not in Finnhub's free plan."),
                _ => null,
            },
        };
    }

    private Task<JsonNode> GetAsync(string path, TimeSpan cacheFor, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Add a Finnhub API key under Sources (it's free).");
        return _http.GetJsonAsync(Base + path, cacheFor, ct, req => req.Headers.Add("X-Finnhub-Token", key));
    }

    public async Task TestKeyAsync(CancellationToken ct)
    {
        var q = await GetAsync("/quote?symbol=AAPL", TimeSpan.Zero, ct);
        if (Num(q["c"]) is not > 0) throw new ProviderException("Finnhub answered, but without a price. The key may be inactive.");
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();
        var root = await GetAsync($"/search?q={Uri.EscapeDataString(query.Trim())}&exchange=US", TimeSpan.FromMinutes(5), ct);
        return Arr(root["result"]).Take(8).Select(r => new SearchHit(
            Str(r?["symbol"]) ?? "", Str(r?["description"]) ?? "", "US", Str(r?["type"]) ?? "")).Where(h => h.Symbol.Length > 0).ToList();
    }

    // ---------------------------------------------------------------- company

    private async Task<(JsonNode Profile, JsonNode Quote, JsonNode Metric)> CoreAsync(string symbol, CancellationToken ct)
    {
        var s = Uri.EscapeDataString(symbol);
        var profile = GetAsync($"/stock/profile2?symbol={s}", Long, ct);
        var quote = GetAsync($"/quote?symbol={s}", Short, ct);
        var metric = GetAsync($"/stock/metric?symbol={s}&metric=all", Long, ct);
        await Task.WhenAll(profile, quote, metric);
        if (Str(profile.Result["name"]) == null && Num(quote.Result["c"]) is not > 0)
            throw new ProviderException($"Finnhub has no data for \"{symbol}\" (the free plan covers US listings).");
        return (profile.Result, quote.Result, metric.Result["metric"] ?? new JsonObject());
    }

    public async Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var peersTask = PeersAsync(symbol, ct);
        var recTask = GetAsync($"/stock/recommendation?symbol={Uri.EscapeDataString(symbol)}", Long, ct);
        var (p, q, m) = await CoreAsync(symbol, ct);

        var trend = new List<RatingTrend>();
        try
        {
            var labels = new[] { "Current", "1 month ago", "2 months ago", "3 months ago" };
            trend = Arr(await recTask).Take(4).Select((t, i) => new RatingTrend(labels[i],
                (int)(Num(t?["strongBuy"]) ?? 0), (int)(Num(t?["buy"]) ?? 0), (int)(Num(t?["hold"]) ?? 0),
                (int)(Num(t?["sell"]) ?? 0), (int)(Num(t?["strongSell"]) ?? 0))).ToList();
        }
        catch (Exception ex) when (ex is ProviderException or NotSupportedException) { }

        double? recMean = null;
        if (trend.FirstOrDefault() is { Total: > 0 } t0)
            recMean = (1.0 * t0.StrongBuy + 2 * t0.Buy + 3 * t0.Hold + 4 * t0.Sell + 5 * t0.StrongSell) / t0.Total;

        PeerSet? peers = null;
        try { peers = await peersTask; } catch (Exception ex) when (ex is ProviderException or NotSupportedException) { }

        var price = Num(q["c"]);
        return new CompanySnapshot
        {
            Symbol = Str(p["ticker"]) ?? symbol,
            Name = Str(p["name"]) ?? symbol,
            Exchange = Str(p["exchange"]) ?? "",
            Currency = Str(p["currency"]) ?? "USD",
            FinancialCurrency = Str(p["currency"]),
            QuoteType = "EQUITY",
            Industry = Str(p["finnhubIndustry"]),
            Country = Str(p["country"]),
            Website = Str(p["weburl"]),
            Price = price,
            Change = Num(q["d"]),
            ChangePct = Num(q["dp"]),
            Metrics = Metrics(m, p, price),
            Analysts = new AnalystView
            {
                RecommendationMean = recMean,
                RecommendationKey = recMean switch
                {
                    null => null, <= 1.5 => "strong_buy", <= 2.5 => "buy", <= 3.5 => "hold", <= 4.5 => "sell", _ => "strong_sell",
                },
                AnalystCount = trend.FirstOrDefault()?.Total,
                CurrentPrice = price,
                Trend = trend,
            },
            Peers = peers?.Symbols ?? new(),
            PeersBasis = peers?.Basis,
            Source = Name,
        };
    }

    /// <summary>Finnhub sends margins and yields in percent and money in millions; the app wants fractions and units.</summary>
    private static Dictionary<string, double?> Metrics(JsonNode m, JsonNode p, double? price)
    {
        double? N(params string[] keys) { foreach (var k in keys) if (Num(m[k]) is { } v) return v; return null; }
        double? Pct(params string[] keys) => N(keys) / 100;
        var shares = Num(p["shareOutstanding"]) * 1e6;
        var mcap = (Num(p["marketCapitalization"]) ?? N("marketCapitalization")) * 1e6;
        var ev = N("enterpriseValue") * 1e6;
        var revPs = N("revenuePerShareTTM");
        var ebitdaPs = N("ebitdPerShareTTM");
        var pfcf = N("pfcfShareTTM");
        var de = N("totalDebt/totalEquityQuarterly", "totalDebt/totalEquityAnnual");
        var bvps = N("bookValuePerShareQuarterly", "bookValuePerShareAnnual");
        return new Dictionary<string, double?>
        {
            ["marketCap"] = mcap,
            ["enterpriseValue"] = ev,
            ["trailingPE"] = N("peTTM", "peBasicExclExtraTTM", "peExclExtraTTM"),
            ["forwardPE"] = null,
            ["pegRatio"] = null,
            ["priceToBook"] = N("pbQuarterly", "pbAnnual", "pb"),
            ["priceToSales"] = N("psTTM", "psAnnual"),
            ["evToRevenue"] = N("evRevenueTTM") ?? (ev != null && revPs is > 0 && shares is > 0 ? ev / (revPs * shares) : null),
            ["evToEbitda"] = N("evEbitdaTTM") ?? (ev != null && ebitdaPs is > 0 && shares is > 0 ? ev / (ebitdaPs * shares) : null),
            ["fcfYield"] = pfcf is > 0 ? 1 / pfcf : null,
            ["dividendYield"] = Pct("currentDividendYieldTTM", "dividendYieldIndicatedAnnual"),
            ["grossMargin"] = Pct("grossMarginTTM", "grossMarginAnnual"),
            ["ebitdaMargin"] = ebitdaPs != null && revPs is > 0 ? ebitdaPs / revPs : null,
            ["operatingMargin"] = Pct("operatingMarginTTM", "operatingMarginAnnual"),
            ["profitMargin"] = Pct("netProfitMarginTTM", "netProfitMarginAnnual"),
            ["returnOnEquity"] = Pct("roeTTM", "roeRfy"),
            ["returnOnAssets"] = Pct("roaTTM", "roaRfy"),
            ["totalRevenue"] = revPs * shares,
            ["revenueGrowth"] = Pct("revenueGrowthTTMYoy", "revenueGrowthQuarterlyYoy"),
            ["ebitda"] = ebitdaPs * shares,
            ["earningsGrowth"] = Pct("epsGrowthTTMYoy", "epsGrowthQuarterlyYoy"),
            ["freeCashflow"] = pfcf is > 0 && mcap != null ? mcap / pfcf : null,
            ["totalCash"] = N("cashPerSharePerShareQuarterly", "cashPerSharePerShareAnnual") * shares,
            ["totalDebt"] = de * bvps * shares,
            ["debtToEquity"] = de,
            ["fiftyTwoWeekLow"] = N("52WeekLow"),
            ["fiftyTwoWeekHigh"] = N("52WeekHigh"),
            ["beta"] = N("beta"),
            ["averageVolume"] = N("3MonthAverageTradingVolume") * 1e6,
            ["sharesOutstanding"] = shares,
            ["trailingEps"] = N("epsTTM", "epsExclExtraItemsTTM", "epsBasicExclExtraItemsTTM"),
            ["forwardEps"] = null,
        };
    }

    public async Task<CompRow> CompAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        try
        {
            var (p, q, m) = await CoreAsync(symbol, ct);
            var price = Num(q["c"]);
            return new CompRow
            {
                Symbol = symbol,
                Name = Str(p["name"]) ?? symbol,
                Currency = Str(p["currency"]) ?? "USD",
                Price = price,
                ChangePct = Num(q["dp"]),
                Metrics = Metrics(m, p, price),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompRow { Symbol = symbol, Name = symbol, Error = ex.Message };
        }
    }

    public async Task<PeerSet> PeersAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var root = await GetAsync($"/stock/peers?symbol={Uri.EscapeDataString(symbol)}", Long, ct);
        var peers = Arr(root).Select(Str).OfType<string>()
            .Where(s => !s.Equals(symbol, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        return new PeerSet(peers, "Finnhub: same sub-industry", Name);
    }

    public async Task<IReadOnlyList<NewsItem>> NewsAsync(string symbol, CancellationToken ct)
    {
        var to = DateTime.UtcNow.Date;
        var root = await GetAsync($"/company-news?symbol={Uri.EscapeDataString(symbol.Trim().ToUpperInvariant())}&from={to.AddDays(-14):yyyy-MM-dd}&to={to:yyyy-MM-dd}",
            TimeSpan.FromMinutes(15), ct);
        return Arr(root).Select(n => new NewsItem(
                Str(n?["headline"]) ?? "", Str(n?["source"]) ?? "",
                DateTimeOffset.FromUnixTimeSeconds((long)(Num(n?["datetime"]) ?? 0)).UtcDateTime,
                Str(n?["url"]) ?? "", Str(n?["summary"])))
            .Where(n => n.Headline.Length > 0 && n.Url.StartsWith("http"))
            .Take(12).ToList();
    }

    public Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct) => throw new NotSupportedException();
    public Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct) => throw new NotSupportedException("Not in Finnhub's free plan.");
    public Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct) => throw new NotSupportedException();
}
