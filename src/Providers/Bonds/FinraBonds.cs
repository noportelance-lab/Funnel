using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using static Funnel.Providers.J;

namespace Funnel.Providers.Bonds;

/// <summary>One US corporate bond as FINRA's TRACE last reported it.</summary>
public sealed record FinraBond
{
    public string Cusip { get; init; } = "";
    public string? Symbol { get; init; }
    public string Issuer { get; init; } = "";
    /// <summary>Percent, e.g. 4.85.</summary>
    public double? Coupon { get; init; }
    /// <summary>FXPV fixed, FRN floating, ZERO zero coupon…</summary>
    public string? CouponType { get; init; }
    public string? Maturity { get; init; }
    /// <summary>Percent of par.</summary>
    public double? Price { get; init; }
    /// <summary>Percent, as TRACE reports it for the last trade.</summary>
    public double? Yield { get; init; }
    public string? LastTrade { get; init; }
    public double? PriceChangePct { get; init; }
    public string? Moodys { get; init; }
    public string? SandP { get; init; }
    /// <summary>I investment grade, H high yield.</summary>
    public string? Grade { get; init; }
    public string? SubType { get; init; }
}

/// <summary>
/// Corporate bonds and their last trades from FINRA's TRACE, through the same public endpoint the
/// bond pages on finra.org use (no key, but it wants the XSRF cookie its own pages set). It has
/// prices, yields, coupons, maturities and ratings, but not the amount outstanding.
/// FINRA's official API needs an account, so this is the keyless route; if FINRA changes the page,
/// the Debt tab says so and the rest of Funnel carries on.
/// </summary>
public sealed class FinraBondSource
{
    private const string Base = "https://services-dynarep.ddwa.finra.org";
    private const string Dataset = Base + "/public/reporting/v2/data/group/FixedIncomeMarket/name/CorporateAndAgencySecurities";
    private static readonly string[] Fields =
    {
        "cusip", "issueSymbolIdentifier", "issuerName", "couponRate", "couponType", "maturityDate", "lastSalePrice",
        "lastSaleYield", "lastTradeDate", "priceChangePercent", "moodysRating", "standardAndPoorsRating", "traceGradeCode", "productSubTypeCode",
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, JsonArray Rows)> _cache = new();
    private string? _token;

    public FinraBondSource()
    {
        var handler = new HttpClientHandler { CookieContainer = _cookies, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Funnel");
        _http.DefaultRequestHeaders.Add("Origin", "https://www.finra.org");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.finra.org/");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private async Task<string> TokenAsync(CancellationToken ct, bool fresh = false)
    {
        if (_token != null && !fresh) return _token;
        // Any request to the service sets the cookie, even one that 404s.
        try { using var _ = await _http.GetAsync($"{Base}/public/reporting/v2/group/FixedIncomeMarket/name/BondSearch", ct); }
        catch (HttpRequestException) { throw new ProviderException("Couldn't reach FINRA's bond data. Check the internet connection."); }
        _token = _cookies.GetCookies(new Uri(Base)).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value
            ?? throw new ProviderException("FINRA's bond data didn't hand out an access token; finra.org may have changed.");
        return _token;
    }

    private async Task<(JsonArray Rows, int Total)> QueryAsync(JsonObject body, CancellationToken ct)
    {
        var key = body.ToJsonString();
        if (_cache.TryGetValue(key, out var hit) && HttpSource.Fresh(hit.At, TimeSpan.FromMinutes(15))) return (hit.Rows, hit.Rows.Count);
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var token = await TokenAsync(ct, fresh: attempt > 0);
                using var req = new HttpRequestMessage(HttpMethod.Post, Dataset) { Content = new StringContent(key, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-XSRF-TOKEN", token);
                HttpResponseMessage res;
                try { res = await _http.SendAsync(req, ct); }
                catch (HttpRequestException) { throw new ProviderException("Couldn't reach FINRA's bond data. Check the internet connection."); }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("FINRA's bond data took too long to answer."); }
                using (res)
                {
                    if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { _token = null; continue; }
                    var text = await res.Content.ReadAsStringAsync(ct);
                    JsonNode? root;
                    try { root = JsonNode.Parse(text); } catch { throw new ProviderException("FINRA's bond data sent back something unexpected."); }
                    if (Str(root?["status"]) != "success")
                        throw new ProviderException($"FINRA's bond data refused the request ({Str(root?["statusMessage"]) ?? res.StatusCode.ToString()}).");
                    var rb = root!["returnBody"];
                    var rows = JsonNode.Parse(Str(rb?["data"]) ?? "[]") as JsonArray ?? new JsonArray();
                    var total = int.TryParse(Str(rb?["headers"]?["Record-Total"]?[0]), out var t) ? t : rows.Count;
                    _cache[key] = (DateTime.UtcNow, rows);
                    return (rows, total);
                }
            }
            throw new ProviderException("FINRA's bond data wouldn't accept the access token.");
        }
        finally { _gate.Release(); }
    }

    private static JsonObject Body(JsonArray fields, string field, string value, string compare, int offset = 0) => new()
    {
        ["fields"] = fields,
        ["compareFilters"] = new JsonArray(new JsonObject { ["fieldName"] = field, ["fieldValue"] = value, ["compareType"] = compare }),
        ["offset"] = offset,
        ["limit"] = 5000,
    };

    /// <summary>Issuer names starting with <paramref name="prefix"/> (FINRA spells them its own way: "AMAZON COM INC") and how many bonds each has.</summary>
    public async Task<List<(string Name, int Bonds)>> IssuersAsync(string prefix, CancellationToken ct)
    {
        var (rows, _) = await QueryAsync(Body(new JsonArray("issuerName"), "issuerName", prefix.ToUpperInvariant(), "BEGINS_WITH"), ct);
        return rows.Select(r => Str(r?["issuerName"])).OfType<string>().GroupBy(n => n)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
    }

    public async Task<List<FinraBond>> BondsAsync(string issuer, CancellationToken ct)
    {
        var list = new List<FinraBond>();
        for (int offset = 0; offset < 20000; offset += 5000)
        {
            var (rows, total) = await QueryAsync(Body(new JsonArray(Fields.Select(f => (JsonNode)f).ToArray()), "issuerName", issuer, "EQUAL", offset), ct);
            list.AddRange(rows.Select(Parse));
            if (rows.Count < 5000 || offset + rows.Count >= total) break;
        }
        return list;
    }

    private static double? Dbl(JsonNode? n) => double.TryParse(Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static FinraBond Parse(JsonNode? r) => new()
    {
        Cusip = Str(r?["cusip"]) ?? "",
        Symbol = Str(r?["issueSymbolIdentifier"]),
        Issuer = Str(r?["issuerName"]) ?? "",
        Coupon = Dbl(r?["couponRate"]),
        CouponType = Str(r?["couponType"]),
        Maturity = Str(r?["maturityDate"]),
        Price = Dbl(r?["lastSalePrice"]),
        Yield = Dbl(r?["lastSaleYield"]),
        LastTrade = Str(r?["lastTradeDate"]),
        PriceChangePct = Dbl(r?["priceChangePercent"]),
        Moodys = Str(r?["moodysRating"]),
        SandP = Str(r?["standardAndPoorsRating"]),
        Grade = Str(r?["traceGradeCode"]),
        SubType = Str(r?["productSubTypeCode"]),
    };
}
