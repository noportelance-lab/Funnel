using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Funnel.Engine;

/// <summary>Per-user preferences, saved as JSON in %LOCALAPPDATA%\Funnel\config.json.</summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.Home, "config.json");

    /// <summary>The data provider in use (see Providers.ProviderRegistry).</summary>
    public string Provider { get; set; } = "yahoo";
    public string ExportDir { get; set; } = "";
    /// <summary>Most recently researched symbols, newest first.</summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>Comp sets the user has edited, by subject symbol.</summary>
    public Dictionary<string, List<string>> CompSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Units { get; set; } = "millions";
    public string Frequency { get; set; } = "annual";

    /// <summary>Which provider serves each section (see Providers.Section); missing = the registry's default.</summary>
    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>API keys by provider id, encrypted for this Windows user (DPAPI) and base64'd.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The SEC asks every EDGAR client to identify itself with a contact email.</summary>
    public string ContactEmail { get; set; } = "";
    /// <summary>
    /// Whether Yahoo Finance may be used at all — as a source or as the fallback. Its data is for
    /// personal use only, so a build that's sold or shared should run with this off.
    /// </summary>
    public bool UseYahoo { get; set; } = true;
    /// <summary>Add the Bank of Canada and FRED groups to the Markets board.</summary>
    public bool MacroOnBoard { get; set; } = true;
    /// <summary>The general DCF assumptions new models start from (Settings → DCF assumptions).</summary>
    public DcfSettings Dcf { get; set; } = new();
    /// <summary>DCF assumptions the user has edited, by symbol (stored as the UI sends them).</summary>
    public Dictionary<string, JsonElement> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a company's saved inputs (edited DCF assumptions, comp set) are kept after their
    /// last change, in days; 0 = forever. Expired inputs are dropped so the company reopens on
    /// fresh numbers.
    /// </summary>
    public int KeepSavedDays { get; set; } = 1;
    /// <summary>When each entry in <see cref="Models"/> was last changed (UTC), by symbol.</summary>
    public Dictionary<string, DateTime> ModelsSavedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When each entry in <see cref="CompSets"/> was last changed (UTC), by symbol.</summary>
    public Dictionary<string, DateTime> CompSetsSavedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly int[] KeepSavedChoices = { 1, 7, 30, 90, 365, 0 };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Opts);
                if (cfg != null)
                {
                    cfg.CompSets = new(cfg.CompSets ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.Sources = new(cfg.Sources ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.ApiKeys = new(cfg.ApiKeys ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.Models = new(cfg.Models ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.ModelsSavedAt = new(cfg.ModelsSavedAt ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.CompSetsSavedAt = new(cfg.CompSetsSavedAt ?? new(), StringComparer.OrdinalIgnoreCase);
                    cfg.Dcf = (cfg.Dcf ?? new()).Normalised();
                    if (!KeepSavedChoices.Contains(cfg.KeepSavedDays)) cfg.KeepSavedDays = 1;
                    // Inputs saved before timestamps existed start their clock now rather than vanishing.
                    foreach (var k in cfg.Models.Keys) cfg.ModelsSavedAt.TryAdd(k, DateTime.UtcNow);
                    foreach (var k in cfg.CompSets.Keys) cfg.CompSetsSavedAt.TryAdd(k, DateTime.UtcNow);
                    if (cfg.PruneExpired()) cfg.Save();
                    return cfg;
                }
            }
        }
        catch { /* a corrupt config falls back to defaults rather than blocking startup */ }
        return new AppConfig();
    }

    public void Save()
    {
        lock (this)
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); }
            catch { /* preferences are a convenience; never fail a command over them */ }
        }
    }

    /// <summary>The source the user picked for a section, or null to let the registry choose.</summary>
    public string? SourceFor(string section) =>
        Sources.TryGetValue(section, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <summary>Keys for this run only, never saved (the self-test uses these).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> KeyOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The decrypted key, or null when none is set (or it was saved by another Windows user).</summary>
    public string? GetKey(string providerId)
    {
        if (KeyOverrides.TryGetValue(providerId, out var k)) return k;
        if (!ApiKeys.TryGetValue(providerId, out var blob) || string.IsNullOrEmpty(blob)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public void SetKey(string providerId, string? key)
    {
        lock (this)
        {
            if (string.IsNullOrWhiteSpace(key)) ApiKeys.Remove(providerId);
            else ApiKeys[providerId] = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }
        Save();
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Funnel api keys v1");

    public string ResolvedExportDir() =>
        !string.IsNullOrWhiteSpace(ExportDir) && Directory.Exists(ExportDir) ? ExportDir : AppPaths.DefaultExportDir;

    // ---------- saved per-company inputs ----------

    public void SetModel(string symbol, JsonElement? assumptions)
    {
        lock (this)
        {
            if (assumptions is { } a) { Models[symbol] = a; ModelsSavedAt[symbol] = DateTime.UtcNow; }
            else { Models.Remove(symbol); ModelsSavedAt.Remove(symbol); }
        }
        Save();
    }

    public void SetCompSet(string symbol, List<string>? peers)
    {
        lock (this)
        {
            if (peers is { Count: > 0 }) { CompSets[symbol] = peers; CompSetsSavedAt[symbol] = DateTime.UtcNow; }
            else { CompSets.Remove(symbol); CompSetsSavedAt.Remove(symbol); }
        }
        Save();
    }

    /// <summary>When an input saved at <paramref name="savedAt"/> expires, or null if kept forever.</summary>
    public DateTime? ExpiresAt(DateTime savedAt) => KeepSavedDays > 0 ? savedAt.AddDays(KeepSavedDays) : null;

    /// <summary>Drop inputs older than the retention window. True when anything was removed.</summary>
    public bool PruneExpired()
    {
        if (KeepSavedDays <= 0) return false;
        var now = DateTime.UtcNow;
        bool Expired(Dictionary<string, DateTime> at, string k) => !at.TryGetValue(k, out var t) || ExpiresAt(t) <= now;
        lock (this)
        {
            var models = Models.Keys.Where(k => Expired(ModelsSavedAt, k)).ToList();
            var comps = CompSets.Keys.Where(k => Expired(CompSetsSavedAt, k)).ToList();
            foreach (var k in models) { Models.Remove(k); ModelsSavedAt.Remove(k); }
            foreach (var k in comps) { CompSets.Remove(k); CompSetsSavedAt.Remove(k); }
            return models.Count + comps.Count > 0;
        }
    }

    /// <summary>Forget everything saved for these symbols (all of them when null).</summary>
    public void ForgetCompanies(IEnumerable<string>? symbols)
    {
        lock (this)
        {
            var list = symbols?.ToList() ?? Models.Keys.Concat(CompSets.Keys).ToList();
            foreach (var k in list) { Models.Remove(k); ModelsSavedAt.Remove(k); CompSets.Remove(k); CompSetsSavedAt.Remove(k); }
        }
        Save();
    }

    public void Touch(string symbol)
    {
        lock (this)
        {
            Recent.RemoveAll(s => s.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, symbol.ToUpperInvariant());
            if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
        }
        Save();
    }
}
