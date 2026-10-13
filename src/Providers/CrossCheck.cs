using System.Globalization;

namespace Funnel.Providers;

/// <summary>
/// One figure that disagrees between two sources. Severity: "severe" (shown in red: probably
/// wrong, don't rely on it) | "warn" (worth a look; sources often classify things a little differently).
/// </summary>
public sealed record DataFlag(string Key, string Label, string Period, double Value, double Other, string Severity, string Message);

/// <summary>The result of checking a statement (or a company's share count) against a second source.</summary>
public sealed class DataCheck
{
    /// <summary>The source compared against, e.g. "SEC EDGAR".</summary>
    public string Against { get; init; } = "";
    /// <summary>ok | flagged | unavailable</summary>
    public string Status { get; init; } = "ok";
    /// <summary>Why no check could be made, when Status is unavailable.</summary>
    public string? Note { get; init; }
    /// <summary>How many values both sources reported, and were compared.</summary>
    public int Compared { get; init; }
    public List<DataFlag> Flags { get; init; } = new();

    public static DataCheck Unavailable(string note, string against = "") => new() { Status = "unavailable", Note = note, Against = against };
}

/// <summary>
/// Compares figures from two sources. Sources legitimately differ in how they classify things
/// (what counts as debt, cash, operating income), so the tolerance depends on the line:
/// share counts and per-share figures come straight from the filings and should match almost
/// exactly; headline totals should be close; everything else is flagged only when it's wildly
/// off (2x or more, the signature of a unit, split or tagging error) and big enough to matter.
/// </summary>
public static class CrossCheck
{
    private sealed record Tolerance(double Warn, double Severe, double Materiality, bool SignMatters);

    // Relative gap = |a − b| / max(|a|, |b|): 0 = equal, 0.5 = one is twice the other, ≥ 1 = opposite signs.
    private static readonly Tolerance Shares = new(0.03, 0.15, 0, true);
    private static readonly Tolerance Core = new(0.10, 0.35, 0.005, true);
    // Other lines differ by definition between sources (net debt, investments, working-capital
    // moves), so 2x+ is only a note; red is kept for gaps of 200x+, which are unit or tagging errors.
    private static readonly Tolerance Other = new(0.5, 0.995, 0.02, false);

    /// <summary>Totals every source should agree on, give or take presentation.</summary>
    private static readonly HashSet<string> CoreKeys = new()
    {
        "TotalRevenue", "PretaxIncome", "NetIncome", "TotalAssets", "CurrentAssets", "CurrentLiabilities",
        "StockholdersEquity", "CashAndCashEquivalents", "OperatingCashFlow",
    };

    public static bool IsShareCount(string key) =>
        key is "BasicAverageShares" or "DilutedAverageShares" or "OrdinarySharesNumber" or "ShareIssued" or "TreasurySharesNumber" or "sharesOutstanding";

    private static Tolerance For(StatementLine line) =>
        IsShareCount(line.Key) || line.Format == "ps" ? Shares : CoreKeys.Contains(line.Key) ? Core : Other;

    private static double Gap(double a, double b) => a == b ? 0 : Math.Abs(a - b) / Math.Max(Math.Abs(a), Math.Abs(b));

    /// <summary>Flags where <paramref name="mine"/> (what's on screen) disagrees with <paramref name="other"/>.</summary>
    public static DataCheck Compare(FinancialStatement mine, FinancialStatement other)
    {
        // Fiscal periods line up when their end dates are within a few days (52/53-week years).
        var match = mine.Periods.Select(p =>
        {
            if (!DateTime.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return -1;
            return other.Periods.FindIndex(q => DateTime.TryParse(q, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) && Math.Abs((e - d).TotalDays) <= 10);
        }).ToArray();

        // Materiality is judged against the size of the business in that period.
        // Revenue, assets or operating cash flow; failing those, the biggest money figure in the column.
        double? Scale(int i)
        {
            var money = mine.Lines.Where(l => l.Format == "amt" && !IsShareCount(l.Key) && l.Values.ElementAtOrDefault(i) != null).ToList();
            var anchors = money.Where(l => l.Key is "TotalRevenue" or "TotalAssets" or "OperatingCashFlow").ToList();
            var pool = (anchors.Count > 0 ? anchors : money).Select(l => Math.Abs(l.Values[i]!.Value)).ToList();
            return pool.Count > 0 ? pool.Max() : null;
        }

        var flags = new List<DataFlag>();
        int compared = 0;
        foreach (var line in mine.Lines)
        {
            if (line.Format is "header" or "pct") continue;
            var theirs = other.Lines.FirstOrDefault(l => l.Key == line.Key);
            if (theirs == null) continue;
            var tol = For(line);
            for (int i = 0; i < line.Values.Count && i < match.Length; i++)
            {
                if (match[i] < 0 || line.Values[i] is not { } a || theirs.Values.ElementAtOrDefault(match[i]) is not { } b) continue;
                compared++;
                if (a == b) continue;
                if (!tol.SignMatters && Math.Sign(a) != Math.Sign(b)) continue;   // sign conventions differ between sources
                if (tol.Materiality > 0 && Scale(i) is { } s && s > 0 && Math.Max(Math.Abs(a), Math.Abs(b)) < tol.Materiality * s) continue;
                var gap = Gap(a, b);
                var severity = gap > tol.Severe ? "severe" : gap > tol.Warn ? "warn" : null;
                if (severity == null) continue;
                flags.Add(new DataFlag(line.Key, line.Label, mine.Periods[i], a, b, severity,
                    Describe(line.Label, PeriodLabel(mine.Periods[i], mine.Frequency), a, b, line.Format, mine.Source, other.Source)));
            }
        }
        return new DataCheck { Against = other.Source, Status = flags.Count > 0 ? "flagged" : "ok", Compared = compared, Flags = flags };
    }

    /// <summary>One figure against another source's, with share-count tolerances.</summary>
    public static DataFlag? CompareShares(string key, string label, string period, double mine, double other, string mineSource, string otherSource)
    {
        var gap = Gap(mine, other);
        var severity = gap > Shares.Severe ? "severe" : gap > Shares.Warn ? "warn" : null;
        return severity == null ? null
            : new DataFlag(key, label, period, mine, other, severity, Describe(label, period, mine, other, "amt", mineSource, otherSource));
    }

    private static string PeriodLabel(string p, string frequency) =>
        p == "TTM" || !DateTime.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? p
            : frequency == "quarterly" ? d.ToString("MMM yyyy", CultureInfo.InvariantCulture) : $"FY{d.Year}";

    public static string Describe(string label, string period, double mine, double other, string format, string mineSource, string otherSource)
    {
        double hi = Math.Max(Math.Abs(mine), Math.Abs(other)), lo = Math.Min(Math.Abs(mine), Math.Abs(other));
        var how = mine * other < 0 ? "opposite signs"
            : lo == 0 ? "one of them is zero"
            : hi / lo >= 1.95 ? $"{hi / lo:#,0.#}x apart"
            : $"{Gap(mine, other):P0} apart";
        return $"{label}, {period}: {mineSource} says {Num(mine, format)}, {otherSource} says {Num(other, format)} ({how}).";
    }

    private static string Num(double v, string format)
    {
        if (format == "ps") return v.ToString("0.00", CultureInfo.InvariantCulture);
        var a = Math.Abs(v);
        var s = a >= 1e9 ? (v / 1e9).ToString("#,0.00", CultureInfo.InvariantCulture) + "B"
              : a >= 1e6 ? (v / 1e6).ToString("#,0.0", CultureInfo.InvariantCulture) + "M"
              : v.ToString("#,0", CultureInfo.InvariantCulture);
        return s;
    }
}
