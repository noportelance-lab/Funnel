using System.Globalization;
using System.Security;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Funnel.Engine;

/// <summary>A peer multiple applied to the company: EV multiples need its metric, net debt and shares; per-share ones just the per-share metric.</summary>
public sealed class FootballMultiple
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Equity { get; init; }
    /// <summary>The company's own multiple today.</summary>
    public double? Own { get; init; }
    public string MetricLabel { get; init; } = "";
    /// <summary>EV multiples: the metric in currency (EBITDA, revenue). Per-share multiples: the metric per share.</summary>
    public double Metric { get; init; }
    public double NetDebt { get; init; }
    public double Shares { get; init; }
}

public sealed class FootballPeer
{
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, double?> Multiples { get; init; } = new();
}

public sealed class FootballDcf
{
    /// <summary>gordon | multiple</summary>
    public string Method { get; init; } = "";
    public double? Lo { get; init; }
    public double? Mid { get; init; }
    public double? Hi { get; init; }
    public bool InUse { get; init; }
}

/// <summary>What the Model tab's football field knows, trimmed to the DCF and the peer multiples that matter most.</summary>
public sealed class FootballData
{
    public double? Price { get; init; }
    public List<FootballDcf> Dcf { get; init; } = new();
    public List<FootballMultiple> Multiples { get; init; } = new();
    public List<FootballPeer> Peers { get; init; } = new();
}

/// <summary>Where the football field's table sits, so the chart can be added once the file is saved.</summary>
internal sealed record FootballLayout(int FirstRow, int LastRow, int ChartTop, int ChartBottom,
    string[] Labels, string[] Colors, double[] Starts, double[] Lengths, double[] Highs, double? AxisMin, string Title);

public static partial class ExcelExport
{
    private const string FootballSheet = "Football field";
    // Columns before the gutter is added: A label, B low, C mid, D high, E basis, G/H the chart's bar start and length.
    private const int FfLo = 2, FfMid = 3, FfHi = 4, FfBasis = 5, FfStart = 7, FfLen = 8;

    // "Forward EPS" → "forward EPS", but "EBITDA" stays.
    private static string Lc1(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static double Quartile(List<double> v, double p)
    {
        var s = v.OrderBy(x => x).ToList();
        if (s.Count == 0) return double.NaN;
        double i = (s.Count - 1) * p; int f = (int)Math.Floor(i), c = (int)Math.Ceiling(i);
        return s[f] + (s[c] - s[f]) * (i - f);
    }

    /// <summary>
    /// The football field: the DCF's two sensitivity ranges (live off the Sensitivity sheet), the
    /// most relevant peer multiples applied to this company (live off the peer table below), and the
    /// current price. The chart itself is added after saving (<see cref="AddFootballChart"/>).
    /// </summary>
    private static FootballLayout? WriteFootball(XLWorkbook wb, ExportBundle b, FootballData ff, double M, string ul)
    {
        var mb = b.ModelBase!;
        var dcf = ff.Dcf.Where(d => d.Method is "gordon" or "multiple").ToList();
        var mults = ff.Multiples.Take(2).ToList();
        var peers = ff.Peers.Take(8).ToList();
        if (dcf.Count == 0 && mults.Count == 0) return null;

        var ws = wb.Worksheets.Add(FootballSheet);
        ws.SetTabColor(XLColor.FromHtml("#16A34A"));
        var cur = string.IsNullOrEmpty(mb.Currency) ? "" : $" ({mb.Currency})";
        Banner(ws, b, $"Football field · value per share{cur} · the DCF and the most relevant peer multiples", mb.StatementsSource);
        Legend(ws, 3);

        int nRows = dcf.Count + mults.Count + 1, head = 6, first = head + 1, last = first + nRows - 1;
        int chartTop = last + 2, chartBottom = chartTop + Math.Max(14, 6 + 2 * nRows);
        int pTop = chartBottom + 2;                       // peer multiples
        int pHead = pTop + 1, p0 = pTop + 2, pEnd = p0 + Math.Max(peers.Count, 1) - 1;
        int sLo = pEnd + 1, sMed = sLo + 1, sHi = sLo + 2, sOwn = sLo + 3;
        int aTop = sOwn + 2, aHead = aTop + 1, a0 = aTop + 2;   // applied to the company

        Section(ws, 5, "Implied value per share", FfBasis);
        string[] heads = { "Method", "Low", "Mid", "High", "Basis" };
        for (int i = 0; i < heads.Length; i++) ws.Cell(head, 1 + i).Value = heads[i];
        ws.Cell(head, FfStart).Value = "Chart: bar start"; ws.Cell(head, FfLen).Value = "Chart: bar length";
        var hr = ws.Range(head, 1, head, FfBasis);
        hr.Style.Font.Bold = true; hr.Style.Fill.BackgroundColor = Band;
        ws.Range(head, FfStart, head, FfLen).Style.Font.FontColor = Muted;
        for (int c = FfLo; c <= FfHi; c++) ws.Cell(head, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        var labels = new List<string>(); var colors = new List<string>();
        var los = new List<double>(); var his = new List<double>();
        int r = first;

        // ---- DCF: the extremes of each 5×5 sensitivity grid; mid is the model's own centre cell.
        foreach (var d in dcf)
        {
            bool g = d.Method == "gordon";
            string grid = g ? "Sensitivity!$C$11:$G$15" : "Sensitivity!$C$20:$G$24", centre = g ? "Sensitivity!$E$13" : "Sensitivity!$E$22";
            var label = (g ? "DCF · perpetuity growth" : "DCF · exit multiple") + (d.InUse ? " (the model's method)" : "");
            ws.Cell(r, 1).Value = label;
            Calc(ws.Cell(r, FfLo), $"MIN({grid})", FmtPrice);
            Link(ws.Cell(r, FfMid), centre, FmtPrice);
            Calc(ws.Cell(r, FfHi), $"MAX({grid})", FmtPrice);
            Calc(ws.Cell(r, FfBasis), g
                ? "\"WACC ±\"&TEXT(2*Sensitivity!$B$5,\"0.0%\")&\" × terminal growth ±\"&TEXT(2*Sensitivity!$B$6,\"0.00%\")&\": the corners of the Sensitivity grid\""
                : "\"WACC ±\"&TEXT(2*Sensitivity!$B$5,\"0.0%\")&\" × exit EV / EBITDA ±\"&TEXT(2*Sensitivity!$B$7,\"0.0\")&\"x: the corners of the Sensitivity grid\"");
            labels.Add(label); colors.Add("2563EB");
            los.Add(d.Lo ?? double.NaN); his.Add(d.Hi ?? double.NaN);
            r++;
        }

        // ---- Peers: low / median / high multiple from the table below, applied to this company's metric.
        for (int j = 0; j < mults.Count; j++)
        {
            var m = mults[j]; int pc = 3 + j; string pcl = Col(pc), ar = $"{a0 + j}";
            string Price(string mult) => m.Equity ? $"{mult}*$B${ar}" : $"({mult}*$B${ar}-$C${ar})/$D${ar}";
            var label = $"Peers · {m.Label}";
            ws.Cell(r, 1).Value = label;
            Calc(ws.Cell(r, FfLo), $"IFERROR({Price($"{pcl}{sLo}")},\"\")", FmtPrice);
            Calc(ws.Cell(r, FfMid), $"IFERROR({Price($"{pcl}{sMed}")},\"\")", FmtPrice);
            Calc(ws.Cell(r, FfHi), $"IFERROR({Price($"{pcl}{sHi}")},\"\")", FmtPrice);
            var range = $"{pcl}{p0}:{pcl}{pEnd}";
            Calc(ws.Cell(r, FfBasis), $"\"{m.Label} \"&TEXT({pcl}{sLo},\"0.0\")&\"x–\"&TEXT({pcl}{sHi},\"0.0\")&\"x (median \"&TEXT({pcl}{sMed},\"0.0\")&\"x, \"&COUNT({range})&\" peers) on {b.Symbol}'s {Lc1(m.MetricLabel)}\"");
            labels.Add(label); colors.Add("16A34A");
            var vals = peers.Select(p => p.Multiples.TryGetValue(m.Key, out var v) ? v : null).OfType<double>().Where(v => v > 0).ToList();
            double ml = vals.Count >= 4 ? Quartile(vals, 0.25) : vals.DefaultIfEmpty(double.NaN).Min();
            double mh = vals.Count >= 4 ? Quartile(vals, 0.75) : vals.DefaultIfEmpty(double.NaN).Max();
            double P(double x) => m.Equity ? x * m.Metric : (x * m.Metric - m.NetDebt) / m.Shares;
            los.Add(P(ml)); his.Add(P(mh));
            r++;
        }

        // ---- Today's price, drawn as a thin marker.
        ws.Cell(r, 1).Value = "Current share price";
        for (int c = FfLo; c <= FfHi; c++) Link(ws.Cell(r, c), "DCF!$B$45", FmtPrice);
        ws.Cell(r, FfBasis).Value = "Last price, from the DCF sheet";
        ws.Range(r, 1, r, FfBasis).Style.Font.Bold = true;
        labels.Add("Current share price"); colors.Add("0F172A");
        los.Add(ff.Price ?? double.NaN); his.Add(ff.Price ?? double.NaN);

        // Bar start and length for the chart; the price marker is 0.6% of the widest value.
        for (int i = first; i <= last; i++)
        {
            Calc(ws.Cell(i, FfStart), $"IF(ISNUMBER(B{i}),B{i},NA())", FmtPrice);
            Calc(ws.Cell(i, FfLen), i == last ? $"MAX(D{i}-B{i},0.006*MAX($D${first}:$D${last}))" : $"IF(ISNUMBER(D{i}),D{i}-B{i},NA())", FmtPrice);
            ws.Range(i, FfStart, i, FfLen).Style.Font.FontColor = Muted;
            if (i < last) ws.Range(i, 1, i, FfBasis).Style.Border.BottomBorder = XLBorderStyleValues.Hair;
        }
        ws.Range(first, FfBasis, last, FfBasis).Style.Font.FontColor = Muted;

        // ---- Peer multiples
        if (mults.Count > 0)
        {
            Section(ws, pTop, "Peer multiples · the most relevant public comps (change or clear any figure)", 2 + mults.Count);
            ws.Cell(pHead, 1).Value = "Ticker"; ws.Cell(pHead, 2).Value = "Company";
            for (int j = 0; j < mults.Count; j++) { ws.Cell(pHead, 3 + j).Value = mults[j].Label; ws.Cell(pHead, 3 + j).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; }
            ws.Range(pHead, 1, pHead, 2 + mults.Count).Style.Font.Bold = true;
            ws.Range(pHead, 1, pHead, 2 + mults.Count).Style.Fill.BackgroundColor = Band;
            for (int i = 0; i < peers.Count; i++)
            {
                ws.Cell(p0 + i, 1).Value = peers[i].Symbol;
                ws.Cell(p0 + i, 2).Value = peers[i].Name;
                for (int j = 0; j < mults.Count; j++)
                {
                    var cell = ws.Cell(p0 + i, 3 + j);
                    if (peers[i].Multiples.TryGetValue(mults[j].Key, out var v) && v is > 0) Input(cell, v.Value, FmtMult);
                    else { cell.Value = "n/m"; cell.Style.Font.FontColor = Muted; cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; }
                }
            }
            (int Row, string Label)[] stats = { (sLo, "Low (25th percentile; lowest under 4 peers)"), (sMed, "Median"), (sHi, "High (75th percentile; highest under 4 peers)") };
            foreach (var (row, lab) in stats) ws.Cell(row, 1).Value = lab;
            ws.Cell(sOwn, 1).Value = $"{b.Symbol} today";
            for (int j = 0; j < mults.Count; j++)
            {
                string c = Col(3 + j), rg = $"{c}{p0}:{c}{pEnd}";
                Calc(ws.Cell(sLo, 3 + j), $"IF(COUNT({rg})>=4,QUARTILE({rg},1),MIN({rg}))", FmtMult);
                Calc(ws.Cell(sMed, 3 + j), $"MEDIAN({rg})", FmtMult, bold: true);
                Calc(ws.Cell(sHi, 3 + j), $"IF(COUNT({rg})>=4,QUARTILE({rg},3),MAX({rg}))", FmtMult);
                if (mults[j].Own is > 0 and var own) Input(ws.Cell(sOwn, 3 + j), own, FmtMult);
            }
            ws.Range(sLo, 1, sLo, 2 + mults.Count).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            ws.Range(sOwn, 1, sOwn, 2 + mults.Count).Style.Fill.BackgroundColor = Subject;

            Section(ws, aTop, $"Applied to {b.Symbol} ({ul} except per share)", 4);
            string[] ah = { "Multiple · metric", "Metric", "Net debt", "Diluted shares" };
            for (int i = 0; i < ah.Length; i++) ws.Cell(aHead, 1 + i).Value = ah[i];
            ws.Range(aHead, 2, aHead, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Range(aHead, 1, aHead, 4).Style.Font.Bold = true;
            ws.Range(aHead, 1, aHead, 4).Style.Fill.BackgroundColor = Band;
            var fmtM = AmtFormat(b.Units);
            for (int j = 0; j < mults.Count; j++)
            {
                var m = mults[j]; int row = a0 + j;
                ws.Cell(row, 1).Value = $"{m.Label} · {m.MetricLabel}{(m.Equity ? " per share" : "")}";
                if (m.Equity)
                {
                    Input(ws.Cell(row, 2), m.Metric, FmtPrice);
                    ws.Cell(row, 3).Value = "–"; ws.Cell(row, 4).Value = "–";
                    ws.Range(row, 3, row, 4).Style.Font.FontColor = Muted;
                    ws.Range(row, 3, row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                }
                else
                {
                    Input(ws.Cell(row, 2), m.Metric / M, fmtM);
                    Input(ws.Cell(row, 3), m.NetDebt / M, fmtM);
                    Input(ws.Cell(row, 4), m.Shares / M, fmtM);
                }
            }
            ws.Cell(a0 + mults.Count + 1, 1).Value = "The metric is backed out of the company's own multiple, so it's measured the same way, by the same source, on the same day as the peers'. EV multiples: (multiple × metric − net debt) ÷ shares. Per-share multiples: multiple × metric.";
            ws.Cell(a0 + mults.Count + 1, 1).Style.Font.FontColor = Muted;
            ws.Cell(a0 + mults.Count + 1, 1).Style.Font.Italic = true;
        }

        ws.Column(1).Width = 44; ws.Column(2).Width = 24;
        for (int c = 3; c <= 4; c++) ws.Column(c).Width = 13;
        ws.Column(5).Width = 74; ws.Column(6).Width = 3; ws.Column(FfStart).Width = 15; ws.Column(FfLen).Width = 15;

        // Cached values so the chart draws before Excel recalculates (and in previewers that never do).
        var starts = los.ToArray();
        var lengths = los.Zip(his, (lo, hi) => hi - lo).ToArray();
        var widest = his.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        lengths[^1] = Math.Max(lengths[^1], 0.006 * widest);
        var lowest = los.Where(double.IsFinite).DefaultIfEmpty(0).Min();
        double? axisMin = null;
        if (lowest > 0)
        {
            var step = Math.Pow(10, Math.Floor(Math.Log10(lowest)));
            axisMin = Math.Floor(lowest * 0.85 / step) * step;
        }
        return new FootballLayout(first, last, chartTop, chartBottom, labels.ToArray(), colors.ToArray(), starts, lengths, his.ToArray(), axisMin,
            $"{b.Symbol} · value per share{cur}");
    }

    // ------------------------------------------------------------------ the chart
    // ClosedXML can't draw charts, so the saved file gets one added with the Open XML SDK: a
    // stacked horizontal bar whose first series is invisible (the bar start) and whose second is
    // the range. Low values label the invisible bar's end, high values the range's (from the High
    // column, through Excel 2013's "value from cells"). All references point at the sheet, so the
    // bars move when the model does.

    private static string N(double v) => double.IsFinite(v) ? v.ToString("0.######", CultureInfo.InvariantCulture) : "";
    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    private static void AddFootballChart(string path, FootballLayout f)
    {
        // Every sheet gained a gutter column after writing, so columns are one to the right.
        string C(int col) => Col(col + 1);
        string sh = $"'{FootballSheet}'";
        string Ref(int col) => $"{sh}!${C(col)}${f.FirstRow}:${C(col)}${f.LastRow}";
        int n = f.Labels.Length;

        string NumCache(IEnumerable<double> vals, string fmt) =>
            $"<c:numCache><c:formatCode>{fmt}</c:formatCode><c:ptCount val=\"{n}\"/>" +
            string.Concat(vals.Select((v, i) => double.IsFinite(v) ? $"<c:pt idx=\"{i}\"><c:v>{N(v)}</c:v></c:pt>" : "")) + "</c:numCache>";
        string StrCache(IEnumerable<string> vals) =>
            $"<c:strCache><c:ptCount val=\"{n}\"/>" + string.Concat(vals.Select((v, i) => $"<c:pt idx=\"{i}\"><c:v>{X(v)}</c:v></c:pt>")) + "</c:strCache>";
        string Txt(int sz, string color, bool bold = false) =>
            $"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"{sz}\" b=\"{(bold ? 1 : 0)}\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>";
        const string NoFill = "<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>";
        const string Flags = "<c:showLegendKey val=\"0\"/><c:showVal val=\"{0}\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/>";
        string cat = $"<c:cat><c:strRef><c:f>{X(Ref(1))}</c:f>{StrCache(f.Labels)}</c:strRef></c:cat>";

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        xml.Append("<c:roundedCorners val=\"0\"/><c:chart>");
        xml.Append($"<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1300\" b=\"1\"/></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"1300\" b=\"1\"><a:solidFill><a:srgbClr val=\"0E141B\"/></a:solidFill></a:rPr><a:t>{X(f.Title)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>");
        xml.Append("<c:autoTitleDeleted val=\"0\"/><c:plotArea><c:layout/>");
        xml.Append("<c:barChart><c:barDir val=\"bar\"/><c:grouping val=\"stacked\"/><c:varyColors val=\"0\"/>");

        // Series 1: the invisible bar up to the low value, labelled with the low value.
        xml.Append($"<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:tx><c:v>Low</c:v></c:tx>{NoFill}<c:invertIfNegative val=\"0\"/>");
        xml.Append($"<c:dLbls><c:numFmt formatCode=\"#,##0.00\" sourceLinked=\"0\"/>{NoFill}{Txt(900, "475569")}<c:dLblPos val=\"inEnd\"/>{string.Format(Flags, 1)}</c:dLbls>");
        xml.Append($"{cat}<c:val><c:numRef><c:f>{X(Ref(FfStart))}</c:f>{NumCache(f.Starts, "#,##0.00")}</c:numRef></c:val></c:ser>");

        // Series 2: the range, coloured by method, labelled with the high value.
        xml.Append("<c:ser><c:idx val=\"1\"/><c:order val=\"1\"/><c:tx><c:v>Range</c:v></c:tx>");
        xml.Append("<c:spPr><a:solidFill><a:srgbClr val=\"2563EB\"/></a:solidFill></c:spPr><c:invertIfNegative val=\"0\"/>");
        for (int i = 0; i < n; i++)
            xml.Append($"<c:dPt><c:idx val=\"{i}\"/><c:invertIfNegative val=\"0\"/><c:bubble3D val=\"0\"/><c:spPr><a:solidFill><a:srgbClr val=\"{f.Colors[i]}\"/></a:solidFill></c:spPr></c:dPt>");
        xml.Append($"<c:dLbls><c:dLbl><c:idx val=\"{n - 1}\"/><c:delete val=\"1\"/></c:dLbl>");
        xml.Append($"<c:numFmt formatCode=\"#,##0.00\" sourceLinked=\"0\"/>{NoFill}{Txt(900, "FFFFFF", true)}<c:dLblPos val=\"inEnd\"/>{string.Format(Flags, 0)}");
        xml.Append("<c:extLst><c:ext uri=\"{CE6537A1-D6FC-4f65-9D91-7224C49458BB}\" xmlns:c15=\"http://schemas.microsoft.com/office/drawing/2012/chart\"><c15:showDataLabelsRange val=\"1\"/><c15:showLeaderLines val=\"0\"/></c:ext></c:extLst></c:dLbls>");
        xml.Append($"{cat}<c:val><c:numRef><c:f>{X(Ref(FfLen))}</c:f>{NumCache(f.Lengths, "#,##0.00")}</c:numRef></c:val>");
        xml.Append("<c:extLst><c:ext uri=\"{02D57815-91ED-43cb-92C2-25804820EDAC}\" xmlns:c15=\"http://schemas.microsoft.com/office/drawing/2012/chart\">");
        xml.Append($"<c15:datalabelsRange><c15:f>{X(Ref(FfHi))}</c15:f><c15:dlblRangeCache><c:ptCount val=\"{n}\"/>");
        xml.Append(string.Concat(f.Highs.Select((v, i) => double.IsFinite(v) ? $"<c:pt idx=\"{i}\"><c:v>{v.ToString("#,##0.00", CultureInfo.InvariantCulture)}</c:v></c:pt>" : "")));
        xml.Append("</c15:dlblRangeCache></c15:datalabelsRange></c:ext></c:extLst></c:ser>");

        xml.Append("<c:gapWidth val=\"55\"/><c:overlap val=\"100\"/><c:axId val=\"51001\"/><c:axId val=\"51002\"/></c:barChart>");
        // Categories top to bottom in sheet order; the value axis along the bottom.
        xml.Append("<c:catAx><c:axId val=\"51001\"/><c:scaling><c:orientation val=\"maxMin\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"l\"/>");
        xml.Append("<c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
        xml.Append($"<c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"BFC7D1\"/></a:solidFill></a:ln></c:spPr>{Txt(1000, "0E141B")}");
        xml.Append("<c:crossAx val=\"51002\"/><c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/></c:catAx>");
        xml.Append("<c:valAx><c:axId val=\"51002\"/><c:scaling><c:orientation val=\"minMax\"/>");
        if (f.AxisMin is { } mn) xml.Append($"<c:min val=\"{N(mn)}\"/>");
        xml.Append("</c:scaling><c:delete val=\"0\"/><c:axPos val=\"b\"/>");
        xml.Append("<c:majorGridlines><c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"E5E7EB\"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>");
        xml.Append("<c:numFmt formatCode=\"#,##0\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
        xml.Append($"<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>{Txt(900, "6B7280")}<c:crossAx val=\"51001\"/><c:crosses val=\"max\"/><c:crossBetween val=\"between\"/></c:valAx>");
        xml.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr></c:plotArea><c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart>");
        xml.Append("<c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"E3E8EF\"/></a:solidFill></a:ln></c:spPr>");
        xml.Append("</c:chartSpace>");

        using var doc = SpreadsheetDocument.Open(path, true);
        var wbPart = doc.WorkbookPart!;
        var sheet = wbPart.Workbook.Sheets!.Elements<S.Sheet>().FirstOrDefault(s => s.Name?.Value == FootballSheet);
        if (sheet?.Id?.Value is not { } sid) return;
        var wsPart = (WorksheetPart)wbPart.GetPartById(sid);
        var drawings = wsPart.DrawingsPart ?? wsPart.AddNewPart<DrawingsPart>();
        var chartPart = drawings.AddNewPart<ChartPart>();
        using (var s = chartPart.GetStream(FileMode.Create))
        using (var w = new StreamWriter(s, new UTF8Encoding(false))) w.Write(xml.ToString());
        var rid = drawings.GetIdOfPart(chartPart);

        // Anchored under the table, across the label to high columns (B–E once the gutter is in).
        var drawing = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            $"<xdr:twoCellAnchor editAs=\"oneCell\"><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{f.ChartTop - 1}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
            $"<xdr:to><xdr:col>{FfHi + 1}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{f.ChartBottom}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
            "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Football field\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
            "<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
            $"<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"{rid}\"/>" +
            "</a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";
        using (var s = drawings.GetStream(FileMode.Create))
        using (var w = new StreamWriter(s, new UTF8Encoding(false))) w.Write(drawing);

        var ws = wsPart.Worksheet;
        if (!ws.Elements<S.Drawing>().Any())
        {
            // <drawing> has to come before these in a worksheet's XML.
            var el = new S.Drawing { Id = wsPart.GetIdOfPart(drawings) };
            var after = ws.ChildElements.FirstOrDefault(e => e is S.LegacyDrawing or S.LegacyDrawingHeaderFooter or S.Picture
                or S.OleObjects or S.Controls or S.WebPublishItems or S.TableParts or S.WorksheetExtensionList);
            if (after != null) ws.InsertBefore(el, after); else ws.Append(el);
        }
        ws.Save();
    }
}
