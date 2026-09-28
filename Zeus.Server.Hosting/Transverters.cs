// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Transverter bands and the one conversion that matters: the operator, DSP,
/// band memory, CAT, TCI and logbook all work in RF; the radio is only ever
/// told <see cref="HardwareHz"/>. This mirrors piHPSDR, where
/// new_protocol.c / old_protocol.c send <c>frequency - vfo.lo</c> and nothing
/// else knows. Every frequency write to a protocol client goes through
/// <see cref="HardwareHz"/> — enforced by TransverterWriteSiteTests.
/// </summary>
public static class Transverters
{
    /// <summary>Highest frequency the radio itself tunes.</summary>
    public const long MaxRadioHz = 60_000_000;
    /// <summary>Highest RF the dial accepts (10 GHz covers microwave transverters).</summary>
    public const long MaxDialHz = 10_000_000_000;
    /// <summary>piHPSDR's transverter band count.</summary>
    public const int MaxBands = 16;
    /// <summary>
    /// A frequency this close outside a band still converts with that band's
    /// LO: CTUN parks the DDC centre and CW pitch shifts the LO away from the
    /// dial, near a band edge. Bands must be at least twice this far apart.
    /// </summary>
    public const long EdgeMarginHz = 1_000_000;

    private static TransverterBandDto[] _enabled = [];

    /// <summary>Enabled bands, as last applied by the store.</summary>
    public static IReadOnlyList<TransverterBandDto> Enabled => Volatile.Read(ref _enabled);

    public static void Apply(IEnumerable<TransverterBandDto> bands) =>
        Volatile.Write(ref _enabled, bands.Where(b => b.Enabled).ToArray());

    /// <summary>The enabled band whose RF range contains <paramref name="hz"/>.</summary>
    public static bool TryFind(long hz, out TransverterBandDto band) => TryFind(hz, 0, out band);

    private static bool TryFind(long hz, long margin, out TransverterBandDto band)
    {
        foreach (var b in Volatile.Read(ref _enabled))
        {
            if (hz >= b.MinHz - margin && hz <= b.MaxHz + margin)
            {
                band = b;
                return true;
            }
        }
        band = null!;
        return false;
    }

    /// <summary>
    /// RF → the frequency the radio is told. At or below the radio's own
    /// range, unchanged. Above it, RF − (LO + LO error) of the band (within
    /// <see cref="EdgeMarginHz"/>). Never outside 0..<see cref="MaxRadioHz"/>:
    /// an RF value with no band behind it clamps rather than reaching the wire.
    /// </summary>
    public static long HardwareHz(long hz)
    {
        if (hz <= MaxRadioHz) return Math.Max(0, hz);
        if (TryFind(hz, EdgeMarginHz, out var b))
            return Math.Clamp(hz - (b.LoHz + b.LoErrorHz), 0, MaxRadioHz);
        return MaxRadioHz;
    }

    /// <summary>Is <paramref name="hz"/> a frequency the operator may tune?</summary>
    public static bool IsTunable(long hz) =>
        hz >= 0 && (hz <= MaxRadioHz || (hz <= MaxDialHz && TryFind(hz, out _)));

    /// <summary>Operator-facing clamp: the radio's range, or an enabled transverter band.</summary>
    public static long ClampTune(long hz) => IsTunable(hz) ? hz : Math.Clamp(hz, 0, MaxRadioHz);

    /// <summary>Null if valid, else the first problem, operator-readable.</summary>
    public static string? Validate(IReadOnlyList<TransverterBandDto>? bands)
    {
        if (bands is null) return "bands required";
        if (bands.Count > MaxBands) return $"at most {MaxBands} transverter bands";
        if (bands.Select(b => b.Id).Distinct().Count() != bands.Count) return "band ids must be unique";
        foreach (var b in bands)
        {
            string n = b.Name?.Trim() ?? "";
            if (n.Length is 0 or > 12) return $"band {b.Id}: name must be 1-12 characters";
            if (BandUtils.HfBands.Contains(n)) return $"band '{n}': name clashes with an HF band";
            if (!b.Enabled) continue;
            if (b.MinHz <= MaxRadioHz) return $"band '{n}': RF start must be above 60 MHz (the radio tunes up to 60 MHz itself)";
            if (b.MaxHz <= b.MinHz) return $"band '{n}': RF end must be above RF start";
            if (b.MaxHz > MaxDialHz) return $"band '{n}': RF end must be at most 10 GHz";
            long lo = b.LoHz + b.LoErrorHz;
            if (b.MinHz - lo < 0 || b.MaxHz - lo > MaxRadioHz)
                return $"band '{n}': IF {(b.MinHz - lo) / 1e6:0.###}-{(b.MaxHz - lo) / 1e6:0.###} MHz is outside the radio's 0-60 MHz";
        }
        var names = bands.Select(b => b.Name.Trim()).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) return "band names must be unique";
        var on = bands.Where(b => b.Enabled).OrderBy(b => b.MinHz).ToList();
        for (int i = 1; i < on.Count; i++)
            if (on[i].MinHz - on[i - 1].MaxHz < 2 * EdgeMarginHz)
                return $"bands '{on[i - 1].Name}' and '{on[i].Name}' must be at least 2 MHz apart";
        return null;
    }
}
