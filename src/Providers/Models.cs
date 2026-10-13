namespace Funnel.Providers;

// Provider-neutral shapes. Every data source maps into these, so the UI, the Bridge and the
// Excel export never know (or care) which API a number came from. Serialised to the web UI
// in camelCase.

public sealed record SearchHit(string Symbol, string Name, string Exchange, string Type);

/// <summary>Unit: null (a price), "pct" (a yield, in percent) or "fx". AsOf is set for daily series.</summary>
public sealed record MarketQuote(
    string Symbol, string Name, double? Price, double? Change, double? ChangePct,
    string? Currency, string Group, string? Unit = null, string? AsOf = null);

public sealed record PricePoint(long T, double? O, double? H, double? L, double? C, long? V);

public sealed class PriceSeries
{
    public string Symbol { get; init; } = "";
    public string Range { get; init; } = "";
    public string Interval { get; init; } = "";
    public string Currency { get; init; } = "";
    public double? PreviousClose { get; init; }
    public List<PricePoint> Points { get; init; } = new();
}

public sealed class CompanySnapshot
{
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string Exchange { get; init; } = "";
    public string Currency { get; init; } = "";
    public string? FinancialCurrency { get; init; }
    public string? QuoteType { get; init; }
    public string? Sector { get; init; }
    public string? Industry { get; init; }
    public string? Country { get; init; }
    public string? City { get; init; }
    public string? Website { get; init; }
    public long? Employees { get; init; }
    public string? Summary { get; init; }
    public double? Price { get; init; }
    public double? Change { get; init; }
    public double? ChangePct { get; init; }
    public string? MarketState { get; init; }
    /// <summary>Keyed by <see cref="MetricDef.Key"/>.</summary>
    public Dictionary<string, double?> Metrics { get; init; } = new();
    public AnalystView Analysts { get; init; } = new();
    public List<string> Peers { get; set; } = new();
    /// <summary>How the suggested peers were picked, e.g. "Same SIC code (3571)".</summary>
    public string? PeersBasis { get; set; }
    public string Source { get; init; } = "";
}

public sealed class AnalystView
{
    public string? RecommendationKey { get; init; }
    /// <summary>1 = Strong Buy … 5 = Strong Sell.</summary>
    public double? RecommendationMean { get; init; }
    public int? AnalystCount { get; init; }
    public double? CurrentPrice { get; init; }
    public double? TargetLow { get; init; }
    public double? TargetMean { get; init; }
    public double? TargetMedian { get; init; }
    public double? TargetHigh { get; init; }
    /// <summary>Rating counts by month; first entry is the current month.</summary>
    public List<RatingTrend> Trend { get; init; } = new();
    /// <summary>Latest rating per named firm, newest first.</summary>
    public List<FirmRating> Firms { get; init; } = new();
}

public sealed record RatingTrend(string Period, int StrongBuy, int Buy, int Hold, int Sell, int StrongSell)
{
    public int Total => StrongBuy + Buy + Hold + Sell + StrongSell;
}

/// <summary>One firm's most recent rating action. Bucket is Buy / Hold / Sell.</summary>
public sealed record FirmRating(
    string Firm, string Grade, string? FromGrade, string Bucket, string Action,
    DateTime Date, double? PriceTarget, double? PriorTarget);

public sealed class CompRow
{
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string Currency { get; init; } = "";
    public double? Price { get; init; }
    public double? ChangePct { get; init; }
    public Dictionary<string, double?> Metrics { get; init; } = new();
    public string? Error { get; init; }
}

public enum StatementKind { Income, Balance, CashFlow }

public sealed class FinancialStatement
{
    public StatementKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Frequency { get; init; } = "annual";
    public string Currency { get; init; } = "";
    /// <summary>Column headers, oldest → newest (a trailing "TTM" column comes last).</summary>
    public List<string> Periods { get; init; } = new();
    public List<StatementLine> Lines { get; init; } = new();
    public string Source { get; set; } = "";
    /// <summary>How the figures compare with a second source, once checked (null = not yet).</summary>
    public DataCheck? Check { get; set; }
}

/// <summary>
/// Annual revenue split into the streams a company reports: products and services, or business
/// segments. Periods are fiscal year ends (yyyy-MM-dd), oldest to newest; each segment's values
/// line up with them, and the segments add up to total revenue.
/// </summary>
public sealed class RevenueSegments
{
    public List<string> Periods { get; init; } = new();
    public List<RevenueSegment> Segments { get; init; } = new();
    /// <summary>What the split is by, for a sentence: "products and services" | "business segments".</summary>
    public string Basis { get; init; } = "";
    /// <summary>The report it came from, e.g. "FY2024 10-K".</summary>
    public string Form { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed record RevenueSegment(string Name, List<double?> Values);

/// <summary>Suggested peers and how they were chosen.</summary>
public sealed record PeerSet(List<string> Symbols, string Basis, string Source);

public sealed record Filing(string Form, DateTime Filed, string? Period, string Description, string Url);

public sealed record NewsItem(string Headline, string Source, DateTime Published, string Url, string? Summary);

/// <summary>
/// A statement row. Format: amt (scaled into thousands/millions on export) | ps (per-share,
/// never scaled) | pct (a ratio) | header (a section label with no values).
/// </summary>
public sealed record StatementLine(string Key, string Label, string Format, bool Emphasis, int Indent, List<double?> Values);

/// <summary>How a metric is displayed. Format: money | x | pct | price | num.</summary>
public sealed record MetricDef(string Key, string Label, string Format, string Group);

/// <summary>The canonical metric list, shared by the UI and the Excel export.</summary>
public static class Metrics
{
    public static readonly IReadOnlyList<MetricDef> All = new List<MetricDef>
    {
        new("marketCap",        "Market cap",        "money", "Valuation"),
        new("enterpriseValue",  "Enterprise value",  "money", "Valuation"),
        new("trailingPE",       "P/E (TTM)",         "x",     "Valuation"),
        new("forwardPE",        "P/E (forward)",     "x",     "Valuation"),
        new("pegRatio",         "PEG",               "x",     "Valuation"),
        new("priceToBook",      "P/B",               "x",     "Valuation"),
        new("priceToSales",     "P/S (TTM)",         "x",     "Valuation"),
        new("evToRevenue",      "EV / Revenue",      "x",     "Valuation"),
        new("evToEbitda",       "EV / EBITDA",       "x",     "Valuation"),
        new("fcfYield",         "FCF yield",         "pct",   "Valuation"),
        new("dividendYield",    "Dividend yield",    "pct",   "Valuation"),

        new("grossMargin",      "Gross margin",      "pct",   "Profitability"),
        new("ebitdaMargin",     "EBITDA margin",     "pct",   "Profitability"),
        new("operatingMargin",  "Operating margin",  "pct",   "Profitability"),
        new("profitMargin",     "Net margin",        "pct",   "Profitability"),
        new("returnOnEquity",   "ROE",               "pct",   "Profitability"),
        new("returnOnAssets",   "ROA",               "pct",   "Profitability"),

        new("totalRevenue",     "Revenue (TTM)",     "money", "Growth & balance sheet"),
        new("revenueGrowth",    "Revenue growth (YoY)",  "pct", "Growth & balance sheet"),
        new("ebitda",           "EBITDA (TTM)",      "money", "Growth & balance sheet"),
        new("earningsGrowth",   "Earnings growth (YoY)", "pct", "Growth & balance sheet"),
        new("freeCashflow",     "Free cash flow",    "money", "Growth & balance sheet"),
        new("totalCash",        "Cash",              "money", "Growth & balance sheet"),
        new("totalDebt",        "Debt",              "money", "Growth & balance sheet"),
        new("debtToEquity",     "Debt / Equity",     "x",     "Growth & balance sheet"),

        new("fiftyTwoWeekLow",  "52-week low",       "price", "Trading"),
        new("fiftyTwoWeekHigh", "52-week high",      "price", "Trading"),
        new("beta",             "Beta",              "num",   "Trading"),
        new("averageVolume",    "Avg. volume (3M)",  "money", "Trading"),
        new("sharesOutstanding","Shares outstanding","money", "Trading"),
        new("impliedSharesOutstanding","Implied shares outstanding","money", "Trading"),
        new("trailingEps",      "EPS (TTM)",         "price", "Trading"),
        new("forwardEps",       "EPS (forward)",     "price", "Trading"),
    };

    /// <summary>The columns of the quick-comps table, in order.</summary>
    public static readonly IReadOnlyList<string> CompKeys = new[]
    {
        "marketCap", "enterpriseValue", "evToRevenue", "evToEbitda", "trailingPE", "forwardPE",
        "priceToBook", "revenueGrowth", "ebitdaMargin", "profitMargin",
    };

    public static MetricDef? Find(string key) => All.FirstOrDefault(m => m.Key == key);
}

// ---------- earnings & insiders ----------

/// <summary>One reported quarter: EPS against the consensus going in.</summary>
public sealed record EarningsQuarter(string Period, double? EpsActual, double? EpsEstimate, double? SurprisePct);

/// <summary>Consensus for one period. Period: "Current quarter" | "Next quarter" | "Current year" | "Next year".</summary>
public sealed record EstimateRow(
    string Period, string? EndDate, double? EpsAvg, double? EpsLow, double? EpsHigh, double? EpsGrowth,
    double? RevenueAvg, double? RevenueGrowth, int? Analysts);

/// <summary>
/// Consensus for one fiscal year. Period: "0y" (the year in progress) | "+1y". Revenue in the
/// source's units; EPS per share; growth rates as fractions.
/// </summary>
public sealed record ConsensusYear(
    string Period, string? EndDate, double? RevenueAvg, double? RevenueGrowth, double? EpsAvg, double? EpsGrowth, int? Analysts);

/// <summary>Annual analyst consensus (this fiscal year and next), for the DCF's defaults and calibration.</summary>
public sealed class ConsensusEstimates
{
    public List<ConsensusYear> Years { get; init; } = new();
    public string Source { get; init; } = "";
}

public sealed class EarningsView
{
    public DateTime? NextDate { get; init; }
    /// <summary>True when the company hasn't confirmed the date and it's a projected window.</summary>
    public bool NextDateEstimated { get; init; }
    public double? NextEpsEstimate { get; init; }
    public double? NextRevenueEstimate { get; init; }
    /// <summary>Oldest → newest.</summary>
    public List<EarningsQuarter> History { get; init; } = new();
    public List<EstimateRow> Estimates { get; init; } = new();
    public string Source { get; init; } = "";
}

/// <summary>
/// One insider transaction. Kind: Buy | Sell (open market) | Award | Exercise | Tax | Gift | Other.
/// Shares are always positive; Kind says which way they went.
/// </summary>
public sealed record InsiderTrade(
    DateTime Date, string Name, string Role, string Kind, double Shares, double? Price, double? Value,
    double? OwnedAfter, bool Planned, string? Url);

public sealed class InsiderView
{
    /// <summary>Newest first.</summary>
    public List<InsiderTrade> Trades { get; init; } = new();
    public string Source { get; init; } = "";
    /// <summary>Over what span the trades were collected, for the UI's caption.</summary>
    public string Window { get; init; } = "";
}
