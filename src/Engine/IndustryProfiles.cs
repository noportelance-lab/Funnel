namespace Funnel.Engine;

/// <summary>
/// Long-run norms for one industry: what a mature company in it typically earns and reinvests,
/// how risky it is and what the market pays for it. Rates are fractions of revenue (NWC: of the
/// change in revenue); <see cref="BetaU"/> is unlevered. <see cref="Cyclicality"/> (0–1) is how
/// much margins swing through a cycle, so how far a few years of history can mislead.
/// </summary>
public sealed record IndustryProfile(
    string Name, double Margin, double MarginLo, double MarginHi, double DaPct, double CapexPct, double NwcPct,
    double BetaU, double EvEbitda, double Cyclicality, bool Financial = false);

/// <summary>
/// Approximate long-run industry norms (rounded medians and interquartile margin ranges in the
/// spirit of Damodaran's US industry data), and the matching from a data source's sector and
/// industry labels (Yahoo, FMP, Finnhub and SEC SIC descriptions all phrase them differently).
/// </summary>
public static class IndustryProfiles
{
    //                                   margin  lo     hi     D&A    capex  NWC    βu    EV/EBITDA cyc
    static IndustryProfile P(string n, double m, double lo, double hi, double da, double cx, double nwc, double b, double ev, double cyc, bool fin = false)
        => new(n, m, lo, hi, da, cx, nwc, b, ev, cyc, fin);

    public static readonly IndustryProfile Market = P("Broad market", 0.12, 0.06, 0.20, 0.040, 0.045, 0.08, 1.00, 13, 0.5);

    /// <summary>Most specific first: the first rule with a keyword in the industry label wins.</summary>
    private static readonly (string[] Keys, IndustryProfile Profile)[] Rules =
    {
        (new[] { "utilit", "electric services", "gas distribution", "water supply", "cogeneration", "electric & other services" },
            P("Utilities", 0.22, 0.17, 0.27, 0.130, 0.300, 0.00, 0.45, 12, 0.1)),
        (new[] { "reit", "real estate investment trust" },
            P("REITs", 0.30, 0.20, 0.40, 0.250, 0.100, 0.00, 0.65, 18, 0.3)),
        (new[] { "real estate" },
            P("Real estate operations", 0.14, 0.06, 0.22, 0.030, 0.020, 0.10, 0.90, 14, 0.5)),
        (new[] { "healthcare plans", "health care plans", "medical service plans", "managed care" },
            P("Health insurers", 0.05, 0.03, 0.07, 0.010, 0.010, 0.00, 0.80, 11, 0.1)),
        (new[] { "bank", "savings institution", "credit services", "mortgage finance", "credit institution" },
            P("Banks & lenders", 0.30, 0.20, 0.40, 0.010, 0.010, 0.00, 0.70, 10, 0.5, true)),
        (new[] { "insurance", "reinsurance" },
            P("Insurance", 0.12, 0.07, 0.18, 0.010, 0.010, 0.00, 0.75, 10, 0.3, true)),
        (new[] { "asset management", "capital markets", "financial data", "exchange", "broker", "investment", "financial conglomerate", "shell compan" },
            P("Capital markets & asset managers", 0.30, 0.18, 0.40, 0.020, 0.015, 0.00, 1.05, 13, 0.6, true)),
        (new[] { "semiconductor equipment" },
            P("Semiconductor equipment", 0.25, 0.15, 0.32, 0.030, 0.040, 0.20, 1.35, 20, 0.6)),
        (new[] { "semiconductor" },
            P("Semiconductors", 0.25, 0.12, 0.35, 0.070, 0.090, 0.15, 1.35, 22, 0.6)),
        (new[] { "software", "prepackaged", "computer programming", "data processing" },
            P("Software", 0.24, 0.10, 0.35, 0.040, 0.030, 0.05, 1.15, 24, 0.2)),
        (new[] { "internet content", "interactive media", "internet information", "online media" },
            P("Internet & interactive media", 0.22, 0.08, 0.32, 0.060, 0.070, 0.03, 1.10, 18, 0.3)),
        (new[] { "internet retail", "catalog & mail-order", "e-commerce", "online retail" },
            P("Internet retail", 0.06, 0.02, 0.10, 0.050, 0.060, -0.02, 1.10, 16, 0.4)),
        (new[] { "gaming & multimedia", "electronic gaming", "video game" },
            P("Video games", 0.20, 0.08, 0.30, 0.050, 0.030, 0.05, 1.05, 18, 0.3)),
        (new[] { "computer hardware", "consumer electronics", "computer storage", "electronic computers", "computer peripheral" },
            P("Hardware & consumer electronics", 0.12, 0.05, 0.25, 0.030, 0.030, 0.05, 1.10, 15, 0.4)),
        (new[] { "communication equipment", "communications equipment", "electronic component", "scientific & technical", "electronic instrument", "telephone & telegraph apparatus", "printed circuit" },
            P("Electronic & communication equipment", 0.12, 0.06, 0.20, 0.040, 0.040, 0.15, 1.05, 15, 0.4)),
        (new[] { "information technology services", "it services", "consulting services", "computer integrated", "computer services" },
            P("IT services & consulting", 0.12, 0.07, 0.18, 0.030, 0.020, 0.10, 0.95, 15, 0.2)),
        (new[] { "telecom", "communications", "wireless", "cable & other pay" },
            P("Telecom services", 0.18, 0.10, 0.25, 0.160, 0.160, 0.02, 0.55, 7, 0.2)),
        (new[] { "advertising", "marketing services" },
            P("Advertising & marketing", 0.12, 0.08, 0.16, 0.030, 0.020, 0.03, 0.95, 10, 0.5)),
        (new[] { "entertainment", "broadcasting", "publishing", "media", "motion picture", "television" },
            P("Media & entertainment", 0.12, 0.05, 0.20, 0.050, 0.030, 0.05, 0.95, 11, 0.4)),
        (new[] { "restaurant", "eating places" },
            P("Restaurants", 0.15, 0.08, 0.20, 0.040, 0.060, 0.00, 0.85, 17, 0.4)),
        (new[] { "grocery", "food distribution", "food stores", "convenience store" },
            P("Grocery & food distribution", 0.03, 0.02, 0.05, 0.020, 0.025, 0.02, 0.50, 8, 0.1)),
        (new[] { "discount", "department store", "variety store", "general merchandise", "warehouse club", "drug store", "pharmaceutical retail" },
            P("Discount, department & drug stores", 0.05, 0.03, 0.08, 0.025, 0.030, 0.05, 0.80, 13, 0.3)),
        (new[] { "retail" },
            P("Apparel & specialty retail", 0.09, 0.05, 0.14, 0.035, 0.035, 0.10, 0.95, 10, 0.5)),
        (new[] { "apparel", "footwear", "luxury", "textile" },
            P("Apparel, footwear & luxury brands", 0.12, 0.06, 0.20, 0.030, 0.030, 0.15, 1.00, 14, 0.5)),
        (new[] { "auto parts", "motor vehicle parts", "tires" },
            P("Auto parts", 0.07, 0.04, 0.10, 0.040, 0.045, 0.10, 1.10, 8, 0.8)),
        (new[] { "auto manufacturers", "motor vehicles", "automobile" },
            P("Automakers", 0.06, 0.03, 0.10, 0.050, 0.060, 0.05, 1.10, 8, 0.8)),
        (new[] { "residential construction", "homebuild", "operative builders" },
            P("Homebuilders", 0.15, 0.10, 0.18, 0.010, 0.010, 0.40, 1.15, 9, 0.8)),
        (new[] { "lodging", "hotel", "resort", "casino", "gambling", "travel services", "leisure", "cruise", "recreational", "amusement" },
            P("Hotels, travel & leisure", 0.14, 0.08, 0.22, 0.060, 0.070, 0.00, 1.15, 12, 0.7)),
        (new[] { "household", "personal products", "cosmetic", "perfume" },
            P("Household & personal products", 0.17, 0.12, 0.22, 0.030, 0.035, 0.05, 0.70, 16, 0.1)),
        (new[] { "beverage", "brewer", "distiller", "winer", "bottled" },
            P("Beverages", 0.20, 0.12, 0.28, 0.040, 0.045, 0.05, 0.70, 17, 0.1)),
        (new[] { "tobacco", "cigarette" },
            P("Tobacco", 0.40, 0.30, 0.50, 0.020, 0.020, 0.00, 0.60, 10, 0.1)),
        (new[] { "packaged food", "food products", "confectioner", "farm products", "food & kindred", "meat", "dairy" },
            P("Packaged food", 0.13, 0.08, 0.18, 0.035, 0.035, 0.05, 0.55, 12, 0.1)),
        (new[] { "biotech", "biological products" },
            P("Biotechnology", 0.18, -0.05, 0.30, 0.040, 0.040, 0.10, 1.10, 15, 0.3)),
        (new[] { "drug manufacturers", "pharmaceutical" },
            P("Pharmaceuticals", 0.25, 0.15, 0.35, 0.060, 0.040, 0.10, 0.85, 13, 0.1)),
        (new[] { "diagnostics", "research", "laboratory", "life sciences tools" },
            P("Diagnostics & life-science tools", 0.17, 0.10, 0.24, 0.060, 0.050, 0.10, 1.00, 18, 0.3)),
        (new[] { "medical device", "medical instrument", "surgical", "medical equipment", "orthopedic", "electromedical" },
            P("Medical devices", 0.18, 0.10, 0.25, 0.050, 0.045, 0.15, 0.95, 19, 0.2)),
        (new[] { "medical care", "health care", "healthcare facilit", "hospital", "nursing", "health information" },
            P("Healthcare providers & services", 0.09, 0.05, 0.14, 0.040, 0.045, 0.05, 0.80, 11, 0.2)),
        (new[] { "aerospace", "defense", "aircraft", "guided missile" },
            P("Aerospace & defense", 0.10, 0.07, 0.15, 0.030, 0.030, 0.10, 0.95, 16, 0.3)),
        (new[] { "airline", "air transportation, scheduled", "air courier" },
            P("Airlines", 0.07, 0.03, 0.10, 0.050, 0.080, -0.05, 1.10, 6, 0.9)),
        (new[] { "railroad" },
            P("Railroads", 0.36, 0.30, 0.40, 0.100, 0.160, 0.00, 0.90, 14, 0.4)),
        (new[] { "marine shipping", "deep sea", "water transportation" },
            P("Marine shipping", 0.20, 0.08, 0.30, 0.120, 0.150, 0.02, 1.00, 6, 0.9)),
        (new[] { "trucking", "freight", "logistics", "courier", "delivery" },
            P("Trucking & logistics", 0.07, 0.04, 0.11, 0.050, 0.070, 0.05, 1.00, 10, 0.7)),
        (new[] { "gold", "silver", "precious" },
            P("Gold & precious metals", 0.25, 0.12, 0.35, 0.120, 0.170, 0.05, 0.85, 9, 0.9)),
        (new[] { "steel", "aluminum", "blast furnace" },
            P("Steel & aluminum", 0.08, 0.04, 0.12, 0.040, 0.050, 0.15, 1.10, 7, 0.9)),
        (new[] { "mining", "copper", "metals", "uranium", "coal" },
            P("Metals & mining", 0.20, 0.10, 0.30, 0.090, 0.120, 0.05, 1.05, 8, 0.9)),
        (new[] { "chemical", "agricultural inputs", "fertilizer", "plastics" },
            P("Chemicals", 0.12, 0.07, 0.16, 0.050, 0.055, 0.15, 0.95, 11, 0.7)),
        (new[] { "packaging", "containers", "paper", "lumber", "wood" },
            P("Paper, packaging & forest products", 0.10, 0.06, 0.14, 0.050, 0.050, 0.10, 0.90, 9, 0.7)),
        (new[] { "e&p", "exploration", "crude petroleum" },
            P("Oil & gas producers", 0.28, 0.15, 0.38, 0.250, 0.350, 0.00, 1.05, 6, 0.9)),
        (new[] { "equipment & services", "oil & gas drilling", "oil & gas field", "oilfield" },
            P("Oilfield services", 0.12, 0.06, 0.17, 0.060, 0.070, 0.15, 1.10, 8, 0.9)),
        (new[] { "midstream", "pipeline", "natural gas transmission" },
            P("Oil & gas midstream", 0.20, 0.12, 0.28, 0.080, 0.120, 0.00, 0.80, 10, 0.4)),
        (new[] { "refining", "petroleum refining" },
            P("Refining & marketing", 0.05, 0.02, 0.08, 0.030, 0.030, 0.05, 1.00, 6, 0.9)),
        (new[] { "oil", "petroleum" },
            P("Integrated oil & gas", 0.12, 0.08, 0.16, 0.070, 0.080, 0.05, 0.90, 6, 0.9)),
        (new[] { "solar", "renewable" },
            P("Solar & renewable equipment", 0.08, -0.05, 0.15, 0.050, 0.080, 0.10, 1.30, 13, 0.7)),
        (new[] { "machinery", "electrical equipment", "industrial", "tools & accessories", "metal fabrication", "pollution", "conglomerate" },
            P("Machinery & industrial equipment", 0.14, 0.09, 0.19, 0.030, 0.030, 0.15, 1.00, 15, 0.6)),
        (new[] { "engineering & construction", "building products", "building materials", "construction", "infrastructure" },
            P("Engineering & building products", 0.10, 0.06, 0.14, 0.025, 0.025, 0.10, 1.05, 13, 0.6)),
        (new[] { "staffing", "employment", "waste", "rental & leasing", "security & protection", "business services", "education", "personal services" },
            P("Business services", 0.13, 0.07, 0.20, 0.060, 0.060, 0.05, 0.90, 15, 0.4)),
        (new[] { "wholesale", "distribution", "distributors" },
            P("Wholesale & distribution", 0.05, 0.03, 0.08, 0.015, 0.015, 0.15, 0.90, 12, 0.5)),
    };

    /// <summary>Sector-level fallbacks: Yahoo / FMP sector names, Finnhub's broad industries and the SEC's SIC divisions.</summary>
    private static readonly (string[] Keys, IndustryProfile Profile)[] Sectors =
    {
        (new[] { "utilities" }, Rules[0].Profile),
        (new[] { "real estate" }, P("Real estate (sector)", 0.28, 0.12, 0.40, 0.200, 0.080, 0.00, 0.70, 17, 0.4)),
        (new[] { "financial", "finance", "banking" }, P("Financials (sector)", 0.28, 0.15, 0.40, 0.015, 0.015, 0.00, 0.85, 11, 0.5, true)),
        (new[] { "technology" }, P("Technology (sector)", 0.18, 0.06, 0.30, 0.040, 0.040, 0.08, 1.15, 18, 0.4)),
        (new[] { "communication" }, P("Communication services (sector)", 0.15, 0.06, 0.25, 0.080, 0.080, 0.03, 0.85, 11, 0.3)),
        (new[] { "consumer cyclical", "consumer discretionary" }, P("Consumer cyclical (sector)", 0.09, 0.04, 0.15, 0.040, 0.045, 0.08, 1.05, 11, 0.6)),
        (new[] { "consumer defensive", "consumer staples" }, P("Consumer defensive (sector)", 0.10, 0.04, 0.18, 0.030, 0.035, 0.04, 0.60, 13, 0.1)),
        (new[] { "health" }, P("Healthcare (sector)", 0.15, 0.05, 0.25, 0.040, 0.040, 0.10, 0.90, 14, 0.2)),
        (new[] { "industrials", "manufacturing" }, P("Industrials (sector)", 0.12, 0.07, 0.17, 0.035, 0.035, 0.12, 1.00, 14, 0.6)),
        (new[] { "energy", "mining & energy" }, P("Energy (sector)", 0.14, 0.06, 0.25, 0.100, 0.140, 0.03, 1.00, 7, 0.9)),
        (new[] { "basic materials", "materials" }, P("Basic materials (sector)", 0.14, 0.07, 0.22, 0.060, 0.080, 0.10, 1.00, 9, 0.8)),
        (new[] { "construction" }, P("Engineering & building products", 0.10, 0.06, 0.14, 0.025, 0.025, 0.10, 1.05, 13, 0.6)),
        (new[] { "transportation" }, P("Transportation (sector)", 0.12, 0.05, 0.25, 0.070, 0.090, 0.02, 0.95, 10, 0.6)),
        (new[] { "wholesale" }, P("Wholesale & distribution", 0.05, 0.03, 0.08, 0.015, 0.015, 0.15, 0.90, 12, 0.5)),
        (new[] { "retail" }, P("Apparel & specialty retail", 0.09, 0.05, 0.14, 0.035, 0.035, 0.10, 0.95, 10, 0.5)),
        (new[] { "agriculture" }, P("Packaged food", 0.13, 0.08, 0.18, 0.035, 0.035, 0.05, 0.55, 12, 0.1)),
        (new[] { "services" }, P("Business services", 0.13, 0.07, 0.20, 0.060, 0.060, 0.05, 0.90, 15, 0.4)),
    };

    /// <summary>
    /// The profile for a company, how it was found ("industry" | "sector" | "market"), the label it
    /// matched on, and how much weight the norms deserve (an exact industry 1.0, a sector 0.7,
    /// the broad market 0.4).
    /// </summary>
    public static (IndustryProfile Profile, string Level, string MatchedOn, double Weight) Match(string? sector, string? industry)
    {
        static IndustryProfile? Find((string[] Keys, IndustryProfile Profile)[] table, string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var l = label.ToLowerInvariant();
            return table.FirstOrDefault(r => r.Keys.Any(k => l.Contains(k))).Profile;
        }
        if (Find(Rules, industry) is { } byIndustry) return (byIndustry, "industry", industry!, 1.0);
        if (Find(Sectors, sector) is { } bySector) return (bySector, "sector", sector!, 0.7);
        // Finnhub's "industry" is often a sector-sized bucket ("Technology", "Banking").
        if (Find(Sectors, industry) is { } broad) return (broad, "sector", industry!, 0.7);
        if (Find(Rules, sector) is { } loose) return (loose, "sector", sector!, 0.6);
        return (Market, "market", "", 0.4);
    }
}

/// <summary>
/// A warning that a standard unlevered DCF says little about this kind of company: why, and what
/// practitioners use instead. Severity "weak" means the method doesn't fit the business;
/// "caution" means it fits but the answer is mostly assumption.
/// </summary>
public sealed record DcfFitWarning(string Kind, string Severity, string Industry, string MatchedOn, string Title, string Why, List<string> Instead);

/// <summary>Which companies a free-cash-flow DCF misreads: financials, REITs, commodity producers, pre-profit companies.</summary>
public static class DcfFit
{
    public static DcfFitWarning? Check(string? sector, string? industry, IReadOnlyList<double?> revenue, IReadOnlyList<double?> ebit)
    {
        var (p, level, matchedOn, _) = IndustryProfiles.Match(sector, industry);
        var name = level == "market" ? "" : p.Name.Replace(" (sector)", "");
        DcfFitWarning W(string kind, string sev, string title, string why, params string[] instead)
            => new(kind, sev, name, matchedOn, title, why, instead.ToList());

        // ---------------------------------------------------------------- financials and REITs
        const string FinWhy = " Enterprise value minus net debt doesn't give the equity, and regulators decide how much capital has to stay in the business.";
        switch (p.Name)
        {
            case "Banks & lenders":
                return W("financial", "weak", "A DCF is a weak fit for banks and lenders",
                    "For a bank, deposits and borrowing are raw material, not financing: interest is its main cost, and capex, working capital and \"free cash flow\" have no clean meaning." + FinWhy,
                    "Price-to-book against return on equity: P/B ≈ (ROE − g) ÷ (cost of equity − g)",
                    "A dividend discount or excess-return (residual income) model",
                    "P/E and price-to-tangible-book against peers");
            case "Insurance":
                return W("financial", "weak", "A DCF is a weak fit for insurers",
                    "An insurer invests the premiums it holds (the float), and its profit depends on reserve estimates and investment returns. The float and its debt are part of the business, so free cash flow and net debt don't separate the operations from the financing." + FinWhy,
                    "Price-to-book against return on equity",
                    "Embedded value (life insurers), or combined ratio and book value growth (property & casualty)",
                    "A dividend discount model");
            case "Capital markets & asset managers":
                return W("financial", "weak", "A DCF is a weak fit for brokers, exchanges and asset managers",
                    "Client money and trading balances sit on their balance sheets, so debt, cash and working capital don't mean what they do for an industrial company, and earnings swing with the markets. For holding companies and shells, the value is in what they own.",
                    "P/E against peers",
                    "Value as a share of assets under management (asset managers)",
                    "Sum of the parts or net asset value (holding companies)");
            case "Financials (sector)":
                return W("financial", "weak", "A DCF is a weak fit for financial companies",
                    "For banks, insurers and lenders, debt is raw material rather than financing: interest is an operating cost, and capex, working capital and \"free cash flow\" have no clean meaning." + FinWhy,
                    "Price-to-book against return on equity",
                    "A dividend discount or excess-return (residual income) model",
                    "P/E against peers");
            case "REITs":
                return W("reit", "weak", "A DCF is a weak fit for REITs",
                    "A REIT must pay out about 90% of its taxable income and funds growth by issuing shares and debt, so retained free cash flow isn't what drives it. Depreciation on property that usually holds its value makes EBIT, D&A and capex misleading.",
                    "Net asset value: the properties at market cap rates, less debt",
                    "Price to FFO or AFFO (funds from operations) against peers",
                    "Dividend yield and dividend growth against peers");
        }

        // ---------------------------------------------------------------- clinical-stage biotech
        var ops = ebit.Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).ToList();
        var lastRev = revenue.LastOrDefault(v => v is { } x && double.IsFinite(x)) ?? 0;
        bool lossNow = ops.Count > 0 && ops[^1] < 0;
        bool neverProfitable = ops.Count >= 2 && ops.All(x => x < 0);
        if (p.Name is "Biotechnology" or "Pharmaceuticals" && lossNow)
            return W("pipeline", "weak", "A DCF is a weak fit for a clinical-stage biotech",
                "The company loses money while its value rides on drugs still in trials, each of which either gets approved or doesn't. One smooth forecast hides what is really a handful of yes-or-no bets.",
                "Risk-adjusted NPV (rNPV) drug by drug, using each trial phase's success rate",
                "Cash runway against the trial timeline",
                "Enterprise value against peers at a similar stage");

        // ---------------------------------------------------------------- commodity producers
        const string Midcycle = "If you keep the DCF: use mid-cycle prices and margins, and lean on the sensitivity table";
        switch (p.Name)
        {
            case "Oil & gas producers":
            case "Integrated oil & gas":
            case "Energy (sector)":
                return W("commodity", "weak", "A DCF says little on its own about oil and gas companies",
                    "Cash flows follow oil and gas prices, which the company doesn't control. Projecting a few years of history forward bakes one point of the price cycle into the terminal value, which is most of a DCF's answer. Producers also use up their reserves, so value comes from reserve life rather than growth forever.",
                    "Net asset value of proven and probable reserves (PV-10) at futures-strip prices",
                    "EV / EBITDAX, EV per flowing barrel and price to cash flow against peers",
                    Midcycle);
            case "Oilfield services":
            case "Refining & marketing":
                return W("commodity", "weak", "A DCF says little on its own about oil and gas companies",
                    "Earnings track drilling activity (services) or refining margins (refiners), both set by oil and gas prices that swing through long cycles. Projecting recent years forward bakes one point of that cycle into the terminal value, which is most of a DCF's answer.",
                    "EV / EBITDA on mid-cycle earnings against peers",
                    "Replacement cost of the assets",
                    Midcycle);
            case "Gold & precious metals":
            case "Metals & mining":
                return W("commodity", "weak", "A DCF says little on its own about miners",
                    "Revenue is volume times a metal price the market sets, and mines run out. Projecting recent years forward bakes today's price into the terminal value, and a perpetual terminal value ignores that the reserves end.",
                    "Net asset value of the reserves at long-run metal prices (P/NAV)",
                    "EV / EBITDA at mid-cycle prices, and EV per ounce or tonne of reserves",
                    Midcycle);
            case "Steel & aluminum":
            case "Marine shipping":
                return W("commodity", "caution", $"A DCF of a {name.ToLowerInvariant()} company depends on where the cycle is",
                    "Prices and margins swing hard through the cycle, so a few years of history projected forward (and a terminal value built on them) can be far from a normal year.",
                    "EV / EBITDA on mid-cycle earnings against peers",
                    p.Name == "Marine shipping" ? "Net asset value of the fleet at second-hand ship prices" : "Price-to-book against replacement cost",
                    Midcycle);
        }

        // ---------------------------------------------------------------- never profitable
        if (neverProfitable && lastRev > 0)
            return W("unprofitable", "caution", "A DCF of a company that has never made an operating profit is mostly guesswork",
                "Every reported year shows an operating loss, so the value rests almost entirely on the margin it's assumed to reach years from now and on the terminal value. Small changes there can swing the answer across zero.",
                "EV / revenue against peers at a similar growth stage",
                "Scenarios: weight a turnaround against running out of cash",
                "Cash runway, and the dilution from future fund-raising");
        return null;
    }
}
