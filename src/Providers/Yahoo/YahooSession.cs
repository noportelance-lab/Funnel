using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Funnel.Providers.Yahoo;

/// <summary>
/// HTTP plumbing for Yahoo Finance's public JSON endpoints — the same ones the yfinance Python
/// package uses. Most of them want a session cookie plus a matching "crumb" token: the cookie
/// comes from fc.yahoo.com, the crumb from /v1/test/getcrumb. Both are fetched lazily, reused,
/// and refreshed when Yahoo answers 401/403. Responses are cached briefly so flipping between
/// tabs doesn't re-hit the API (and Yahoo's rate limiter).
/// </summary>
internal sealed class YahooSession
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _crumbLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, JsonNode Node)> _cache = new();
    private string? _crumb;

    public YahooSession()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public Task<JsonNode> GetJsonAsync(string url, bool needsCrumb, TimeSpan cacheFor, CancellationToken ct) =>
        SendJsonAsync(url, null, needsCrumb, cacheFor, ct);

    /// <summary>POST a JSON body (the custom screener). Always sends the crumb; cached per URL + body.</summary>
    public Task<JsonNode> PostJsonAsync(string url, string jsonBody, TimeSpan cacheFor, CancellationToken ct) =>
        SendJsonAsync(url, jsonBody, true, cacheFor, ct);

    private async Task<JsonNode> SendJsonAsync(string url, string? postBody, bool needsCrumb, TimeSpan cacheFor, CancellationToken ct)
    {
        var key = postBody == null ? url : url + "\n" + postBody;
        if (cacheFor > TimeSpan.Zero && _cache.TryGetValue(key, out var hit) && HttpSource.Fresh(hit.At, cacheFor))
            return hit.Node;

        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var u = url;
            if (needsCrumb)
            {
                var crumb = await EnsureCrumbAsync(ct);
                u += (u.Contains('?') ? "&" : "?") + "crumb=" + Uri.EscapeDataString(crumb);
            }

            using var resp = postBody == null
                ? await _http.GetAsync(u, ct)
                : await _http.PostAsync(u, new StringContent(postBody, System.Text.Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _crumb = null;   // stale session: get a fresh cookie + crumb and go again
                last = new ProviderException("Yahoo Finance refused the request (session expired).");
                continue;
            }
            if ((int)resp.StatusCode == 429)
            {
                last = new ProviderException("Yahoo Finance is rate-limiting requests. Wait a minute and try again.");
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new ProviderException(ErrorText(body) ?? "Not found on Yahoo Finance.");
            if (!resp.IsSuccessStatusCode)
                throw new ProviderException($"Yahoo Finance returned {(int)resp.StatusCode}. {ErrorText(body)}".Trim());

            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch { throw new ProviderException("Yahoo Finance sent back something that isn't JSON."); }
            if (node == null) throw new ProviderException("Empty response from Yahoo Finance.");

            if (cacheFor > TimeSpan.Zero) _cache[key] = (DateTime.UtcNow, node);
            return node;
        }
        throw last ?? new ProviderException("Yahoo Finance request failed.");
    }

    private async Task<string> EnsureCrumbAsync(CancellationToken ct)
    {
        if (_crumb != null) return _crumb;
        await _crumbLock.WaitAsync(ct);
        try
        {
            if (_crumb != null) return _crumb;
            // fc.yahoo.com answers 404 but sets the session cookie — the status is irrelevant.
            try { using var _ = await _http.GetAsync("https://fc.yahoo.com", ct); } catch { /* cookie may still be set */ }
            var crumb = (await _http.GetStringAsync("https://query1.finance.yahoo.com/v1/test/getcrumb", ct)).Trim();
            if (crumb.Length == 0 || crumb.Length > 40 || crumb.Contains('<') || crumb.Contains(' '))
                throw new ProviderException("Couldn't open a Yahoo Finance session. Check your internet connection or proxy.");
            return _crumb = crumb;
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("Couldn't reach Yahoo Finance: " + ex.Message);
        }
        finally { _crumbLock.Release(); }
    }

    /// <summary>Yahoo's own error description, from whichever envelope the endpoint uses.</summary>
    private static string? ErrorText(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            foreach (var env in new[] { "quoteSummary", "chart", "finance", "timeseries" })
                if (n?[env]?["error"]?["description"]?.GetValue<string>() is { Length: > 0 } d) return d;
        }
        catch { }
        return null;
    }
}
