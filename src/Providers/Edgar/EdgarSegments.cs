using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Funnel.Providers.J;

namespace Funnel.Providers.Edgar;

/// <summary>
/// Revenue by product or business segment, read from the XBRL of the latest annual report.
/// The SEC's company-facts API drops every dimensional fact, so this opens the filing itself: the
/// instance document holds revenue tagged with a product / segment axis for the year and the two
/// before it, and the label linkbase holds the names the company gives each member.
/// </summary>
public sealed partial class EdgarProvider : ISegmentSource
{
    /// <summary>Axes worth splitting revenue on, best first, with how to describe them.</summary>
    private static readonly (string Axis, string Basis)[] SegmentAxes =
    {
        ("ProductOrServiceAxis", "products and services"),          // US GAAP (srt:)
        ("ProductsAndServicesAxis", "products and services"),       // IFRS
        ("StatementBusinessSegmentsAxis", "business segments"),     // US GAAP (us-gaap:)
        ("SegmentsAxis", "business segments"),                      // IFRS
    };

    private static readonly string[] RevenueTags =
    {
        "RevenueFromContractWithCustomerExcludingAssessedTax", "Revenues", "RevenueFromContractWithCustomerIncludingAssessedTax",
        "SalesRevenueNet", "Revenue", "RevenueFromContractsWithCustomers",
    };

    /// <summary>More streams than this and the smallest are pooled, so the model stays readable.</summary>
    private const int MaxStreams = 8;

    private Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        var email = _contact().Trim();
        if (!email.Contains('@'))
            throw new ProviderException("SEC EDGAR needs a contact email for revenue streams. Add yours under Settings → Data sources.");
        return _http.GetTextAsync(url, Day, ct, req =>
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd($"Funnel/{Bridge.AppVersion}");
            req.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue($"({email})"));
        });
    }

    public async Task<RevenueSegments> RevenueSegmentsAsync(string symbol, CancellationToken ct)
    {
        var (me, _, rows) = await RecentAsync(symbol, ct);
        var annual = rows.FirstOrDefault(x => x.Form is "10-K" or "20-F" or "40-F")
            ?? throw new ProviderException($"SEC EDGAR has no recent annual report for {me.Ticker}.");
        var folder = annual.Url[..annual.Url.LastIndexOf('/')];
        var index = await GetAsync($"{folder}/index.json", Day, ct);
        var files = Arr(index["directory"]?["item"]).Select(i => Str(i?["name"]) ?? "").ToList();
        // Inline-XBRL filings ship an extracted instance (*_htm.xml); older ones a plain ticker-date.xml.
        var instance = files.FirstOrDefault(f => f.EndsWith("_htm.xml", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => Regex.IsMatch(f, @"^[a-z0-9]+-\d{8}\.xml$", RegexOptions.IgnoreCase))
            ?? throw new ProviderException($"The {annual.Form} for {me.Ticker} has no machine-readable XBRL.");
        // Some filers (Microsoft) embed the linkbases in the schema instead of shipping a _lab.xml.
        var labels = files.FirstOrDefault(f => f.EndsWith("_lab.xml", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase));

        var xml = await GetTextAsync($"{folder}/{instance}", ct);
        Dictionary<string, string> names = new();
        if (labels != null)
        {
            try { names = ReadLabels(await GetTextAsync($"{folder}/{labels}", ct)); }
            catch (Exception ex) when (ex is ProviderException or System.Xml.XmlException) { /* fall back to the tag names */ }
        }

        var year = annual.Period is { Length: >= 4 } p ? p[..4] : annual.Filed.Year.ToString(CultureInfo.InvariantCulture);
        return Pick(XDocument.Parse(xml), names)
            is { } seg ? new RevenueSegments { Periods = seg.Periods, Segments = seg.Segments, Basis = seg.Basis, Form = $"FY{year} {annual.Form}", Source = Name }
            : throw new NotSupportedException($"{me.Ticker}'s {annual.Form} doesn't break revenue into products or segments.");
    }

    private sealed record Ctx(DateOnly? Start, DateOnly End, List<(string Axis, string Member)> Members, bool Typed);

    /// <summary>
    /// Finds the axis whose members add up to total revenue, and returns their annual history.
    /// Companies often tag roll-ups on the same axis as the detail (Apple's "Products" next to
    /// iPhone, Mac and iPad), so up to two members may be dropped to make the sum reconcile.
    /// </summary>
    private static (List<string> Periods, List<RevenueSegment> Segments, string Basis)? Pick(XDocument doc, Dictionary<string, string> labels)
    {
        var root = doc.Root!;
        var contexts = new Dictionary<string, Ctx>();
        foreach (var c in root.Elements().Where(e => e.Name.LocalName == "context"))
        {
            var id = (string?)c.Attribute("id");
            var period = c.Elements().FirstOrDefault(e => e.Name.LocalName == "period");
            var end = period?.Elements().FirstOrDefault(e => e.Name.LocalName == "endDate")?.Value;
            var start = period?.Elements().FirstOrDefault(e => e.Name.LocalName == "startDate")?.Value;
            if (id == null || !DateOnly.TryParse(end, CultureInfo.InvariantCulture, out var e1)) continue;
            DateOnly? s1 = DateOnly.TryParse(start, CultureInfo.InvariantCulture, out var sd) ? sd : null;
            var members = c.Descendants().Where(e => e.Name.LocalName == "explicitMember")
                .Select(e => (Axis: LocalPart((string?)e.Attribute("dimension") ?? ""), Member: e.Value.Trim())).ToList();
            contexts[id] = new Ctx(s1, e1, members, c.Descendants().Any(e => e.Name.LocalName == "typedMember"));
        }

        // tag → end → total, and (tag, axis) → member → end → value; annual periods only.
        var totals = new Dictionary<string, Dictionary<DateOnly, double>>();
        var split = new Dictionary<(string Tag, string Axis), Dictionary<string, Dictionary<DateOnly, double>>>();
        foreach (var f in root.Elements())
        {
            var tag = f.Name.LocalName;
            var ns = f.Name.NamespaceName;
            if (!RevenueTags.Contains(tag) || !(ns.Contains("us-gaap") || ns.Contains("ifrs"))) continue;
            if ((string?)f.Attribute("contextRef") is not { } cref || !contexts.TryGetValue(cref, out var ctx)) continue;
            if (ctx.Start is not { } st || (ctx.End.DayNumber - st.DayNumber) is < 350 or > 380 || ctx.Typed) continue;
            if (!double.TryParse(f.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            if (ctx.Members.Count == 0) (totals.TryGetValue(tag, out var t) ? t : totals[tag] = new())[ctx.End] = v;
            else if (ctx.Members.Count == 1)
            {
                var key = (tag, ctx.Members[0].Axis);
                var byMember = split.TryGetValue(key, out var bm) ? bm : split[key] = new();
                (byMember.TryGetValue(ctx.Members[0].Member, out var bd) ? bd : byMember[ctx.Members[0].Member] = new())[ctx.End] = v;
            }
        }

        static bool Near(double a, double b) => b > 0 && Math.Abs(a / b - 1) < 0.015;
        foreach (var (axis, basis) in SegmentAxes)
            foreach (var tag in RevenueTags)
            {
                if (!totals.TryGetValue(tag, out var tot) || !split.TryGetValue((tag, axis), out var members)) continue;
                var latest = tot.Keys.Max();
                var candidates = members.Where(m => m.Value.ContainsKey(latest)).Select(m => m.Key).ToList();
                if (candidates.Count < 2 || candidates.Count > 16) continue;

                // Fewest members dropped first; the detail is what we're after.
                List<string>? chosen = null;
                for (int drop = 0; drop <= 2 && chosen == null; drop++)
                    foreach (var skip in Combinations(candidates.Count, drop))
                    {
                        var keep = candidates.Where((_, i) => !skip.Contains(i)).ToList();
                        if (keep.Count >= 2 && Near(keep.Sum(m => members[m][latest]), tot[latest])) { chosen = keep; break; }
                    }
                if (chosen == null) continue;

                // Years where every chosen member is reported and they still add up.
                var years = tot.Keys.Where(y => chosen.All(m => members[m].ContainsKey(y)) && Near(chosen.Sum(m => members[m][y]), tot[y]))
                    .OrderBy(y => y).ToList();
                var segs = chosen.Select(m => new RevenueSegment(Label(m, labels), years.Select(y => (double?)members[m][y]).ToList()))
                    .OrderByDescending(s => s.Values[^1]).ToList();
                return (years.Select(y => y.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(), Pool(segs, years.Count), basis);
            }
        return null;
    }

    /// <summary>Keeps the biggest streams and pools the tail (and anything not positive) as "All other".</summary>
    private static List<RevenueSegment> Pool(List<RevenueSegment> segs, int years)
    {
        var keep = segs.Where(s => s.Values[^1] > 0).Take(segs.Count > MaxStreams ? MaxStreams - 1 : MaxStreams).ToList();
        var rest = segs.Except(keep).ToList();
        if (rest.Count == 0) return keep;
        keep.Add(new RevenueSegment("All other", Enumerable.Range(0, years).Select(i => (double?)rest.Sum(s => s.Values[i] ?? 0)).ToList()));
        return keep;
    }

    private static IEnumerable<HashSet<int>> Combinations(int n, int k)
    {
        if (k == 0) { yield return new HashSet<int>(); yield break; }
        for (int i = 0; i < n; i++)
            if (k == 1) yield return new HashSet<int> { i };
            else for (int j = i + 1; j < n; j++) yield return new HashSet<int> { i, j };
    }

    private static string LocalPart(string qname) => qname[(qname.IndexOf(':') + 1)..];

    /// <summary>The company's own name for a member ("iPhone"), else one made from the tag ("Wearables Home And Accessories").</summary>
    private static string Label(string member, Dictionary<string, string> labels)
    {
        if (labels.TryGetValue(member.Replace(':', '_'), out var l)) return l;
        var local = LocalPart(member);
        if (local.EndsWith("Member")) local = local[..^6];
        return Regex.Replace(local, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
    }

    /// <summary>Concept id (prefix_Name) → its terse label, else its standard label, without the "[Member]" suffix.</summary>
    private static Dictionary<string, string> ReadLabels(string xml)
    {
        var doc = XDocument.Parse(xml);
        XNamespace xl = "http://www.w3.org/1999/xlink";
        var locs = new Dictionary<string, string>();
        var texts = new Dictionary<string, List<(string Role, string Text)>>();
        var arcs = new List<(string From, string To)>();
        foreach (var e in doc.Descendants())
        {
            var label = (string?)e.Attribute(xl + "label");
            switch (e.Name.LocalName)
            {
                case "loc" when label != null && (string?)e.Attribute(xl + "href") is { } href:
                    locs[label] = href[(href.IndexOf('#') + 1)..];
                    break;
                case "label" when label != null:
                    (texts.TryGetValue(label, out var list) ? list : texts[label] = new()).Add(((string?)e.Attribute(xl + "role") ?? "", e.Value.Trim()));
                    break;
                case "labelArc" when (string?)e.Attribute(xl + "from") is { } from && (string?)e.Attribute(xl + "to") is { } to:
                    arcs.Add((from, to));
                    break;
            }
        }
        var result = new Dictionary<string, string>();
        foreach (var (from, to) in arcs)
        {
            if (!locs.TryGetValue(from, out var concept) || !texts.TryGetValue(to, out var options)) continue;
            var best = options.FirstOrDefault(o => o.Role.EndsWith("/terseLabel")).Text ?? options.FirstOrDefault(o => o.Role.EndsWith("/label")).Text;
            if (string.IsNullOrWhiteSpace(best)) continue;
            best = Regex.Replace(best, @"\s*\[(Member|Domain)\]\s*$", "", RegexOptions.IgnoreCase);
            if (!result.ContainsKey(concept) || options.Any(o => o.Role.EndsWith("/terseLabel"))) result[concept] = best;
        }
        return result;
    }
}
