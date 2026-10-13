using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Funnel.Providers.J;

namespace Funnel.Providers.Edgar;

/// <summary>One note from the 10-K's debt schedule: what the company owes on it, its coupon and when it's due.</summary>
public sealed record DebtInstrument(string Name, double Face, double? Rate, int? MaturityYear, int? MaturityMonth);

/// <summary>
/// The debt schedule in the latest annual report, note by note. Like revenue streams, it sits on a
/// dimension (DebtInstrumentAxis) that the company-facts API drops, so it's read from the filing's
/// own XBRL. Companies tag it unevenly: Oracle tags every note with its face amount, rate and
/// maturity; Apple tags none. What's there is used to weight the bonds trading in the market.
/// </summary>
public sealed partial class EdgarProvider
{
    private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(m => m.Length > 0).ToArray();

    public async Task<(List<DebtInstrument> Notes, string Form)> DebtInstrumentsAsync(string symbol, CancellationToken ct)
    {
        var (me, _, rows) = await RecentAsync(symbol, ct);
        var annual = rows.FirstOrDefault(x => x.Form is "10-K" or "20-F" or "40-F")
            ?? throw new ProviderException($"SEC EDGAR has no recent annual report for {me.Ticker}.");
        var folder = annual.Url[..annual.Url.LastIndexOf('/')];
        var index = await GetAsync($"{folder}/index.json", Day, ct);
        var files = Arr(index["directory"]?["item"]).Select(i => Str(i?["name"]) ?? "").ToList();
        var instance = files.FirstOrDefault(f => f.EndsWith("_htm.xml", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => Regex.IsMatch(f, @"^[a-z0-9]+-\d{8}\.xml$", RegexOptions.IgnoreCase))
            ?? throw new ProviderException($"The {annual.Form} for {me.Ticker} has no machine-readable XBRL.");
        var labels = files.FirstOrDefault(f => f.EndsWith("_lab.xml", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase));

        var xml = await GetTextAsync($"{folder}/{instance}", ct);
        Dictionary<string, string> names = new();
        if (labels != null)
        {
            try { names = ReadLabels(await GetTextAsync($"{folder}/{labels}", ct)); }
            catch (Exception ex) when (ex is ProviderException or System.Xml.XmlException) { }
        }
        var year = annual.Period is { Length: >= 4 } p ? p[..4] : annual.Filed.Year.ToString(CultureInfo.InvariantCulture);
        return (ReadDebt(XDocument.Parse(xml), names), $"FY{year} {annual.Form}");
    }

    private static List<DebtInstrument> ReadDebt(XDocument doc, Dictionary<string, string> labels)
    {
        var root = doc.Root!;
        // context id → (end date, the debt instrument member, how many other axes)
        var contexts = new Dictionary<string, (DateOnly End, string? Member, int Others)>();
        foreach (var c in root.Elements().Where(e => e.Name.LocalName == "context"))
        {
            var id = (string?)c.Attribute("id");
            var period = c.Elements().FirstOrDefault(e => e.Name.LocalName == "period");
            var end = period?.Elements().FirstOrDefault(e => e.Name.LocalName is "endDate" or "instant")?.Value;
            if (id == null || !DateOnly.TryParse(end, CultureInfo.InvariantCulture, out var e1)) continue;
            var members = c.Descendants().Where(e => e.Name.LocalName == "explicitMember")
                .Select(e => (Axis: LocalPart((string?)e.Attribute("dimension") ?? ""), Member: e.Value.Trim())).ToList();
            var debt = members.FirstOrDefault(m => m.Axis == "DebtInstrumentAxis").Member;
            contexts[id] = (e1, debt, members.Count(m => m.Axis != "DebtInstrumentAxis"));
        }

        // member → tag → (end, others, value); the fewest extra axes wins, then the latest date.
        var facts = new Dictionary<string, Dictionary<string, (DateOnly End, int Others, string Value)>>();
        string[] tags = { "DebtInstrumentFaceAmount", "DebtInstrumentInterestRateStatedPercentage", "DebtInstrumentMaturityDate", "DebtInstrumentCarryingAmount", "LongTermDebt" };
        foreach (var f in root.Elements())
        {
            if (!tags.Contains(f.Name.LocalName) || !f.Name.NamespaceName.Contains("us-gaap")) continue;
            if ((string?)f.Attribute("contextRef") is not { } cref || !contexts.TryGetValue(cref, out var ctx) || ctx.Member == null) continue;
            var byTag = facts.TryGetValue(ctx.Member, out var bt) ? bt : facts[ctx.Member] = new();
            var v = (ctx.End, ctx.Others, f.Value.Trim());
            if (!byTag.TryGetValue(f.Name.LocalName, out var old) || ctx.Others < old.Others || (ctx.Others == old.Others && ctx.End > old.End))
                byTag[f.Name.LocalName] = v;
        }

        static double? D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        var latest = facts.Values.SelectMany(t => t.Values).Select(x => x.End).DefaultIfEmpty().Max();
        var list = new List<DebtInstrument>();
        foreach (var (member, byTag) in facts)
        {
            // What's owed today: the face amount, else the carrying amount, at the latest balance sheet.
            (DateOnly End, int Others, string Value)? amt = byTag.TryGetValue("DebtInstrumentFaceAmount", out var fa) ? fa
                : byTag.TryGetValue("DebtInstrumentCarryingAmount", out var ca) ? ca
                : byTag.TryGetValue("LongTermDebt", out var lt) ? lt : null;
            if (amt is not { } a || a.End < latest.AddDays(-400) || D(a.Value) is not { } face || face <= 0) continue;
            var rate = byTag.TryGetValue("DebtInstrumentInterestRateStatedPercentage", out var r) ? D(r.Value) : null;
            var name = Label(member, labels);
            int? my = null, mm = null;
            if (byTag.TryGetValue("DebtInstrumentMaturityDate", out var md) && DateOnly.TryParse(md.Value, CultureInfo.InvariantCulture, out var mdate))
                (my, mm) = (mdate.Year, mdate.Month);
            else (my, mm) = MaturityFromName(member + " " + name);
            list.Add(new DebtInstrument(name, face, rate, my, mm));
        }
        return list;
    }

    /// <summary>"FixedRateSeniorNotesDueJuly2025Member" → (2025, 7); "DebenturesDueNovember152025" → (2025, 11).</summary>
    private static (int? Year, int? Month) MaturityFromName(string s)
    {
        var m = Regex.Match(s, @"(?:Due|Maturing|Matures)\s*(?:(?<mon>[A-Z][a-z]+)\s*(?:\d{1,2},?\s*)?)?(?<y>20\d\d|19\d\d)");
        if (!m.Success) return (null, null);
        int y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        var mon = m.Groups["mon"].Value;
        int idx = Array.FindIndex(Months, x => x.Equals(mon, StringComparison.OrdinalIgnoreCase) || (mon.Length >= 3 && x.StartsWith(mon, StringComparison.OrdinalIgnoreCase)));
        return (y, idx >= 0 ? idx + 1 : null);
    }
}
