namespace Funnel.Providers;

/// <summary>
/// One source of market data. Funnel talks to every API through this interface, so adding a
/// new source means writing one class that maps its responses into the shared models and
/// registering it in <see cref="ProviderRegistry"/>. Nothing in the UI or the Excel export changes.
///
/// Sources don't have to do everything. Each lists the <see cref="Sections"/> it serves; the
/// user picks a source per section, and <see cref="CompositeProvider"/> falls back to Yahoo for
/// anything a source can't answer. A call outside a source's sections throws
/// <see cref="NotSupportedException"/>.
/// </summary>
public interface IFinanceProvider
{
    /// <summary>Stable id saved in the user's config, e.g. "yahoo".</summary>
    string Id { get; }
    /// <summary>Shown to the user and stamped on every export, e.g. "Yahoo Finance".</summary>
    string Name { get; }
    /// <summary>Which <see cref="Section"/>s this source can serve.</summary>
    IReadOnlyCollection<string> Sections { get; }

    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct);

    /// <summary>Indices, rates, commodities, FX — the market-overview board.</summary>
    Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct);

    /// <summary>Today's biggest movers. kind: gainers | losers | actives.</summary>
    Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct);

    /// <summary>Profile, price, multiples, analyst view and suggested peers in one shot.</summary>
    Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct);

    /// <summary>range: 1d 5d 1mo 6mo ytd 1y 5y max (and 5y-daily for the Excel price sheet).</summary>
    Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct);

    /// <summary>Multiples for one comp-table row. Never throws: failures go in <see cref="CompRow.Error"/>.</summary>
    Task<CompRow> CompAsync(string symbol, CancellationToken ct);

    /// <summary>frequency: annual | quarterly.</summary>
    Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct);
}

/// <summary>The parts of the app a source can be picked for.</summary>
public static class Section
{
    public const string Search = "search";
    public const string Market = "market";
    public const string Movers = "movers";
    public const string Company = "company";
    public const string Chart = "chart";
    public const string Comps = "comps";
    public const string Statements = "statements";
    public const string Peers = "peers";
    public const string Filings = "filings";
    public const string News = "news";
    public const string Earnings = "earnings";
    public const string Insiders = "insiders";
    public const string Segments = "segments";

    /// <summary>Shown in the Sources panel, in this order.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All = new[]
    {
        (Company, "Company, price & analysts"),
        (Chart, "Charts & price history"),
        (Statements, "Financial statements"),
        (Peers, "Suggested peers"),
        (Comps, "Comps multiples"),
        (Search, "Search"),
        (Market, "Markets board"),
        (Movers, "Top movers"),
        (Earnings, "Earnings & estimates"),
        (Insiders, "Insider trades"),
        (Segments, "Revenue streams (DCF)"),
        (Filings, "Filings"),
        (News, "News"),
    };
}

// Optional extras. A provider implements the ones it can serve.

public interface IPeerSource { Task<PeerSet> PeersAsync(string symbol, CancellationToken ct); }
public interface IFilingSource { Task<IReadOnlyList<Filing>> FilingsAsync(string symbol, CancellationToken ct); }
public interface INewsSource { Task<IReadOnlyList<NewsItem>> NewsAsync(string symbol, CancellationToken ct); }
public interface IEarningsSource { Task<EarningsView> EarningsAsync(string symbol, CancellationToken ct); }
public interface IInsiderSource { Task<InsiderView> InsidersAsync(string symbol, CancellationToken ct); }
/// <summary>Revenue by product / segment from the latest annual report, for the DCF's revenue streams.</summary>
public interface ISegmentSource { Task<RevenueSegments> RevenueSegmentsAsync(string symbol, CancellationToken ct); }
/// <summary>Consensus revenue growth for the current (not yet reported) fiscal year, as a fraction.</summary>
public interface IEstimateSource
{
    Task<double?> RevenueGrowthEstimateAsync(string symbol, CancellationToken ct);

    /// <summary>Annual consensus for this fiscal year and next. By default, just the revenue growth.</summary>
    async Task<ConsensusEstimates?> ConsensusAsync(string symbol, CancellationToken ct) =>
        await RevenueGrowthEstimateAsync(symbol, ct) is { } g
            ? new ConsensusEstimates { Years = { new ConsensusYear("0y", null, null, g, null, null, null) } }
            : null;
}

/// <summary>A source that needs an API key the user pastes into the Sources panel.</summary>
public interface IKeyedSource
{
    /// <summary>Where to sign up for a free key.</summary>
    string KeyUrl { get; }
    string KeyNote { get; }
    /// <summary>One cheap call that proves the key works; throws with a readable message if not.</summary>
    Task TestKeyAsync(CancellationToken ct);
}

/// <summary>Daily economic series (rates, FX) for the Markets board and the DCF's risk-free rate.</summary>
public interface IMacroSource
{
    string Id { get; }
    string Name { get; }
    /// <summary>Tiles for the Markets board, already grouped.</summary>
    Task<IReadOnlyList<MarketQuote>> BoardAsync(CancellationToken ct);
}

/// <summary>A data-source failure with a message fit to show the user as-is.</summary>
public sealed class ProviderException(string message) : Exception(message);
