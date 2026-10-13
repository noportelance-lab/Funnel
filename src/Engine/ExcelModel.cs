using System.Globalization;
using ClosedXML.Excel;
using Funnel.Providers;

namespace Funnel.Engine;

/// <summary>
/// The DCF as a live Excel model. Every calculated cell is a formula, so changing any blue input
/// re-prices the share. Colour code, the banker's way:
///   blue on yellow = hard-coded input · black = formula on this sheet · green = link to another sheet.
/// The maths matches <see cref="DcfEngine"/> and the JavaScript engine cell for cell.
/// </summary>
public static partial class ExcelExport
{
    private static readonly XLColor InputBlue = XLColor.FromHtml("#0000FF");
    private static readonly XLColor InputFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor LinkGreen = XLColor.FromHtml("#008000");

    private const string FmtPct1 = "0.0%;(0.0%);0.0%";
    private const string FmtPct2 = "0.00%;(0.00%);0.00%";
    private const string FmtMult = "0.0\"x\"";

    private static void Input(IXLCell c, XLCellValue v, string? fmt = null)
    {
        c.Value = v;
        c.Style.Font.FontColor = InputBlue;
        c.Style.Fill.BackgroundColor = InputFill;
        if (fmt != null) c.Style.NumberFormat.Format = fmt;
    }

    private static void Link(IXLCell c, string target, string? fmt = null)
    {
        c.FormulaA1 = target;
        c.Style.Font.FontColor = LinkGreen;
        if (fmt != null) c.Style.NumberFormat.Format = fmt;
    }

    private static void Calc(IXLCell c, string formula, string? fmt = null, bool bold = false)
    {
        c.FormulaA1 = formula;
        c.Style.Font.FontColor = XLColor.Black;
        if (fmt != null) c.Style.NumberFormat.Format = fmt;
        if (bold) c.Style.Font.Bold = true;
    }

    private static string Col(int c) => XLHelper.GetColumnLetterFromNumber(c);
    private static string Q(string sheet) => $"'{sheet}'";

    private static void Section(IXLWorksheet ws, int row, string title, int toCol)
    {
        ws.Cell(row, 1).Value = title;
        HeaderRow(ws, row, 1, toCol);
    }

    private static void Legend(IXLWorksheet ws, int row)
    {
        ws.Cell(row, 1).Value = "Key";
        ws.Cell(row, 1).Style.Font.FontColor = Muted;
        Input(ws.Cell(row, 2), "Input");
        ws.Cell(row, 3).Value = "Formula";
        ws.Cell(row, 4).Value = "Link to another sheet";
        ws.Cell(row, 4).Style.Font.FontColor = LinkGreen;
    }

    private static string FyLabel(string period, int offset, bool estimate)
    {
        var year = DateTime.TryParse(period, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Year : 0;
        return $"FY{year + offset}{(estimate ? "E" : "A")}";
    }

    private static FootballLayout? WriteModel(XLWorkbook wb, ExportBundle b, Dictionary<FinancialStatement, Dictionary<string, int>> rowMaps)
    {
        var a = b.Model!.Normalised();
        var mb = b.ModelBase!;
        int n = a.Years, h = mb.Periods.Count;
        // Money follows the export's units so the green links into the statement sheets line up.
        var M = Divisor(b.Units);
        var ul = UnitsLabel(b.Units).Replace("in ", "");
        var fmtM = AmtFormat(b.Units);
        var cur = string.IsNullOrEmpty(mb.Currency) ? "" : mb.Currency + " ";
        var lastPeriod = mb.Periods.LastOrDefault() ?? DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd");
        // Revenue streams: each has a share and a growth row on Assumptions (from row StreamTop)
        // and a block on its own sheet, which shares the DCF sheet's columns. DCF revenue links to its total.
        int k = a.Streams.Count;
        bool streams = k > 0;
        const string RevSheet = "Revenue build";
        const int StreamTop = 38;
        int shareTotalRow = StreamTop + k, revTotalRow = 6 + 2 * k, revBaseRow = revTotalRow + 3;

        // ============================================================ Assumptions
        var wa = wb.Worksheets.Add("Assumptions");
        wa.SetTabColor(InputBlue);
        Banner(wa, b, $"DCF assumptions · {cur}{ul} except per-share · every blue cell can be changed", mb.StatementsSource);
        Legend(wa, 3);
        const int yc = 3;   // first forecast-year column on the Assumptions sheet
        Section(wa, 5, "Operating drivers (by forecast year)", yc + n);
        for (int t = 0; t < n; t++)
        {
            var c = wa.Cell(5, yc + t);
            c.Value = FyLabel(lastPeriod, t + 1, true);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        var drivers = new (string Label, double[] Vals, string Note)[]
        {
            ("Revenue growth", a.Growth, "growth"),
            ("EBIT margin", a.EbitMargin, "ebitMargin"),
            ("Tax rate", a.TaxRate, "taxRate"),
            ("D&A % of revenue", a.DaPct, "daPct"),
            ("Capex % of revenue", a.CapexPct, "capexPct"),
            ("Change in NWC % of change in revenue", a.NwcPct, "nwcPct"),
        };
        for (int i = 0; i < drivers.Length; i++)
        {
            int r = 6 + i;
            wa.Cell(r, 1).Value = drivers[i].Label;
            if (i == 0 && streams)
            {
                // Total growth falls out of the streams below, so it's a link here, not an input.
                wa.Cell(r, 1).Value = "Revenue growth (total of the streams below)";
                for (int t = 0; t < n; t++) Link(wa.Cell(r, yc + t), $"{Q(RevSheet)}!{Col(3 + h + t)}{revTotalRow + 1}", FmtPct1);
                Note(wa.Cell(r, yc + n + 1), mb, "streams");
                continue;
            }
            for (int t = 0; t < n; t++) Input(wa.Cell(r, yc + t), drivers[i].Vals[t], FmtPct1);
            Note(wa.Cell(r, yc + n + 1), mb, drivers[i].Note);
        }

        void Scalar(int r, string label, XLCellValue v, string fmt, string? note)
        {
            wa.Cell(r, 1).Value = label;
            Input(wa.Cell(r, 2), v, fmt);
            if (note != null) Note(wa.Cell(r, 3), mb, note);
        }
        Section(wa, 13, "Cost of capital", yc + n);
        Scalar(14, "Risk-free rate", a.Rf, FmtPct2, "rf");
        Scalar(15, "Equity risk premium", a.Erp, FmtPct2, "erp");
        Scalar(16, "Beta (levered)", a.Beta, "0.00", "beta");
        Scalar(17, "Size / other premium", a.SizePremium, FmtPct2, null);
        Scalar(18, "Pre-tax cost of debt", a.CostOfDebt, FmtPct2, "costOfDebt");
        Scalar(19, "Target debt / (debt + equity)", a.DebtWeight, FmtPct1, "debtWeight");

        Section(wa, 21, "Terminal value", yc + n);
        Scalar(22, "Method (Gordon or Multiple)", a.TvMethod == "multiple" ? "Multiple" : "Gordon", "@", null);
        wa.Cell(22, 2).CreateDataValidation().List("\"Gordon,Multiple\"");
        Scalar(23, "Terminal growth rate", a.Tgr, FmtPct2, "tgr");
        Scalar(24, "Exit EV / EBITDA multiple", a.ExitMultiple, FmtMult, "exitMultiple");

        Section(wa, 26, "Timing", yc + n);
        Scalar(27, "Mid-year convention (1 = on, 0 = off)", a.MidYear ? 1 : 0, "0", null);
        wa.Cell(27, 2).CreateDataValidation().List("\"0,1\"");

        Section(wa, 29, $"Equity bridge ({cur}{ul})", yc + n);
        Scalar(30, "Cash & short-term investments", a.Cash / M, fmtM, "bridge");
        Scalar(31, "Total debt", a.Debt / M, fmtM, null);
        Scalar(32, "Minority interest", a.Minority / M, fmtM, null);
        Scalar(33, $"Diluted shares ({ul})", a.Shares / M, fmtM, "shares");
        if (b.SharesFlag is { } sf) Flag(wa.Cell(33, 2), sf);
        Scalar(34, "Current share price", mb.Price ?? 0, FmtPrice, null);

        if (streams)
        {
            Section(wa, 36, $"Revenue streams · share of {FyLabel(lastPeriod, 0, false)} revenue, then growth by forecast year", yc + n);
            wa.Cell(37, 2).Value = "Share";
            for (int t = 0; t < n; t++) wa.Cell(37, yc + t).Value = FyLabel(lastPeriod, t + 1, true);
            var heads = wa.Range(37, 2, 37, yc + n - 1);
            heads.Style.Font.Bold = true; heads.Style.Font.FontColor = Muted;
            heads.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            for (int i = 0; i < k; i++)
            {
                var st = a.Streams[i];
                wa.Cell(StreamTop + i, 1).Value = StreamName(st, i);
                Input(wa.Cell(StreamTop + i, 2), st.Share, FmtPct1);
                for (int t = 0; t < n; t++) Input(wa.Cell(StreamTop + i, yc + t), st.Growth[t], FmtPct1);
            }
            Note(wa.Cell(StreamTop, yc + n + 1), mb, "streams");
            wa.Cell(shareTotalRow, 1).Value = "Total of shares (scaled to 100% if they don't add up)";
            Calc(wa.Cell(shareTotalRow, 2), $"SUM(B{StreamTop}:B{shareTotalRow - 1})", FmtPct1, bold: true);
            wa.Cell(shareTotalRow, 2).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        wa.Column(1).Width = 40;
        wa.Column(2).Width = 13;
        for (int c = yc; c < yc + n; c++) wa.Column(c).Width = 11;
        wa.Column(yc + n + 1).Width = 70;

        // ============================================================ WACC
        var ww = wb.Worksheets.Add("WACC");
        ww.SetTabColor(Ink);
        Banner(ww, b, "Weighted average cost of capital (CAPM cost of equity)", mb.StatementsSource);
        Legend(ww, 3);
        Section(ww, 5, "Cost of equity", 2);
        void WRow(int r, string label) => ww.Cell(r, 1).Value = label;
        WRow(6, "Risk-free rate"); Link(ww.Cell(6, 2), "Assumptions!B14", FmtPct2);
        WRow(7, "Beta"); Link(ww.Cell(7, 2), "Assumptions!B16", "0.00");
        WRow(8, "Equity risk premium"); Link(ww.Cell(8, 2), "Assumptions!B15", FmtPct2);
        WRow(9, "Size / other premium"); Link(ww.Cell(9, 2), "Assumptions!B17", FmtPct2);
        WRow(10, "Cost of equity"); Calc(ww.Cell(10, 2), "B6+B7*B8+B9", FmtPct2, bold: true);
        Section(ww, 12, "Cost of debt", 2);
        WRow(13, "Pre-tax cost of debt"); Link(ww.Cell(13, 2), "Assumptions!B18", FmtPct2);
        WRow(14, "Tax rate (first forecast year)"); Link(ww.Cell(14, 2), $"Assumptions!{Col(yc)}8", FmtPct1);
        WRow(15, "After-tax cost of debt"); Calc(ww.Cell(15, 2), "B13*(1-B14)", FmtPct2, bold: true);
        Section(ww, 17, "Capital structure", 2);
        WRow(18, "Debt / (debt + equity)"); Link(ww.Cell(18, 2), "Assumptions!B19", FmtPct1);
        WRow(19, "Equity / (debt + equity)"); Calc(ww.Cell(19, 2), "1-B18", FmtPct1);
        WRow(21, "WACC"); Calc(ww.Cell(21, 2), "B19*B10+B18*B15", FmtPct2, bold: true);
        Box(ww.Range(21, 1, 21, 2));
        ww.Column(1).Width = 34; ww.Column(2).Width = 14;

        // ============================================================ DCF
        var wd = wb.Worksheets.Add("DCF");
        wd.SetTabColor(Accent);
        Banner(wd, b, $"Discounted cash flow · {cur}{ul} except per-share", mb.StatementsSource);
        Legend(wd, 3);
        int hc = 3, fc = hc + h, lc = fc + n - 1;   // first history col, first forecast col, last forecast col
        string L = Col(lc);
        wd.Cell(5, 1).Value = $"{cur}{ul}";
        for (int i = 0; i < h; i++) wd.Cell(5, hc + i).Value = FyLabel(mb.Periods[i], 0, false);
        for (int t = 0; t < n; t++) wd.Cell(5, fc + t).Value = FyLabel(lastPeriod, t + 1, true);
        HeaderRow(wd, 5, 1, lc);
        wd.Range(5, hc, 5, lc).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        if (h > 0) wd.Range(6, hc, 26, fc - 1).Style.Fill.BackgroundColor = Band;

        // Historical links into the statement sheets.
        var income = b.Statements.FirstOrDefault(s => s.Kind == StatementKind.Income && s.Frequency == "annual");
        var cash = b.Statements.FirstOrDefault(s => s.Kind == StatementKind.CashFlow && s.Frequency == "annual");
        void Hist(int row, FinancialStatement? st, string[] keys, double[]? fallback = null)
        {
            if (st == null || !rowMaps.TryGetValue(st, out var map)) return;
            var key = keys.FirstOrDefault(k => map.ContainsKey(k));
            if (key == null) return;
            var sheet = Q(SheetName(b, st));
            for (int i = 0; i < h; i++)
            {
                var pc = st.Periods.IndexOf(mb.Periods[i]);
                if (pc < 0) continue;
                var line = st.Lines.First(l => l.Key == key);
                if (pc >= line.Values.Count || line.Values[pc] == null) continue;
                Link(wd.Cell(row, hc + i), $"{sheet}!{Col(pc + 2)}{map[key]}", fmtM);
            }
        }

        string[] labels =
        {
            "Revenue", "  % growth", "EBIT", "  % margin", "Taxes on EBIT", "  % tax rate", "NOPAT",
            "D&A", "  % of revenue", "Capex", "  % of revenue", "Change in NWC", "  % of change in revenue", "Unlevered free cash flow",
        };
        for (int i = 0; i < labels.Length; i++) wd.Cell(6 + i, 1).Value = labels[i];
        Hist(6, income, new[] { "TotalRevenue" });
        Hist(8, income, new[] { "OperatingIncome", "EBIT" });
        Hist(13, cash, new[] { "DepreciationAndAmortization" });
        Hist(15, cash, new[] { "CapitalExpenditure" });
        for (int i = 0; i < h; i++)
        {
            string c = Col(hc + i), p = Col(hc + i - 1);
            if (i > 0) Calc(wd.Cell(7, hc + i), $"IFERROR({c}6/{p}6-1,\"\")", FmtPct1);
            Calc(wd.Cell(9, hc + i), $"IFERROR({c}8/{c}6,\"\")", FmtPct1);
            Calc(wd.Cell(14, hc + i), $"IFERROR({c}13/{c}6,\"\")", FmtPct1);
            Calc(wd.Cell(16, hc + i), $"IFERROR(-{c}15/{c}6,\"\")", FmtPct1);
        }

        // Forecast. The base revenue is the last historical column, or a hard input if there's no history link.
        var baseRev = mb.Revenue.LastOrDefault(v => v != null) ?? 0;
        for (int t = 0; t < n; t++)
        {
            string c = Col(fc + t), p = Col(fc + t - 1), ac = Col(yc + t);
            var prevRev = t == 0 && (h == 0 || wd.Cell(6, fc - 1).IsEmpty()) ? null : $"{p}6";
            if (streams)
            {
                Link(wd.Cell(6, fc + t), $"{Q(RevSheet)}!{c}{revTotalRow}", fmtM);
                wd.Cell(6, fc + t).Style.Font.Bold = true;
            }
            else if (prevRev == null)
            {
                // No linked history: start from a hard-coded base so the model still stands up.
                Calc(wd.Cell(6, fc), $"{(baseRev / M).ToString("0.###", CultureInfo.InvariantCulture)}*(1+{c}7)", fmtM, bold: true);
            }
            else Calc(wd.Cell(6, fc + t), $"{prevRev}*(1+{c}7)", fmtM, bold: true);
            Link(wd.Cell(7, fc + t), $"Assumptions!{ac}6", FmtPct1);
            Calc(wd.Cell(8, fc + t), $"{c}6*{c}9", fmtM);
            Link(wd.Cell(9, fc + t), $"Assumptions!{ac}7", FmtPct1);
            Calc(wd.Cell(10, fc + t), $"-{c}8*{c}11", fmtM);
            Link(wd.Cell(11, fc + t), $"Assumptions!{ac}8", FmtPct1);
            Calc(wd.Cell(12, fc + t), $"{c}8+{c}10", fmtM);
            Calc(wd.Cell(13, fc + t), $"{c}6*{c}14", fmtM);
            Link(wd.Cell(14, fc + t), $"Assumptions!{ac}9", FmtPct1);
            Calc(wd.Cell(15, fc + t), $"-{c}6*{c}16", fmtM);
            Link(wd.Cell(16, fc + t), $"Assumptions!{ac}10", FmtPct1);
            var prior = t == 0 && prevRev == null ? (baseRev / M).ToString("0.###", CultureInfo.InvariantCulture) : $"{p}6";
            Calc(wd.Cell(17, fc + t), $"-({c}6-{prior})*{c}18", fmtM);
            Link(wd.Cell(18, fc + t), $"Assumptions!{ac}11", FmtPct1);
            Calc(wd.Cell(19, fc + t), $"{c}12+{c}13+{c}15+{c}17", fmtM, bold: true);
            Input(wd.Cell(23, fc + t), t + 1, "0");
            Calc(wd.Cell(24, fc + t), $"{c}23-0.5*$B$22", "0.0");
            Calc(wd.Cell(25, fc + t), $"1/(1+$B$21)^{c}24", "0.000");
            Calc(wd.Cell(26, fc + t), $"{c}19*{c}25", fmtM);
        }
        foreach (var r in new[] { 6, 8, 12, 19 }) wd.Cell(r, 1).Style.Font.Bold = true;
        var ufcfRow = wd.Range(19, 1, 19, lc);
        ufcfRow.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ufcfRow.Style.Font.Bold = true;

        wd.Cell(21, 1).Value = "WACC"; Link(wd.Cell(21, 2), "WACC!B21", FmtPct2);
        wd.Cell(22, 1).Value = "Mid-year convention (1 = on)"; Link(wd.Cell(22, 2), "Assumptions!B27", "0");
        wd.Cell(23, 1).Value = "Forecast year";
        wd.Cell(24, 1).Value = "Discount period (years)";
        wd.Cell(25, 1).Value = "Discount factor";
        wd.Cell(26, 1).Value = "PV of unlevered FCF";

        Section(wd, 28, "Valuation", 2);
        var v = new (string Label, Action<IXLCell> Write)[]
        {
            ("Sum of PV of free cash flows", c => Calc(c, $"SUM({Col(fc)}26:{L}26)", fmtM)),                        // 29
            ("Terminal value method", c => Link(c, "Assumptions!B22")),                                             // 30
            ("Terminal growth rate", c => Link(c, "Assumptions!B23", FmtPct2)),                                     // 31
            ("Exit EV / EBITDA multiple", c => Link(c, "Assumptions!B24", FmtMult)),                                // 32
            ("Final-year EBITDA", c => Calc(c, $"{L}8+{L}13", fmtM)),                                               // 33
            ("Terminal value — perpetuity growth", c => Calc(c, $"{L}19*(1+B31)/(B21-B31)", fmtM)),                 // 34
            ("Terminal value — exit multiple", c => Calc(c, "B33*B32", fmtM)),                                       // 35
            ("Terminal value used", c => Calc(c, "IF(B30=\"Multiple\",B35,B34)", fmtM)),                            // 36
            ("PV of terminal value", c => Calc(c, $"B36/(1+B21)^{L}23", fmtM)),                                     // 37
            ("Enterprise value", c => Calc(c, "B29+B37", fmtM, bold: true)),                                        // 38
            ("Total debt", c => Link(c, "Assumptions!B31", fmtM)),                                                  // 39
            ("Cash & short-term investments", c => Link(c, "Assumptions!B30", fmtM)),                               // 40
            ("Minority interest", c => Link(c, "Assumptions!B32", fmtM)),                                           // 41
            ("Equity value", c => Calc(c, "B38-B39+B40-B41", fmtM, bold: true)),                                    // 42
            ($"Diluted shares ({ul})", c => Link(c, "Assumptions!B33", fmtM)),                                   // 43
            ("Implied share price", c => Calc(c, "B42/B43", FmtPrice, bold: true)),                                 // 44
            ("Current share price", c => Link(c, "Assumptions!B34", FmtPrice)),                                     // 45
            ("Upside / (downside)", c => Calc(c, "IFERROR(B44/B45-1,\"\")", "+0.0%;-0.0%", bold: true)),            // 46
            ("Terminal value % of EV", c => Calc(c, "IFERROR(B37/B38,\"\")", FmtPct1)),                             // 47
            ("Implied exit multiple (perpetuity method)", c => Calc(c, "IFERROR(B34/B33,\"\")", FmtMult)),          // 48
            ("Implied perpetuity growth (multiple method)", c => Calc(c, $"IFERROR((B35*B21-{L}19)/(B35+{L}19),\"\")", FmtPct2)), // 49
        };
        for (int i = 0; i < v.Length; i++)
        {
            wd.Cell(29 + i, 1).Value = v[i].Label;
            v[i].Write(wd.Cell(29 + i, 2));
        }
        foreach (var r in new[] { 38, 42, 44 }) wd.Cell(r, 1).Style.Font.Bold = true;
        Box(wd.Range(44, 1, 44, 2));
        wd.Cell(44, 2).Style.Fill.BackgroundColor = Subject;
        wd.Column(1).Width = 42; wd.Column(2).Width = 14;
        for (int c = hc; c <= lc; c++) wd.Column(c).Width = 12;

        // ============================================================ Revenue build
        IXLWorksheet? wr = null;
        if (streams)
        {
            wr = wb.Worksheets.Add(RevSheet);
            wr.SetTabColor(Accent);
            Banner(wr, b, $"Revenue build by stream · {cur}{ul} · history as reported in the filings", mb.StatementsSource);
            Legend(wr, 3);
            wr.Cell(5, 1).Value = $"{cur}{ul}";
            for (int i = 0; i < h; i++) wr.Cell(5, hc + i).Value = FyLabel(mb.Periods[i], 0, false);
            for (int t = 0; t < n; t++) wr.Cell(5, fc + t).Value = FyLabel(lastPeriod, t + 1, true);
            HeaderRow(wr, 5, 1, lc);
            wr.Range(5, hc, 5, lc).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            if (h > 0) wr.Range(6, hc, revTotalRow + 1, fc - 1).Style.Fill.BackgroundColor = Band;

            // The base is the last reported revenue on the DCF sheet (itself linked to the income statement).
            var linkedBase = h > 0 && !wd.Cell(6, fc - 1).IsEmpty();
            wr.Cell(revBaseRow, 1).Value = $"Base-year revenue ({FyLabel(lastPeriod, 0, false)}, income statement)";
            if (linkedBase) Link(wr.Cell(revBaseRow, 2), $"DCF!{Col(fc - 1)}6", fmtM);
            else Input(wr.Cell(revBaseRow, 2), baseRev / M, fmtM);
            var baseRef = $"$B${revBaseRow}";

            var revRows = new List<int>();
            for (int i = 0; i < k; i++)
            {
                int r = 6 + 2 * i;
                revRows.Add(r);
                var st = a.Streams[i];
                wr.Cell(r, 1).Value = StreamName(st, i);
                wr.Cell(r, 1).Style.Font.Bold = true;
                wr.Cell(r + 1, 1).Value = "  % growth";
                var past = mb.Streams.FirstOrDefault(x => x.Key.Length > 0 && x.Key == st.Key)?.Values;
                for (int j = 0; j < h; j++)
                {
                    if (past == null || j >= past.Count || past[j] is not { } pv) continue;
                    Input(wr.Cell(r, hc + j), pv / M, fmtM);
                    if (j > 0 && past[j - 1] != null) Calc(wr.Cell(r + 1, hc + j), $"IFERROR({Col(hc + j)}{r}/{Col(hc + j - 1)}{r}-1,\"\")", FmtPct1);
                }
                for (int t = 0; t < n; t++)
                {
                    string c = Col(fc + t), p = Col(fc + t - 1);
                    Calc(wr.Cell(r, fc + t), t == 0
                        ? $"IFERROR({baseRef}*Assumptions!$B${StreamTop + i}/Assumptions!$B${shareTotalRow},0)*(1+{c}{r + 1})"
                        : $"{p}{r}*(1+{c}{r + 1})", fmtM);
                    Link(wr.Cell(r + 1, fc + t), $"Assumptions!{Col(yc + t)}{StreamTop + i}", FmtPct1);
                }
            }
            wr.Cell(revTotalRow, 1).Value = "Total revenue";
            wr.Cell(revTotalRow + 1, 1).Value = "  % growth";
            for (int col = hc; col <= lc; col++)
            {
                string c = Col(col), p = Col(col - 1);
                if (col < fc && revRows.All(r => wr.Cell(r, col).IsEmpty())) continue;
                Calc(wr.Cell(revTotalRow, col), $"SUM({string.Join(",", revRows.Select(r => $"{c}{r}"))})", fmtM, bold: true);
                if (col == fc) Calc(wr.Cell(revTotalRow + 1, col), $"IFERROR({c}{revTotalRow}/{baseRef}-1,\"\")", FmtPct1);
                else if (col > hc && !wr.Cell(revTotalRow, col - 1).IsEmpty()) Calc(wr.Cell(revTotalRow + 1, col), $"IFERROR({c}{revTotalRow}/{p}{revTotalRow}-1,\"\")", FmtPct1);
            }
            var tot = wr.Range(revTotalRow, 1, revTotalRow, lc);
            tot.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            tot.Style.Font.Bold = true;
            Note(wr.Cell(revBaseRow + 1, 1), mb, "streams");
            wr.Column(1).Width = 42; wr.Column(2).Width = 14;
            for (int c = hc; c <= lc; c++) wr.Column(c).Width = 12;
        }

        // ============================================================ Sensitivity
        var wsn = wb.Worksheets.Add("Sensitivity");
        wsn.SetTabColor(XLColor.FromHtml("#7C3AED"));
        Banner(wsn, b, "Implied share price sensitivity (each cell recomputes the whole DCF)", mb.StatementsSource);
        Legend(wsn, 3);
        wsn.Cell(5, 1).Value = "WACC step"; Input(wsn.Cell(5, 2), 0.005, FmtPct2);
        wsn.Cell(6, 1).Value = "Terminal growth step"; Input(wsn.Cell(6, 2), 0.0025, FmtPct2);
        wsn.Cell(7, 1).Value = "Exit multiple step"; Input(wsn.Cell(7, 2), 1.0, FmtMult);

        string Pv(string w) => $"SUMPRODUCT(DCF!${Col(fc)}$19:${L}$19,(1+{w})^(-DCF!${Col(fc)}$24:${L}$24))";
        const string Bridge = "-DCF!$B$39+DCF!$B$40-DCF!$B$41";
        Grid(wsn, 9, "Perpetuity growth method — WACC (down) × terminal growth (across)", "DCF!B31", "$B$6", FmtPct2,
            (w, g) => $"({Pv(w)}+DCF!${L}$19*(1+{g})/({w}-{g})/(1+{w})^DCF!${L}$23{Bridge})/DCF!$B$43");
        Grid(wsn, 18, "Exit multiple method — WACC (down) × exit EV / EBITDA (across)", "DCF!B32", "$B$7", FmtMult,
            (w, m) => $"({Pv(w)}+DCF!$B$33*{m}/(1+{w})^DCF!${L}$23{Bridge})/DCF!$B$43");
        wsn.Column(1).Width = 24; wsn.Column(2).Width = 12;
        for (int c = 3; c <= 7; c++) wsn.Column(c).Width = 12;

        // ============================================================ Football field
        var ffl = b.Football is { } ff ? WriteFootball(wb, b, ff, M, ul) : null;
        wb.Worksheets.TryGetWorksheet(FootballSheet, out var wf);

        // ============================================================ names, order
        wb.DefinedNames.Add("SharePrice", wd.Range("B44"));
        wb.DefinedNames.Add("WACC", ww.Range("B21"));
        wb.DefinedNames.Add("TerminalGrowth", wa.Range("B23"));

        int pos = 1;
        if (wb.Worksheets.TryGetWorksheet("Summary", out var summary)) summary.Position = pos++;
        foreach (var sheet in new[] { wd, wf, wa, wr, ww, wsn }.OfType<IXLWorksheet>()) sheet.Position = pos++;
        wd.SetTabActive();
        return ffl;
    }

    /// <summary>A 5×5 sensitivity table. Row heads are WACC ± steps, column heads the second driver ± steps.</summary>
    private static void Grid(IXLWorksheet ws, int top, string title, string centerLink, string stepCell, string colFmt, Func<string, string, string> price)
    {
        ws.Cell(top, 1).Value = title;
        ws.Cell(top, 1).Style.Font.Bold = true;
        int hr = top + 1, r0 = top + 2;
        ws.Cell(hr, 2).Value = "WACC ↓";
        ws.Cell(hr, 2).Style.Font.FontColor = Muted;
        for (int k = 0; k < 5; k++)
        {
            var hc = ws.Cell(hr, 3 + k);
            if (k == 2) Link(hc, centerLink, colFmt);
            else Calc(hc, $"$E${hr}+({k - 2})*{stepCell}", colFmt);
            hc.Style.Font.Bold = true;
            var rc = ws.Cell(r0 + k, 2);
            if (k == 2) Link(rc, "WACC!B21", FmtPct2);
            else Calc(rc, $"$B${r0 + 2}+({k - 2})*$B$5", FmtPct2);
            rc.Style.Font.Bold = true;
        }
        for (int i = 0; i < 5; i++)
            for (int k = 0; k < 5; k++)
                Calc(ws.Cell(r0 + i, 3 + k), $"IFERROR({price($"$B{r0 + i}", $"{Col(3 + k)}${hr}")},\"\")", FmtPrice);
        var body = ws.Range(r0, 3, r0 + 4, 7);
        body.AddConditionalFormat().ColorScale()
            .LowestValue(XLColor.FromHtml("#F8D7DA")).Midpoint(XLCFContentType.Percentile, 50, XLColor.White).HighestValue(XLColor.FromHtml("#D1E7DD"));
        ws.Range(hr, 2, hr, 7).Style.Fill.BackgroundColor = Band;
        ws.Range(r0, 2, r0 + 4, 2).Style.Fill.BackgroundColor = Band;
        Box(ws.Range(r0 + 2, 5, r0 + 2, 5));
        ws.Cell(r0 + 2, 5).Style.Font.Bold = true;
    }

    private static string StreamName(RevenueStream st, int i) => string.IsNullOrWhiteSpace(st.Name) ? $"Stream {i + 1}" : st.Name;

    private static void Box(IXLRange r)
    {
        r.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        r.Style.Border.OutsideBorderColor = Ink;
    }

    private static void Note(IXLCell c, DcfBase mb, string key)
    {
        if (!mb.Notes.TryGetValue(key, out var text)) return;
        c.Value = text;
        c.Style.Font.FontColor = Muted;
        c.Style.Font.Italic = true;
    }
}
