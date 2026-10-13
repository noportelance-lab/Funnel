using System.Globalization;
using System.Text.RegularExpressions;
using Funnel.Providers.Bonds;
using Funnel.Providers.Edgar;

namespace Funnel.Engine;

/// <summary>
/// Matching a company to its bonds. FINRA files bonds under its own spelling of the issuer
/// ("AMAZON COM INC", "FORD MTR CR CO LLC"), and companies borrow through finance arms, so the
/// company name is cut to its core words and compared with each candidate issuer's, abbreviations
/// spelled out. An issuer is picked when it is the company's own name, or the company's name plus
/// only finance-arm words (Ford Motor Credit, Berkshire Hathaway Finance); the Debt tab lists the
/// rest so they can be ticked by hand.
/// </summary>
public static class DebtProfile
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "LTD", "LIMITED", "PLC", "LLC", "LP", "L", "P",
        "SA", "NV", "AG", "SE", "THE", "OF", "HOLDINGS", "HLDGS", "HLDG", "HOLDING", "GROUP", "GRP", "&", "AND", "DEL", "NEW", "CL", "A", "B",
    };
    private static readonly Dictionary<string, string> Spelled = new(StringComparer.Ordinal)
    {
        ["MTR"] = "MOTOR", ["MTRS"] = "MOTORS", ["INTL"] = "INTERNATIONAL", ["FINL"] = "FINANCIAL", ["CR"] = "CREDIT", ["CAP"] = "CAPITAL",
        ["FIN"] = "FINANCE", ["FDG"] = "FUNDING", ["NATL"] = "NATIONAL", ["AMERN"] = "AMERICAN", ["AMER"] = "AMERICAN", ["CDA"] = "CANADA",
        ["BK"] = "BANK", ["SVCS"] = "SERVICES", ["SVC"] = "SERVICE", ["COMMUNICATIONS"] = "COMMUNICATIONS", ["COMMNS"] = "COMMUNICATIONS",
        ["TECHNOLOGIES"] = "TECHNOLOGIES", ["TECH"] = "TECHNOLOGIES", ["MFG"] = "MANUFACTURING", ["PETE"] = "PETROLEUM", ["PETE."] = "PETROLEUM",
        ["PHARMACEUTICALS"] = "PHARMACEUTICALS", ["PHARMA"] = "PHARMACEUTICALS", ["ENTMT"] = "ENTERTAINMENT", ["INDS"] = "INDUSTRIES",
        ["LABS"] = "LABORATORIES", ["SYS"] = "SYSTEMS", ["ELEC"] = "ELECTRIC", ["PWR"] = "POWER", ["GEN"] = "GENERAL", ["MGMT"] = "MANAGEMENT",
    };
    private static readonly HashSet<string> FinanceArm = new(StringComparer.Ordinal)
    {
        "FINANCE", "CAPITAL", "CREDIT", "FUNDING", "FINANCIAL", "TREASURY", "INTERNATIONAL", "AMERICA", "USA", "US", "GLOBAL",
    };
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "AMERICAN", "GENERAL", "UNITED", "FIRST", "BANK", "NATIONAL", "INTERNATIONAL", "US", "NEW", "GLOBAL", "ROYAL",
    };

    /// <summary>"Ford Motor Company" → [FORD, MOTOR]; "AMAZON COM INC" → [AMAZON, COM].</summary>
    public static List<string> Core(string name)
    {
        var s = Regex.Replace(name.ToUpperInvariant(), @"[.,'’()/\-]", " ");
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Spelled.TryGetValue(t, out var w) ? w : t)
            .Where(t => !Suffixes.Contains(t)).ToList();
    }

    /// <summary>
    /// What to ask FINRA for: the first word, or when that's common, the first word and the next
    /// one's initial ("GENERAL M" finds GENERAL MTRS; FINRA abbreviates the rest its own way).
    /// </summary>
    public static string Prefix(string companyName)
    {
        var raw = Regex.Replace(companyName.ToUpperInvariant(), @"[.,'’()/\-]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t is not ("THE" or "OF" or "&" or "AND")).ToList();
        if (raw.Count == 0) return companyName.ToUpperInvariant();
        return (raw[0].Length <= 3 || Generic.Contains(raw[0])) && raw.Count > 1 ? $"{raw[0]} {raw[1][0]}" : raw[0];
    }

    static bool Same(string a, string b) => a == b || (a.Length >= 4 && b.Length >= 4 && (a.StartsWith(b) || b.StartsWith(a)));

    /// <summary>The company's own name, or its name followed only by finance-arm words.</summary>
    public static bool Matches(string companyName, string issuer)
    {
        var co = Core(companyName); var iss = Core(issuer);
        if (co.Count == 0 || iss.Count < co.Count) return false;
        for (int i = 0; i < co.Count; i++) if (!Same(co[i], iss[i])) return false;
        return iss.Skip(co.Count).All(FinanceArm.Contains);
    }

    /// <summary>
    /// Puts the 10-K's face amounts on the bonds: a note matches a bond with the same coupon (to
    /// 0.03 points) maturing in the same year (and month, when the note gives one). Only one-to-one
    /// matches count, so two 4% notes due 2030 stay unweighted rather than guessed.
    /// </summary>
    public static Dictionary<string, (double Face, string Note)> MatchFaces(IReadOnlyList<FinraBond> bonds, IReadOnlyList<DebtInstrument> notes)
    {
        var faces = new Dictionary<string, (double, string)>();
        foreach (var n in notes)
        {
            if (n.Rate is not { } r || n.MaturityYear is not { } y) continue;
            var pct = r < 1 ? r * 100 : r;
            var hits = bonds.Where(b => b.Coupon is { } c && Math.Abs(c - pct) < 0.03 && Year(b.Maturity) == y && (n.MaturityMonth == null || Month(b.Maturity) == n.MaturityMonth)).ToList();
            if (hits.Count == 1 && !faces.ContainsKey(hits[0].Cusip)) faces[hits[0].Cusip] = (n.Face, n.Name);
        }
        return faces;
    }

    static int? Year(string? d) => d is { Length: >= 4 } && int.TryParse(d[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
    static int? Month(string? d) => d is { Length: >= 7 } && int.TryParse(d.AsSpan(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
}
