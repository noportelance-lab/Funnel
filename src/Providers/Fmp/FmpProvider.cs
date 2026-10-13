using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Funnel.Providers.Yahoo;
using static Funnel.Providers.J;

namespace Funnel.Providers.Fmp;

/// <summary>
/// Financial Modeling Prep, "stable" API (free key, 250 calls a day). The closest thing to a
/// drop-in replacement for Yahoo: profile, quote, TTM ratios, statements, daily prices, peers,
/// named-firm grades and price-target consensus. Paid-only endpoints answer 402 and become
/// <see cref="NotSupportedException"/>, so that section falls back to Yahoo.
/// </summary>
public sealed class FmpProvider : IFinanceProvider, IPeerSource, IKeyedSource
{
    public string Id => "fmp";
    public string Name => "Financial Modeling Prep";
    public IReadOnlyCollection<string> Sections { get; } = new[]
    {
        Section.Search, Section.Company, Section.Chart, Section.Comps, Section.Statements, Section.Peers, Section.Movers,
    };
    public string KeyUrl => "https://site.financialmodelingprep.com/register";
    public string KeyNote => "Free key · 250 calls/day · US listings";

    private const string Base = "https://financialmodelingprep.com/stable";
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Long = TimeSpan.FromHours(6);

    private readonly Func<string?> _key;
    private readonly HttpSource _http;

    public FmpProvider(Func<string?> key)
    {
        _key = key;
        _http = new HttpSource("Financial Modeling Prep", "Funnel")
        {
            Classify = (status, body) =>
            {
                var paid = body.Contains("Restricted Endpoint", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("Premium", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("Exclusive Endpoint", StringComparison.OrdinalIgnoreCase);
                if (status is HttpStatusCode.PaymentRequired || (status is HttpStatusCode.Forbidden && paid) || (status == HttpStatusCode.OK && paid && body.TrimStart().StartsWith('{')))
                    return new NotSupportedException("Not in the FMP free plan.");
                if (status is HttpStatusCode.Unauthorized || body.Contains("Invalid API KEY", StringComparison.OrdinalIgnoreCase))
                    return new ProviderException("Financial Modeling Prep rejected the API key. Check it under Sources.");
                if ((int)status == 429 || body.Contains("Limit Reach", StringComparison.OrdinalIgnoreCase))
                    return new ProviderException("Financial Modeling Prep's daily limit (250 calls on the free plan) is used up. It resets at midnight UTC.");
                return null;
            },
        };
    }

    private Task<JsonNode> GetAsync(string path, TimeSpan cacheFor, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Add a Financial Modeling Prep API key under Sources (it's free).");
        return _http.GetJsonAsync($"{Base}{path}{(path.Contains('?') ? "&" : "?")}apikey={Uri.EscapeDataString(key.Trim())}", cacheFor, ct);
    }

    private async Task<JsonNode?> FirstAsync(string path, TimeSpan cacheFor, CancellationToken ct) =>
        Arr(await GetAsync(path, cacheFor, ct)).FirstOrDefault();

    /// <summary>Calls that only add colour: a paid-only or failed answer just leaves those fields empty.</summary>
    private async Task<JsonNode?> OptionalAsync(string path, TimeSpan cacheFor, CancellationToken ct)
    {
        try { return await GetAsync(path, cacheFor, ct); }
        catch (Exception ex) when (ex is NotSupportedException or ProviderException) { return null; }
    }

    public async Task TestKeyAsync(CancellationToken ct)
    {
        if (await FirstAsync("/quote?symbol=AAPL", TimeSpan.Zero, ct) is not { } q || Num(q["price"]) is not > 0)
            throw new ProviderException("Financial Modeling Prep answered, but without a price.");
    }

    private static string S(string symbol) => Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

    // ---------------------------------------------------------------- search & movers

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchHit>();
        var q = Uri.EscapeDataString(query.Trim());
        var bySymbol = GetAsync($"/search-symbol?query={q}&limit=8", TimeSpan.FromMinutes(10), ct);
        var byName = OptionalAsync($"/search-name?query={q}&limit=8", TimeSpan.FromMinutes(10), ct);
        await Task.WhenAll((Task)bySymbol, byName);
        return Arr(bySymbol.Result).Concat(Arr(byName.Result))
            .Select(r => new SearchHit(Str(r?["symbol"]) ?? "", Str(r?["name"]) ?? "",
                Str(r?["exchange"]) ?? Str(r?["exchangeFullName"]) ?? "", "Equity"))
            .Where(h => h.Symbol.Length > 0).DistinctBy(h => h.Symbol).Take(8).ToList();
    }

    public async Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct)
    {
        var path = kind switch { "losers" => "/biggest-losers", "actives" => "/most-actives", _ => "/biggest-gainers" };
        return Arr(await GetAsync(path, Short, ct)).Take(8).Select(q => new MarketQuote(
            Str(q?["symbol"]) ?? "", Str(q?["name"]) ?? "", Num(q?["price"]), Num(q?["change"]),
            Num(q?["changesPercentage"]) ?? Num(q?["changePercentage"]), "USD", kind)).ToList();
    }

    public Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct) => throw new NotSupportedException();

    // ---------------------------------------------------------------- company

    private sealed record Core(JsonNode Profile, JsonNode? Quote, JsonNode? KeyMetrics, JsonNode? Ratios);

    private async Task<Core> CoreAsync(string symbol, CancellationToken ct)
    {
        var s = S(symbol);
        var profile = FirstAsync($"/profile?symbol={s}", Long, ct);
        var quote = OptionalAsync($"/quote?symbol={s}", Short, ct);
        var km = OptionalAsync($"/key-metrics-ttm?symbol={s}", Long, ct);
        var ratios = OptionalAsync($"/ratios-ttm?symbol={s}", Long, ct);
        await Task.WhenAll(profile, quote, km, ratios);
        var p = profile.Result ?? throw new ProviderException($"Financial Modeling Prep has no profile for \"{symbol}\".");
        return new Core(p, Arr(quote.Result).FirstOrDefault(), Arr(km.Result).FirstOrDefault(), Arr(ratios.Result).FirstOrDefault());
    }

    private static Dictionary<string, double?> Metrics(Core c)
    {
        var p = c.Profile; var q = c.Quote; var km = c.KeyMetrics; var r = c.Ratios;
        var price = Num(q?["price"]) ?? Num(p["price"]);
        var mcap = Num(q?["marketCap"]) ?? Num(p["marketCap"]) ?? Num(km?["marketCap"]);
        var ev = Num(km?["enterpriseValueTTM"]);
        var evEbitda = Num(km?["evToEBITDATTM"]);
        var fcfYield = Num(km?["freeCashFlowYieldTTM"]);
        var shares = mcap != null && price is > 0 ? mcap / price : null;
        var range = (Str(p["range"]) ?? "").Split('-', StringSplitOptions.TrimEntries);
        double? Rng(int i) => range.Length == 2 && double.TryParse(range[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var ebitda = ev != null && evEbitda is > 0 ? ev / evEbitda : null;
        return new Dictionary<string, double?>
        {
            ["marketCap"] = mcap,
            ["enterpriseValue"] = ev,
            ["trailingPE"] = Num(r?["priceToEarningsRatioTTM"]),
            ["forwardPE"] = null,
            ["pegRatio"] = Num(r?["priceToEarningsGrowthRatioTTM"]),
            ["priceToBook"] = Num(r?["priceToBookRatioTTM"]),
            ["priceToSales"] = Num(r?["priceToSalesRatioTTM"]),
            ["evToRevenue"] = Num(km?["evToSalesTTM"]),
            ["evToEbitda"] = evEbitda,
            ["fcfYield"] = fcfYield,
            ["dividendYield"] = Num(r?["dividendYieldTTM"]),
            ["grossMargin"] = Num(r?["grossProfitMarginTTM"]),
            ["ebitdaMargin"] = Num(r?["ebitdaMarginTTM"]),
            ["operatingMargin"] = Num(r?["operatingProfitMarginTTM"]),
            ["profitMargin"] = Num(r?["netProfitMarginTTM"]),
            ["returnOnEquity"] = Num(km?["returnOnEquityTTM"]),
            ["returnOnAssets"] = Num(km?["returnOnAssetsTTM"]),
            ["totalRevenue"] = Num(r?["revenuePerShareTTM"]) * shares,
            ["revenueGrowth"] = null,
            ["ebitda"] = ebitda,
            ["earningsGrowth"] = null,
            ["freeCashflow"] = fcfYield * mcap,
            ["totalCash"] = null,
            ["totalDebt"] = null,
            ["debtToEquity"] = Num(r?["debtToEquityRatioTTM"]),
            ["fiftyTwoWeekLow"] = Num(q?["yearLow"]) ?? Rng(0),
            ["fiftyTwoWeekHigh"] = Num(q?["yearHigh"]) ?? Rng(1),
            ["beta"] = Num(p["beta"]),
            ["averageVolume"] = Num(p["averageVolume"]),
            ["sharesOutstanding"] = shares,
            ["trailingEps"] = Num(r?["netIncomePerShareTTM"]),
            ["forwardEps"] = null,
        };
    }

    public async Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var s = S(symbol);
        var coreTask = CoreAsync(symbol, ct);
        var gradesTask = OptionalAsync($"/grades?symbol={s}", Long, ct);
        var consensusTask = OptionalAsync($"/grades-consensus?symbol={s}", Long, ct);
        var targetTask = OptionalAsync($"/price-target-consensus?symbol={s}", Long, ct);
        var peersTask = OptionalAsync($"/stock-peers?symbol={s}", Long, ct);
        await Task.WhenAll(coreTask, gradesTask, consensusTask, targetTask, peersTask);
        var c = coreTask.Result;
        var p = c.Profile;
        var price = Num(c.Quote?["price"]) ?? Num(p["price"]);

        var cons = Arr(consensusTask.Result).FirstOrDefault();
        var trend = cons == null ? new List<RatingTrend>() : new List<RatingTrend>
        {
            new("Current", (int)(Num(cons["strongBuy"]) ?? 0), (int)(Num(cons["buy"]) ?? 0), (int)(Num(cons["hold"]) ?? 0),
                (int)(Num(cons["sell"]) ?? 0), (int)(Num(cons["strongSell"]) ?? 0)),
        };
        double? recMean = trend.FirstOrDefault() is { Total: > 0 } t
            ? (1.0 * t.StrongBuy + 2 * t.Buy + 3 * t.Hold + 4 * t.Sell + 5 * t.StrongSell) / t.Total : null;
        var pt = Arr(targetTask.Result).FirstOrDefault();

        var cutoff = DateTime.UtcNow.AddMonths(-18);
        var firms = Arr(gradesTask.Result)
            .Select(g => new
            {
                Firm = Str(g?["gradingCompany"]) ?? "",
                To = Str(g?["newGrade"]) ?? "",
                From = Str(g?["previousGrade"]),
                Action = Str(g?["action"]) ?? "",
                When = DateTime.TryParse(Str(g?["date"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : DateTime.MinValue,
            })
            .Where(g => g.Firm.Length > 0 && g.To.Length > 0 && g.When >= cutoff)
            .OrderByDescending(g => g.When)
            .GroupBy(g => g.Firm, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(g => new FirmRating(g.Firm, g.To, string.IsNullOrEmpty(g.From) ? null : g.From, YahooProvider.Bucket(g.To),
                g.Action.ToLowerInvariant() switch
                {
                    "upgrade" => "Upgrade", "downgrade" => "Downgrade", "initialise" or "initiate" or "init" => "Initiated",
                    "reiterate" or "reiterated" => "Reiterated", _ => "Maintained",
                }, g.When, null, null))
            .ToList();

        var peers = Arr(peersTask.Result).Select(x => Str(x?["symbol"])).OfType<string>()
            .Where(x => !x.Equals(symbol, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();

        return new CompanySnapshot
        {
            Symbol = Str(p["symbol"]) ?? symbol,
            Name = Str(p["companyName"]) ?? symbol,
            Exchange = Str(p["exchangeFullName"]) ?? Str(p["exchange"]) ?? "",
            Currency = Str(p["currency"]) ?? "USD",
            FinancialCurrency = Str(p["currency"]),
            QuoteType = Num(p["isEtf"]) == 1 ? "ETF" : "EQUITY",
            Sector = Str(p["sector"]),
            Industry = Str(p["industry"]),
            Country = Str(p["country"]),
            City = Str(p["city"]),
            Website = Str(p["website"]),
            Employees = long.TryParse(Str(p["fullTimeEmployees"]), out var emp) ? emp : (long?)Num(p["fullTimeEmployees"]),
            Summary = Str(p["description"]),
            Price = price,
            Change = Num(c.Quote?["change"]) ?? Num(p["change"]),
            ChangePct = Num(c.Quote?["changePercentage"]) ?? Num(p["changePercentage"]),
            Metrics = Metrics(c),
            Analysts = new AnalystView
            {
                RecommendationMean = recMean,
                RecommendationKey = Str(cons?["consensus"])?.ToLowerInvariant().Replace(' ', '_'),
                AnalystCount = trend.FirstOrDefault()?.Total,
                CurrentPrice = price,
                TargetLow = Num(pt?["targetLow"]),
                TargetMean = Num(pt?["targetConsensus"]),
                TargetMedian = Num(pt?["targetMedian"]),
                TargetHigh = Num(pt?["targetHigh"]),
                Trend = trend,
                Firms = firms,
            },
            Peers = peers,
            PeersBasis = "FMP: same sector & exchange, similar size",
            Source = Name,
        };
    }

    public async Task<CompRow> CompAsync(string symbol, CancellationToken ct)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        try
        {
            var c = await CoreAsync(symbol, ct);
            return new CompRow
            {
                Symbol = symbol,
                Name = Str(c.Profile["companyName"]) ?? symbol,
                Currency = Str(c.Profile["currency"]) ?? "USD",
                Price = Num(c.Quote?["price"]) ?? Num(c.Profile["price"]),
                ChangePct = Num(c.Quote?["changePercentage"]) ?? Num(c.Profile["changePercentage"]),
                Metrics = Metrics(c),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompRow { Symbol = symbol, Name = symbol, Error = ex.Message };
        }
    }

    public async Task<PeerSet> PeersAsync(string symbol, CancellationToken ct)
    {
        var peers = Arr(await GetAsync($"/stock-peers?symbol={S(symbol)}", Long, ct)).Select(x => Str(x?["symbol"])).OfType<string>()
            .Where(x => !x.Equals(symbol.Trim(), StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        return new PeerSet(peers, "FMP: same sector & exchange, similar size", Name);
    }

    // ---------------------------------------------------------------- chart

    public async Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct)
    {
        // Intraday bars are a paid feature; Yahoo picks those up.
        if (range is "1d" or "5d") throw new NotSupportedException("Not in the FMP free plan.");
        var to = DateTime.UtcNow.Date;
        var from = range switch
        {
            "1mo" => to.AddMonths(-1), "6mo" => to.AddMonths(-6), "ytd" => new DateTime(to.Year, 1, 1),
            "5y" or "5y-daily" => to.AddYears(-5), "max" => to.AddYears(-30), _ => to.AddYears(-1),
        };
        var root = await GetAsync($"/historical-price-eod/full?symbol={S(symbol)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", TimeSpan.FromMinutes(15), ct);
        var pts = Arr(root)
            .Select(b => (Date: DateTime.TryParse(Str(b?["date"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : DateTime.MinValue, B: b))
            .Where(x => x.Date > DateTime.MinValue && Num(x.B?["close"]) != null)
            .OrderBy(x => x.Date)
            .Select(x => new PricePoint(new DateTimeOffset(x.Date).ToUnixTimeSeconds(), Num(x.B?["open"]), Num(x.B?["high"]),
                Num(x.B?["low"]), Num(x.B?["close"]), (long?)Num(x.B?["volume"])))
            .ToList();
        if (pts.Count == 0) throw new ProviderException($"No price history for \"{symbol}\" on Financial Modeling Prep.");

        // Match Yahoo's bar sizes: weekly for 5Y, monthly for Max.
        var interval = "1d";
        if (range is "5y" or "max")
        {
            interval = range == "5y" ? "1wk" : "1mo";
            pts = pts.GroupBy(p =>
                {
                    var d = DateTimeOffset.FromUnixTimeSeconds(p.T).UtcDateTime;
                    return range == "5y" ? $"{ISOWeek.GetYear(d)}-{ISOWeek.GetWeekOfYear(d)}" : $"{d.Year}-{d.Month}";
                })
                .Select(g => new PricePoint(g.First().T, g.First().O, g.Max(p => p.H), g.Min(p => p.L), g.Last().C, g.Sum(p => p.V ?? 0)))
                .ToList();
        }
        return new PriceSeries { Symbol = symbol.ToUpperInvariant(), Range = range, Interval = interval, Currency = "USD", Points = pts };
    }

    // ---------------------------------------------------------------- statements

    /// <summary>FMP field → the shared line key (same keys as <c>YahooStatements</c>).</summary>
    private static readonly Dictionary<StatementKind, (string Key, string Field)[]> FieldMap = new()
    {
        [StatementKind.Income] = new[]
        {
            ("TotalRevenue", "revenue"), ("CostOfRevenue", "costOfRevenue"), ("GrossProfit", "grossProfit"),
            ("ResearchAndDevelopment", "researchAndDevelopmentExpenses"), ("SellingGeneralAndAdministration", "sellingGeneralAndAdministrativeExpenses"),
            ("OtherOperatingExpenses", "otherExpenses"), ("OperatingExpense", "operatingExpenses"), ("OperatingIncome", "operatingIncome"),
            ("InterestIncomeNonOperating", "interestIncome"), ("InterestExpenseNonOperating", "interestExpense"),
            ("NetNonOperatingInterestIncomeExpense", "netInterestIncome"), ("OtherIncomeExpense", "totalOtherIncomeExpensesNet"),
            ("PretaxIncome", "incomeBeforeTax"), ("TaxProvision", "incomeTaxExpense"),
            ("NetIncomeContinuousOperations", "netIncomeFromContinuingOperations"), ("NetIncome", "netIncome"),
            ("BasicEPS", "eps"), ("DilutedEPS", "epsDiluted"), ("BasicAverageShares", "weightedAverageShsOut"),
            ("DilutedAverageShares", "weightedAverageShsOutDil"), ("EBIT", "ebit"), ("EBITDA", "ebitda"),
            ("ReconciledDepreciation", "depreciationAndAmortization"),
        },
        [StatementKind.Balance] = new[]
        {
            ("CashAndCashEquivalents", "cashAndCashEquivalents"), ("OtherShortTermInvestments", "shortTermInvestments"),
            ("CashCashEquivalentsAndShortTermInvestments", "cashAndShortTermInvestments"), ("AccountsReceivable", "accountsReceivables"),
            ("OtherReceivables", "otherReceivables"), ("Receivables", "netReceivables"), ("Inventory", "inventory"),
            ("PrepaidAssets", "prepaids"), ("OtherCurrentAssets", "otherCurrentAssets"), ("CurrentAssets", "totalCurrentAssets"),
            ("NetPPE", "propertyPlantEquipmentNet"), ("Goodwill", "goodwill"), ("OtherIntangibleAssets", "intangibleAssets"),
            ("GoodwillAndOtherIntangibleAssets", "goodwillAndIntangibleAssets"), ("InvestmentsAndAdvances", "longTermInvestments"),
            ("NonCurrentDeferredTaxesAssets", "taxAssets"), ("OtherNonCurrentAssets", "otherNonCurrentAssets"),
            ("TotalNonCurrentAssets", "totalNonCurrentAssets"), ("TotalAssets", "totalAssets"),
            ("AccountsPayable", "accountPayables"), ("CurrentAccruedExpenses", "accruedExpenses"), ("CurrentDebt", "shortTermDebt"),
            ("CurrentCapitalLeaseObligation", "capitalLeaseObligationsCurrent"), ("CurrentDeferredRevenue", "deferredRevenue"),
            ("OtherCurrentLiabilities", "otherCurrentLiabilities"), ("CurrentLiabilities", "totalCurrentLiabilities"),
            ("LongTermDebt", "longTermDebt"), ("LongTermCapitalLeaseObligation", "capitalLeaseObligationsNonCurrent"),
            ("NonCurrentDeferredRevenue", "deferredRevenueNonCurrent"), ("NonCurrentDeferredTaxesLiabilities", "deferredTaxLiabilitiesNonCurrent"),
            ("OtherNonCurrentLiabilities", "otherNonCurrentLiabilities"), ("TotalNonCurrentLiabilitiesNetMinorityInterest", "totalNonCurrentLiabilities"),
            ("TotalLiabilitiesNetMinorityInterest", "totalLiabilities"), ("CommonStock", "commonStock"),
            ("AdditionalPaidInCapital", "additionalPaidInCapital"), ("RetainedEarnings", "retainedEarnings"), ("TreasuryStock", "treasuryStock"),
            ("GainsLossesNotAffectingRetainedEarnings", "accumulatedOtherComprehensiveIncomeLoss"), ("StockholdersEquity", "totalStockholdersEquity"),
            ("MinorityInterest", "minorityInterest"), ("TotalEquityGrossMinorityInterest", "totalEquity"), ("TotalDebt", "totalDebt"),
            ("NetDebt", "netDebt"),
        },
        [StatementKind.CashFlow] = new[]
        {
            ("NetIncomeFromContinuingOperations", "netIncome"), ("DepreciationAndAmortization", "depreciationAndAmortization"),
            ("DeferredIncomeTax", "deferredIncomeTax"), ("StockBasedCompensation", "stockBasedCompensation"),
            ("OtherNonCashItems", "otherNonCashItems"), ("ChangeInReceivables", "accountsReceivables"), ("ChangeInInventory", "inventory"),
            ("ChangeInPayablesAndAccruedExpense", "accountsPayables"), ("ChangeInWorkingCapital", "changeInWorkingCapital"),
            ("OperatingCashFlow", "netCashProvidedByOperatingActivities"), ("CapitalExpenditure", "investmentsInPropertyPlantAndEquipment"),
            ("NetBusinessPurchaseAndSale", "acquisitionsNet"), ("PurchaseOfInvestment", "purchasesOfInvestments"),
            ("SaleOfInvestment", "salesMaturitiesOfInvestments"), ("NetOtherInvestingChanges", "otherInvestingActivities"),
            ("InvestingCashFlow", "netCashProvidedByInvestingActivities"), ("IssuanceOfDebt", "netDebtIssuance"),
            ("IssuanceOfCapitalStock", "commonStockIssuance"), ("RepurchaseOfCapitalStock", "commonStockRepurchased"),
            ("CashDividendsPaid", "netDividendsPaid"), ("NetOtherFinancingCharges", "otherFinancingActivities"),
            ("FinancingCashFlow", "netCashProvidedByFinancingActivities"), ("EffectOfExchangeRateChanges", "effectOfForexChangesOnCash"),
            ("ChangesInCash", "netChangeInCash"), ("BeginningCashPosition", "cashAtBeginningOfPeriod"), ("EndCashPosition", "cashAtEndOfPeriod"),
            ("FreeCashFlow", "freeCashFlow"), ("IncomeTaxPaidSupplementalData", "incomeTaxesPaid"), ("InterestPaidSupplementalData", "interestPaid"),
        },
    };

    public async Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct)
    {
        var quarterly = frequency == "quarterly";
        var endpoint = kind switch { StatementKind.Income => "income-statement", StatementKind.Balance => "balance-sheet-statement", _ => "cash-flow-statement" };
        var rows = Arr(await GetAsync($"/{endpoint}?symbol={S(symbol)}&period={(quarterly ? "quarter" : "annual")}&limit={(quarterly ? 8 : 5)}", Long, ct))
            .Where(r => Str(r?["date"]) != null)
            .OrderBy(r => Str(r?["date"]), StringComparer.Ordinal).ToList();
        if (rows.Count == 0) throw new ProviderException($"Financial Modeling Prep has no {frequency} figures for {symbol.ToUpperInvariant()}.");

        var map = FieldMap[kind].ToDictionary(m => m.Key, m => m.Field);
        var lines = new List<StatementLine>();
        // Presentation order and labels come from the Yahoo template, so both sources look alike.
        foreach (var item in YahooStatements.For(kind))
        {
            if (item.Format == "header") { lines.Add(new StatementLine(item.Key, item.Label, "header", true, 0, new())); continue; }
            if (!map.TryGetValue(item.Key, out var field)) continue;
            var vals = rows.Select(r => Num(r?[field])).ToList();
            if (vals.All(v => v == null || v == 0)) continue;
            lines.Add(new StatementLine(item.Key, item.Label, item.Format, item.Emphasis, item.Indent, vals));
        }
        if (kind == StatementKind.Income && rows.Count > 0)
        {
            // FMP has no effective-rate line; the DCF wants one.
            var rate = rows.Select(r => Num(r?["incomeTaxExpense"]) is { } t && Num(r?["incomeBeforeTax"]) is { } p && p != 0 ? t / p : (double?)null).ToList();
            if (rate.Any(v => v != null)) lines.Add(new StatementLine("TaxRateForCalcs", "Effective tax rate", "pct", false, 0, rate));
        }
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Format == "header" && (i == lines.Count - 1 || lines[i + 1].Format == "header"))
                lines.RemoveAt(i);

        return new FinancialStatement
        {
            Kind = kind,
            Title = YahooStatements.Title(kind),
            Frequency = quarterly ? "quarterly" : "annual",
            Currency = Str(rows[^1]?["reportedCurrency"]) ?? "USD",
            Periods = rows.Select(r => Str(r?["date"])!).ToList(),
            Lines = lines,
            Source = Name,
        };
    }
}
