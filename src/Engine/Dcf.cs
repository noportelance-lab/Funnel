using System.Globalization;
using Funnel.Providers;

namespace Funnel.Engine;

/// <summary>
/// Everything the DCF can change. Per-year drivers always hold <see cref="MaxYears"/> entries;
/// only the first <see cref="Years"/> are used. Rates are fractions (0.025 = 2.5%), money is in
/// the reporting currency's units (not millions).
/// </summary>
public sealed class DcfAssumptions
{
    public const int MaxYears = 10;

    public int Years { get; set; } = 5;
    public bool MidYear { get; set; } = true;

    public double[] Growth { get; set; } = new double[MaxYears];
    public double[] EbitMargin { get; set; } = new double[MaxYears];
    public double[] TaxRate { get; set; } = new double[MaxYears];
    public double[] DaPct { get; set; } = new double[MaxYears];
    public double[] CapexPct { get; set; } = new double[MaxYears];
    /// <summary>Change in net working capital as a share of the change in revenue.</summary>
    public double[] NwcPct { get; set; } = new double[MaxYears];
    /// <summary>
    /// Revenue built stream by stream (the products / segments a company reports). Empty = one
    /// revenue line driven by <see cref="Growth"/>; otherwise <see cref="Growth"/> is ignored.
    /// </summary>
    public List<RevenueStream> Streams { get; set; } = new();

    public double Rf { get; set; }
    public double Erp { get; set; } = 0.05;
    public double Beta { get; set; } = 1;
    public double SizePremium { get; set; }
    public double CostOfDebt { get; set; }
    /// <summary>Target debt / (debt + equity).</summary>
    public double DebtWeight { get; set; }

    /// <summary>gordon | multiple</summary>
    public string TvMethod { get; set; } = "gordon";
    public double Tgr { get; set; } = 0.025;
    public double ExitMultiple { get; set; } = 12;

    public double Cash { get; set; }
    public double Debt { get; set; }
    public double Minority { get; set; }
    public double Shares { get; set; }

    /// <summary>
    /// The other valuation models' inputs and which model is showing ({ model, nav, lbo, er }),
    /// kept and saved with the DCF's but never read here: the Model tab owns their shape.
    /// </summary>
    public System.Text.Json.JsonElement? Alt { get; set; }

    /// <summary>Pad or trim every per-year array to <see cref="MaxYears"/> so a stale save can't break the maths.</summary>
    public DcfAssumptions Normalised()
    {
        double[] Fix(double[]? a)
        {
            var r = new double[MaxYears];
            if (a == null || a.Length == 0) return r;
            for (int i = 0; i < MaxYears; i++) r[i] = i < a.Length ? a[i] : a[^1];
            return r;
        }
        Years = Math.Clamp(Years, 1, MaxYears);
        Growth = Fix(Growth); EbitMargin = Fix(EbitMargin); TaxRate = Fix(TaxRate);
        DaPct = Fix(DaPct); CapexPct = Fix(CapexPct); NwcPct = Fix(NwcPct);
        Streams = (Streams ?? new()).Where(x => x != null).ToList();
        foreach (var st in Streams) { st.Growth = Fix(st.Growth); st.Name = (st.Name ?? "").Trim(); st.Key ??= ""; }
        TvMethod = TvMethod == "multiple" ? "multiple" : "gordon";
        return this;
    }

    /// <summary>A deep copy (arrays and streams included).</summary>
    public DcfAssumptions Clone()
    {
        var c = (DcfAssumptions)MemberwiseClone();
        c.Growth = Growth.ToArray(); c.EbitMargin = EbitMargin.ToArray(); c.TaxRate = TaxRate.ToArray();
        c.DaPct = DaPct.ToArray(); c.CapexPct = CapexPct.ToArray(); c.NwcPct = NwcPct.ToArray();
        c.Streams = Streams.Select(s => new RevenueStream { Name = s.Name, Key = s.Key, Share = s.Share, Growth = s.Growth.ToArray() }).ToList();
        return c;
    }

    /// <summary>
    /// Each stream's slice of the base-year revenue. Shares are weights — scaled to add to 100% —
    /// so the streams always start from the reported total. All zero → nothing (the workbook's IFERROR).
    /// </summary>
    public double[] StreamBases(double baseRevenue)
    {
        var total = Streams.Sum(x => x.Share);
        return Streams.Select(x => total != 0 ? baseRevenue * x.Share / total : 0).ToArray();
    }
}

/// <summary>One revenue stream: its share of the base-year revenue and its own growth path.</summary>
public sealed class RevenueStream
{
    public string Name { get; set; } = "";
    /// <summary>The segment name as reported, linking the stream to its history; empty for one the user added.</summary>
    public string Key { get; set; } = "";
    /// <summary>Share of the last reported year's revenue (a weight; see <see cref="DcfAssumptions.StreamBases"/>).</summary>
    public double Share { get; set; }
    public double[] Growth { get; set; } = new double[DcfAssumptions.MaxYears];
}

/// <summary>
/// The general assumptions every new model starts from (Settings → DCF assumptions). A model the
/// user has edited keeps its own numbers; these only seed defaults.
/// </summary>
public sealed class DcfSettings
{
    /// <summary>
    /// Long-run nominal growth of the economy. It is the terminal growth rate, and the growth rate
    /// every revenue stream fades to by the final forecast year.
    /// </summary>
    public double EconomyGrowth { get; set; } = 0.025;
    public double Erp { get; set; } = 0.05;
    /// <summary>True = today's 10-year Treasury yield; false = <see cref="RiskFree"/>.</summary>
    public bool LiveRiskFree { get; set; } = true;
    public double RiskFree { get; set; } = 0.0425;
    public double SizePremium { get; set; }
    /// <summary>True = each company's 3-year average effective rate; false = <see cref="TaxRate"/>.</summary>
    public bool HistoricalTax { get; set; } = true;
    public double TaxRate { get; set; } = 0.21;
    public int Years { get; set; } = 5;
    public bool MidYear { get; set; } = true;
    /// <summary>gordon | multiple</summary>
    public string TvMethod { get; set; } = "gordon";
    /// <summary>Forecast each revenue stream on its own when the filings break revenue down.</summary>
    public bool SplitRevenue { get; set; } = true;
    /// <summary>
    /// off | light | strong: how far new models adapt to the company's industry norms and lean
    /// toward the analysts' price target (see <see cref="Calibrator"/>).
    /// </summary>
    public string Calibration { get; set; } = "light";

    /// <summary>Keep a hand-edited config from producing a model that can't be solved.</summary>
    public DcfSettings Normalised()
    {
        static double Fin(double v, double fallback, double lo, double hi) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
        EconomyGrowth = Fin(EconomyGrowth, 0.025, -0.05, 0.10);
        Erp = Fin(Erp, 0.05, 0, 0.20);
        RiskFree = Fin(RiskFree, 0.0425, -0.02, 0.25);
        SizePremium = Fin(SizePremium, 0, -0.05, 0.20);
        TaxRate = Fin(TaxRate, 0.21, 0, 0.60);
        Years = Years >= 10 ? 10 : 5;
        TvMethod = TvMethod == "multiple" ? "multiple" : "gordon";
        Calibration = Calibration is "off" or "strong" ? Calibration : "light";
        return this;
    }
}

/// <summary>The history the model starts from and where each default came from.</summary>
public sealed class DcfBase
{
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string Currency { get; init; } = "";
    public double? Price { get; init; }
    /// <summary>Fiscal year-end dates (yyyy-MM-dd), oldest → newest, matching the statement columns.</summary>
    public List<string> Periods { get; init; } = new();
    public List<double?> Revenue { get; init; } = new();
    public List<double?> Ebit { get; init; } = new();
    public List<double?> Da { get; init; } = new();
    /// <summary>As reported: negative.</summary>
    public List<double?> Capex { get; init; } = new();
    public List<double?> TaxRate { get; init; } = new();
    /// <summary>Net working capital excluding cash and debt.</summary>
    public List<double?> Nwc { get; init; } = new();
    public double? CurrentEvToEbitda { get; init; }
    /// <summary>Shares outstanding today, before dilution.</summary>
    public double BasicShares { get; init; }
    /// <summary>
    /// Shares the options, RSUs and convertibles add, as the company counts them (diluted minus basic
    /// weighted shares, latest period). The default share count is <see cref="BasicShares"/> plus this.
    /// </summary>
    public double DilutiveShares { get; init; }
    /// <summary>The largest implied shares outstanding (market cap ÷ price) any source gave, and whose it was.</summary>
    public double? ImpliedShares { get; init; }
    public string ImpliedSharesSource { get; init; } = "";
    /// <summary>Set when the implied count and shares + dilutive securities disagree by more than 3%.</summary>
    public DataFlag? SharesFlag { get; init; }
    public string StatementsSource { get; init; } = "";
    /// <summary>Reported revenue by stream, aligned to <see cref="Periods"/> (empty when the filings don't split it).</summary>
    public List<StreamHistory> Streams { get; init; } = new();
    /// <summary>Plain-English provenance of each default, keyed like the assumption fields.</summary>
    public Dictionary<string, string> Notes { get; init; } = new();
    /// <summary>How the defaults were adapted to the industry and the analysts; null when that's off.</summary>
    public CalibrationReport? Calibration { get; init; }
    /// <summary>Set when a free-cash-flow DCF is a poor guide for this kind of company (banks, oil and gas…).</summary>
    public DcfFitWarning? Fit { get; init; }
    /// <summary>Which valuation model suits the industry, how well each fits, and the other models' starting inputs.</summary>
    public ValuationKit? Kit { get; set; }
}

/// <summary>A reported revenue stream's history. Key matches <see cref="RevenueStream.Key"/>.</summary>
public sealed record StreamHistory(string Key, List<double?> Values);

public sealed record DcfResult(
    double[] Revenue, double[] Ebit, double[] Nopat, double[] Da, double[] Capex, double[] DNwc, double[] Ufcf,
    double[] Period, double[] Discount, double[] Pv, double CostOfEquity, double CostOfDebtAfterTax, double Wacc,
    double SumPv, double TerminalValue, double PvTerminal, double EnterpriseValue, double EquityValue, double SharePrice,
    double ImpliedMultiple, double ImpliedGrowth, double[][] StreamRevenue);

/// <summary>
/// The valuation maths, kept identical to the JavaScript engine in app.js and to the formulas the
/// Excel model writes. The self-test checks all three agree.
/// </summary>
public static class DcfEngine
{
    public static double Wacc(DcfAssumptions a, out double ke, out double kdAfter)
    {
        ke = a.Rf + a.Beta * a.Erp + a.SizePremium;
        kdAfter = a.CostOfDebt * (1 - a.TaxRate[0]);
        return (1 - a.DebtWeight) * ke + a.DebtWeight * kdAfter;
    }

    public static DcfResult Compute(DcfAssumptions a, double baseRevenue)
    {
        int n = a.Years;
        var rev = new double[n]; var ebit = new double[n]; var nopat = new double[n]; var da = new double[n];
        var capex = new double[n]; var dnwc = new double[n]; var ufcf = new double[n];
        var period = new double[n]; var df = new double[n]; var pv = new double[n];
        var wacc = Wacc(a, out var ke, out var kdAfter);
        double prev = baseRevenue, sum = 0;
        // Streams: each grows from its slice of the base year; revenue is their sum.
        var sv = a.StreamBases(baseRevenue);
        var srev = a.Streams.Select(_ => new double[n]).ToArray();
        for (int t = 0; t < n; t++)
        {
            if (sv.Length == 0) rev[t] = prev * (1 + a.Growth[t]);
            else
            {
                for (int i = 0; i < sv.Length; i++) srev[i][t] = sv[i] *= 1 + a.Streams[i].Growth[t];
                rev[t] = sv.Sum();
            }
            ebit[t] = rev[t] * a.EbitMargin[t];
            nopat[t] = ebit[t] * (1 - a.TaxRate[t]);
            da[t] = rev[t] * a.DaPct[t];
            capex[t] = -rev[t] * a.CapexPct[t];
            dnwc[t] = -(rev[t] - prev) * a.NwcPct[t];
            ufcf[t] = nopat[t] + da[t] + capex[t] + dnwc[t];
            period[t] = t + 1 - (a.MidYear ? 0.5 : 0);
            df[t] = 1 / Math.Pow(1 + wacc, period[t]);
            pv[t] = ufcf[t] * df[t];
            sum += pv[t];
            prev = rev[t];
        }
        var ebitdaN = ebit[n - 1] + da[n - 1];
        var tvGordon = ufcf[n - 1] * (1 + a.Tgr) / (wacc - a.Tgr);
        var tvMultiple = ebitdaN * a.ExitMultiple;
        var tv = a.TvMethod == "multiple" ? tvMultiple : tvGordon;
        var pvTv = tv / Math.Pow(1 + wacc, n);
        var ev = sum + pvTv;
        var equity = ev - a.Debt + a.Cash - a.Minority;
        var price = a.Shares > 0 ? equity / a.Shares : double.NaN;
        var impliedMultiple = ebitdaN != 0 ? tvGordon / ebitdaN : double.NaN;
        var impliedG = (tvMultiple * wacc - ufcf[n - 1]) / (tvMultiple + ufcf[n - 1]);
        return new DcfResult(rev, ebit, nopat, da, capex, dnwc, ufcf, period, df, pv, ke, kdAfter, wacc,
            sum, tv, pvTv, ev, equity, price, impliedMultiple, impliedG, srev);
    }
}

/// <summary>Builds the starting assumptions from the pulled statements and market data.</summary>
public static class DcfDefaults
{
    /// <param name="segments">Reported revenue streams, or null; <paramref name="segmentsNote"/> says why there are none.</param>
    /// <param name="quarterlyIncome">The latest quarter's diluted and basic shares give the dilution; null = the annual statement's.</param>
    public static (DcfBase Base, DcfAssumptions Assumptions) Build(
        CompanySnapshot co, FinancialStatement income, FinancialStatement? balance, FinancialStatement? cashflow,
        (double Value, string Source)? riskFree, (double Value, string Source)? growthEstimate,
        DcfSettings? settings = null, RevenueSegments? segments = null, string? segmentsNote = null,
        FinancialStatement? quarterlyIncome = null, IReadOnlyList<(string Source, double Shares)>? impliedShares = null,
        ConsensusEstimates? estimates = null)
    {
        var set = (settings ?? new DcfSettings()).Normalised();
        // Fiscal years only (the TTM column isn't a fiscal year), newest four.
        var periods = income.Periods.Where(p => p != "TTM").TakeLast(4).ToList();
        List<double?> Row(FinancialStatement? st, params string[] keys)
        {
            var line = keys.Select(k => st?.Lines.FirstOrDefault(l => l.Key == k && l.Values.Any(v => v != null))).FirstOrDefault(l => l != null);
            return periods.Select(p =>
            {
                var i = st?.Periods.IndexOf(p) ?? -1;
                return line != null && i >= 0 && i < line.Values.Count ? line.Values[i] : null;
            }).ToList();
        }

        var revenue = Row(income, "TotalRevenue");
        var ebit = Row(income, "OperatingIncome", "EBIT");
        var da = Row(cashflow, "DepreciationAndAmortization");
        if (da.All(v => v == null)) da = Row(income, "ReconciledDepreciation");
        var capex = Row(cashflow, "CapitalExpenditure");
        var tax = Row(income, "TaxProvision"); var pretax = Row(income, "PretaxIncome");
        var taxRate = periods.Select((_, i) => tax[i] is { } t && pretax[i] is > 0 ? t / pretax[i] : (double?)null).ToList();
        var ca = Row(balance, "CurrentAssets"); var cash = Row(balance, "CashCashEquivalentsAndShortTermInvestments", "CashAndCashEquivalents");
        var cl = Row(balance, "CurrentLiabilities"); var cd = Row(balance, "CurrentDebt");
        var nwc = periods.Select((_, i) => StatementExtras.Nwc(ca[i], cash[i], cl[i], cd[i])).ToList();

        var notes = new Dictionary<string, string>();
        static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));
        double? Avg(IEnumerable<double?> xs) { var v = xs.Where(x => x != null && double.IsFinite(x.Value)).Select(x => x!.Value).ToList(); return v.Count > 0 ? v.Average() : null; }
        IEnumerable<double?> Ratio(List<double?> num, List<double?> den, double sign = 1) =>
            num.Select((x, i) => x != null && den[i] is { } d && d != 0 ? sign * x / d : null).TakeLast(3);

        var a = new DcfAssumptions { Years = set.Years, MidYear = set.MidYear, TvMethod = set.TvMethod };
        // The economy's long-run growth is both the terminal rate and where every revenue line
        // ends up by the final forecast year.
        var econ = set.EconomyGrowth;
        a.Tgr = econ;
        int horizon = set.Years;
        var fade = $"fading in a straight line to {econ:P1} economy growth by year {horizon} (Settings → DCF assumptions)";
        void Fade(double[] g, double first)
        {
            for (int t = 0; t < DcfAssumptions.MaxYears; t++)
                g[t] = t >= horizon - 1 ? econ : first + (econ - first) * t / (horizon - 1.0);
        }

        // The consensus rows that line up with forecast years 1 and 2; its year-1 growth wins.
        var (cy1, cy2) = AlignConsensus(estimates, periods.LastOrDefault());
        if (cy1?.RevenueGrowth is { } cg) growthEstimate = (cg, estimates!.Source);

        // Growth: consensus for the year in progress, else the 3-year CAGR.
        var revs = revenue.Where(r => r is > 0).Select(r => r!.Value).ToList();
        double g1;
        double? consensus = null;
        if (growthEstimate is { } ge && double.IsFinite(ge.Value)) { g1 = Clamp(ge.Value, -0.2, 0.5); consensus = g1; notes["growth"] = $"Year 1: analyst consensus revenue growth ({ge.Source}), {fade}."; }
        else if (revs.Count >= 2) { g1 = Clamp(Math.Pow(revs[^1] / revs[0], 1.0 / (revs.Count - 1)) - 1, -0.1, 0.3); notes["growth"] = $"Year 1: {revs.Count - 1}-year historical revenue CAGR, {fade}."; }
        else { g1 = 0.05; notes["growth"] = $"Year 1: 5% placeholder (not enough history), {fade}."; }
        Fade(a.Growth, g1);

        var streamHist = set.SplitRevenue ? BuildStreams(a, segments, periods, consensus, g1, Fade, fade, notes) : new();
        if (streamHist.Count == 0)
            notes["streams"] = !set.SplitRevenue ? "Revenue streams are switched off in Settings → DCF assumptions."
                : segmentsNote ?? "The filings don't break revenue into streams, so it's forecast as one line.";

        var margin = Avg(Ratio(ebit, revenue)) ?? 0.15;
        Array.Fill(a.EbitMargin, margin);
        notes["ebitMargin"] = "3-year average operating margin.";
        if (set.HistoricalTax)
        {
            Array.Fill(a.TaxRate, Clamp(Avg(taxRate.TakeLast(3)) ?? 0.21, 0.10, 0.30));
            notes["taxRate"] = "3-year average effective tax rate, kept between 10% and 30%.";
        }
        else
        {
            Array.Fill(a.TaxRate, set.TaxRate);
            notes["taxRate"] = $"{set.TaxRate:P1} for every company (Settings → DCF assumptions).";
        }
        Array.Fill(a.DaPct, Avg(Ratio(da, revenue)) ?? 0.03);
        notes["daPct"] = "3-year average D&A as a share of revenue.";
        Array.Fill(a.CapexPct, Avg(Ratio(capex, revenue, -1)) ?? 0.04);
        notes["capexPct"] = "3-year average capex as a share of revenue.";
        var nwcRatios = nwc.Select((x, i) => i > 0 && x != null && nwc[i - 1] != null && revenue[i] != null && revenue[i - 1] != null
                && revenue[i] != revenue[i - 1] ? (x - nwc[i - 1]) / (revenue[i] - revenue[i - 1]) : null).Where(x => x != null).Select(x => x!.Value).ToList();
        var nwcPct = nwcRatios.Count > 0 ? Clamp(nwcRatios.OrderBy(x => x).ElementAt(nwcRatios.Count / 2), -0.2, 0.3) : 0.05;
        Array.Fill(a.NwcPct, nwcPct);
        notes["nwcPct"] = nwcRatios.Count > 0 ? "Median historical change in working capital per dollar of revenue change." : "5% placeholder.";

        // Cost of capital.
        if (!set.LiveRiskFree) { a.Rf = set.RiskFree; notes["rf"] = $"{set.RiskFree:P2}, fixed in Settings → DCF assumptions."; }
        else if (riskFree != null) { a.Rf = riskFree.Value.Value; notes["rf"] = $"10-year Treasury yield ({riskFree.Value.Source})."; }
        else { a.Rf = set.RiskFree; notes["rf"] = $"{set.RiskFree:P2} from Settings (the rates feed was unavailable)."; }
        a.Erp = set.Erp; notes["erp"] = $"{set.Erp:P1} equity risk premium (Settings → DCF assumptions).";
        a.SizePremium = set.SizePremium;
        co.Metrics.TryGetValue("beta", out var beta);
        a.Beta = beta is { } b && b > 0 ? Clamp(b, 0.4, 2.5) : 1.0;
        notes["beta"] = beta != null ? $"Beta from {co.Source}." : "1.0 placeholder (no beta reported).";

        double? Latest(FinancialStatement? st, params string[] keys)
        {
            foreach (var k in keys)
                if (st?.Lines.FirstOrDefault(l => l.Key == k) is { } line && line.Values.LastOrDefault(v => v != null) is { } v) return v;
            return null;
        }
        co.Metrics.TryGetValue("totalDebt", out var mDebt);
        co.Metrics.TryGetValue("totalCash", out var mCash);
        co.Metrics.TryGetValue("marketCap", out var mcap);
        co.Metrics.TryGetValue("sharesOutstanding", out var shares);
        a.Debt = Latest(balance, "TotalDebt") ?? mDebt ?? 0;
        a.Cash = Latest(balance, "CashCashEquivalentsAndShortTermInvestments", "CashAndCashEquivalents") ?? mCash ?? 0;
        a.Minority = Math.Max(0, Latest(balance, "MinorityInterest") ?? 0);
        notes["bridge"] = $"Cash, debt and minority interest from the latest balance sheet ({income.Source}).";

        // Diluted shares: today's count plus the options, RSUs and convertibles the company itself
        // counts as dilutive (treasury-stock and if-converted methods), i.e. diluted minus basic
        // weighted shares in the latest period that reports both.
        double basicShares = 0, dilutive = 0;
        var dil = Dilution(quarterlyIncome) ?? Dilution(income);
        if (shares is > 0)
        {
            basicShares = shares.Value;
            dilutive = dil?.Shares ?? 0;
            notes["shares"] = dil is { } d
                ? $"{basicShares / 1e6:#,0.0}M shares outstanding ({co.Source}) + {dilutive / 1e6:#,0.0}M from options, RSUs and convertibles " +
                  $"(diluted − basic weighted shares, {d.Period}, {d.Source})." +
                  (dilutive == 0 ? " None counted: a loss-making company's options are anti-dilutive, so it reports diluted = basic." : "")
                : $"{basicShares / 1e6:#,0.0}M shares outstanding ({co.Source}). No diluted share count reported, so nothing added for options, RSUs or convertibles.";
        }
        else if (Latest(income, "DilutedAverageShares") is { } wd && wd > 0)
        {
            basicShares = wd;
            notes["shares"] = $"Latest diluted weighted shares ({income.Source}); no current share count reported.";
        }
        a.Shares = basicShares + dilutive;

        // Implied shares outstanding (market cap ÷ price) counts every share class; the reported
        // count can cover only one. Take the larger of the two, and flag any real gap.
        var (impSource, imp) = (impliedShares ?? Array.Empty<(string, double)>()).Where(x => x.Shares > 0)
            .OrderByDescending(x => x.Shares).FirstOrDefault();
        DataFlag? sharesFlag = null;
        if (imp > 0)
        {
            double diluted = a.Shares;
            static string M(double v) => $"{v / 1e6:#,0.0}M";
            var all = string.Join(", ", impliedShares!.Where(x => x.Shares > 0).Select(x => $"{x.Source} {M(x.Shares)}"));
            var gap = diluted > 0 ? Math.Abs(imp - diluted) / Math.Max(imp, diluted) : 1;
            if (imp > diluted)
            {
                a.Shares = imp;
                notes["shares"] = $"{M(imp)} implied shares outstanding (market cap ÷ price, {impSource}), the larger of that and " +
                    (diluted > 0 ? $"{M(diluted)} shares outstanding + dilutive securities. " : "the reported share count (none given). ") +
                    (notes.TryGetValue("shares", out var was) ? $"That count: {was}" : "");
            }
            else notes["shares"] = (notes.TryGetValue("shares", out var was) ? was + " " : "") +
                $"Larger than implied shares outstanding ({all}), so it's the one used.";
            if (gap > 0.03 && diluted > 0)
            {
                var ratio = Math.Max(imp, diluted) / Math.Min(imp, diluted);
                var msg = imp > diluted
                    ? $"Implied shares outstanding ({all}; market cap ÷ price) is {(ratio >= 1.5 ? $"{ratio:0.0}x" : $"{gap:P0} above")} shares outstanding + dilutive securities ({M(diluted)}). " +
                      $"Funnel uses the larger, {M(imp)}. " +
                      (gap > 0.15 ? "A gap this size usually means more than one share class (e.g. multiple and subordinate voting shares) and the reported count covers only one. " : "") +
                      "Check the share count in the latest filing."
                    : $"Shares outstanding + dilutive securities ({M(diluted)}) is above implied shares outstanding ({all}; market cap ÷ price). " +
                      $"Funnel uses the larger, {M(diluted)}. The market cap may be stale or count one share class only. Check the share count in the latest filing.";
                sharesFlag = new DataFlag("impliedShares", "Diluted shares", "latest", a.Shares, imp > diluted ? diluted : imp,
                    gap > 0.15 ? "severe" : "warn", msg);
            }
        }

        var interest = Math.Abs(Latest(income, "InterestExpenseNonOperating") ?? 0);
        a.CostOfDebt = a.Debt > 0 && interest > 0 ? Clamp(interest / a.Debt, 0.03, 0.12) : a.Rf + 0.015;
        notes["costOfDebt"] = a.Debt > 0 && interest > 0 ? "Last year's interest expense ÷ total debt, kept between 3% and 12%." : "Risk-free rate + 1.5% (no interest expense reported).";
        a.DebtWeight = mcap is > 0 ? Clamp(a.Debt / (a.Debt + mcap.Value), 0, 0.8) : 0.1;
        notes["debtWeight"] = "Current market weights: debt ÷ (debt + market cap).";

        co.Metrics.TryGetValue("evToEbitda", out var evEbitda);
        a.ExitMultiple = evEbitda is > 0 ? Math.Round(Clamp(evEbitda.Value, 4, 30), 1) : 12;
        notes["exitMultiple"] = evEbitda is > 0 ? "Current EV / EBITDA (switch to the peer median on the Comps tab)." : "12.0x placeholder.";
        notes["tgr"] = $"{econ:P1} long-run economy growth (Settings → DCF assumptions): no company outgrows the economy forever.";

        // Lightly adapt the history-based defaults to the industry and the analysts' view.
        CalibrationReport? calibration = null;
        if (set.Calibration != "off")
        {
            var margins = Ratio(ebit, revenue).Where(v => v is { } m && double.IsFinite(m)).Select(v => v!.Value).ToList();
            var inputs = new CalibrationInputs(co, income.Currency, revenue.LastOrDefault(v => v != null) ?? 0, margins,
                beta is > 0 ? beta : null, evEbitda is > 0 ? evEbitda : null, cy1, cy2, estimates?.Source ?? "",
                estimates != null && cy1?.RevenueGrowth != null,
                Latest(income, "DilutedAverageShares") is > 0 and var eps ? eps : a.Shares > 0 ? a.Shares : null, interest);
            calibration = Calibrator.Run(a, inputs, set.Calibration, notes);
        }

        return (new DcfBase
        {
            Symbol = co.Symbol,
            Name = co.Name,
            Currency = income.Currency.Length > 0 ? income.Currency : co.Currency,
            Price = co.Price,
            Periods = periods,
            Revenue = revenue,
            Ebit = ebit,
            Da = da,
            Capex = capex,
            TaxRate = taxRate,
            Nwc = nwc,
            CurrentEvToEbitda = evEbitda,
            BasicShares = basicShares,
            DilutiveShares = dilutive,
            ImpliedShares = imp > 0 ? imp : null,
            ImpliedSharesSource = impSource ?? "",
            SharesFlag = sharesFlag,
            StatementsSource = income.Source,
            Streams = streamHist,
            Notes = notes,
            Calibration = calibration,
            Fit = DcfFit.Check(co.Sector, co.Industry, revenue, ebit),
        }, a);
    }

    /// <summary>
    /// The consensus rows for forecast years 1 and 2. A row ending on or before the last reported
    /// fiscal year (a source that hasn't rolled forward yet) is skipped.
    /// </summary>
    private static (ConsensusYear? Y1, ConsensusYear? Y2) AlignConsensus(ConsensusEstimates? c, string? lastPeriod)
    {
        if (c == null || c.Years.Count == 0) return (null, null);
        var rows = c.Years.OrderBy(y => y.Period == "0y" ? 0 : 1).ToList();
        if (DateTime.TryParse(lastPeriod, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
            rows = rows.Where(y => !DateTime.TryParse(y.EndDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end > last.AddDays(45)).ToList();
        return (rows.ElementAtOrDefault(0), rows.ElementAtOrDefault(1));
    }

    /// <summary>
    /// Diluted minus basic weighted shares in the newest period that reports both. A gap above half
    /// the basic count is a data error (units, share classes), not dilution, so it's ignored.
    /// </summary>
    private static (double Shares, string Period, string Source)? Dilution(FinancialStatement? st)
    {
        var basic = st?.Lines.FirstOrDefault(l => l.Key == "BasicAverageShares");
        var diluted = st?.Lines.FirstOrDefault(l => l.Key == "DilutedAverageShares");
        if (st == null || basic == null || diluted == null) return null;
        for (int i = Math.Min(Math.Min(basic.Values.Count, diluted.Values.Count), st.Periods.Count) - 1; i >= 0; i--)
        {
            if (basic.Values[i] is not { } b || b <= 0 || diluted.Values[i] is not { } d || d <= 0) continue;
            if (d > b * 1.5) return null;
            var p = st.Periods[i];
            var label = p == "TTM" ? "trailing twelve months"
                : st.Frequency == "quarterly" ? $"quarter to {p}" : $"fiscal year to {p}";
            return (Math.Max(0, d - b), label, st.Source);
        }
        return null;
    }

    /// <summary>
    /// One stream per reported segment: its share of the latest year, and year-1 growth from its
    /// own history. When there's a consensus for the whole company, every stream shifts by the same
    /// amount so the total still matches it (the mix stays the filings', the level the analysts').
    /// Each stream then fades to the economy's growth rate like the single line does.
    /// </summary>
    private static List<StreamHistory> BuildStreams(DcfAssumptions a, RevenueSegments? seg, List<string> periods,
        double? consensus, double g1, Action<double[], double> fade, string fadeText, Dictionary<string, string> notes)
    {
        var hist = new List<StreamHistory>();
        if (seg == null || seg.Segments.Count < 2 || seg.Periods.Count == 0) return hist;
        var last = seg.Periods.Count - 1;
        var latest = seg.Segments.Select(s => s.Values[last] ?? 0).ToArray();
        var total = latest.Sum();
        if (total <= 0) return hist;

        var own = seg.Segments.Select(s =>
        {
            // CAGR over the years the stream was positive; a single year gives no rate.
            var pts = s.Values.Select((v, i) => (v, i)).Where(x => x.v is > 0).ToList();
            if (pts.Count < 2) return (double?)null;
            var span = pts[^1].i - pts[0].i;
            return Math.Clamp(Math.Pow(pts[^1].v!.Value / pts[0].v!.Value, 1.0 / span) - 1, -0.2, 0.4);
        }).ToArray();
        var weights = latest.Select(v => v / total).ToArray();
        var mix = own.Select((g, i) => weights[i] * (g ?? g1)).Sum();
        var shift = consensus is { } c ? c - mix : 0;

        for (int i = 0; i < seg.Segments.Count; i++)
        {
            var s = seg.Segments[i];
            var st = new RevenueStream { Name = s.Name, Key = s.Name, Share = Math.Round(weights[i], 4) };
            fade(st.Growth, Math.Clamp((own[i] ?? g1) + shift, -0.3, 0.6));
            a.Streams.Add(st);
            // Line the history up with the statement columns (fiscal year ends can differ by a few days).
            hist.Add(new StreamHistory(s.Name, periods.Select(p =>
            {
                var d = DateTime.Parse(p, CultureInfo.InvariantCulture);
                var j = seg.Periods.FindIndex(x => Math.Abs((DateTime.Parse(x, CultureInfo.InvariantCulture) - d).TotalDays) <= 10);
                return j >= 0 ? s.Values[j] : null;
            }).ToList()));
        }
        notes["streams"] = $"Revenue by {seg.Basis}, from the {seg.Form} ({seg.Source}). Year-1 growth is each stream's own " +
            (consensus != null ? "historical rate, shifted so the total matches the analyst consensus, " : "historical rate, ") + fadeText + ".";
        return hist;
    }
}
