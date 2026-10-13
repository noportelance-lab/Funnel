using System.Globalization;
using Funnel.Providers;

namespace Funnel.Engine;

/// <summary>One default the calibration changed. Stage: industry (norms) | estimates (EPS / revenue consensus) | target (the pull toward the price target).</summary>
public sealed class CalibrationStep
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Stage { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Why { get; init; } = "";
}

/// <summary>What the calibration did to a new model's defaults, and why.</summary>
public sealed class CalibrationReport
{
    /// <summary>light | strong</summary>
    public string Mode { get; set; } = "light";
    public string Industry { get; set; } = "";
    /// <summary>industry | sector | market: how the profile was found.</summary>
    public string MatchLevel { get; set; } = "";
    public string MatchedOn { get; set; } = "";
    /// <summary>How much the industry norms count (1 for an exact industry, less for a sector or the market).</summary>
    public double IndustryWeight { get; set; }
    public IndustryProfile Profile { get; set; } = IndustryProfiles.Market;

    public double? Price { get; set; }
    public double? Target { get; set; }
    public double? TargetLow { get; set; }
    public double? TargetHigh { get; set; }
    /// <summary>mean target | median target | rating (no target given, so one implied by the rating).</summary>
    public string TargetBasis { get; set; } = "";
    public int? Analysts { get; set; }
    public string? Recommendation { get; set; }
    public double? RecommendationMean { get; set; }

    /// <summary>Implied share price from the history alone, after the industry step, and after the pull toward the analysts.</summary>
    public double? ValueHistory { get; set; }
    public double? ValueIndustry { get; set; }
    public double? ValueCalibrated { get; set; }
    /// <summary>The share of the gap to the target the model was asked to close (0–0.8), from the analysts' number and agreement.</summary>
    public double Pull { get; set; }
    /// <summary>How far along the levers' ranges the solver went (−1 … 1).</summary>
    public double Tilt { get; set; }
    /// <summary>The share of the history-only value's distance to the target that's gone (negative = further).</summary>
    public double? GapClosed { get; set; }
    /// <summary>True when every lever hit the end of its range before reaching the aim.</summary>
    public bool LeversExhausted { get; set; }
    /// <summary>How far (0–1) industry adjustments that worked against the analysts were eased back, and which.</summary>
    public double Eased { get; set; }
    public List<string> EasedParts { get; set; } = new();

    public string Summary { get; set; } = "";
    public List<string> Caveats { get; set; } = new();
    public List<CalibrationStep> Steps { get; set; } = new();
    /// <summary>The defaults from the company's own history, before any of this.</summary>
    public DcfAssumptions Uncalibrated { get; set; } = new();
}

/// <summary>What the calibration needs beyond the assumptions.</summary>
public sealed record CalibrationInputs(
    CompanySnapshot Company, string StatementCurrency, double BaseRevenue,
    IReadOnlyList<double> Margins, double? RawBeta, double? EvEbitda,
    ConsensusYear? Y1, ConsensusYear? Y2, string ConsensusSource, bool GrowthFromConsensus,
    double? EpsShares, double Interest);

/// <summary>
/// Lightly adapts a new model's history-based defaults, in two stages.
/// <para>1. Industry realities. A few years of history are a noisy guide to the long run, so the
/// long-run margin, reinvestment, working capital, beta and exit multiple are shrunk toward the
/// industry's norms: more for a cyclical industry, a volatile or short history or a loss-maker;
/// less when the company already sits inside the industry's range. Year-1 (and 2) margins move
/// toward what the analysts' EPS consensus implies, and year 2 takes the revenue consensus too.
/// Beta is Blume-adjusted and blended with the industry's unlevered beta relevered at the
/// company's own debt. The final year's capex at least nears D&amp;A: a company growing with the
/// economy can't shrink its asset base forever.</para>
/// <para>2. The analysts' view. The aim is part of the way from the model's value to the mean
/// price target: more with many analysts who agree with each other, less with few, a wide range
/// of targets, or a rating that contradicts the target. A solver then slides a handful of levers
/// (long-run margin, the growth after the consensus years, beta, long-run capex, the exit
/// multiple) along realistic ranges, which are wider where the industry supports the move, until
/// the model reaches that aim or the ranges run out. Terminal growth and the cost-of-capital
/// settings are never touched.</para>
/// </summary>
public static class Calibrator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int Max = DcfAssumptions.MaxYears;

    private sealed record Lever(string Key, string Label, double Weight, Action<DcfAssumptions, double> Apply);

    /// <summary>Implied share price, or NaN where the model can't be solved (WACC at or under terminal growth).</summary>
    public static double Value(DcfAssumptions s, double baseRevenue)
    {
        var w = DcfEngine.Wacc(s, out _, out _);
        if (s.TvMethod != "multiple" && w <= s.Tgr + 1e-4) return double.NaN;
        var r = DcfEngine.Compute(s, baseRevenue);
        return double.IsFinite(r.SharePrice) ? r.SharePrice : double.NaN;
    }

    public static CalibrationReport Run(DcfAssumptions a, CalibrationInputs x, string mode, Dictionary<string, string> notes)
    {
        bool strong = mode == "strong";
        var co = x.Company;
        var (prof, level, matchedOn, conf) = IndustryProfiles.Match(co.Sector, co.Industry);
        var rep = new CalibrationReport
        {
            Mode = strong ? "strong" : "light", Profile = prof, Industry = prof.Name, MatchLevel = level,
            MatchedOn = matchedOn, IndustryWeight = conf, Uncalibrated = a.Clone(),
        };
        int n = a.Years;
        double econ = a.Tgr, tax = a.TaxRate[0];
        double V(DcfAssumptions s) => Value(s, x.BaseRevenue);
        rep.ValueHistory = Fin(V(a));
        var norms = level == "market" ? "broad-market norms" : $"{prof.Name} norms";
        var cur = x.StatementCurrency;
        bool sameCurrency = string.IsNullOrEmpty(cur) || string.IsNullOrEmpty(co.Currency) || cur == co.Currency;
        void Step(string key, string label, string stage, string from, string to, string why)
        {
            if (from != to) rep.Steps.Add(new CalibrationStep { Key = key, Label = label, Stage = stage, From = from, To = to, Why = why });
        }
        void Note(string key, string text) => notes[key] = (notes.TryGetValue(key, out var was) ? was + " " : "") + text;

        // ================================================================ 1. industry realities
        int mAnchored = 0;           // forecast years whose margin leans on the EPS consensus
        double mHist = a.EbitMargin[0], mLR = mHist, mFwd = mHist;
        if (prof.Financial)
            rep.Caveats.Add($"An unlevered DCF is a weak fit for {prof.Name.ToLowerInvariant()}: debt is raw material for them, so operating margin, capex and working capital aren't adapted. Cross-check with P/B or a dividend model.");
        else
        {
            // Years 1 and 2: the margin the EPS consensus implies (net income ÷ (1 − tax) + interest
            // = EBIT), taken partway since consensus EPS is often adjusted (before stock pay, amortisation).
            var cons = new List<(double M, double Implied, ConsensusYear Y)>();
            double wC = strong ? 0.7 : 0.5, revPath = x.BaseRevenue;
            var years = new[] { x.Y1, x.Y2 };
            for (int k = 0; k < 2; k++)
            {
                revPath *= 1 + a.Growth[k];
                var y = years[k];
                if (!sameCurrency || y?.EpsAvg is not { } eps || x.EpsShares is not { } sh || sh <= 0 || tax >= 1) break;
                // The consensus revenue goes with the consensus EPS, unless it's on another scale (units, currency).
                var rev = y.RevenueAvg is { } ra && ra > 0 && ra / revPath is > 0.5 and < 2 ? ra : revPath;
                var m = (eps * sh / (1 - tax) + x.Interest) / rev;
                if (!double.IsFinite(m) || m < -0.5 || Math.Abs(m - mHist) > (strong ? 0.20 : 0.15)) break;
                cons.Add((mHist + wC * (m - mHist), m, y));
            }
            // Long-run margin: shrink toward the industry, from the forward margin (the consensus
            // years' if there are any, else the history). The anchor sits halfway between that margin
            // (kept inside the industry's typical range) and the median. When the analysts' EPS says an
            // above-industry margin holds or widens, that premium is evidence of a moat: shrink less.
            bool hasHist = x.Margins.Count > 0;
            mFwd = cons.Count > 0 ? cons[^1].M : mHist;
            bool moat = cons.Count > 0 && mHist > prof.Margin && cons[^1].Implied >= mHist;
            double sd = x.Margins.Count >= 2 ? StdDev(x.Margins) : 0.05;
            double s = (strong ? 0.35 : 0.20) + 0.15 * prof.Cyclicality + Math.Min(0.15, sd) + (x.Margins.Count < 3 ? 0.10 : 0) + (mHist < 0 ? 0.15 : 0);
            s = hasHist ? Math.Min(0.6, s * conf * (moat ? 0.6 : 1)) : conf;
            var anchor = 0.5 * Math.Clamp(mFwd, prof.MarginLo, prof.MarginHi) + 0.5 * prof.Margin;
            mLR = mFwd + s * (anchor - mFwd);
            // A loss-maker: a DCF assumes the business survives, and one that never earns money isn't
            // a going concern. By the final year it earns a sustainable margin, midway between the
            // industry's bottom quartile and its median (weighted by how well the industry matched).
            bool loss = hasHist && mFwd < 0;
            double lossTarget = Math.Max(0.02, (prof.MarginLo + prof.Margin) / 2);
            if (loss) mLR = conf * lossTarget + (1 - conf) * Math.Max(mFwd, -0.05);

            mAnchored = cons.Count;
            double start = mAnchored > 0 ? cons[^1].M : mHist;
            for (int t = 0; t < Max; t++)
                a.EbitMargin[t] = t < mAnchored ? cons[t].M
                    : t >= n - 1 ? mLR
                    : mAnchored > 0 ? start + (mLR - start) * (t - mAnchored + 1) / (n - mAnchored)
                    : mHist + (mLR - mHist) * t / (n - 1.0);

            var consText = mAnchored == 0 ? "" :
                $"Year 1{(mAnchored > 1 ? " and 2" : "")}: {string.Join(" and ", cons.Select(c => Pct(c.M)))}, {(strong ? "70%" : "halfway")} from the history toward the " +
                $"{string.Join(" / ", cons.Select(c => Pct(c.Implied)))} the {x.ConsensusSource} EPS consensus implies (EPS × {x.EpsShares!.Value / 1e6:#,0.0}M diluted shares ÷ (1 − tax) + interest). ";
            var lrText = loss
                ? $"Converges to {Pct(mLR)} by year {n}: the company loses money today, and a DCF assumes it survives, so by the final year it earns a sustainable margin for {norms} (midway between the bottom quartile, {Pct(prof.MarginLo)}, and the median, {Pct(prof.Margin)})."
                : hasHist
                ? $"Converges to {Pct(mLR)} by year {n}: {s:P0} of the way from {(cons.Count > 0 ? "that forward" : "the company's")} {Pct(mFwd)} toward {norms} ({Pct(prof.Margin)} median, {Pct(prof.MarginLo)}–{Pct(prof.MarginHi)} typical)." +
                  (moat ? " Shrunk less than usual: the consensus has this above-industry margin holding, a sign of a durable edge." : "")
                : $"No margin history, so it converges to {Pct(mLR)} from {norms} by year {n}.";
            Note("ebitMargin", "Calibrated: " + consText + lrText);
            Step("ebitMargin", "EBIT margin", "industry", $"{Pct(mHist)} flat",
                $"{Pct(a.EbitMargin[0])} → {Pct(a.EbitMargin[n - 1])} (Y{n})",
                (mAnchored > 0 ? "Year 1 toward the EPS consensus; " : "") +
                (loss ? $"a loss-maker reaches a sustainable margin ({prof.Name}) by year {n}" : $"long run shrunk {s:P0} toward {norms}" + (moat ? ", less than usual as the consensus sees the premium holding" : "")) +
                (prof.Cyclicality >= 0.6 ? " (cyclical industry: a few years can mislead)" : "") + ".");

            // Reinvestment: D&A, capex and working capital drift toward the industry; the final year's
            // capex closes on D&A so growth is funded.
            // A loss-maker's reinvestment is a scale-up's; it matures toward the industry's faster.
            double sr = (loss ? (strong ? 0.7 : 0.6) : strong ? 0.3 : 0.2) * conf;
            double da0 = a.DaPct[0], cx0 = a.CapexPct[0], nw0 = a.NwcPct[0];
            double daLR = da0 + sr * (prof.DaPct - da0), cxLR = cx0 + sr * (prof.CapexPct - cx0);
            bool squeezed = false;
            if (econ > 0 && daLR > cxLR)
            {
                var gap = (daLR - cxLR) * (strong ? 1 : 0.5) / 2;
                daLR -= gap; cxLR += gap; squeezed = true;
            }
            Path(a.DaPct, da0, daLR, n); Path(a.CapexPct, cx0, cxLR, n);
            var nwLR = nw0 + sr * (prof.NwcPct - nw0);
            Array.Fill(a.NwcPct, nwLR);
            var sq = squeezed ? $" Capex and D&A also close {(strong ? "the" : "half the")} gap between them by year {n}: growing with the economy forever needs capex at least near depreciation." : "";
            Note("daPct", $"Calibrated: drifts to {Pct(daLR, 2)} by year {n}, {sr:P0} toward {norms} ({Pct(prof.DaPct)}).{sq}");
            Note("capexPct", $"Calibrated: drifts to {Pct(cxLR, 2)} by year {n}, {sr:P0} toward {norms} ({Pct(prof.CapexPct)}).{sq}");
            Note("nwcPct", $"Calibrated: {sr:P0} toward {norms} ({Pct(prof.NwcPct)} of each dollar of revenue growth).");
            if (Math.Abs(daLR - da0) >= 0.0005) Step("daPct", "D&A % of revenue", "industry", Pct(da0), $"{Pct(da0)} → {Pct(daLR)} (Y{n})", $"{sr:P0} toward {norms} ({Pct(prof.DaPct)})" + (squeezed ? "; closing on capex." : "."));
            if (Math.Abs(cxLR - cx0) >= 0.0005) Step("capexPct", "Capex % of revenue", "industry", Pct(cx0), $"{Pct(cx0)} → {Pct(cxLR)} (Y{n})", $"{sr:P0} toward {norms} ({Pct(prof.CapexPct)})" + (squeezed ? "; at least near D&A by the end." : "."));
            if (Math.Abs(nwLR - nw0) >= 0.0005) Step("nwcPct", "NWC % of revenue change", "industry", Pct(nw0), Pct(nwLR), $"{sr:P0} toward {norms} ({Pct(prof.NwcPct)}).");
        }

        // Year-2 growth from the revenue consensus, when year 1 already came from it.
        int gAnchored = x.GrowthFromConsensus ? 1 : 0;
        if (x.GrowthFromConsensus && x.Y2?.RevenueGrowth is { } g2raw && n >= 3)
        {
            var g2 = Math.Clamp(g2raw, -0.2, 0.5);
            double old1 = a.Growth[1], aim = old1 + (strong ? 1.0 : 0.75) * (g2 - old1);
            var d = new double[Max];
            for (int t = 1; t < Max; t++)
                d[t] = (t >= n - 1 ? econ : aim + (econ - aim) * (t - 1) / (n - 2.0)) - a.Growth[t];
            ShiftGrowth(a, d);
            gAnchored = 2;
            Note("growth", $"Calibrated: year 2 moves {(strong ? "onto" : "75% of the way to")} the {Pct(g2)} {x.ConsensusSource} consensus for next year, then fades to the economy rate." +
                (a.Streams.Count > 0 ? " Every revenue stream shifts by the same amount." : ""));
            Step("growth", "Revenue growth, year 2", "estimates", Pct(old1), Pct(old1 + d[1]), $"Toward the {Pct(g2)} next-year revenue consensus ({x.ConsensusSource}).");
        }

        // Beta: Blume-adjusted (betas drift toward 1), blended with the industry's unlevered beta
        // relevered at the company's own debt / equity.
        double de = a.DebtWeight < 0.95 ? a.DebtWeight / (1 - a.DebtWeight) : 19;
        double betaInd = Math.Clamp(prof.Financial ? prof.BetaU : prof.BetaU * (1 + (1 - tax) * de), 0.4, 2.5);
        double b0 = a.Beta, b1;
        if (x.RawBeta is null)
        {
            b1 = 1 + conf * (betaInd - 1);
            Note("beta", $"Calibrated: {norms} unlevered beta {prof.BetaU:0.00}, relevered at this company's debt to {betaInd:0.00}, weighted {conf:P0} against 1.0.");
        }
        else
        {
            double blume = 0.67 * b0 + 0.33, wb = (strong ? 0.5 : 0.35) * conf;
            b1 = blume + wb * (betaInd - blume);
            Note("beta", $"Calibrated: Blume-adjusted (0.67 × {b0:0.00} + 0.33 = {blume:0.00}), then {wb:P0} toward {norms}: unlevered {prof.BetaU:0.00}, relevered at this company's debt to {betaInd:0.00}.");
        }
        a.Beta = Math.Round(Math.Clamp(b1, 0.4, 2.5), 2);
        Step("beta", "Beta", "industry", b0.ToString("0.00", Inv), a.Beta.ToString("0.00", Inv),
            x.RawBeta is null ? $"No beta reported: {norms} relevered ({betaInd:0.00})." : $"Blume-adjusted, blended toward {norms} relevered ({betaInd:0.00}).");

        // Exit multiple: today's EV / EBITDA, partway to the industry's.
        double ex0 = a.ExitMultiple, we = x.EvEbitda is > 0 ? (strong ? 0.4 : 0.3) * conf : conf;
        a.ExitMultiple = Math.Round(ex0 + we * (prof.EvEbitda - ex0), 1);
        Note("exitMultiple", $"Calibrated: {we:P0} toward the {prof.EvEbitda:0.0}x {norms} (today's multiple can carry a boom or a slump into the terminal year).");
        Step("exitMultiple", "Exit EV / EBITDA", "industry", Mult(ex0), Mult(a.ExitMultiple), $"{we:P0} toward {norms} ({Mult(prof.EvEbitda)}).");

        rep.ValueIndustry = Fin(V(a));

        // ================================================================ 2. toward the analysts
        var an = co.Analysts;
        rep.Price = co.Price; rep.Analysts = an.AnalystCount;
        rep.Recommendation = an.RecommendationKey; rep.RecommendationMean = an.RecommendationMean;
        rep.TargetLow = an.TargetLow; rep.TargetHigh = an.TargetHigh;
        double? target = an.TargetMean ?? an.TargetMedian;
        rep.TargetBasis = an.TargetMean != null ? "mean target" : an.TargetMedian != null ? "median target" : "";
        bool fromRating = false;
        if (target is not > 0 && an.RecommendationMean is { } rm0 && co.Price is > 0)
        {
            // No target: a rating of 1 (strong buy) reads as +15%, 5 (strong sell) as −15%.
            target = co.Price * (1 + 0.15 * (3 - rm0) / 2);
            fromRating = true; rep.TargetBasis = "rating";
        }
        rep.Target = target;

        if (!sameCurrency)
            rep.Caveats.Add($"Analyst targets are in {co.Currency} but the statements are in {cur}, so the model isn't pulled toward them.");
        else if (target is not > 0)
            rep.Caveats.Add("No analyst price target or rating, so only the industry step applies.");
        else if (rep.ValueIndustry is not { } v1)
            rep.Caveats.Add("The model has no finite value (WACC at or below terminal growth), so it isn't pulled toward the analysts.");
        else
        {
            int na = an.AnalystCount ?? 0;
            double fN = na > 0 ? 1 - Math.Exp(-na / 5.0) : 0.3;
            double fD = an.TargetHigh is { } hi && an.TargetLow is { } lo && hi >= lo && !fromRating
                ? 1 / (1 + Math.Pow((hi - lo) / target.Value / 0.7, 2)) : 0.7;
            double fR = 1;
            if (!fromRating && an.RecommendationMean is { } rm && co.Price is > 0)
            {
                double up = target.Value / co.Price.Value - 1, bull = (3 - rm) / 2;
                if (Math.Abs(up) > 0.05 && Math.Abs(bull) > 0.2 && Math.Sign(up) != Math.Sign(bull)) fR = 0.6;
            }
            double kappa = (strong ? 0.8 : 0.5) * fN * fD * fR * (fromRating ? 0.5 : 1);
            rep.Pull = kappa;
            double vStar = v1 + kappa * (target.Value - v1);
            // The point is to end closer to the analysts. If the industry step pushed the value the
            // other way, aim for what the same pull would have made of the history-only value instead.
            bool floored = false;
            if (rep.ValueHistory is { } v0)
            {
                var floorAim = v0 + kappa * (target.Value - v0);
                if (Math.Abs(floorAim - target.Value) < Math.Abs(vStar - target.Value)) { vStar = floorAim; floored = true; }
            }
            int dir = Math.Sign(vStar - v1);

            if (dir != 0 && Math.Abs(vStar - v1) > 0.002 * Math.Abs(v1))
            {
                var a1 = a.Clone();
                var a0 = rep.Uncalibrated;
                double k15 = strong ? 1.5 : 1;
                var levers = new List<Lever>();

                // Relax: ease the industry adjustments that pushed the value away from the analysts
                // back toward the company's own figures (the consensus years' margins and growth stay).
                var eased = new List<string>();
                if (floored)
                {
                    var keepM = Enumerable.Range(0, Max).Select(t => t < mAnchored ? a1.EbitMargin[t] : mFwd).ToArray();
                    var parts = new List<(string Name, Action<DcfAssumptions, double> Blend)>
                    {
                        ("long-run margin", (s, l) => { for (int t = 0; t < Max; t++) s.EbitMargin[t] += l * (keepM[t] - a1.EbitMargin[t]); }),
                        ("D&A and capex", (s, l) => { for (int t = 0; t < Max; t++) { s.DaPct[t] += l * (a0.DaPct[t] - a1.DaPct[t]); s.CapexPct[t] += l * (a0.CapexPct[t] - a1.CapexPct[t]); } }),
                        ("working capital", (s, l) => { for (int t = 0; t < Max; t++) s.NwcPct[t] += l * (a0.NwcPct[t] - a1.NwcPct[t]); }),
                        ("beta", (s, l) => s.Beta += l * (a0.Beta - a1.Beta)),
                        ("exit multiple", (s, l) => s.ExitMultiple += l * (a0.ExitMultiple - a1.ExitMultiple)),
                    };
                    var against = parts.Where(p =>
                    {
                        var s = a1.Clone(); p.Blend(s, 1);
                        var v = V(s);
                        return double.IsFinite(v) && (v - v1) * dir > 1e-9 * Math.Max(1, Math.Abs(v1));
                    }).ToList();
                    eased.AddRange(against.Select(p => p.Name));
                    if (against.Count > 0)
                        levers.Add(new("relax", "Ease industry adjustments", 1, (s, u) =>
                        {
                            var l = Math.Clamp(u * dir, 0, 1);
                            foreach (var p in against) p.Blend(s, l);
                        }));
                }

                double mUp = Math.Clamp(prof.MarginHi - mLR, 0.01, 0.05) * k15, mDn = Math.Clamp(mLR - prof.MarginLo, 0.01, 0.05) * k15;
                bool mToward = dir > 0 ? mLR < prof.Margin : mLR > prof.Margin;
                levers.Add(new("ebitMargin", "Long-run EBIT margin", mToward ? 1 : 0.6, (s, u) =>
                {
                    var d = u > 0 ? u * mUp : u * mDn;
                    for (int t = 0; t < Max; t++) s.EbitMargin[t] += d * Ramp(t, mAnchored, n);
                }));
                if (n - 1 - gAnchored > 0)
                {
                    double band = strong ? 0.03 : 0.02;
                    levers.Add(new("growth", "Growth after the consensus years", gAnchored > 0 ? 0.8 : 1, (s, u) =>
                    {
                        var d = new double[Max];
                        for (int t = gAnchored; t < n - 1; t++) d[t] = u * band * (n - 1 - t) / (double)(n - 1 - gAnchored);
                        ShiftGrowth(s, d);
                    }));
                }
                double bb = strong ? 0.25 : 0.15;
                bool bToward = dir > 0 ? a1.Beta > betaInd : a1.Beta < betaInd;
                levers.Add(new("beta", "Beta", bToward ? 1 : 0.5, (s, u) => s.Beta = Math.Clamp(s.Beta - u * bb, 0.4, 2.5)));
                if (!prof.Financial)
                {
                    double cxN = a1.CapexPct[n - 1], cb = (strong ? 0.25 : 0.15) * cxN;
                    bool cToward = dir > 0 ? cxN > prof.CapexPct : cxN < prof.CapexPct;
                    levers.Add(new("capexPct", "Long-run capex", cToward ? 1 : 0.5, (s, u) =>
                    {
                        for (int t = 0; t < Max; t++) s.CapexPct[t] = Math.Max(0, s.CapexPct[t] - u * cb * (t >= n - 1 ? 1 : t / (n - 1.0)));
                    }));
                }
                if (a.TvMethod == "multiple")
                {
                    double eb = strong ? 0.25 : 0.15;
                    bool eToward = dir > 0 ? a1.ExitMultiple < prof.EvEbitda : a1.ExitMultiple > prof.EvEbitda;
                    levers.Add(new("exitMultiple", "Exit EV / EBITDA", eToward ? 1 : 0.5, (s, u) => s.ExitMultiple = Math.Max(1, s.ExitMultiple * (1 + u * eb))));
                }

                // Keep only levers that move the value the way it needs to go (more growth can destroy
                // value at a thin margin; a lower beta can when cash flows are negative).
                levers = levers.Where(l =>
                {
                    var s = a1.Clone(); l.Apply(s, dir);
                    var v = V(s);
                    return double.IsFinite(v) && (v - v1) * dir > 1e-9 * Math.Max(1, Math.Abs(v1));
                }).ToList();

                DcfAssumptions At(double theta)
                {
                    var s = a1.Clone();
                    foreach (var l in levers) l.Apply(s, theta * l.Weight);
                    return s;
                }
                double theta = dir;
                var vEnd = V(At(dir));
                if (levers.Count == 0) theta = 0;
                else if (!double.IsFinite(vEnd) || (vEnd - vStar) * dir <= 0) rep.LeversExhausted = true;
                else
                {
                    double lo2 = 0, hi2 = dir;
                    for (int i = 0; i < 50; i++)
                    {
                        var mid = (lo2 + hi2) / 2;
                        var vm = V(At(mid));
                        if (double.IsFinite(vm) && (vm - vStar) * dir < 0) lo2 = mid; else hi2 = mid;
                    }
                    theta = (lo2 + hi2) / 2;
                }
                rep.Tilt = theta;

                var f = At(theta);
                f.Beta = Math.Round(f.Beta, 2);
                f.ExitMultiple = Math.Round(f.ExitMultiple, 1);
                a.EbitMargin = f.EbitMargin; a.Growth = f.Growth; a.Streams = f.Streams;
                a.CapexPct = f.CapexPct; a.DaPct = f.DaPct; a.NwcPct = f.NwcPct; a.Beta = f.Beta; a.ExitMultiple = f.ExitMultiple;
                var relax = levers.Any(l => l.Key == "relax") ? Math.Abs(theta) : 0;
                rep.Eased = relax;
                if (relax > 0.005)
                {
                    rep.EasedParts = eased;
                    rep.Caveats.Add($"On their own, the industry adjustments would have left the model further from the analysts than the company's history did, so those working against them ({string.Join(", ", eased)}) were eased {relax:P0} of the way back.");
                }

                var tgt = $"the analysts' {Money(target.Value, co.Currency)} {(fromRating ? "rating-implied price" : rep.TargetBasis)}";
                string Why(string key) => $"Toward {tgt}" + (levers.FirstOrDefault(l => l.Key == key)?.Weight == 1 ? ", and toward industry norms." : ".");
                if (Math.Abs(a.EbitMargin[n - 1] - a1.EbitMargin[n - 1]) > 1e-5)
                {
                    Step("ebitMargin", $"EBIT margin, year {n}", "target", Pct(a1.EbitMargin[n - 1]), Pct(a.EbitMargin[n - 1]), Why("ebitMargin"));
                    Note("ebitMargin", $"Then nudged toward {tgt}: {Pct(a1.EbitMargin[n - 1])} → {Pct(a.EbitMargin[n - 1])} by year {n}.");
                }
                if (gAnchored < n - 1 && Math.Abs(a.Growth[gAnchored] - a1.Growth[gAnchored]) > 1e-5)
                {
                    var dg = a.Growth[gAnchored] - a1.Growth[gAnchored];
                    Step("growth", $"Revenue growth, year {gAnchored + 1}", "target",
                        a.Streams.Count > 0 ? "each stream" : Pct(a1.Growth[gAnchored]),
                        a.Streams.Count > 0 ? $"{Pts(dg)}, tapering to 0 by year {n}" : $"{Pct(a.Growth[gAnchored])}, tapering back by year {n}", Why("growth"));
                    Note("growth", $"Then nudged toward {tgt}: {Pts(dg)} in year {gAnchored + 1}, tapering to nothing by year {n}.");
                }
                if (Math.Abs(a.Beta - a1.Beta) >= 0.005)
                {
                    Step("beta", "Beta", "target", a1.Beta.ToString("0.00", Inv), a.Beta.ToString("0.00", Inv), Why("beta"));
                    Note("beta", $"Then nudged toward {tgt}: {a1.Beta:0.00} → {a.Beta:0.00}.");
                }
                if (Math.Abs(a.DaPct[n - 1] - a1.DaPct[n - 1]) > 1e-5)
                {
                    Step("daPct", $"D&A % of revenue, year {n}", "target", Pct(a1.DaPct[n - 1]), Pct(a.DaPct[n - 1]), $"Industry adjustment eased toward {tgt}.");
                    Note("daPct", $"Then eased toward {tgt}: {Pct(a1.DaPct[n - 1])} → {Pct(a.DaPct[n - 1])} by year {n}.");
                }
                if (Math.Abs(a.NwcPct[0] - a1.NwcPct[0]) > 1e-5)
                {
                    Step("nwcPct", "NWC % of revenue change", "target", Pct(a1.NwcPct[0]), Pct(a.NwcPct[0]), $"Industry adjustment eased toward {tgt}.");
                    Note("nwcPct", $"Then eased toward {tgt}: {Pct(a1.NwcPct[0])} → {Pct(a.NwcPct[0])}.");
                }
                if (Math.Abs(a.CapexPct[n - 1] - a1.CapexPct[n - 1]) > 1e-5)
                {
                    Step("capexPct", $"Capex % of revenue, year {n}", "target", Pct(a1.CapexPct[n - 1]), Pct(a.CapexPct[n - 1]), Why("capexPct"));
                    Note("capexPct", $"Then nudged toward {tgt}: {Pct(a1.CapexPct[n - 1])} → {Pct(a.CapexPct[n - 1])} by year {n}.");
                }
                if (Math.Abs(a.ExitMultiple - a1.ExitMultiple) >= 0.05)
                {
                    Step("exitMultiple", "Exit EV / EBITDA", "target", Mult(a1.ExitMultiple), Mult(a.ExitMultiple), Why("exitMultiple"));
                    Note("exitMultiple", $"Then nudged toward {tgt}: {Mult(a1.ExitMultiple)} → {Mult(a.ExitMultiple)}.");
                }
                if (levers.Count == 0) rep.Caveats.Add("No lever could move the value toward the analysts without breaking the model, so only the industry step applies.");
                else if (rep.LeversExhausted) rep.Caveats.Add("Every lever reached the end of its realistic range before the model got to its aim: the gap to the analysts is bigger than industry norms can explain. Check the inputs, or the story the analysts are pricing in.");
            }
        }

        rep.ValueCalibrated = Fin(V(a));
        // How much of the history-only model's distance to the analysts is gone.
        if (rep.ValueCalibrated is { } vf && rep.ValueHistory is { } vh && rep.Target is { } tg && Math.Abs(tg - vh) > 1e-9 && rep.Pull > 0)
            rep.GapClosed = (Math.Abs(vh - tg) - Math.Abs(vf - tg)) / Math.Abs(vh - tg);
        rep.Summary = Summary(rep, norms, co.Currency, cur);
        notes["calibration"] = rep.Summary;
        return rep;
    }

    private static string Summary(CalibrationReport r, string norms, string quoteCcy, string stmtCcy)
    {
        var ccy = string.IsNullOrEmpty(stmtCcy) ? quoteCcy : stmtCcy;
        string M(double? v) => v is { } x ? Money(x, ccy) : "n/a";
        var who = r.MatchLevel == "industry" ? $"{norms} (matched on “{r.MatchedOn}”)"
            : r.MatchLevel == "sector" ? $"{norms} (sector-level: “{r.MatchedOn}”)" : norms;
        var s = $"Adapted to {who}: {M(r.ValueHistory)} from the history alone → {M(r.ValueIndustry)} with industry realities";
        if (r.Target is { } t && r.Pull > 0)
        {
            var src = r.TargetBasis == "rating" ? $"the {r.Recommendation?.Replace('_', ' ') ?? "consensus"} rating (no price target)"
                : $"{(r.Analysts is { } n ? $"{n} analysts' " : "the ")}{Money(t, quoteCcy)} {r.TargetBasis}";
            s += $" → {M(r.ValueCalibrated)} after a {r.Mode} pull toward {src}";
            if (r.GapClosed is { } g) s += g >= 0 ? $": {Math.Min(g, 1):P0} closer to it than the history alone" : $" (still {-g:P0} further from it than the history alone)";
        }
        return s + ".";
    }

    // ---------------------------------------------------------------- helpers

    private static double Ramp(int t, int anchored, int n) =>
        t < anchored ? 0 : t >= n - 1 ? 1 : (t - anchored + 1.0) / (n - anchored);

    private static void Path(double[] arr, double from, double to, int n)
    {
        for (int t = 0; t < arr.Length; t++) arr[t] = t >= n - 1 ? to : from + (to - from) * t / (n - 1.0);
    }

    /// <summary>Adds the same change to the single revenue line and every stream, year by year.</summary>
    private static void ShiftGrowth(DcfAssumptions s, double[] d)
    {
        for (int t = 0; t < Max; t++)
        {
            s.Growth[t] += d[t];
            foreach (var st in s.Streams) st.Growth[t] += d[t];
        }
    }

    private static double StdDev(IReadOnlyList<double> xs)
    {
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(v => (v - m) * (v - m)) / (xs.Count - 1));
    }

    private static double? Fin(double v) => double.IsFinite(v) ? v : null;
    private static string Pct(double v, int dp = 1) => (v * 100).ToString(dp == 2 ? "0.00" : "0.0", Inv) + "%";
    private static string Pts(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v * 100).ToString("0.0", Inv) + " pts";
    private static string Mult(double v) => v.ToString("0.0", Inv) + "x";
    private static string Money(double v, string ccy) => v.ToString("#,0.00", Inv) + (string.IsNullOrEmpty(ccy) ? "" : " " + ccy);
}
