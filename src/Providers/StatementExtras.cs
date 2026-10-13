namespace Funnel.Providers;

/// <summary>
/// Lines Funnel adds to statements itself, whichever source they came from, so every source
/// shows them the same way (and the DCF uses the same definition).
/// </summary>
public static class StatementExtras
{
    /// <summary>
    /// Net (operating) working capital: current assets less cash and short-term investments, less
    /// current liabilities other than short-term debt. Cash and debt sit in the equity bridge, so the
    /// DCF leaves them out of working capital too.
    /// </summary>
    public static double? Nwc(double? currentAssets, double? cash, double? currentLiabilities, double? currentDebt) =>
        currentAssets is { } a && currentLiabilities is { } l ? a - (cash ?? 0) - (l - (currentDebt ?? 0)) : null;

    /// <summary>
    /// Adds net working capital and its change from the previous column to a balance sheet's
    /// Supplemental section (right after "Working capital" where there is one). An increase is
    /// positive, the balance-sheet view; the cash flow statement shows the same move as a use of cash.
    /// </summary>
    public static FinancialStatement WithNwcChange(FinancialStatement st)
    {
        if (st.Kind != StatementKind.Balance || st.Lines.Any(l => l.Key == "ChangeInNetWorkingCapital")) return st;
        List<double?>? Row(params string[] keys) => keys
            .Select(k => st.Lines.FirstOrDefault(l => l.Key == k && l.Values.Any(v => v != null))?.Values)
            .FirstOrDefault(v => v != null);
        var ca = Row("CurrentAssets");
        var cl = Row("CurrentLiabilities");
        if (ca == null || cl == null) return st;
        var cash = Row("CashCashEquivalentsAndShortTermInvestments", "CashAndCashEquivalents");
        var cd = Row("CurrentDebt");
        static double? At(List<double?>? r, int i) => r != null && i < r.Count ? r[i] : null;

        var nwc = st.Periods.Select((p, i) => p == "TTM" ? null : Nwc(At(ca, i), At(cash, i), At(cl, i), At(cd, i))).ToList();
        var change = nwc.Select((v, i) => i > 0 && v != null && nwc[i - 1] != null ? v - nwc[i - 1] : null).ToList();
        if (nwc.All(v => v == null)) return st;

        var lines = new List<StatementLine>(st.Lines);
        var added = new List<StatementLine> { new("NetWorkingCapital", "Net working capital (ex. cash & debt)", "amt", false, 0, nwc) };
        if (change.Any(v => v != null))
            added.Add(new("ChangeInNetWorkingCapital", "Increase / (decrease) in net working capital", "amt", false, 1, change));
        var at = lines.FindIndex(l => l.Key == "WorkingCapital");
        if (at >= 0) lines.InsertRange(at + 1, added);
        else
        {
            if (!lines.Any(l => l.Format == "header" && l.Label == "Supplemental"))
                lines.Add(new StatementLine("#Supplemental", "Supplemental", "header", true, 0, new()));
            lines.AddRange(added);
        }
        return new FinancialStatement
        {
            Kind = st.Kind, Title = st.Title, Frequency = st.Frequency, Currency = st.Currency,
            Periods = st.Periods, Lines = lines, Source = st.Source,
        };
    }
}
