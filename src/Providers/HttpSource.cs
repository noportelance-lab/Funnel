using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Funnel.Providers;

/// <summary>
/// Shared HTTP plumbing for the keyed and keyless JSON/CSV sources (EDGAR, FRED, Bank of Canada,
/// Finnhub, FMP). Same habits as the Yahoo session: a short in-memory cache per URL, backoff on
/// 429, and errors phrased for the user. Yahoo keeps its own session because of the crumb dance.
/// </summary>
internal sealed class HttpSource
{
    private readonly HttpClient _http;
    private readonly string _name;
    private readonly TimeSpan _minGap;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, object Body)> _cache = new();
    private DateTime _last = DateTime.MinValue;

    /// <summary>
    /// Lets a source turn a non-success answer into its own message (e.g. "not in the free plan").
    /// Return null to fall through to the generic handling.
    /// </summary>
    public Func<HttpStatusCode, string, Exception?>? Classify { get; init; }

    /// <param name="minGap">Minimum spacing between requests — EDGAR asks for at most 10 per second.</param>
    public HttpSource(string name, string userAgent, TimeSpan? minGap = null, TimeSpan? timeout = null)
    {
        _name = name;
        _minGap = minGap ?? TimeSpan.Zero;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/csv,text/plain,*/*");
    }

    /// <summary>
    /// A cached answer is reused while it's younger than <paramref name="cacheFor"/> and was fetched
    /// today (local time), so every number is refreshed at least once a day.
    /// </summary>
    internal static bool Fresh(DateTime atUtc, TimeSpan cacheFor) =>
        DateTime.UtcNow - atUtc < cacheFor && atUtc.ToLocalTime().Date == DateTime.Today;

    public async Task<JsonNode> GetJsonAsync(string url, TimeSpan cacheFor, CancellationToken ct, Action<HttpRequestMessage>? prepare = null)
    {
        if (cacheFor > TimeSpan.Zero && _cache.TryGetValue(url, out var hit) && Fresh(hit.At, cacheFor) && hit.Body is JsonNode n0)
            return n0;
        var body = await GetBodyAsync(url, ct, prepare);
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { throw new ProviderException($"{_name} sent back something that isn't JSON."); }
        if (node == null) throw new ProviderException($"Empty response from {_name}.");
        if (cacheFor > TimeSpan.Zero) _cache[url] = (DateTime.UtcNow, node);
        return node;
    }

    public async Task<string> GetTextAsync(string url, TimeSpan cacheFor, CancellationToken ct, Action<HttpRequestMessage>? prepare = null)
    {
        if (cacheFor > TimeSpan.Zero && _cache.TryGetValue(url, out var hit) && Fresh(hit.At, cacheFor) && hit.Body is string s0)
            return s0;
        var body = await GetBodyAsync(url, ct, prepare);
        if (cacheFor > TimeSpan.Zero) _cache[url] = (DateTime.UtcNow, body);
        return body;
    }

    private async Task<string> GetBodyAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? prepare)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await PaceAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            prepare?.Invoke(req);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException ex) { throw new ProviderException($"Couldn't reach {_name}. Check your internet connection. ({ex.Message})"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException($"{_name} took too long to answer. Try again in a moment."); }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    // Some APIs answer 200 with an error envelope; let the source have a look first.
                    if (Classify?.Invoke(resp.StatusCode, body) is { } soft) throw soft;
                    return body;
                }
                if (Classify?.Invoke(resp.StatusCode, body) is { } custom) throw custom;
                if ((int)resp.StatusCode == 429)
                {
                    last = new ProviderException($"{_name} is rate-limiting requests. Wait a minute and try again.");
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
                    continue;
                }
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ProviderException($"{_name} refused the request ({(int)resp.StatusCode}). Check the API key in Sources.");
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new ProviderException($"Not found on {_name}.");
                throw new ProviderException($"{_name} returned {(int)resp.StatusCode}.");
            }
        }
        throw last ?? new ProviderException($"{_name} request failed.");
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        if (_minGap <= TimeSpan.Zero) return;
        await _pace.WaitAsync(ct);
        try
        {
            var wait = _last + _minGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
        }
        finally { _pace.Release(); }
    }
}

/// <summary>Small JSON readers shared by the providers.</summary>
internal static class J
{
    public static IEnumerable<JsonNode?> Arr(JsonNode? n) => n is JsonArray a ? a : Enumerable.Empty<JsonNode?>();

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A number, whether sent bare, as a numeric string, or wrapped as { raw }.</summary>
    public static double? Num(JsonNode? n)
    {
        if (n is JsonObject o) n = o["raw"];
        if (n is not JsonValue v) return null;
        if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue<double>(out var d))
            return double.IsFinite(d) ? d : null;
        if (v.GetValueKind() == System.Text.Json.JsonValueKind.String &&
            double.TryParse(v.GetValue<string>(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
            return double.IsFinite(s) ? s : null;
        return null;
    }
}
