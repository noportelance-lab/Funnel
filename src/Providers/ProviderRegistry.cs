using Funnel.Engine;
using Funnel.Providers.Edgar;
using Funnel.Providers.Finnhub;
using Funnel.Providers.Fmp;
using Funnel.Providers.Macro;
using Funnel.Providers.TwelveData;
using Funnel.Providers.Yahoo;

namespace Funnel.Providers;

/// <summary>Every data source Funnel knows about. Register new providers here.</summary>
public static class ProviderRegistry
{
    private static AppConfig? _config;

    /// <summary>Hands the providers their keys and the EDGAR contact email (read on every call, so edits apply at once).</summary>
    public static void Configure(AppConfig config) => _config = config;

    public static readonly YahooProvider Yahoo = new();
    public static readonly TreasuryProvider Treasury = new();
    public static readonly BocProvider Boc = new();

    private static readonly List<IFinanceProvider> Providers = new()
    {
        Yahoo,
        new EdgarProvider(() => _config?.ContactEmail ?? ""),
        new TwelveDataProvider(() => _config?.GetKey("twelvedata")),
        new FmpProvider(() => _config?.GetKey("fmp")),
        new FinnhubProvider(() => _config?.GetKey("finnhub")),
    };

    /// <summary>False when the user has switched Yahoo Finance off; it is then never called.</summary>
    public static bool YahooEnabled => _config?.UseYahoo ?? true;

    /// <summary>Every registered source, whether or not it's switched on.</summary>
    public static IReadOnlyList<IFinanceProvider> Everything => Providers;

    /// <summary>The sources the user can pick from (Yahoo drops out when it's switched off).</summary>
    public static IReadOnlyList<IFinanceProvider> All => Providers.Where(p => p != Yahoo || YahooEnabled).ToList();

    /// <summary>Rates and FX for the Markets board, shown after the main board.</summary>
    public static IReadOnlyList<IMacroSource> Macro { get; } = new IMacroSource[] { Boc, Treasury };

    public static IFinanceProvider? Get(string? id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static bool Usable(IFinanceProvider p) =>
        p is not IKeyedSource || !string.IsNullOrEmpty(_config?.GetKey(p.Id));

    /// <summary>Whether a source can be called right now (a keyed one needs its key).</summary>
    public static bool IsUsable(IFinanceProvider p) => Usable(p);

    /// <summary>
    /// Everyone who can answer a section, best first: the user's pick, then Yahoo (when on), then
    /// keyed sources that have a key, then keyless ones, then keyed sources still waiting for a key.
    /// </summary>
    public static IReadOnlyList<IFinanceProvider> Candidates(string section, string? configured)
    {
        var serving = All.Where(p => p.Sections.Contains(section)).ToList();
        return serving
            .OrderBy(p => p.Id.Equals(configured, StringComparison.OrdinalIgnoreCase) ? 0
                : p == Yahoo ? 1
                : p is IKeyedSource && Usable(p) ? 2
                : p is not IKeyedSource ? 3
                : 4)
            .ToList();
    }

    /// <summary>The source that will be asked first for a section, or a stand-in that explains why there is none.</summary>
    public static IFinanceProvider For(string section, string? configured) =>
        Candidates(section, configured).FirstOrDefault() ?? new NoSourceProvider(section);

    /// <summary>Who to try next when the first source can't answer: anything else serving the section that can actually be called.</summary>
    public static IEnumerable<IFinanceProvider> Fallbacks(string section, IFinanceProvider first) =>
        Candidates(section, null).Where(p => p != first && Usable(p));
}

/// <summary>Stands in for a section nobody can serve, so the UI gets a clear message instead of a crash.</summary>
public sealed class NoSourceProvider(string section) : IFinanceProvider
{
    public string Id => "none";
    public string Name => "No source";
    public IReadOnlyCollection<string> Sections { get; } = Section.All.Select(s => s.Id).ToArray();

    private Exception Why()
    {
        var label = Section.All.FirstOrDefault(s => s.Id == section).Label ?? section;
        return new ProviderException($"No data source is set up for \"{label}\". Add a Twelve Data key under Sources" +
            (ProviderRegistry.YahooEnabled ? "." : ", or switch Yahoo Finance back on for personal use."));
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct) => throw Why();
    public Task<IReadOnlyList<MarketQuote>> MarketOverviewAsync(CancellationToken ct) => throw Why();
    public Task<IReadOnlyList<MarketQuote>> MoversAsync(string kind, CancellationToken ct) => throw Why();
    public Task<CompanySnapshot> CompanyAsync(string symbol, CancellationToken ct) => throw Why();
    public Task<PriceSeries> ChartAsync(string symbol, string range, CancellationToken ct) => throw Why();
    public Task<CompRow> CompAsync(string symbol, CancellationToken ct) =>
        Task.FromResult(new CompRow { Symbol = symbol.ToUpperInvariant(), Name = symbol.ToUpperInvariant(), Error = Why().Message });
    public Task<FinancialStatement> StatementAsync(string symbol, StatementKind kind, string frequency, CancellationToken ct) => throw Why();
}
