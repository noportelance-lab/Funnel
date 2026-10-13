using System.Globalization;
using static Funnel.Providers.J;

namespace Funnel.Providers.Macro;

/// <summary>Bank of Canada Valet API: CAD exchange rates, the policy rate and GoC bond yields. No key.</summary>
public sealed class BocProvider : IMacroSource
{
    public string Id => "boc";
    public string Name => "Bank of Canada";
    private const string Group = "Canada · Bank of Canada";

    private readonly HttpSource _http = new("Bank of Canada", "Funnel");

    private static readonly (string Series, string Label, string Unit)[] Series =
    {
        ("FXUSDCAD", "USD / CAD", "fx"),
        ("FXEURCAD", "EUR / CAD", "fx"),
        ("V39079", "BoC policy rate", "pct"),
        ("BD.CDN.2YR.DQ.YLD", "GoC 2Y yield", "pct"),
        ("BD.CDN.5YR.DQ.YLD", "GoC 5Y yield", "pct"),
        ("BD.CDN.10YR.DQ.YLD", "GoC 10Y yield", "pct"),
    };

    public async Task<IReadOnlyList<MarketQuote>> BoardAsync(CancellationToken ct)
    {
        var url = $"https://www.bankofcanada.ca/valet/observations/{string.Join(",", Series.Select(s => s.Series))}/json?recent=10";
        var root = await _http.GetJsonAsync(url, TimeSpan.FromMinutes(30), ct);
        // Observations come newest-first; a series can be blank on a given day, so walk back to
        // its two most recent prints.
        var obs = Arr(root["observations"]).OrderByDescending(o => Str(o?["d"]), StringComparer.Ordinal).ToList();
        return Series.Select(s =>
        {
            var pts = obs.Select(o => (Date: Str(o?["d"]), V: Num(o?[s.Series]?["v"]))).Where(p => p.V != null).Take(2).ToList();
            return Quote(s.Series, s.Label, s.Unit, pts.Count > 0 ? pts[0].V : null, pts.Count > 1 ? pts[1].V : null, pts.FirstOrDefault().Date, Group);
        }).ToList();
    }

    internal static MarketQuote Quote(string sym, string label, string unit, double? last, double? prev, string? date, string group)
    {
        double? chg = last != null && prev != null ? last - prev : null;
        double? pct = unit == "fx" && chg != null && prev is > 0 ? chg / prev * 100 : null;
        return new MarketQuote(sym, label, last, chg, pct, unit == "fx" ? "CAD" : null, group, unit, date);
    }
}

/// <summary>
/// US Treasury daily par yield curve (the series FRED's DGS* rates are copied from), straight from
/// home.treasury.gov. No key. Used for the Markets board and the DCF's risk-free rate.
/// FRED's own download site stalls Windows HTTP clients, so it isn't used directly.
/// </summary>
public sealed class TreasuryProvider : IMacroSource
{
    public string Id => "treasury";
    public string Name => "US Treasury";
    private const string Group = "US Treasury yields";

    private readonly HttpSource _http = new("US Treasury", "Funnel", timeout: TimeSpan.FromSeconds(15));

    private static readonly (string Field, string Label)[] Tenors =
    {
        ("BC_1MONTH", "UST 1M"), ("BC_3MONTH", "UST 3M"), ("BC_2YEAR", "UST 2Y"),
        ("BC_5YEAR", "UST 5Y"), ("BC_10YEAR", "UST 10Y"), ("BC_30YEAR", "UST 30Y"),
    };

    public async Task<IReadOnlyList<MarketQuote>> BoardAsync(CancellationToken ct)
    {
        var days = await CurveAsync(ct);
        if (days.Count == 0) throw new ProviderException("The US Treasury didn't return any yields.");
        var last = days[^1];
        var prev = days.Count > 1 ? days[^2] : null;
        var quotes = Tenors.Select(t => BocProvider.Quote(t.Field, t.Label, "pct",
            last.Rates.GetValueOrDefault(t.Field), prev?.Rates.GetValueOrDefault(t.Field), last.Date, Group)).ToList();
        double? Spread(Day? d) => d?.Rates.GetValueOrDefault("BC_10YEAR") - d?.Rates.GetValueOrDefault("BC_2YEAR");
        quotes.Add(BocProvider.Quote("10Y2Y", "10Y – 2Y spread", "pct", Spread(last), Spread(prev), last.Date, Group));
        return quotes;
    }

    /// <summary>The latest 10-year par yield, e.g. ("2026-10-07", 5.28) in percent.</summary>
    public async Task<(string Date, double Value)?> TenYearAsync(CancellationToken ct)
    {
        var days = await CurveAsync(ct);
        var d = days.LastOrDefault(x => x.Rates.ContainsKey("BC_10YEAR"));
        return d == null ? null : (d.Date, d.Rates["BC_10YEAR"]!.Value);
    }

    private static readonly (string Field, double Years)[] CurveTenors =
    {
        ("BC_1MONTH", 1 / 12.0), ("BC_2MONTH", 2 / 12.0), ("BC_3MONTH", 0.25), ("BC_4MONTH", 4 / 12.0), ("BC_6MONTH", 0.5),
        ("BC_1YEAR", 1), ("BC_2YEAR", 2), ("BC_3YEAR", 3), ("BC_5YEAR", 5), ("BC_7YEAR", 7), ("BC_10YEAR", 10), ("BC_20YEAR", 20), ("BC_30YEAR", 30),
    };

    /// <summary>The latest full par curve as (years, percent) points, for spreads over Treasuries.</summary>
    public async Task<(string Date, List<double[]> Points)?> CurvePointsAsync(CancellationToken ct)
    {
        var days = await CurveAsync(ct);
        var d = days.LastOrDefault(x => x.Rates.ContainsKey("BC_10YEAR"));
        if (d == null) return null;
        var pts = CurveTenors.Where(t => d.Rates.GetValueOrDefault(t.Field) != null).Select(t => new[] { t.Years, d.Rates[t.Field]!.Value }).ToList();
        return (d.Date, pts);
    }

    private sealed record Day(string Date, Dictionary<string, double?> Rates);

    /// <summary>This month's and last month's curves, oldest → newest (early in a month, yesterday may be last month).</summary>
    private async Task<List<Day>> CurveAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var months = new[] { now.AddMonths(-1), now };
        var days = new List<Day>();
        foreach (var m in months)
        {
            var xml = await _http.GetTextAsync(
                $"https://home.treasury.gov/resource-center/data-chart-center/interest-rates/pages/xml?data=daily_treasury_yield_curve&field_tdr_date_value_month={m:yyyyMM}",
                TimeSpan.FromHours(1), ct);
            System.Xml.Linq.XDocument doc;
            try { doc = System.Xml.Linq.XDocument.Parse(xml); }
            catch { throw new ProviderException("The US Treasury sent back something that isn't XML."); }
            System.Xml.Linq.XNamespace d = "http://schemas.microsoft.com/ado/2007/08/dataservices";
            foreach (var props in doc.Descendants().Where(e => e.Name.LocalName == "properties"))
            {
                var date = props.Element(d + "NEW_DATE")?.Value;
                if (string.IsNullOrEmpty(date)) continue;
                var rates = new Dictionary<string, double?>();
                foreach (var e in props.Elements())
                    if (e.Name.LocalName.StartsWith("BC_") && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        rates[e.Name.LocalName] = v;
                days.Add(new Day(date[..10], rates));
            }
        }
        return days.OrderBy(x => x.Date, StringComparer.Ordinal).ToList();
    }
}
