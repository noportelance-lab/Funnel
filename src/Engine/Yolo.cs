using System.Text.Json.Nodes;
using Funnel.Providers;
using Funnel.Providers.Yahoo;

namespace Funnel.Engine;

/// <summary>One scored trait behind a pick: 0–1, its weight in the group, and what it says.</summary>
public sealed record YoloFactor(string Key, string Label, double? Score, double Weight, string? Why, string? Risk);

public sealed class YoloPick
{
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string Exchange { get; init; } = "";
    public string Currency { get; init; } = "";
    /// <summary>mining | biotech | smallcap</summary>
    public string Group { get; set; } = "";
    public string GroupLabel { get; set; } = "";
    public string? Industry { get; set; }
    public string? Country { get; set; }
    public string? Summary { get; set; }
    public double? Price { get; init; }
    public double? ChangePct { get; init; }
    /// <summary>Change over 52 weeks, as a fraction.</summary>
    public double? YearChange { get; init; }
    /// <summary>In the trading currency.</summary>
    public double? MarketCap { get; init; }
    public double MarketCapUsd { get; init; }
    /// <summary>1 (not very promising) … 10 (very promising).</summary>
    public int Level { get; set; }
    /// <summary>0–100, the weighted factor score the level comes from.</summary>
    public double Score { get; set; }
    public string Headline { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public List<string> Why { get; set; } = new();
    public List<string> Risks { get; set; } = new();
    public List<YoloFactor> Factors { get; set; } = new();
}

public sealed record YoloGroupStat(string Key, string Label, int Matched, int Screened, int Reviewed);

public sealed class YoloResult
{
    public DateTime AsOf { get; init; }
    public List<YoloPick> Picks { get; init; } = new();
    public List<YoloGroupStat> Groups { get; init; } = new();
    /// <summary>Score needed for each level, 2…10 (level 1 is anything below the first).</summary>
    public double[] LevelCuts { get; init; } = YoloScreen.LevelCuts;
    public List<string> Notes { get; init; } = new();
    /// <summary>A group (or a page of one) didn't come back from the screener.</summary>
    public bool Partial { get; init; }
    public string Source { get; init; } = "Yahoo Finance";
}

/// <summary>One company scored by the YOLO review, on or off today's board.</summary>
public sealed class YoloSingle
{
    public YoloPick? Pick { get; init; }
    public bool OnBoard { get; init; }
    public List<string> Outside { get; init; } = new();
    public string? LeftOut { get; init; }
    public DateTime AsOf { get; init; }
}

/// <summary>
/// YOLO mode: hunts for the mining, biotech and small-cap names with a credible path to 100x.
///
/// Two passes. First, Yahoo's screener lists every US and Canadian listing in each group (no OTC)
/// and each hit gets a quick score from what the screener sends: how small it is (room to grow),
/// its trend against the 200-day average, where it sits in its 52-week range, and whether it
/// trades enough to buy. The best <see cref="ReviewPerGroup"/> per group then get a full review:
/// cash and burn, debt, insider and institutional ownership, analyst targets, short interest, and
/// group-specific traits (cash backing for biotech, metal prices and book value for miners,
/// growth, margins and price-for-growth for small caps). Each trait scores 0–1 and says why it
/// helps or what it risks; the weighted average (0–100) maps onto levels 1–10 by fixed cuts, so a
/// 9 means the same thing every day.
/// </summary>
public static class YoloScreen
{
    public const int ReviewPerGroup = 40;
    private const int MaxScreenPerGroup = 1000;
    private const double MinCapUsd = 15e6, MaxCapUsd = 2e9, MinDollarVolume = 75_000;

    /// <summary>Score (0–100) needed for levels 2…10.</summary>
    public static readonly double[] LevelCuts = { 32, 38, 44, 50, 55, 60, 65, 69, 73.5 };

    /// <summary>
    /// Added to a group's raw score so a 9 is about as rare in each group: a junior miner rarely has
    /// analyst coverage or revenue, which the other groups get credit for.
    /// </summary>
    private static readonly Dictionary<string, double> Calibration = new() { ["mining"] = 6, ["biotech"] = 0, ["smallcap"] = 0 };

    public static int LevelFor(double score)
    {
        int level = 1;
        foreach (var cut in LevelCuts) if (score >= cut) level++;
        return level;
    }

    private static readonly string[] Exchanges = { "NMS", "NGM", "NCM", "NYQ", "ASE", "TOR", "VAN", "CNQ", "NEO" };
    internal static readonly string[] MiningIndustries =
        { "Gold", "Silver", "Other Precious Metals & Mining", "Other Industrial Metals & Mining", "Copper", "Uranium" };

    /// <summary>The metal each mining industry rides on (a 52-week move from its future or ETF).</summary>
    private static readonly Dictionary<string, (string Symbol, string Name)> Commodity = new()
    {
        ["Gold"] = ("GC=F", "Gold"),
        ["Silver"] = ("SI=F", "Silver"),
        ["Copper"] = ("HG=F", "Copper"),
        ["Other Precious Metals & Mining"] = ("PL=F", "Platinum"),
        ["Uranium"] = ("URA", "Uranium (URA)"),
    };

    private sealed record GroupDef(string Key, string Label, string Sort, Func<JsonObject> Query);

    private static JsonObject Op(string op, params JsonNode[] operands) => new() { ["operator"] = op, ["operands"] = new JsonArray(operands) };
    private static JsonObject Eq(string f, string v) => Op("eq", f, v);
    private static JsonObject AnyOf(string f, IEnumerable<string> vs) => Op("or", vs.Select(v => (JsonNode)Eq(f, v)).ToArray());
    private static JsonObject Btwn(string f, double a, double b) => Op("btwn", f, a, b);
    private static JsonObject Gt(string f, double v) => Op("gt", f, v);
    private static JsonObject Listed() => AnyOf("exchange", Exchanges);
    private static JsonObject Regions() => AnyOf("region", new[] { "us", "ca" });

    private static readonly GroupDef[] Groups =
    {
        new("mining", "Mining", "intradaymarketcap", () => Op("and",
            Regions(), Listed(), AnyOf("industry", MiningIndustries),
            Btwn("intradaymarketcap", 10e6, 3e9), Gt("avgdailyvol3m", 20_000))),
        new("biotech", "Biotech", "intradaymarketcap", () => Op("and",
            Regions(), Listed(), Eq("industry", "Biotechnology"),
            Btwn("intradaymarketcap", 10e6, 3e9), Gt("avgdailyvol3m", 20_000))),
        // Small caps of any kind that are growing fast; miners and biotechs found here move to their own group.
        new("smallcap", "Small cap", "intradaymarketcap", () => Op("and",
            Regions(), Listed(), Btwn("intradaymarketcap", 20e6, 3e9),
            Gt("quarterlyrevenuegrowth.quarterly", 20), Gt("avgdailyvol3m", 50_000))),
    };

    private static double? Num(JsonNode? n) => J.Num(n);
    private static string? Str(JsonNode? n) => J.Str(n);
    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    public static async Task<YoloResult> RunAsync(YahooProvider y, Action<int, string>? progress, CancellationToken ct)
    {
        void Report(int pct, string msg) => progress?.Invoke(pct, msg);
        var notes = new List<string>();

        // ---- pass 1: screen each group
        // The screener is slow and touchy under load: one page at a time, three tries each with a
        // pause between, and a group that still fails drops out with a note rather than sinking the run.
        async Task<(int Total, List<JsonNode> Quotes)?> Page(GroupDef g, int offset)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await y.ScreenAsync(g.Query(), g.Sort, offset, 250, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (attempt >= 2) return null;
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
            }
        }
        var screened = new List<(GroupDef Group, int Total, List<JsonNode> Quotes)>();
        bool partial = false;
        foreach (var g in Groups)
        {
            Report(2 + 5 * screened.Count, $"Screening {g.Label.ToLowerInvariant()} listings…");
            if (await Page(g, 0) is not var (total, first))
            {
                partial = true;
                notes.Add($"Yahoo's screener didn't answer for {g.Label.ToLowerInvariant()}, so that group is missing. Run the screen again in a minute.");
                screened.Add((g, 0, new List<JsonNode>()));
                continue;
            }
            var all = new List<JsonNode>(first);
            for (int o = 250; o < Math.Min(total, MaxScreenPerGroup); o += 250)
            {
                if (await Page(g, o) is not var (_, more)) { partial = true; break; }
                all.AddRange(more);
            }
            screened.Add((g, total, all));
        }
        if (screened.All(s => s.Quotes.Count == 0))
            throw new ProviderException("Yahoo Finance's screener isn't answering right now. Try again in a minute.");
        Report(18, "Ranking the screen…");

        var fx = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 1 };
        await AddFx(y, fx, screened.SelectMany(s => s.Quotes).Select(q => Str(q["currency"])), ct);

        // Mining and biotech claim their names first; the small-cap screen keeps the rest.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A company listed on two exchanges (HSTR.TO and HSTR.V) is one company: keep the first.
        var takenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortlist = new List<(GroupDef Group, JsonNode Quote, double CapUsd)>();
        var stats = new List<(GroupDef Group, int Total, int Screened)>();
        foreach (var (g, total, quotes) in screened)
        {
            var ok = new List<(JsonNode Q, double CapUsd, double S1)>();
            foreach (var q in quotes)
            {
                var sym = Str(q["symbol"]);
                if (sym == null || !taken.Add(sym)) continue;
                var name = (Str(q["longName"]) ?? Str(q["shortName"]) ?? sym).Trim();
                if (!takenNames.Add(name)) continue;
                if (Prescore(q, fx) is { } pre) ok.Add((q, pre.CapUsd, pre.Score));
            }
            stats.Add((g, total, ok.Count));
            shortlist.AddRange(ok.OrderByDescending(x => x.S1).Take(ReviewPerGroup).Select(x => (g, x.Q, x.CapUsd)));
        }

        // ---- pass 2: full review of the shortlist
        var metals = new Dictionary<string, double?>();
        try
        {
            var mq = await y.RawQuotesAsync(Commodity.Values.Select(c => c.Symbol), ct);
            foreach (var (sym, _) in Commodity.Values)
                metals[sym] = mq.TryGetValue(sym, out var n) ? YearChangeOf(n) : null;
        }
        catch { notes.Add("Metal prices didn't load, so the commodity trend is left out of the mining scores."); }

        int done = 0, failed = 0;
        using var gate = new SemaphoreSlim(5);
        var details = await Task.WhenAll(shortlist.Select(async s =>
        {
            await gate.WaitAsync(ct);
            try { return (s.Group, s.Quote, s.CapUsd, Detail: (JsonNode?)await y.ScreenDetailAsync(Str(s.Quote["symbol"])!, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref failed); return (s.Group, s.Quote, s.CapUsd, Detail: (JsonNode?)null); }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                Report(20 + 75 * n / Math.Max(1, shortlist.Count), $"Reviewing {n} of {shortlist.Count}: {Str(s.Quote["symbol"])}");
            }
        }));
        if (failed > 0) notes.Add($"{failed} of {shortlist.Count} shortlisted companies couldn't be reviewed (no data from Yahoo) and are left out.");

        await AddFx(y, fx, details.Where(d => d.Detail != null).Select(d => Str(d.Detail!["financialData"]?["financialCurrency"])), ct);

        var picks = new List<YoloPick>();
        foreach (var d in details)
        {
            if (d.Detail == null) continue;
            var pick = Review(d.Group, d.Quote, d.Detail, d.CapUsd, fx, metals);
            if (pick != null) picks.Add(pick);
        }

        Report(100, "Done");
        return new YoloResult
        {
            AsOf = DateTime.Now,
            Partial = partial,
            Picks = picks.OrderByDescending(p => p.Score).ToList(),
            Groups = stats.Select(s => new YoloGroupStat(s.Group.Key, s.Group.Label, s.Total, s.Screened,
                picks.Count(p => p.Group == s.Group.Key))).ToList(),
            Notes = notes,
        };
    }

    /// <summary>
    /// Scores one company the way the screen would, for the YOLO tab on a company that isn't on
    /// today's board. Outside lists what would have kept it off the screen (too big, too thinly
    /// traded…); Pick is null when the screen leaves its kind of business out altogether.
    /// </summary>
    public static async Task<YoloSingle> ScoreOneAsync(YahooProvider y, string symbol, CancellationToken ct)
    {
        var quotes = await y.RawQuotesAsync(new[] { symbol }, ct);
        var q = quotes.TryGetValue(symbol, out var hit) ? hit : quotes.Values.FirstOrDefault();
        if (q == null || Num(q["regularMarketPrice"]) == null) throw new ProviderException($"No quote for {symbol} from Yahoo Finance.");
        var d = await y.ScreenDetailAsync(symbol, ct);
        var cur = Str(q["currency"]) ?? "USD";
        var fx = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 1 };
        await AddFx(y, fx, new[] { cur, Str(d["financialData"]?["financialCurrency"]) }, ct);
        if (Usd(Num(q["marketCap"]), cur, fx) is not { } capUsd || capUsd <= 0)
            throw new ProviderException($"Yahoo Finance has no market value for {symbol}, so it can't be scored.");

        var industry = Str(d["assetProfile"]?["industry"]);
        var g = industry == "Biotechnology" ? Groups[1] : industry != null && MiningIndustries.Contains(industry) ? Groups[0] : Groups[2];
        var metals = new Dictionary<string, double?>();
        if (g.Key == "mining" && industry != null && Commodity.TryGetValue(industry, out var metal))
            try { metals[metal.Symbol] = (await y.RawQuotesAsync(new[] { metal.Symbol }, ct)).TryGetValue(metal.Symbol, out var mq) ? YearChangeOf(mq) : null; }
            catch { /* scored without the metal's trend */ }

        var outside = new List<string>();
        var p = Num(q["regularMarketPrice"]) ?? 0;
        var dollarVol = Usd(Num(q["averageDailyVolume3Month"]) * p, cur, fx) ?? 0;
        var exch = Str(q["exchange"]);
        if (capUsd > MaxCapUsd)
            outside.Add($"At about US${capUsd / 1e9:N1}B it's above the screen's US$2B ceiling: a 100x would make it a US${capUsd * 100 / 1e12:N1} trillion company.");
        else if (capUsd < MinCapUsd)
            outside.Add($"At about US${capUsd / 1e6:N1}M it's below the screen's US$15M floor, where listings are hard to buy and often fail.");
        if (dollarVol < MinDollarVolume)
            outside.Add($"It trades about US${dollarVol / 1e3:N0}K a day, under the screen's US$75K minimum.");
        if (p <= 0.05) outside.Add("Its price is under 5 cents.");
        if (exch != null && !Exchanges.Contains(exch))
            outside.Add($"It isn't on a main US or Canadian exchange ({Str(q["fullExchangeName"]) ?? exch}).");
        var growth = Num(d["financialData"]?["revenueGrowth"]);
        if (g.Key == "smallcap" && !(growth > 0.2))
            outside.Add(growth == null
                ? "The small-cap screen wants revenue growing 20%+ a year, and there's no revenue growth on record."
                : $"The small-cap screen wants revenue growing 20%+ a year; it's {(growth >= 0 ? "up" : "down")} {Pct(Math.Abs(growth.Value))}.");

        var pick = Review(g, q, d, capUsd, fx, metals);
        return new YoloSingle { Pick = pick, Outside = outside, AsOf = DateTime.Now,
            LeftOut = pick == null ? "Banks, insurers, REITs, utilities, asset managers and shell companies are left out of YOLO mode: they don't compound their way to 100x." : null };
    }

    private static async Task AddFx(YahooProvider y, Dictionary<string, double> fx, IEnumerable<string?> currencies, CancellationToken ct)
    {
        var need = currencies.OfType<string>().Where(c => c.Length == 3 && !fx.ContainsKey(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (need.Count == 0) return;
        try
        {
            var q = await y.RawQuotesAsync(need.Select(c => $"{c.ToUpperInvariant()}USD=X"), ct);
            foreach (var c in need)
                if (q.TryGetValue($"{c.ToUpperInvariant()}USD=X", out var n) && Num(n?["regularMarketPrice"]) is > 0 and var rate)
                    fx[c] = rate;
        }
        catch { /* names in a currency without a rate drop out */ }
    }

    private static double? YearChangeOf(JsonNode? q)
    {
        // Sent in percent (82.1 = +82.1%).
        var v = Num(q?["fiftyTwoWeekChangePercent"]);
        return v / 100;
    }

    /// <summary>Market cap in US dollars, or null when there's no rate for the currency.</summary>
    private static double? Usd(double? v, string? currency, Dictionary<string, double> fx) =>
        v != null && currency != null && fx.TryGetValue(currency, out var r) ? v * r : null;

    /// <summary>Pass 1: a quick score from screener fields only, or null if it fails the hard filters.</summary>
    private static (double CapUsd, double Score)? Prescore(JsonNode q, Dictionary<string, double> fx)
    {
        var cur = Str(q["currency"]);
        var p = Num(q["regularMarketPrice"]);
        if (p is not > 0.05) return null;
        if (Usd(Num(q["marketCap"]), cur, fx) is not { } cap || cap < MinCapUsd || cap > MaxCapUsd) return null;
        var dollarVol = Usd(Num(q["averageDailyVolume3Month"]) * p, cur, fx) ?? 0;
        if (dollarVol < MinDollarVolume) return null;

        var trend = Num(q["twoHundredDayAverage"]) is > 0 and var ma ? Clamp((p.Value / ma - 1 + 0.25) / 0.65) : 0.4;
        var lo = Num(q["fiftyTwoWeekLow"]); var hi = Num(q["fiftyTwoWeekHigh"]);
        var range = lo != null && hi > lo ? Clamp((p.Value - lo.Value) / (hi.Value - lo.Value)) : 0.5;
        var score = 0.35 * Headroom(cap) + 0.30 * trend + 0.15 * range + 0.20 * Liquidity(dollarVol);
        return (cap, score);
    }

    private static double Headroom(double capUsd) => Clamp((Math.Log10(3e9) - Math.Log10(capUsd)) / 2);
    private static double Liquidity(double dollarVol) => dollarVol > 0 ? Clamp((Math.Log10(dollarVol) - 5) / 2) : 0;

    // Weights per group; normalised, so only the ratios matter.
    private static readonly Dictionary<string, Dictionary<string, double>> Weights = new()
    {
        ["biotech"] = new() { ["H"] = 14, ["R"] = 16, ["C"] = 14, ["U"] = 16, ["S"] = 10, ["I"] = 6, ["M"] = 10, ["X"] = 5, ["D"] = 4, ["V"] = 5, ["L"] = 2 },
        ["mining"] = new() { ["H"] = 16, ["R"] = 12, ["D"] = 6, ["I"] = 12, ["M"] = 14, ["T"] = 10, ["B"] = 10, ["V"] = 8, ["U"] = 6, ["S"] = 3, ["X"] = 3, ["L"] = 3 },
        ["smallcap"] = new() { ["H"] = 14, ["K"] = 14, ["G"] = 18, ["Q"] = 10, ["P"] = 10, ["F"] = 8, ["R"] = 8, ["I"] = 10, ["M"] = 10, ["U"] = 4, ["S"] = 3, ["X"] = 2, ["D"] = 2, ["L"] = 2 },
    };

    /// <summary>A missing figure counts as below average, so gaps in the data are never rewarded.</summary>
    private const double Neutral = 0.4;

    private static readonly Dictionary<string, string> GroupRisk = new()
    {
        ["biotech"] = "Biotech outcomes are binary: one trial readout or FDA decision can halve or double the price.",
        ["mining"] = "Miners live on metal prices, permits and financing; a falling metal or a failed raise hits juniors hardest.",
        ["smallcap"] = "Small companies can stumble on one lost customer, a funding squeeze or a slowdown in growth.",
    };

    /// <summary>Pass 2: score every trait, write the reasons, and set the level.</summary>
    private static YoloPick? Review(GroupDef g, JsonNode q, JsonNode d, double capUsd, Dictionary<string, double> fx, Dictionary<string, double?> metals)
    {
        var fd = d["financialData"]; var ks = d["defaultKeyStatistics"]; var ap = d["assetProfile"];
        var sym = Str(q["symbol"])!;
        var cur = Str(q["currency"]) ?? "USD";
        var industry = Str(ap?["industry"]);
        var group = g.Key;
        if (industry == "Biotechnology") group = "biotech";
        else if (industry != null && MiningIndustries.Contains(industry)) group = "mining";
        var groupLabel = Groups.First(x => x.Key == group).Label;

        double p = Num(q["regularMarketPrice"]) ?? 0;
        double mc = Num(q["marketCap"]) ?? 0;
        // Statement figures are in the reporting currency; bring them into the trading currency.
        var fcur = Str(fd?["financialCurrency"]) ?? cur;
        double? Fin(string key)
        {
            var v = Num(fd?[key]);
            if (v == null) return null;
            if (string.Equals(fcur, cur, StringComparison.OrdinalIgnoreCase)) return v;
            return fx.TryGetValue(fcur, out var a) && fx.TryGetValue(cur, out var b) && b > 0 ? v * a / b : null;
        }
        var cash = Fin("totalCash"); var debt = Fin("totalDebt"); var ocf = Fin("operatingCashflow");
        var rev = Fin("totalRevenue");
        var gm = Num(fd?["grossMargins"]); var growth = Num(fd?["revenueGrowth"]);
        // Growth off a tiny base (10,000%) says little; score anything past 200% as 200%.
        double? growthCapped = growth == null ? null : Math.Min(growth.Value, 2.0);
        string GrowthText(double g) => g >= 10 ? "more than 10x" : g >= 2 ? $"{g + 1:0.#}x" : $"up {Pct(g)}";
        var target = Num(fd?["targetMeanPrice"]); var analysts = (int)(Num(fd?["numberOfAnalystOpinions"]) ?? 0);
        var insiders = Num(ks?["heldPercentInsiders"]); var inst = Num(ks?["heldPercentInstitutions"]);
        var shortPct = Num(ks?["shortPercentOfFloat"]);
        var pb = Num(q["priceToBook"]) ?? Num(ks?["priceToBook"]);
        var evRev = Num(ks?["enterpriseToRevenue"]);
        var ma200 = Num(q["twoHundredDayAverage"]);
        var yearChg = YearChangeOf(q);
        var dollarVol = Usd(Num(q["averageDailyVolume3Month"]) * p, cur, fx) ?? 0;

        string M(double? v) => Money(v, cur);
        var f = new List<YoloFactor>();
        var w = Weights[group];
        void Add(string key, string label, double? score, string? why = null, string? risk = null)
        {
            if (w.TryGetValue(key, out var weight)) f.Add(new YoloFactor(key, label, score, weight, why, risk));
        }

        // H — room to grow
        {
            var s = Headroom(capUsd);
            Add("H", "Room to grow", s,
                capUsd <= 300e6 ? $"Worth {M(mc)} today, so even a 100x ({M(mc * 100)}) would leave it smaller than the biggest names in its field." : null,
                capUsd >= 1e9 ? $"Already worth {M(mc)}: a 100x would mean {M(mc * 100)}, bigger than almost anything in its field. Think 5–10x, not 100x." : null);
        }

        // R — runway
        if (ocf is >= 0)
            Add("R", "Cash runway", 1, $"Already brings in cash from operations ({M(ocf)} over the past year), so it can fund itself without selling new shares.");
        else if (ocf is < 0 && cash is { } c0)
        {
            var burn = -ocf.Value; var months = c0 / burn * 12;
            Add("R", "Cash runway", Clamp((months - 6) / 30),
                months >= 24 ? $"{M(c0)} of cash covers {(months >= 120 ? "more than 10" : $"about {months / 12:0.0}")} years of spending at the current burn ({M(burn)} a year)." : null,
                months < 12 ? $"Only about {Math.Max(1, months):0} months of cash at the current burn: expect it to raise money by selling new shares (dilution)." : null);
        }
        else Add("R", "Cash runway", null);

        // D — debt
        if (cash != null && debt != null && mc > 0)
        {
            var net = cash.Value - debt.Value;
            Add("D", "Balance sheet", net >= 0 ? 1 : Clamp(1 + net / mc / 0.5), null,
                net < 0 && -net / mc > 0.25 ? $"Net debt of {M(-net)} ({Pct(-net / mc)} of its market value) leaves little room for error." : null);
        }
        else Add("D", "Balance sheet", null);

        // C — cash backing (biotech)
        if (cash != null && mc > 0)
        {
            var ratio = cash.Value / mc; var ev = mc - cash.Value + (debt ?? 0);
            Add("C", "Cash backing", Clamp(ratio / 0.6),
                ratio >= 0.35 ? (ev <= 0
                    ? $"Trades for less than its cash ({M(cash)}): the market values the science at less than nothing."
                    : $"Cash is {Pct(ratio)} of its market value, so the market values the pipeline at only {M(ev)}.") : null);
        }
        else Add("C", "Cash backing", null);

        // U — analyst upside
        if (analysts >= 2 && target is > 0 && p > 0)
        {
            var up = target.Value / p - 1;
            Add("U", "Analyst upside", Clamp(up / 1.5),
                up >= 0.5 ? $"{analysts} analysts' average target of {Price(target, cur)} is {Pct(up)} above the price." : null,
                up < 0 ? $"Already above the analysts' average target ({Price(target, cur)})." : null);
        }
        else Add("U", "Analyst upside", null);

        // S — institutions
        if (inst != null)
            Add("S", "Institutional backing", Clamp(inst.Value / 0.6),
                inst >= 0.5 ? $"Institutions own {Pct(Math.Min(1, inst.Value))} of the shares: professional investors have done the homework." : null);
        else Add("S", "Institutional backing", null);

        // I — insiders
        if (insiders != null)
            Add("I", "Insider ownership", insiders >= 0.7 ? 0.7 : Clamp(insiders.Value / 0.2),
                insiders >= 0.15 ? $"Insiders own {Pct(insiders.Value)}: management only gets rich if shareholders do." : null,
                insiders < 0.01 ? "Insiders own almost none of the shares." : null);
        else Add("I", "Insider ownership", null);

        // M — momentum
        if (ma200 is > 0 && p > 0)
        {
            var r = p / ma200.Value - 1;
            var s = Clamp((r + 0.15) / 0.5);
            string? risk = r <= -0.2 ? $"{Pct(-r)} below its 200-day average: the trend is still down." : null;
            if (yearChg > 3) { s *= 0.7; risk = $"Already up {Pct(yearChg.Value)} in a year: part of the move may be done."; }
            Add("M", "Momentum", s, r >= 0.15 ? $"Trading {Pct(r)} above its 200-day average: the market is starting to notice." : null, risk);
        }
        else Add("M", "Momentum", null);

        // X — short interest
        if (shortPct != null)
            Add("X", "Short interest", 1 - Clamp((shortPct.Value - 0.05) / 0.25), null,
                shortPct >= 0.15 ? $"{Pct(shortPct.Value)} of the tradable shares are sold short: many professionals are betting it falls (a squeeze cuts both ways)." : null);
        else Add("X", "Short interest", null);

        // L — liquidity
        Add("L", "Liquidity", Liquidity(dollarVol), null,
            dollarVol < 300_000 ? $"Thinly traded (about {Money(dollarVol, "USD")} a day): hard to buy or sell size without moving the price." : null);

        // T — the metal (mining)
        if (industry != null && Commodity.TryGetValue(industry, out var metal) && metals.TryGetValue(metal.Symbol, out var mchg) && mchg != null)
            Add("T", "Metal price trend", Clamp((mchg.Value + 0.1) / 0.5),
                mchg >= 0.15 ? $"{metal.Name} is up {Pct(mchg.Value)} over the past year, and miners' profits rise faster than the metal." : null,
                mchg <= -0.1 ? $"{metal.Name} is down {Pct(-mchg.Value)} over the past year, squeezing miners' margins." : null);
        else Add("T", "Metal price trend", null);

        // B — book value (mining)
        if (pb is > 0)
            Add("B", "Asset value", Clamp((3 - pb.Value) / 2.5),
                pb <= 1.2 ? $"Trades at {pb:0.0}x book value: about what its assets cost to build." : null,
                pb >= 5 ? $"Priced at {pb:0}x book value: the market already pays up for what's in the ground." : null);
        else Add("B", "Asset value", null);

        // V — producing / selling
        if (group == "mining")
            Add("V", "In production", rev is > 0 && gm is > 0.2 ? Clamp(0.5 + gm.Value) : Neutral,
                rev is > 0 && gm is > 0.2 ? $"Already producing: {M(rev)} of revenue at a {Pct(gm!.Value)} gross margin." : null);
        else if (group == "biotech")
            Add("V", "Product on the market", rev is > 0 && growth is > 0.3 ? 1 : rev is > 0 ? 0.6 : Neutral,
                rev is > 0 && growth is > 0.3 ? $"Revenue {GrowthText(growth!.Value)} on a year ago: it already has a product selling." : null);

        // K — can it scale? Growth in a cyclical industry (oil, shipping, metals) is mostly the
        // cycle, and a 100x needs a business that grows without a matching pile of new assets.
        var (profile, _, _, _) = IndustryProfiles.Match(Str(ap?["sector"]), industry);
        // Lenders, insurers, landlords, utilities and shells don't compound 100x; leave them out.
        if (group == "smallcap" && (profile.Financial || profile.Name is "Utilities" or "REITs"
            || industry is "Shell Companies" or "Asset Management")) return null;
        var cyc = profile.Financial ? 0.6 : profile.Cyclicality;
        Add("K", "Scalable business", profile.Financial ? 0.3 : Clamp((1 - cyc) / 0.8), null,
            cyc >= 0.7 ? $"{industry ?? "Its industry"} is cyclical: revenue swings with prices and rates, so today's growth may be the cycle rather than the business." : null);

        // G, Q, P, F — small-cap business quality
        if (growth != null)
            Add("G", "Revenue growth", Clamp(growth.Value / 0.8) * (1 - 0.7 * cyc),
                growth >= 0.4 ? $"Revenue {GrowthText(growth.Value)} on a year ago{(growth >= 2 ? ", from a small base" : "")}." : null,
                growth < 0.1 ? $"Revenue growth has slowed to {Pct(growth.Value)}." : null);
        else Add("G", "Revenue growth", null);

        if (gm != null && rev is > 0)
            Add("Q", "Gross margin", Clamp((gm.Value - 0.2) / 0.5),
                gm >= 0.55 ? $"{Pct(gm.Value)} gross margins: most of each extra dollar of sales is profit." : null,
                gm < 0.2 ? $"Thin {Pct(gm.Value)} gross margins leave little room to grow into a profit." : null);
        else Add("Q", "Gross margin", null);

        var evs = evRev ?? (rev is > 0 && mc > 0 ? (mc - (cash ?? 0) + (debt ?? 0)) / rev : null);
        if (evs is > 0 && growthCapped != null)
        {
            var gs = evs.Value / Math.Max(growthCapped.Value * 100, 5);
            var s = Clamp((0.35 - gs) / 0.3);
            Add("P", "Price for the growth", s,
                s >= 0.6 && growth >= 0.2 && evs <= 8 ? $"Only {evs:0.0}x sales while revenue grows {GrowthText(growth!.Value).Replace("up ", "")}: cheap for how fast it's growing." : null,
                evs > 15 ? $"Already {evs:0}x sales: priced for perfection." : null);
        }
        else Add("P", "Price for the growth", null);

        if (ocf is >= 0) Add("F", "Path to profit", 1);
        else if (ocf != null && rev is > 0)
        {
            var m = ocf.Value / rev.Value;
            Add("F", "Path to profit", Clamp((m + 0.4) / 0.4), null,
                m < -0.5 ? $"Spends {Pct(-m)} of its revenue on top of that revenue in cash burn." : null);
        }
        else Add("F", "Path to profit", null);

        // ---- score and level
        var totalW = f.Sum(x => x.Weight);
        var score = f.Sum(x => x.Weight * (x.Score ?? Neutral)) / totalW * 100;
        var runwayMonths = ocf is < 0 && cash != null ? cash.Value / -ocf.Value * 12 : (double?)null;
        if (runwayMonths < 6) score *= 0.8;   // a raise is all but certain: dilution eats the upside
        score = Math.Clamp(score + Calibration[group], 0, 100);

        var strong = f.Where(x => x.Score >= 0.7).OrderByDescending(x => x.Weight * x.Score!.Value).ToList();
        var tags = strong.Select(x => Tag(x, metal: industry != null && Commodity.TryGetValue(industry, out var mt) ? mt.Name : null, ocf))
            .OfType<string>().Distinct().Take(3).ToList();
        var why = f.Where(x => x.Why != null).OrderByDescending(x => x.Weight * (x.Score ?? 0)).Select(x => x.Why!).Take(4).ToList();
        var risks = f.Where(x => x.Risk != null).OrderByDescending(x => x.Weight * (1 - (x.Score ?? Neutral))).Select(x => x.Risk!).Take(3).ToList();
        if (risks.Count < 2) risks.Add(GroupRisk[group]);

        var summary = Str(ap?["longBusinessSummary"]);
        return new YoloPick
        {
            Symbol = sym,
            Name = Str(q["longName"]) ?? Str(q["shortName"]) ?? sym,
            Exchange = Str(q["fullExchangeName"]) ?? Str(q["exchange"]) ?? "",
            Currency = cur,
            Group = group, GroupLabel = groupLabel,
            Industry = industry, Country = Str(ap?["country"]),
            Summary = FirstSentence(summary),
            Price = p, ChangePct = Num(q["regularMarketChangePercent"]), YearChange = yearChg,
            MarketCap = mc, MarketCapUsd = capUsd,
            Score = Math.Round(score, 1), Level = LevelFor(score),
            Headline = string.Join(" · ", tags),
            Tags = tags, Why = why, Risks = risks, Factors = f,
        };
    }

    private static string? Tag(YoloFactor x, string? metal, double? ocf) => x.Key switch
    {
        "H" => "Tiny vs its potential",
        "R" => ocf is >= 0 ? "Self-funding" : "Years of cash",
        "C" => "Priced near its cash",
        "U" => "Big analyst upside",
        "S" => "Institution-backed",
        "I" => "Insider-owned",
        "M" => "Momentum",
        "T" => metal != null ? $"{metal.Split(' ')[0]} tailwind" : null,
        "B" => "Near book value",
        "K" => "Scalable",
        "V" => "Already producing",
        "G" => "Fast growth",
        "Q" => "High margins",
        "P" => "Cheap for its growth",
        "F" => "Self-funding",
        _ => null,
    };

    private static string? FirstSentence(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var i = s.IndexOf(". ", StringComparison.Ordinal);
        var first = i > 40 ? s[..(i + 1)] : s;
        return first.Length > 260 ? first[..257].TrimEnd() + "…" : first;
    }

    private static string Pct(double v) => v >= 10 ? $"{v * 100:N0}%" : $"{v * 100:0}%";

    private static string Symbol(string cur) => cur.ToUpperInvariant() switch
    {
        "USD" => "$", "CAD" => "C$", "AUD" => "A$", "EUR" => "€", "GBP" => "£", _ => cur + " ",
    };

    internal static string Money(double? v, string cur)
    {
        if (v == null) return "–";
        var a = Math.Abs(v.Value); var s = Symbol(cur);
        var body = a >= 1e12 ? $"{a / 1e12:0.0}T" : a >= 1e9 ? $"{a / 1e9:0.0}B" : a >= 1e6 ? $"{a / 1e6:0}M" : a >= 1e3 ? $"{a / 1e3:0}K" : $"{a:0}";
        return (v < 0 ? "-" : "") + s + body;
    }

    private static string Price(double? v, string cur) => v == null ? "–" : Symbol(cur) + (v >= 10 ? v.Value.ToString("0.00") : v.Value.ToString("0.00##"));
}
