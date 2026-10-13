using Funnel.Engine;

namespace Funnel.Providers;

/// <summary>
/// The provider the app actually talks to. Each call goes to the source the user picked for that
/// section in Sources; if that source can't answer (not supported, no key, paid-only, not a US
/// listing…) the call quietly moves on to the next source that can — Yahoo first when it's
/// switched on — and <c>notice</c> says so once.
/// </summary>
public sealed class CompositeProvider : IFinanceProvider, IPeerSource, IFilingSource, INewsSource
{
    private readonly AppConfig _config;
    private readonly Action<string>? _notice;

    public CompositeProvider(AppConfig config, Action<string>? notice = null)
    {
        _config = config;
        _notice = notice;
    }

    public string Id => "composite";
    public string Name => "Funnel";
    public IReadOnlyCollection<string> Sections { get; } = Section.All.Select(s => s.Id).ToArray();

    /// <summary>The source that will be asked first for a section.</summary>
    public IFinanceProvider SourceFor(string section) => ProviderRegistry.For(section, _config.SourceFor(section));

    private static bool CanFallBack(Exception ex) => ex is NotSupportedException or ProviderException or HttpRequestException or TaskCanceledException;

    private static string Reason(Exception ex) =>
        ex is NotSupportedException && (ex.Message.Length == 0 || ex.Message.StartsWith("Specified method")) ? "doesn't provide this" : ex.Message.TrimEnd('.');

    /// <summary>Ask the section's source; on a recoverable failure, try each fallback in turn.</summary>
    private async Task<T> Route<T>(string section, Func<IFinanceProvider, Task<T>> call, string? what = null, Func<IFinanceProvider, bool>? can = null)
    {
        var first = SourceFor(section);
        try { return await call(first); }
        catch (Exception ex) when (CanFallBack(ex))
        {
            foreach (var next in ProviderRegistry.Fallbacks(section, first).Where(p => can?.Invoke(p) ?? true))
            {
                try
                {
                    var r = await call(next);
                    _notice?.Invoke($"{first.Name} — {Reason(ex)}. Showing {next.Name} {what ?? "data"} instead.");
                    return r;
                }
                catch (Exception ex2) when (CanFallBack(ex2)) { }
            }
            throw;
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct) =>
        Route(Section.Search, p => p.SearchAsync(query, ct), "results");

    public Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct) =>
        Route(Section.Market, p => p.MarketOverviewAsync(ct));

    public Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct) =>
        Route(Section.Movers, p => p.MoversAsync(kind, ct), "movers");

    /// <summary>The Bank of Canada and US Treasury groups; each fails on its own without sinking the board.</summary>
    public async Task<IReadOnlyList<MarketQuote>> MacroBoardAsync(CancellationToken ct)
    {
        if (!_config.MacroOnBoard) return Array.Empty<MarketQuote>();
        var tasks = ProviderRegistry.Macro.Select(async m =>
        {
            try { return await m.BoardAsync(ct); }
            catch (Exception ex) when (CanFallBack(ex)) { return (IReadOnlyList<MarketQuote>)Array.Empty<MarketQuote>(); }
        });
        return (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
    }

    public async Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct)
    {
        var peerSource = SourceFor(Section.Peers);
        var peersTask = peerSource is IPeerSource ps && peerSource != SourceFor(Section.Company)
            ? ps.PeersAsync(symbol, ct) : null;
        var co = await Route(Section.Company, p => p.CompanyAsync(symbol, ct), "company data");
        if (ImpliedShares(co) is { } implied) co.Metrics["impliedSharesOutstanding"] = implied;
        if (peersTask != null)
        {
            try
            {
                var peers = await peersTask;
                if (peers.Symbols.Count > 0) { co.Peers = peers.Symbols; co.PeersBasis = peers.Basis; }
            }
            catch (Exception ex) when (CanFallBack(ex))
            {
                _notice?.Invoke($"{peerSource.Name} — {Reason(ex)}. " +
                    (co.Peers.Count > 0 ? $"Showing {co.Source} peers instead." : "No peer suggestions for now."));
            }
        }
        return co;
    }

    public Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct) =>
        Route(Section.Chart, p => p.ChartAsync(symbol, range, ct), "prices");

    public async Task<CompRow> CompAsync(string symbol, CancellationToken ct)
    {
        var first = SourceFor(Section.Comps);
        var row = await first.CompAsync(symbol, ct);
        // Comp rows never throw, so fall back on the error field instead.
        if (row.Error == null) return row;
        foreach (var next in ProviderRegistry.Fallbacks(Section.Comps, first))
        {
            var r = await next.CompAsync(symbol, ct);
            if (r.Error == null) return r;
        }
        return row;
    }

    /// <summary>Balance sheets also get net working capital and its change, whichever source sent them.</summary>
    public async Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct) =>
        StatementExtras.WithNwcChange(await Route(Section.Statements, p => p.StatementAsync(symbol, kind, frequency, ct), "statements"));

    // ---------------------------------------------------------------- cross-checks

    /// <summary>
    /// Who to check a statement against: SEC EDGAR first (the filings themselves), then any other
    /// source that serves statements and can be called. Never the source that produced it.
    /// </summary>
    private IEnumerable<IFinanceProvider> References(string producedBy)
    {
        var mine = SourceFor(Section.Statements);
        return ProviderRegistry.All.Where(p => p.Id == "edgar")
            .Concat(ProviderRegistry.Fallbacks(Section.Statements, mine)).Append(mine)
            .Where(p => p.Name != producedBy && p.Sections.Contains(Section.Statements)).Distinct().Take(2);
    }

    /// <summary>Compares a statement with the same statement from a second source. Never throws.</summary>
    public async Task<DataCheck> CheckStatementAsync(string symbol, StatementKind kind, string frequency, FinancialStatement mine, CancellationToken ct)
    {
        string? why = null;
        foreach (var p in References(mine.Source))
        {
            try { return CrossCheck.Compare(mine, await p.StatementAsync(symbol, kind, frequency, ct)); }
            catch (Exception ex) when (CanFallBack(ex)) { why ??= $"{p.Name}: {Reason(ex)}"; }
        }
        return DataCheck.Unavailable(why ?? "No second source serves statements. Switch Yahoo Finance on, or add a key for another source.");
    }

    /// <summary>
    /// The company's share count against the SEC cover page, or, when the company data came from
    /// EDGAR, against Yahoo's figure. The DCF divides by this number, so it gets checked on its own.
    /// </summary>
    public async Task<DataCheck> CheckSharesAsync(CompanySnapshot co, CancellationToken ct)
    {
        if (co.Metrics.GetValueOrDefault("sharesOutstanding") is not { } mine || mine <= 0)
            return DataCheck.Unavailable($"{co.Source} gave no share count to check.");
        const string Label = "Shares outstanding";
        try
        {
            if (co.Source != "SEC EDGAR" && ProviderRegistry.All.OfType<Edgar.EdgarProvider>().FirstOrDefault() is { } ed)
            {
                DataFlag? flag;
                try
                {
                    var (sec, asOf) = await ed.CoverSharesAsync(co.Symbol, ct);
                    flag = CrossCheck.CompareShares("sharesOutstanding", Label, $"latest (SEC figure from the report cover dated {asOf:MMM d, yyyy})", mine, sec, co.Source, ed.Name);
                }
                catch (ProviderException)
                {
                    // Multi-class companies (Alphabet) file no single cover figure: use the latest quarter's
                    // weighted basic shares. Buybacks move that a little, so only a big gap counts.
                    var q = await ed.StatementAsync(co.Symbol, StatementKind.Income, "quarterly", ct);
                    var row = q.Lines.FirstOrDefault(l => l.Key == "BasicAverageShares")?.Values;
                    var i = row?.FindLastIndex(v => v != null) ?? -1;
                    if (i < 0) return DataCheck.Unavailable("SEC EDGAR has no share count for this company.", ed.Name);
                    flag = CrossCheck.CompareShares("sharesOutstanding", Label, $"latest (SEC weighted basic shares, quarter to {q.Periods[i]})", mine, row![i]!.Value, co.Source, ed.Name);
                    if (flag?.Severity == "warn") flag = null;
                }
                return new DataCheck { Against = ed.Name, Status = flag == null ? "ok" : "flagged", Compared = 1, Flags = flag == null ? new() : new() { flag } };
            }
            if (co.Source != ProviderRegistry.Yahoo.Name && ProviderRegistry.YahooEnabled
                && (await ProviderRegistry.Yahoo.CompanyAsync(co.Symbol, ct)).Metrics.GetValueOrDefault("sharesOutstanding") is { } y && y > 0)
            {
                var flag = CrossCheck.CompareShares("sharesOutstanding", Label, "latest", mine, y, co.Source, ProviderRegistry.Yahoo.Name);
                return new DataCheck { Against = ProviderRegistry.Yahoo.Name, Status = flag == null ? "ok" : "flagged", Compared = 1, Flags = flag == null ? new() : new() { flag } };
            }
            return DataCheck.Unavailable("No second source for the share count. Switch Yahoo Finance on.");
        }
        catch (Exception ex) when (CanFallBack(ex)) { return DataCheck.Unavailable(Reason(ex)); }
    }

    /// <summary>
    /// Implied shares outstanding: the source's own figure (Yahoo reports one), else market cap ÷
    /// price. Either way it counts every share class, where a reported share count can cover only
    /// one (Groupe Dynamite: 24M subordinate voting shares of ~108M in all).
    /// </summary>
    public static double? ImpliedShares(CompanySnapshot co)
    {
        if (co.Metrics.GetValueOrDefault("impliedSharesOutstanding") is double v && v > 0) return v;
        if (co.Metrics.GetValueOrDefault("marketCap") is double mc && mc > 0 && co.Price is double px && px > 0) return mc / px;
        return null;
    }

    /// <summary>
    /// Implied shares outstanding from <paramref name="co"/>'s source and from every other source
    /// that can be called, for the DCF's share count. A source that fails or is slow is skipped.
    /// </summary>
    public async Task<List<(string Source, double Shares)>> ImpliedSharesAsync(CompanySnapshot co, CancellationToken ct)
    {
        var found = new List<(string Source, double Shares)>();
        if (ImpliedShares(co) is { } mine) found.Add((co.Source, mine));
        var others = ProviderRegistry.Candidates(Section.Company, null)
            .Where(p => p.Name != co.Source && ProviderRegistry.IsUsable(p)).ToList();
        var asks = others.Select(async p =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            try { return ImpliedShares(await p.CompanyAsync(co.Symbol, cts.Token)) is { } s ? (p.Name, s) : ((string, double)?)null; }
            catch { return null; }
        });
        found.AddRange((await Task.WhenAll(asks)).OfType<(string, double)>());
        return found;
    }

    public Task<PeerSet> PeersAsync(string symbol, CancellationToken ct) =>
        Route(Section.Peers, p => p is IPeerSource ps ? ps.PeersAsync(symbol, ct) : throw new NotSupportedException(), "peers", p => p is IPeerSource);

    public Task<IReadOnlyList<Filing>> FilingsAsync(string symbol, CancellationToken ct) =>
        Route(Section.Filings, p => p is IFilingSource fs ? fs.FilingsAsync(symbol, ct) : throw new NotSupportedException(), "filings", p => p is IFilingSource);

    public Task<IReadOnlyList<NewsItem>> NewsAsync(string symbol, CancellationToken ct) =>
        Route(Section.News, p => p is INewsSource ns ? ns.NewsAsync(symbol, ct) : throw new NotSupportedException(), "news", p => p is INewsSource);

    public Task<RevenueSegments> RevenueSegmentsAsync(string symbol, CancellationToken ct) =>
        Route(Section.Segments, p => p is ISegmentSource ss ? ss.RevenueSegmentsAsync(symbol, ct) : throw new NotSupportedException(), "revenue streams", p => p is ISegmentSource);

    /// <summary>
    /// Consensus revenue growth for the DCF, from the company source if it has estimates, else any
    /// other source that does. Null when nobody can say.
    /// </summary>
    /// <summary>Annual revenue and EPS consensus from the first source that has it.</summary>
    public async Task<ConsensusEstimates?> ConsensusAsync(string symbol, CancellationToken ct)
    {
        var first = SourceFor(Section.Company);
        var order = new[] { first }.Concat(ProviderRegistry.Fallbacks(Section.Company, first)).OfType<IEstimateSource>();
        foreach (var est in order)
        {
            try
            {
                if (await est.ConsensusAsync(symbol, ct) is { Years.Count: > 0 } c)
                    return new ConsensusEstimates { Years = c.Years, Source = ((IFinanceProvider)est).Name };
            }
            catch (Exception ex) when (CanFallBack(ex)) { }
        }
        return null;
    }

    public async Task<(double Value, string Source)?> RevenueGrowthEstimateAsync(string symbol, CancellationToken ct)
    {
        var first = SourceFor(Section.Company);
        var order = new[] { first }.Concat(ProviderRegistry.Fallbacks(Section.Company, first)).OfType<IEstimateSource>();
        foreach (var est in order)
        {
            try { if (await est.RevenueGrowthEstimateAsync(symbol, ct) is { } g) return (g, ((IFinanceProvider)est).Name); }
            catch (Exception ex) when (CanFallBack(ex)) { }
        }
        return null;
    }
}
