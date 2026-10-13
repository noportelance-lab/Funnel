using Funnel.Providers;

namespace Funnel.Engine;

/// <summary>
/// How well one valuation model suits this company. Level: good | caution | weak | na (can't run
/// or means nothing here). <see cref="Instead"/> lists model ids worth opening instead.
/// </summary>
public sealed record ModelFitNote(string Model, string Level, string Title, string Why, List<string> Instead);

/// <summary>
/// Everything the Model tab needs beyond the DCF: which model suits the industry, how well each
/// one fits, and the starting inputs for the NAV, LBO and excess-return models. The maths runs in
/// app.js, live as inputs change, like the DCF's.
/// </summary>
public sealed class ValuationKit
{
    /// <summary>dcf | nav | lbo | er</summary>
    public string Recommended { get; init; } = "dcf";
    public string RecommendedWhy { get; init; } = "";
    /// <summary>The industry the choice was made on, as shown to the user.</summary>
    public string Industry { get; init; } = "";
    public List<ModelFitNote> Fits { get; init; } = new();
    public NavKit Nav { get; init; } = new();
    public LboKit Lbo { get; init; } = new();
    public ErKit Er { get; init; } = new();
}

/// <summary>Net asset value: reserves in the ground (resource) or properties at a cap rate (property).</summary>
public sealed class NavKit
{
    /// <summary>resource | property | none</summary>
    public string Kind { get; init; } = "none";

    // ---- resource
    public string Commodity { get; init; } = "";
    public string? CommoditySymbol { get; init; }
    /// <summary>oz, lb, bbl, t… or "index" when there's no live price and revenue stands in.</summary>
    public string Unit { get; init; } = "";
    /// <summary>Today's price, in the reporting currency.</summary>
    public double? Spot { get; init; }
    /// <summary>Average price over the last fiscal year, in the reporting currency (what revenue was earned at).</summary>
    public double? FyAvgPrice { get; init; }
    public double Production { get; init; }
    /// <summary>All-in sustaining cost per unit: cash operating costs plus sustaining capex.</summary>
    public double Aisc { get; init; }
    public bool PreProduction { get; init; }
    public double DiscountRate { get; init; } = 0.08;
    public int Life { get; init; } = 10;

    // ---- property
    public double? Noi { get; init; }
    public double CapRate { get; init; } = 0.0625;
    public string PropertyType { get; init; } = "";
    public double Preferred { get; init; }

    public Dictionary<string, string> Notes { get; init; } = new();
}

/// <summary>Leveraged buyout: what a sponsor could pay and earn. The operating case is the DCF's.</summary>
public sealed class LboKit
{
    public double? Ebitda { get; init; }
    public double Leverage { get; init; } = 5;
    public double InterestRate { get; init; } = 0.08;
    public double EntryPremium { get; init; } = 0.30;
    public double FeesPct { get; init; } = 0.02;
    public double TargetIrr { get; init; } = 0.20;
    public int Years { get; init; } = 5;
    public Dictionary<string, string> Notes { get; init; } = new();
}

/// <summary>Excess return (residual income): book value plus the present value of returns above the cost of equity.</summary>
public sealed class ErKit
{
    public double? BookValue { get; init; }
    public double? RoeLatest { get; init; }
    public double? RoeAverage { get; init; }
    public List<double?> RoeHistory { get; init; } = new();
    public double Payout { get; init; } = 0.35;
    public double? NetIncome { get; init; }
    public double? Dividends { get; init; }
    public Dictionary<string, string> Notes { get; init; } = new();
}

public static class Valuation
{
    // Commodity behind each resource industry: Yahoo futures symbol, name, unit.
    private static (string? Symbol, string Name, string Unit, double Discount)? CommodityFor(string profile, string? industry)
    {
        var ind = (industry ?? "").ToLowerInvariant();
        if (ind.Contains("gold")) return ("GC=F", "Gold", "oz", 0.05);
        if (ind.Contains("silver")) return ("SI=F", "Silver", "oz", 0.05);
        if (ind.Contains("other precious")) return ("PL=F", "Platinum", "oz", 0.05);
        if (ind.Contains("copper")) return ("HG=F", "Copper", "lb", 0.08);
        if (ind.Contains("aluminum")) return ("ALI=F", "Aluminum", "t", 0.08);
        if (profile is "Oil & gas producers" or "Integrated oil & gas" or "Energy (sector)") return ("CL=F", "WTI crude oil", "bbl", 0.10);
        if (profile == "Gold & precious metals") return ("GC=F", "Gold", "oz", 0.05);
        if (profile == "Metals & mining") return (null, ind.Contains("uranium") ? "Uranium" : ind.Contains("coal") ? "Coal" : "Metal", "index", 0.08);
        return null;
    }

    private static double CapRateFor(string? industry, out string type)
    {
        var ind = (industry ?? "").ToLowerInvariant();
        (string, double)[] table =
        {
            ("industrial", 0.055), ("residential", 0.0525), ("office", 0.075), ("retail", 0.0675),
            ("healthcare", 0.065), ("hotel", 0.08), ("specialty", 0.06), ("diversified", 0.065),
        };
        foreach (var (k, r) in table)
            if (ind.Contains(k)) { type = k; return r; }
        type = "";
        return 0.0625;
    }

    private static string Cat(string profile, string? industry, bool lossNow, bool neverProfitable)
    {
        var ind = (industry ?? "").ToLowerInvariant();
        switch (profile)
        {
            case "Banks & lenders": case "Financials (sector)": return "bank";
            case "Insurance": return "insurance";
            case "Capital markets & asset managers": return "capmkts";
            case "REITs": return ind.Contains("mortgage") ? "reitMortgage" : "reitEquity";
            case "Real estate operations": return "reitEquity";
            case "Oil & gas producers": case "Gold & precious metals": case "Metals & mining": return "resource";
            case "Integrated oil & gas": case "Energy (sector)": return "resourceMixed";
            case "Oilfield services": case "Refining & marketing": return "commodityService";
            case "Steel & aluminum": case "Marine shipping": return "cyclical";
        }
        if (profile is "Biotechnology" or "Pharmaceuticals" && lossNow) return "biotechLoss";
        if (neverProfitable) return "unprofitable";
        return "general";
    }

    public static async Task<ValuationKit> BuildAsync(
        CompanySnapshot co, DcfBase b, DcfAssumptions a,
        FinancialStatement income, FinancialStatement? balance, FinancialStatement? cashflow, CancellationToken ct)
    {
        var (p, level, matchedOn, _) = IndustryProfiles.Match(co.Sector, co.Industry);
        var industryName = level == "market" ? (co.Industry ?? "this industry") : p.Name.Replace(" (sector)", "");

        double? Last(List<double?> xs) => xs.LastOrDefault(v => v is { } x && double.IsFinite(x));
        var ebitHist = b.Ebit;
        var ops = ebitHist.Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).ToList();
        bool lossNow = ops.Count > 0 && ops[^1] < 0;
        bool neverProfitable = ops.Count >= 2 && ops.All(x => x < 0);
        var revenue = Last(b.Revenue);
        var ebit = Last(b.Ebit);
        var da = Last(b.Da);
        var capex = Last(b.Capex) is { } cx ? -cx : (double?)null;
        double? ebitda = ebit != null ? ebit + (da ?? 0) : null;
        co.Metrics.TryGetValue("marketCap", out var mcap);
        co.Metrics.TryGetValue("revenueGrowth", out var revGrowth);

        // Statement rows, newest last, aligned to the model's fiscal years.
        List<double?> Row(FinancialStatement? st, params string[] keys)
        {
            var line = keys.Select(k => st?.Lines.FirstOrDefault(l => l.Key == k && l.Values.Any(v => v != null))).FirstOrDefault(l => l != null);
            return b.Periods.Select(per =>
            {
                var i = st?.Periods.IndexOf(per) ?? -1;
                return line != null && i >= 0 && i < line.Values.Count ? line.Values[i] : null;
            }).ToList();
        }

        var cat = Cat(p.Name, co.Industry, lossNow, neverProfitable);

        // ---------------------------------------------------------------- NAV
        NavKit nav;
        var navNotes = new Dictionary<string, string>();
        if (cat is "resource" or "resourceMixed" && CommodityFor(p.Name, co.Industry) is { } com)
        {
            double? spot = null, fyAvg = null;
            string? sym = com.Symbol;
            if (sym != null && ProviderRegistry.YahooEnabled)
            {
                try
                {
                    var series = await ProviderRegistry.Yahoo.ChartAsync(sym, "5y", ct);
                    spot = series.Points.LastOrDefault()?.C;
                    if (b.Periods.LastOrDefault() is { } fy && DateTime.TryParse(fy, out var fyEnd))
                    {
                        long t1 = new DateTimeOffset(fyEnd.AddDays(1)).ToUnixTimeSeconds(), t0 = new DateTimeOffset(fyEnd.AddYears(-1)).ToUnixTimeSeconds();
                        var inYear = series.Points.Where(x => x.T >= t0 && x.T < t1 && x.C != null).Select(x => x.C!.Value).ToList();
                        if (inYear.Count > 0) fyAvg = inYear.Average();
                    }
                    // Futures are in US dollars; the statements may not be.
                    var cur = (b.Currency ?? "USD").ToUpperInvariant();
                    if (cur != "USD" && cur.Length == 3)
                    {
                        var q = await ProviderRegistry.Yahoo.RawQuotesAsync(new[] { $"USD{cur}=X" }, ct);
                        if (q.TryGetValue($"USD{cur}=X", out var n) && J.Num(n?["regularMarketPrice"]) is > 0 and var fx)
                        { spot *= fx; fyAvg *= fx; navNotes["fx"] = $"{com.Name} prices converted from US dollars at {fx:0.####} {cur} per USD."; }
                        else { spot = null; fyAvg = null; }
                    }
                }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException) { spot = null; fyAvg = null; }
            }

            bool index = spot is not > 0 || fyAvg is not > 0;
            string unit = index ? "index" : com.Unit;
            double price0 = index ? 1 : fyAvg!.Value;
            // A miner losing money at the operating line (an explorer with a little revenue, say) has
            // no production costs worth projecting: treat it as pre-production and ask for the mine plan.
            bool pre = revenue is not > 0 || ebitda is not > 0;
            double production = pre ? 0 : revenue!.Value / price0;
            // All-in sustaining cost: revenue less EBITDA is the cash cost base; sustaining capex
            // is taken as the smaller of capex and D&A (keeping the asset base whole).
            var sustaining = capex is > 0 ? Math.Min(capex.Value, da is > 0 ? da.Value : capex.Value * 0.6) : 0;
            double aisc = pre || ebitda == null ? 0 : Math.Max(0, (revenue!.Value - ebitda.Value + sustaining) / production);

            navNotes["price"] = index
                ? $"No live {com.Name.ToLowerInvariant()} price is available, so the model runs on a price index: 1.00 = what the company earned per unit last fiscal year. Change it to test price moves."
                : $"{com.Name} ({sym}) today; it fades to the long-term price by year 3. Many analysts use a long-term price below spot.";
            navNotes["production"] = pre
                ? (revenue is > 0 ? "Revenue doesn't cover operating costs yet, so there's no production to project. " : "No revenue yet. ") + "Enter the mine plan from the latest technical study (feasibility study or PEA): annual production, all-in cost, life and initial capex."
                : index ? "Last fiscal year's revenue, in index units." : $"Implied: last fiscal year's revenue ÷ the average {com.Name.ToLowerInvariant()} price that year ({price0:#,0.00}/{com.Unit}). Replace it with reported production if you have it.";
            navNotes["aisc"] = pre ? "Enter the all-in sustaining cost per unit from the technical study."
                : "Implied all-in sustaining cost: (revenue − EBITDA + sustaining capex) ÷ production, with sustaining capex taken as the smaller of capex and D&A.";
            navNotes["life"] = "Reserve life in years is not in the financial data. 10 years is a placeholder: set it to reserves ÷ annual production from the latest reserve statement.";
            navNotes["discount"] = $"Real discount rate: prices and costs are in today's money. Conventions: 5% for gold and silver, 8% for base metals, 10% for oil and gas (PV-10).";
            nav = new NavKit
            {
                Kind = "resource", Commodity = com.Name, CommoditySymbol = index ? null : sym, Unit = unit,
                Spot = index ? 1 : spot, FyAvgPrice = price0, Production = production, Aisc = aisc, PreProduction = pre,
                DiscountRate = com.Discount, Life = 10, Notes = navNotes,
            };
        }
        else if (cat == "reitEquity")
        {
            var capRate = CapRateFor(co.Industry, out var type);
            var pref = Row(balance, "PreferredStock", "PreferredStockEquity").LastOrDefault(v => v != null) ?? 0;
            navNotes["noi"] = "Last fiscal year's EBITDA stands in for net operating income (after corporate costs, so a little conservative). Replace it with the NOI from the supplemental package if you have it.";
            navNotes["capRate"] = $"Typical private-market cap rate for {(type.Length > 0 ? type + " property" : "a diversified portfolio")}; use the rate recent comparable sales imply.";
            nav = new NavKit { Kind = "property", Noi = ebitda, CapRate = capRate, PropertyType = type, Preferred = Math.Max(0, pref), Notes = navNotes };
        }
        else nav = new NavKit { Kind = "none" };

        // ---------------------------------------------------------------- LBO
        var lboNotes = new Dictionary<string, string>
        {
            ["case"] = "Revenue growth, margins, D&A, capex, working capital and tax come from the DCF tab, so the buyout runs on the same operating case.",
            ["leverage"] = $"Debt as a multiple of EBITDA. {p.Name}: cyclicality {p.Cyclicality:0.0} of 1, so lenders would likely go to about this much.",
            ["rate"] = "Blended rate on buyout debt: the risk-free rate plus about 4% (leveraged loans and high-yield bonds).",
        };
        var lbo = new LboKit
        {
            Ebitda = ebitda,
            Leverage = Math.Round(Math.Clamp(6 - 4 * p.Cyclicality, 2, 6) * 2) / 2,
            InterestRate = Math.Round((a.Rf + 0.04) * 400) / 400,
            Notes = lboNotes,
        };

        // ---------------------------------------------------------------- excess return
        var equity = Row(balance, "StockholdersEquity", "CommonStockEquity", "TotalEquityGrossMinorityInterest");
        var ni = Row(income, "NetIncomeCommonStockholders", "NetIncome", "NetIncomeFromContinuingOperations");
        var divs = Row(cashflow, "CashDividendsPaid", "CommonStockDividendPaid");
        var pref2 = Row(balance, "PreferredStock", "PreferredStockEquity").LastOrDefault(v => v != null) ?? 0;
        var roe = b.Periods.Select((_, i) =>
        {
            if (ni[i] is not { } n || equity[i] is not { } e) return (double?)null;
            var avg = i > 0 && equity[i - 1] is { } e0 ? (e + e0) / 2 : e;
            return avg > 0 ? n / avg : null;
        }).ToList();
        var roes = roe.Where(x => x != null).Select(x => x!.Value).TakeLast(3).ToList();
        var payouts = b.Periods.Select((_, i) => ni[i] is > 0 && divs[i] is { } d ? Math.Abs(d) / ni[i]!.Value : (double?)null)
            .Where(x => x != null).Select(x => x!.Value).TakeLast(3).ToList();
        var bv = equity.LastOrDefault(v => v != null) is { } bvl ? bvl - Math.Max(0, pref2) : (double?)null;
        var erNotes = new Dictionary<string, string>
        {
            ["book"] = $"Common shareholders' equity on the latest balance sheet ({b.StatementsSource}){(pref2 > 0 ? ", less preferred shares" : "")}.",
            ["roe"] = roes.Count > 0 ? $"Year 1: last year's return on average equity ({roe.LastOrDefault(x => x != null):P1}); it fades in a straight line to the long-run ROE by the final year." : "No return on equity could be worked out from the statements.",
            ["roeLong"] = roes.Count > 0 ? $"{roes.Count}-year average ROE ({roes.Average():P1}), kept within 6 points of the cost of equity: excess returns rarely last forever." : "",
            ["payout"] = payouts.Count > 0 ? $"{payouts.Count}-year average dividends ÷ net income." : "35% placeholder (no dividend history).",
        };
        var er = new ErKit
        {
            BookValue = bv,
            RoeHistory = roe,
            RoeLatest = roe.LastOrDefault(x => x != null),
            RoeAverage = roes.Count > 0 ? roes.Average() : null,
            Payout = payouts.Count > 0 ? Math.Clamp(payouts.Average(), 0, 0.95) : 0.35,
            NetIncome = ni.LastOrDefault(x => x != null),
            Dividends = divs.LastOrDefault(x => x != null) is { } dv ? Math.Abs(dv) : null,
            Notes = erNotes,
        };

        // ---------------------------------------------------------------- fit
        var fits = Fits(cat, p, industryName, b.Fit, nav, lbo, er, ebitda, mcap, revGrowth, revenue);
        var (rec, recWhy) = cat switch
        {
            "bank" => ("er", $"Banks are valued on book value and the return they earn on it, so the excess-return model leads."),
            "insurance" => ("er", "Insurers are valued on book value and the return they earn on it, so the excess-return model leads."),
            "capmkts" => ("er", "Financial firms' balance sheets hold client money, so book value and return on equity lead, not free cash flow."),
            "reitMortgage" => ("er", "A mortgage REIT is a lender: its value is its book of loans and the return on it."),
            "reitEquity" => ("nav", "Property companies are valued on what their buildings would sell for: net asset value at market cap rates."),
            "resource" => ("nav", "Miners and producers are valued on their reserves: the net asset value of what's left in the ground at long-run prices."),
            "resourceMixed" => ("nav", "Integrated energy companies are often valued as reserves plus the rest; NAV leads, with the DCF as a cross-check."),
            _ => ("dcf", "Its value comes from the cash its operations generate, which is what a DCF measures."),
        };
        if (rec == "nav" && nav.Kind == "none") (rec, recWhy) = ("dcf", "Its value comes from the cash its operations generate, which is what a DCF measures.");
        if (rec == "er" && er.BookValue is not > 0) (rec, recWhy) = ("dcf", "Book value isn't usable here, so the DCF is the starting point.");

        return new ValuationKit { Recommended = rec, RecommendedWhy = recWhy, Industry = level == "market" ? "" : industryName, Fits = fits, Nav = nav, Lbo = lbo, Er = er };
    }

    private static List<ModelFitNote> Fits(string cat, IndustryProfile p, string industry, DcfFitWarning? dcfFit,
        NavKit nav, LboKit lbo, ErKit er, double? ebitda, double? mcap, double? growth, double? revenue)
    {
        var list = new List<ModelFitNote>();
        ModelFitNote F(string model, string level, string title, string why, params string[] instead) => new(model, level, title, why, instead.ToList());
        var ind = industry.ToLowerInvariant();
        string[] better = cat switch
        {
            "bank" or "insurance" or "capmkts" or "reitMortgage" => new[] { "er" },
            "reitEquity" or "resource" or "resourceMixed" => new[] { "nav" },
            _ => new[] { "dcf" },
        };

        // DCF
        list.Add(dcfFit != null
            ? F("dcf", dcfFit.Severity == "weak" ? "weak" : "caution", dcfFit.Title, dcfFit.Why, better.Where(x => x != "dcf").ToArray())
            : F("dcf", "good", "A DCF suits this company", "Its value comes from the cash its operations generate, and the history is a reasonable guide to the forecast."));

        // NAV
        if (nav.Kind == "resource")
        {
            if (nav.PreProduction)
                list.Add(F("nav", "caution", "A NAV fits, but there's no profitable production yet",
                    "Value for a developer or explorer comes from a mine plan the financial statements don't show. Enter production, cost, life and initial capex from the latest technical study, or the NAV is just the cash."));
            else if (nav.Unit == "index")
                list.Add(F("nav", "caution", $"A NAV fits, but there's no live {nav.Commodity.ToLowerInvariant()} price",
                    "The model runs on a price index (1.00 = last year's realised price), so test price moves by changing it. Reserve life is a placeholder until you set it."));
            else if (cat == "resourceMixed")
                list.Add(F("nav", "caution", "A NAV fits the upstream business only",
                    "Integrated companies also refine and sell products; this NAV treats all of revenue as oil production. Use it with the DCF, or as a sum of the parts."));
            else
                list.Add(F("nav", "good", "NAV is the standard way to value a producer",
                    "Revenue is volume times a price the market sets, and the reserves run out. NAV values the remaining production at a chosen price deck, then subtracts net debt. Set the reserve life from the latest reserve statement; 10 years is a placeholder."));
        }
        else if (nav.Kind == "property")
            list.Add(F("nav", "good", "NAV is the standard way to value a property company",
                "Buildings have a market price set by cap rates. Net operating income at a market cap rate, less debt and preferred, is what the portfolio would fetch."));
        else
        {
            var why = cat switch
            {
                "reitMortgage" => "A mortgage REIT owns loans, not buildings: its asset value is its book value, which the excess-return model starts from.",
                "bank" or "insurance" or "capmkts" => "A financial company's assets are loans and securities already carried near market value, so asset value is book value. The excess-return model starts there and adds the value of returns above the cost of equity.",
                "cyclical" when ind.Contains("shipping") => "Shipping companies are often valued on fleet NAV at second-hand ship prices, which Funnel doesn't have. Use EV / EBITDA at mid-cycle earnings and the DCF's sensitivity table.",
                _ => $"NAV values a company by its saleable assets: reserves in the ground or buildings at market cap rates. A {industry.ToLowerInvariant()} company's value is in the cash its operations generate, not in assets someone would buy one by one.",
            };
            list.Add(F("nav", "na", "NAV doesn't apply here", why, better));
        }

        // LBO
        if (cat is "bank" or "insurance" or "capmkts" or "reitMortgage")
            list.Add(F("lbo", "na", "LBOs don't work for financial companies",
                "Borrowing is already the business, and regulators cap leverage, so there's no room to layer buyout debt on top.", better));
        else if (ebitda is not > 0)
            list.Add(F("lbo", "na", "No EBITDA to borrow against",
                "A buyout is paid for with debt that the company's cash flow services. With no positive EBITDA there is nothing for lenders to lend against, so an LBO says nothing about value.", better));
        else if (cat == "reitEquity")
            list.Add(F("lbo", "weak", "An LBO is a weak fit for a REIT",
                "REITs already run on property debt and pay out most of their income, leaving little cash to repay buyout debt. Private equity buys them on NAV, so use the NAV model.", "nav"));
        else if (cat is "resource" or "resourceMixed" or "commodityService" or "cyclical")
            list.Add(F("lbo", "weak", "Buyout debt and commodity cycles don't mix",
                "Cash flow swings with prices the company doesn't control, so lenders allow little debt and a bad year can breach covenants. The LBO price here says more about the cycle than the business.", better));
        else if (cat is "biotechLoss" or "unprofitable")
            list.Add(F("lbo", "weak", "Not a buyout candidate",
                "Without steady profits there's no cash to pay down debt.", better));
        else if (mcap > 60e9)
            list.Add(F("lbo", "caution", "Too big for most buyouts",
                $"At {mcap / 1e9:#,0}B, few sponsors could raise the equity and debt (the largest LBOs have been around $40–70B). Read the result as a floor on value, not a likely deal."));
        else if (growth > 0.25)
            list.Add(F("lbo", "caution", "Fast growers rarely carry buyout debt",
                "Growth companies reinvest their cash rather than repaying debt, so sponsors use less leverage and pay up for the growth. The price here is a floor."));
        else
            list.Add(F("lbo", "good", "An LBO gives a floor on value",
                "Steady cash flows can carry buyout debt. The price a sponsor could pay and still earn its target return is a floor the market rarely lets the stock fall far below for long."));

        // Excess return
        if (er.BookValue is not > 0)
            list.Add(F("er", "na", "No usable book value",
                "The excess-return model starts from book value, and this company's is negative or missing (often after large buybacks or losses).", better));
        else if (cat is "bank" or "insurance" or "reitMortgage")
            list.Add(F("er", "good", "The excess-return model is built for lenders and insurers",
                "Their assets sit on the balance sheet near market value, so book value is a solid start, and value beyond it comes from earning more than the cost of equity."));
        else if (cat == "capmkts")
            list.Add(F("er", "caution", "Fits, but book value understates asset-light firms",
                "Brokers and asset managers earn on client assets that aren't on their books, so ROE runs high and most of the value is in the excess returns, which depend on how long you let them last."));
        else
            list.Add(F("er", "caution", "Works, but book value means less here",
                $"For a {industry.ToLowerInvariant()} company much of the value is in brands, know-how or customers that aren't on the balance sheet, so most of the answer comes from the assumed ROE, not book value.", better));
        return list;
    }
}
