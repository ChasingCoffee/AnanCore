// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Callsign → DXCC entity, from AD1C's country file (cty.csv, embedded; MIT,
// see LICENSE.cty) — the list WSJT-X, JTDX and most loggers use.
//
// cty.csv: one entity per line —
//   primary prefix, name, DXCC (ADIF) number, continent, CQ zone, ITU zone,
//   lat, lon, UTC offset, then its prefixes separated by spaces, ending ';'.
// A prefix may carry overrides — (CQ) [ITU] <lat/lon> {continent} ~offset~ —
// and "=CALL" is an exact callsign. Entities whose primary prefix starts with
// '*' are WAE-only (Sicily, Shetland…): not DXCC entities, so they are skipped
// and their calls resolve to the DXCC entity that contains them.
//
// Resolution, as the loggers do it: an exact call first; then, for a
// portable call A/B, the part that names a location (a bare suffix such as
// /P, /M, /QRP or a call-area digit is ignored; /MM and /AM have no entity;
// otherwise the shorter part is the prefix); then the longest matching prefix.

namespace Zeus.Server.Hosting.Dxcc;

/// <summary>A DXCC entity: ADIF number, name as the country file gives it,
/// continent and primary prefix.</summary>
public sealed record DxccEntity(int Dxcc, string Name, string Continent, string PrimaryPrefix);

public sealed class DxccTable
{
    private readonly Dictionary<string, DxccEntity> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DxccEntity> _prefix = new(StringComparer.Ordinal);
    private int _maxPrefix;

    /// <summary>The embedded country file, loaded once.</summary>
    public static DxccTable Default => s_default.Value;

    private static readonly Lazy<DxccTable> s_default = new(() =>
    {
        using var s = typeof(DxccTable).Assembly.GetManifestResourceStream("Zeus.Dxcc.cty.csv")
            ?? throw new InvalidOperationException("cty.csv is not embedded");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    });

    /// <summary>The country file's version tag ("VER20260915"), if present.</summary>
    public string? Version { get; private set; }

    private DxccTable() { }

    public static DxccTable Parse(string csv)
    {
        var t = new DxccTable();
        int max = 0;
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(';');
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length < 10) continue;
            if (f[0].StartsWith('*')) continue;                       // WAE-only
            if (!int.TryParse(f[2], out int dxcc)) continue;
            var entity = new DxccEntity(dxcc, f[1], f[3], f[0]);

            foreach (var token in f[9].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                bool exact = token.StartsWith('=');
                string p = StripOverrides(exact ? token[1..] : token);
                if (p.Length == 0) continue;
                if (exact)
                {
                    if (p.StartsWith("VER", StringComparison.Ordinal) && p.Length == 11 && p[3..].All(char.IsDigit))
                        t.Version = p;
                    t._exact[p] = entity;
                }
                else
                {
                    t._prefix[p] = entity;
                    max = Math.Max(max, p.Length);
                }
            }
        }
        t._maxPrefix = max;
        return t;
    }

    private static string StripOverrides(string token)
    {
        int cut = token.IndexOfAny(['(', '[', '<', '{', '~']);
        return cut < 0 ? token : token[..cut];
    }

    // Portable suffixes that do not name a location.
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "P", "M", "QRP", "QRPP", "A", "B", "R", "T", "LH", "J", "SAT",
    };

    /// <summary>The DXCC entity of <paramref name="callsign"/>, or null when it
    /// has none (maritime/aeronautical mobile, a hashed "&lt;...&gt;", no match).</summary>
    public DxccEntity? Lookup(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return null;
        string call = callsign.Trim().Trim('<', '>').ToUpperInvariant();
        if (call.Length == 0 || call.Contains("...")) return null;

        if (_exact.TryGetValue(call, out var e)) return e;

        if (call.Contains('/'))
        {
            var parts = call.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p is "MM" or "AM")) return null;       // at sea / in the air
            var located = parts.Where(p => !Ignored.Contains(p) && !(p.Length == 1 && char.IsDigit(p[0]))).ToList();
            if (located.Count == 0) return null;
            string basis = located.Count == 1
                ? located[0]
                : located.OrderBy(p => p.Length).First();           // the shorter part is the prefix
            if (_exact.TryGetValue(basis, out e)) return e;
            call = basis;
        }

        for (int len = Math.Min(call.Length, _maxPrefix); len > 0; len--)
            if (_prefix.TryGetValue(call[..len], out e)) return e;
        return null;
    }
}
