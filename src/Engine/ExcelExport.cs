using ClosedXML.Excel;
using Funnel.Providers;

namespace Funnel.Engine;

/// <summary>Everything one export pulls, gathered up front so the workbook is written in one pass.</summary>
public sealed record ExportBundle
{
    public required string Symbol { get; init; }
    public CompanySnapshot? Company { get; init; }
    public bool IncludeSummary { get; init; } = true;
    public bool IncludeAnalysts { get; init; } = true;
    public List<FinancialStatement> Statements { get; init; } = new();
    public List<CompRow>? Comps { get; init; }
    public PriceSeries? Prices { get; init; }
    /// <summary>actual | thousands | millions | billions</summary>
    public string Units { get; init; } = "millions";
    public string Source { get; init; } = "";
    public DateTime Pulled { get; init; } = DateTime.Now;
    /// <summary>When set, the workbook leads with a live DCF built on the annual statements.</summary>
    public DcfAssumptions? Model { get; init; }
    public DcfBase? ModelBase { get; init; }
    /// <summary>When the share count disagrees with a second source, the model's shares cell is flagged.</summary>
    public DataFlag? SharesFlag { get; init; }
    /// <summary>With a model: the football field's DCF ranges and peer multiples, from the Model tab.</summary>
    public FootballData? Football { get; init; }
    /// <summary>Per-sheet source names (e.g. comps from Finnhub, statements from EDGAR); falls back to <see cref="Source"/>.</summary>
    public string? CompsSource { get; init; }
    public string? PricesSource { get; init; }
    /// <summary>Statements for more than one frequency → sheet names carry the frequency.</summary>
    public bool MixedFrequency => Statements.Select(s => s.Frequency).Distinct().Count() > 1;
}

/// <summary>
/// Writes an <see cref="ExportBundle"/> to a formatted .xlsx. Every figure goes in as a real
/// number (not text), scaled into the chosen units, so the workbook is ready to model off.
/// </summary>
public static partial class ExcelExport
{
    private static readonly XLColor Ink = XLColor.FromHtml("#0E141B");
    private static readonly XLColor Accent = XLColor.FromHtml("#2563EB");
    private static readonly XLColor Band = XLColor.FromHtml("#EEF2F7");
    private static readonly XLColor Subject = XLColor.FromHtml("#FFF7E0");
    private static readonly XLColor Muted = XLColor.FromHtml("#6B7280");

    private const string FmtAmt = "#,##0;(#,##0);\"–\"";
    private const string FmtAmt1 = "#,##0.0;(#,##0.0);\"–\"";
    private const string FmtAmt2 = "#,##0.00;(#,##0.00);\"–\"";
    private const string FmtPs = "0.00;(0.00);\"–\"";
    private const string FmtPct = "0.0%;(0.0%);\"–\"";
    private const string FmtX = "0.0\"x\";(0.0\"x\");\"–\"";
    private const string FmtPrice = "#,##0.00";

    public static double Divisor(string units) => units switch
    {
        "thousands" => 1e3,
        "millions" => 1e6,
        "billions" => 1e9,
        _ => 1,
    };

    public static string UnitsLabel(string units) => units switch
    {
        "thousands" => "in thousands",
        "millions" => "in millions",
        "billions" => "in billions",
        _ => "actual",
    };

    private static string AmtFormat(string units) => units switch
    {
        "billions" => FmtAmt2,
        "millions" => FmtAmt1,
        _ => FmtAmt,
    };

    public static void Write(string path, ExportBundle b)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Author = "Funnel";
        wb.Properties.Title = $"{b.Symbol} financials";

        if (b.Company != null && b.IncludeSummary) WriteSummary(wb, b);
        var rowMaps = new Dictionary<FinancialStatement, Dictionary<string, int>>();
        foreach (var st in b.Statements) rowMaps[st] = WriteStatement(wb, b, st);
        if (b.Comps is { Count: > 0 }) WriteComps(wb, b);
        if (b.Company != null && b.IncludeAnalysts) WriteAnalysts(wb, b);
        if (b.Prices is { Points.Count: > 0 }) WritePrices(wb, b);
        FootballLayout? football = null;
        if (b.Model != null && b.ModelBase != null) football = WriteModel(wb, b, rowMaps);

        if (wb.Worksheets.Count == 0) throw new InvalidOperationException("Nothing was selected to export.");
        foreach (var ws in wb.Worksheets) Indent(ws);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        wb.SaveAs(path);
        if (football != null) AddFootballChart(path, football);
    }

    // ------------------------------------------------------------------ shared bits

    // Every sheet gets a narrow blank gutter in column A; sheets are written from column A and
    // shifted here, which also moves formulas, cross-sheet refs, names, validation and merges.
    private static void Indent(IXLWorksheet ws)
    {
        ws.Column(1).InsertColumnsBefore(1);
        ws.Column(1).Width = 3;
    }

    private static string CompanyTitle(ExportBundle b) =>
        b.Company != null ? $"{b.Company.Name} ({b.Symbol})" : b.Symbol;

    private static int Banner(IXLWorksheet ws, ExportBundle b, string subtitle, string? source = null)
    {
        ws.ShowGridLines = false;
        var t = ws.Cell(1, 1);
        t.Value = CompanyTitle(b);
        t.Style.Font.Bold = true; t.Style.Font.FontSize = 14; t.Style.Font.FontColor = Ink;
        var s = ws.Cell(2, 1);
        s.Value = $"{subtitle} · Source: {(string.IsNullOrEmpty(source) ? b.Source : source)} · Pulled {b.Pulled:yyyy-MM-dd HH:mm}";
        s.Style.Font.FontColor = Muted; s.Style.Font.Italic = true;
        return 4;   // first free row
    }

    private static void HeaderRow(IXLWorksheet ws, int row, int fromCol, int toCol)
    {
        var r = ws.Range(row, fromCol, row, toCol);
        r.Style.Fill.BackgroundColor = Ink;
        r.Style.Font.FontColor = XLColor.White;
        r.Style.Font.Bold = true;
    }

    private static void Put(IXLCell cell, double? v, string fmt)
    {
        if (v == null) return;
        cell.Value = v.Value;
        cell.Style.NumberFormat.Format = fmt;
    }

    private static string MetricFormat(string fmt, string units) => fmt switch
    {
        "pct" => FmtPct,
        "x" => FmtX,
        "price" => FmtPrice,
        "num" => "0.00",
        _ => AmtFormat(units),
    };

    private static double? MetricValue(MetricDef def, double? v, double div) =>
        v == null ? null : def.Format == "money" ? v / div : v;

    // ------------------------------------------------------------------ statements

    private static string SheetName(ExportBundle b, FinancialStatement st)
    {
        if (!b.MixedFrequency)
            return st.Kind switch { StatementKind.Income => "Income Statement", StatementKind.Balance => "Balance Sheet", _ => "Cash Flow" };
        var abbr = st.Kind switch { StatementKind.Income => "IS", StatementKind.Balance => "BS", _ => "CF" };
        return $"{abbr} {(st.Frequency == "quarterly" ? "Quarterly" : "Annual")}";
    }

    /// <summary>Writes one statement sheet and returns the row of each line key (for the model's links).</summary>
    private static Dictionary<string, int> WriteStatement(XLWorkbook wb, ExportBundle b, FinancialStatement st)
    {
        var rows = new Dictionary<string, int>();
        var ws = wb.Worksheets.Add(SheetName(b, st));
        ws.SetTabColor(Accent);
        var freq = st.Frequency == "quarterly" ? "Quarterly" : "Annual";
        var cur = string.IsNullOrEmpty(st.Currency) ? "" : st.Currency + " ";
        var row = Banner(ws, b, $"{st.Title} · {freq} · {cur}{UnitsLabel(b.Units)} (per-share data as reported)", st.Source);

        var div = Divisor(b.Units);
        var amtFmt = AmtFormat(b.Units);
        var lastCol = st.Periods.Count + 1;

        ws.Cell(row, 1).Value = $"{cur}{UnitsLabel(b.Units)}".Trim();
        for (int i = 0; i < st.Periods.Count; i++)
        {
            var c = ws.Cell(row, i + 2);
            if (DateTime.TryParse(st.Periods[i], out var d))
            {
                c.Value = d;
                c.Style.NumberFormat.Format = st.Frequency == "quarterly" ? "mmm yyyy" : "\"FY\" mmm yyyy";
            }
            else c.Value = st.Periods[i];
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        HeaderRow(ws, row, 1, lastCol);
        var headerRow = row;
        row++;

        foreach (var line in st.Lines)
        {
            var label = ws.Cell(row, 1);
            label.Value = line.Label;
            if (line.Format == "header")
            {
                var band = ws.Range(row, 1, row, lastCol);
                band.Style.Fill.BackgroundColor = Band;
                band.Style.Font.Bold = true;
                row++;
                continue;
            }
            rows[line.Key] = row;
            if (line.Indent > 0) label.Style.Alignment.Indent = line.Indent;
            for (int i = 0; i < line.Values.Count; i++)
            {
                var v = line.Values[i];
                switch (line.Format)
                {
                    case "ps": Put(ws.Cell(row, i + 2), v, FmtPs); break;
                    case "pct": Put(ws.Cell(row, i + 2), v, FmtPct); break;
                    default: Put(ws.Cell(row, i + 2), v / div, amtFmt); break;
                }
            }
            if (line.Emphasis)
            {
                var r = ws.Range(row, 1, row, lastCol);
                r.Style.Font.Bold = true;
                r.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                r.Style.Border.TopBorderColor = XLColor.FromHtml("#9CA3AF");
            }
            row++;
        }

        ws.Column(1).Width = 44;
        for (int c = 2; c <= lastCol; c++) ws.Column(c).Width = 15;
        MarkChecks(ws, st, rows);
        return rows;
    }

    // ------------------------------------------------------------------ cross-checks

    private static readonly XLColor SevereFill = XLColor.FromHtml("#FDE2E1");
    private static readonly XLColor SevereText = XLColor.FromHtml("#B91C1C");
    private static readonly XLColor WarnFill = XLColor.FromHtml("#FFF4CC");

    /// <summary>Red (or amber) fill plus a cell note with both sources' figures.</summary>
    private static void Flag(IXLCell c, DataFlag f)
    {
        c.Style.Fill.BackgroundColor = f.Severity == "severe" ? SevereFill : WarnFill;
        if (f.Severity == "severe") { c.Style.Font.FontColor = SevereText; c.Style.Font.Bold = true; }
        c.CreateComment().AddText((f.Severity == "severe" ? "Sources disagree sharply. " : "Sources differ. ") + f.Message);
    }

    /// <summary>Flags each disagreeing cell and says what was checked in the empty row under the banner.</summary>
    private static void MarkChecks(IXLWorksheet ws, FinancialStatement st, Dictionary<string, int> rows)
    {
        if (st.Check is not { } check) return;
        foreach (var f in check.Flags)
        {
            var col = st.Periods.IndexOf(f.Period);
            if (col >= 0 && rows.TryGetValue(f.Key, out var r)) Flag(ws.Cell(r, col + 2), f);
        }
        int severe = check.Flags.Count(f => f.Severity == "severe"), warn = check.Flags.Count - severe;
        var note = ws.Cell(3, 1);
        note.Value = check.Status == "unavailable" ? $"Not cross-checked: {check.Note}"
            : check.Flags.Count == 0 ? $"Cross-checked against {check.Against}: {check.Compared} figures agree."
            : $"Cross-checked against {check.Against} ({check.Compared} figures). " +
              (severe > 0 ? $"⚠ {severe} disagree sharply (red): treat them with care. " : "") +
              (warn > 0 ? $"{warn} differ, usually because the sources define the line differently (amber). " : "") + "Hover a cell for both figures.";
        note.Style.Font.FontColor = severe > 0 ? SevereText : Muted;
        note.Style.Font.Bold = severe > 0;
    }

    // ------------------------------------------------------------------ summary

    private static void WriteSummary(XLWorkbook wb, ExportBundle b)
    {
        var co = b.Company!;
        var ws = wb.Worksheets.Add("Summary");
        ws.SetTabColor(Ink);
        var row = Banner(ws, b, $"Company summary · {co.Currency} · money values {UnitsLabel(b.Units)}", co.Source);
        var div = Divisor(b.Units);

        void Kv(string k, object? v, string? fmt = null)
        {
            if (v == null || (v is string s && s.Length == 0)) return;
            ws.Cell(row, 1).Value = k;
            ws.Cell(row, 1).Style.Font.FontColor = Muted;
            var c = ws.Cell(row, 2);
            XLCellValue val = v switch { double d => d, long l => (double)l, _ => v.ToString() ?? "" };
            c.Value = val;
            if (fmt != null) c.Style.NumberFormat.Format = fmt;
            row++;
        }

        Kv("Ticker", co.Symbol);
        Kv("Exchange", co.Exchange);
        Kv("Sector", co.Sector);
        Kv("Industry", co.Industry);
        Kv("Headquarters", string.Join(", ", new[] { co.City, co.Country }.Where(x => !string.IsNullOrEmpty(x))));
        Kv("Employees", co.Employees, "#,##0");
        Kv("Website", co.Website);
        Kv("Price", co.Price, FmtPrice);
        Kv("Change today", co.ChangePct / 100, "+0.00%;-0.00%");
        row++;

        foreach (var group in Metrics.All.GroupBy(m => m.Group))
        {
            ws.Cell(row, 1).Value = group.Key;
            HeaderRow(ws, row, 1, 2);
            row++;
            foreach (var def in group)
            {
                co.Metrics.TryGetValue(def.Key, out var v);
                ws.Cell(row, 1).Value = def.Label;
                Put(ws.Cell(row, 2), MetricValue(def, v, div), MetricFormat(def.Format, b.Units));
                if (v == null) ws.Cell(row, 2).Value = "n/a";
                ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                row++;
            }
            row++;
        }

        if (!string.IsNullOrWhiteSpace(co.Summary))
        {
            ws.Cell(row, 1).Value = "Business description";
            HeaderRow(ws, row, 1, 6);
            row++;
            var d = ws.Range(row, 1, row, 6).Merge();
            d.Value = co.Summary;
            d.Style.Alignment.WrapText = true;
            d.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Row(row).Height = Math.Min(409, 15 * Math.Ceiling(co.Summary.Length / 110.0));
        }

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 22;
        for (int c = 3; c <= 6; c++) ws.Column(c).Width = 16;
    }

    // ------------------------------------------------------------------ comps

    private static void WriteComps(XLWorkbook wb, ExportBundle b)
    {
        var ws = wb.Worksheets.Add("Comps");
        ws.SetTabColor(XLColor.FromHtml("#0EA5E9"));
        var row = Banner(ws, b, $"Trading comparables · money values {UnitsLabel(b.Units)} (each in its own trading currency)", b.CompsSource);
        var div = Divisor(b.Units);
        var defs = Metrics.CompKeys.Select(k => Metrics.Find(k)!).ToList();

        var headers = new List<string> { "Ticker", "Company", "Ccy", "Price" };
        headers.AddRange(defs.Select(d => d.Label));
        for (int i = 0; i < headers.Count; i++) ws.Cell(row, i + 1).Value = headers[i];
        HeaderRow(ws, row, 1, headers.Count);
        var headerRow = row;
        row++;

        // Subject first, then peers; the mean/median rows are formulas over the peers only.
        var subject = b.Comps!.FirstOrDefault(c => c.Symbol.Equals(b.Symbol, StringComparison.OrdinalIgnoreCase));
        var peers = b.Comps!.Where(c => c != subject).ToList();
        var ordered = (subject != null ? new[] { subject } : Array.Empty<CompRow>()).Concat(peers).ToList();
        int firstPeerRow = -1, lastPeerRow = -1;

        foreach (var c in ordered)
        {
            ws.Cell(row, 1).Value = c.Symbol;
            ws.Cell(row, 2).Value = c.Error != null ? $"{c.Name} — {c.Error}" : c.Name;
            ws.Cell(row, 3).Value = c.Currency;
            Put(ws.Cell(row, 4), c.Price, FmtPrice);
            for (int i = 0; i < defs.Count; i++)
            {
                c.Metrics.TryGetValue(defs[i].Key, out var v);
                Put(ws.Cell(row, i + 5), MetricValue(defs[i], v, div), MetricFormat(defs[i].Format, b.Units));
            }
            if (c == subject)
            {
                var r = ws.Range(row, 1, row, headers.Count);
                r.Style.Fill.BackgroundColor = Subject;
                r.Style.Font.Bold = true;
            }
            else
            {
                if (firstPeerRow < 0) firstPeerRow = row;
                lastPeerRow = row;
            }
            row++;
        }

        if (firstPeerRow > 0 && peers.Any(c => c.Error == null))
        {
            foreach (var (label, fn) in new[] { ("Peer median", "MEDIAN"), ("Peer mean", "AVERAGE") })
            {
                ws.Cell(row, 2).Value = label;
                for (int i = 0; i < defs.Count; i++)
                {
                    // A column no peer has a value for (e.g. P/E from a source without prices) stays blank.
                    if (!peers.Any(c => c.Metrics.GetValueOrDefault(defs[i].Key) != null)) continue;
                    var col = i + 5;
                    var letter = ws.Column(col).ColumnLetter();
                    var cell = ws.Cell(row, col);
                    cell.FormulaA1 = $"IFERROR({fn}({letter}{firstPeerRow}:{letter}{lastPeerRow}),\"\")";
                    cell.Style.NumberFormat.Format = MetricFormat(defs[i].Format, b.Units);
                }
                var r = ws.Range(row, 1, row, headers.Count);
                r.Style.Font.Bold = true;
                r.Style.Fill.BackgroundColor = Band;
                if (label == "Peer median") r.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                row++;
            }
        }

        ws.Column(1).Width = 10;
        ws.Column(2).Width = 34;
        ws.Column(3).Width = 6;
        for (int c = 4; c <= headers.Count; c++) ws.Column(c).Width = 14;
        ws.Range(headerRow, 4, headerRow, headers.Count).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    // ------------------------------------------------------------------ analysts

    private static void WriteAnalysts(XLWorkbook wb, ExportBundle b)
    {
        var a = b.Company!.Analysts;
        var ws = wb.Worksheets.Add("Analysts");
        ws.SetTabColor(XLColor.FromHtml("#16A34A"));
        var row = Banner(ws, b, "Analyst consensus, price targets and named-firm ratings", b.Company!.Source);

        void Kv(string k, double? v, string fmt)
        {
            ws.Cell(row, 1).Value = k;
            ws.Cell(row, 1).Style.Font.FontColor = Muted;
            Put(ws.Cell(row, 2), v, fmt);
            row++;
        }
        ws.Cell(row, 1).Value = "Consensus";
        ws.Cell(row, 1).Style.Font.FontColor = Muted;
        ws.Cell(row, 2).Value = (a.RecommendationKey ?? "n/a").Replace('_', ' ').ToUpperInvariant();
        ws.Cell(row, 2).Style.Font.Bold = true;
        row++;
        Kv("Mean rating (1 = Strong Buy, 5 = Strong Sell)", a.RecommendationMean, "0.00");
        Kv("Number of analysts", a.AnalystCount, "0");
        Kv("Current price", a.CurrentPrice, FmtPrice);
        Kv("Target — low", a.TargetLow, FmtPrice);
        Kv("Target — mean", a.TargetMean, FmtPrice);
        Kv("Target — median", a.TargetMedian, FmtPrice);
        Kv("Target — high", a.TargetHigh, FmtPrice);
        if (a.TargetMean != null && a.CurrentPrice is > 0)
            Kv("Implied upside to mean target", a.TargetMean / a.CurrentPrice - 1, "+0.0%;-0.0%");
        row++;

        if (a.Trend.Count > 0)
        {
            var h = new[] { "Period", "Strong Buy", "Buy", "Hold", "Sell", "Strong Sell", "Total" };
            for (int i = 0; i < h.Length; i++) ws.Cell(row, i + 1).Value = h[i];
            HeaderRow(ws, row, 1, h.Length);
            row++;
            foreach (var t in a.Trend)
            {
                ws.Cell(row, 1).Value = t.Period;
                ws.Cell(row, 2).Value = t.StrongBuy; ws.Cell(row, 3).Value = t.Buy; ws.Cell(row, 4).Value = t.Hold;
                ws.Cell(row, 5).Value = t.Sell; ws.Cell(row, 6).Value = t.StrongSell;
                ws.Cell(row, 7).FormulaA1 = $"SUM(B{row}:F{row})";
                ws.Cell(row, 7).Style.Font.Bold = true;
                row++;
            }
            row++;
        }

        if (a.Firms.Count > 0)
        {
            var h = new[] { "Firm", "Rating", "Stance", "Action", "Prior rating", "Price target", "Prior target", "Date" };
            for (int i = 0; i < h.Length; i++) ws.Cell(row, i + 1).Value = h[i];
            HeaderRow(ws, row, 1, h.Length);
            row++;
            foreach (var f in a.Firms)
            {
                ws.Cell(row, 1).Value = f.Firm;
                ws.Cell(row, 2).Value = f.Grade;
                ws.Cell(row, 3).Value = f.Bucket;
                ws.Cell(row, 3).Style.Font.FontColor = f.Bucket switch
                {
                    "Buy" => XLColor.FromHtml("#15803D"), "Sell" => XLColor.FromHtml("#B91C1C"), _ => XLColor.FromHtml("#92400E"),
                };
                ws.Cell(row, 3).Style.Font.Bold = true;
                ws.Cell(row, 4).Value = f.Action;
                ws.Cell(row, 5).Value = f.FromGrade ?? "";
                Put(ws.Cell(row, 6), f.PriceTarget, FmtPrice);
                Put(ws.Cell(row, 7), f.PriorTarget, FmtPrice);
                ws.Cell(row, 8).Value = f.Date.ToLocalTime().Date;
                ws.Cell(row, 8).Style.NumberFormat.Format = "yyyy-mm-dd";
                row++;
            }
        }

        ws.Column(1).Width = 44;
        for (int c = 2; c <= 8; c++) ws.Column(c).Width = 15;
    }

    // ------------------------------------------------------------------ prices

    private static void WritePrices(XLWorkbook wb, ExportBundle b)
    {
        var p = b.Prices!;
        var ws = wb.Worksheets.Add("Prices");
        ws.SetTabColor(Muted);
        var row = Banner(ws, b, $"Price history · {p.Currency} · {p.Interval} bars", b.PricesSource);
        var h = new[] { "Date", "Open", "High", "Low", "Close", "Volume" };
        for (int i = 0; i < h.Length; i++) ws.Cell(row, i + 1).Value = h[i];
        HeaderRow(ws, row, 1, h.Length);
        var headerRow = row;
        row++;

        // Newest first — that's the end anyone opening this sheet cares about.
        foreach (var pt in Enumerable.Reverse(p.Points))
        {
            ws.Cell(row, 1).Value = DateTimeOffset.FromUnixTimeSeconds(pt.T).LocalDateTime.Date;
            ws.Cell(row, 1).Style.NumberFormat.Format = "yyyy-mm-dd";
            Put(ws.Cell(row, 2), pt.O, FmtPrice);
            Put(ws.Cell(row, 3), pt.H, FmtPrice);
            Put(ws.Cell(row, 4), pt.L, FmtPrice);
            Put(ws.Cell(row, 5), pt.C, FmtPrice);
            Put(ws.Cell(row, 6), pt.V, "#,##0");
            row++;
        }
        ws.Column(1).Width = 12;
        for (int c = 2; c <= 6; c++) ws.Column(c).Width = 13;
    }
}
