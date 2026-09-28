// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Live two-tone intermodulation readout from the TX panadapter bins.
///
/// With PureSignal armed the TX panadapter is the post-PA feedback spectrum,
/// so the products it shows are the amplifier's — measured before, during
/// and after the predistorter converges. Tones sit at the carrier ± f1 / f2
/// (sign by sideband); the odd-order products at RF are
///   IMD3: 2f1−f2 and 2f2−f1      IMD5: 3f1−2f2 and 3f2−2f1
/// each relative to the carrier. Every value is a peak search in a small
/// window around its expected bin (the WDSP display bins are already
/// peak-held per pixel), and the readout is the WORSE of the two products
/// relative to the MEAN of the two tones, in dBc.
///
/// Returns false (NaN) whenever a number would be a guess: two-tone off, bins
/// too coarse to separate the tones, a tone that isn't clearly above the
/// floor, or a product window that falls off the display.
/// </summary>
internal static class TwoToneImdAnalyzer
{
    // A tone must clear the display's median floor by this much to count as
    // found — below it we're measuring noise, not the PA.
    private const double MinToneAboveFloorDb = 25.0;
    // A product must clear the floor by this much to be a measurement rather
    // than floor noise (post-convergence products sink below the display).
    private const double MinProductAboveFloorDb = 3.0;
    // Tone search half-window around the carrier-derived position. Generous:
    // the carrier estimate on the TX display can be a few hundred Hz off.
    private const double ToneSearchHalfWidthHz = 400.0;

    // A product must clear the NOISE the way it is measured — as the peak of a
    // product-sized window — not merely the median floor. Peak-detected noise
    // pixels sit well above their median, so the old median+3 dB gate let
    // noise peaks through as "products" once PureSignal had pushed the real
    // ones into the floor: the readout stuck a few dB above the floor, i.e.
    // WORSE than the amplifier really was (field: PS IMD3/IMD5 read high).
    // The limit is the 95th percentile of window maxima over line-free
    // pixels, plus this margin. Below it a product is reported as an upper
    // bound ("< X dBc"), never as a number it cannot support.
    private const double ProductAboveNoiseMaxDb = 3.0;
    private const double NoisePercentile = 0.95;
    private const int MinNoiseWindows = 8;
    // Line-free region: everything more than this many tone spacings outboard
    // of the tone pair (IMD3 sits 1 spacing out, IMD5 2, IMD7 3).
    private const int NoiseExclusionSpacings = 4;

    /// <summary>One two-tone reading. When <c>IsBound</c> is set the value is
    /// an UPPER BOUND in dBc: the product is below what this display can
    /// measure, and the true IMD is at least that good.</summary>
    public readonly record struct ImdMeasurement(
        double Imd3Dbc, bool Imd3IsBound, double Imd5Dbc, bool Imd5IsBound);

    /// <summary>Measured values only: false (NaN) whenever IMD3 is below the
    /// measurable limit, NaN IMD5 when IMD5 is. See <see cref="TryMeasureWithLimit"/>
    /// for the variant that reports the limit as an upper bound.</summary>
    public static bool TryMeasure(
        ReadOnlySpan<float> bins, float hzPerPixel, long centerHz, long carrierHz,
        double f1Hz, double f2Hz, bool lowerSideband,
        out double imd3Dbc, out double imd5Dbc)
    {
        imd3Dbc = double.NaN;
        imd5Dbc = double.NaN;
        if (!TryMeasureWithLimit(bins, hzPerPixel, centerHz, carrierHz, f1Hz, f2Hz, lowerSideband, out var m))
            return false;
        if (m.Imd3IsBound) return false;
        imd3Dbc = m.Imd3Dbc;
        imd5Dbc = m.Imd5IsBound ? double.NaN : m.Imd5Dbc;
        return true;
    }

    /// <summary>
    /// Returns false (NaN) only when there is nothing to measure against: two-
    /// tone off, bins too coarse, a tone not clearly above the floor, or IMD3
    /// off the display. Otherwise each product is either a measurement or, when
    /// it does not clear the noise limit, that limit as an upper bound.
    /// </summary>
    public static bool TryMeasureWithLimit(
        ReadOnlySpan<float> bins, float hzPerPixel, long centerHz, long carrierHz,
        double f1Hz, double f2Hz, bool lowerSideband,
        out ImdMeasurement result)
    {
        result = new ImdMeasurement(double.NaN, false, double.NaN, false);
        int n = bins.Length;
        if (n < 16 || !(hzPerPixel > 0f) || !double.IsFinite(f1Hz) || !double.IsFinite(f2Hz))
            return false;
        double lo = Math.Min(f1Hz, f2Hz), hi = Math.Max(f1Hz, f2Hz);
        double spacing = hi - lo;
        if (spacing < 50.0) return false;
        // Need at least ~3 bins between the tones to tell them (and the
        // products, which sit one spacing outboard) apart.
        if (hzPerPixel > spacing / 3.0) return false;
        double sign = lowerSideband ? -1.0 : 1.0;
        double floorDb = MedianFloor(bins);

        // 1) Find the two tones near where the carrier says they should be.
        int toneHalf = Math.Max(2, (int)Math.Ceiling(ToneSearchHalfWidthHz / hzPerPixel));
        int spacingPxTheory = (int)(spacing / hzPerPixel);
        toneHalf = Math.Min(toneHalf, Math.Max(1, spacingPxTheory / 2 - 1));
        if (!ArgMaxNear(bins, hzPerPixel, centerHz, carrierHz + sign * lo, toneHalf, out int pxA, out double toneA)) return false;
        if (!ArgMaxNear(bins, hzPerPixel, centerHz, carrierHz + sign * hi, toneHalf, out int pxB, out double toneB)) return false;
        if (toneA - floorDb < MinToneAboveFloorDb || toneB - floorDb < MinToneAboveFloorDb) return false;
        int spacingPx = Math.Abs(pxB - pxA);
        if (spacingPx < 3) return false;
        double toneMean = 0.5 * (toneA + toneB);

        // 2) Products placed from the MEASURED tone bins (carrier error
        //    cancels), searched a quarter-spacing either side.
        int prodHalf = Math.Max(1, spacingPx / 4);
        double limitDb = NoiseLimit(bins, Math.Min(pxA, pxB), Math.Max(pxA, pxB), spacingPx, prodHalf, floorDb);

        if (!PeakAt(bins, 2 * pxA - pxB, prodHalf, out double p3a)) return false;
        if (!PeakAt(bins, 2 * pxB - pxA, prodHalf, out double p3b)) return false;
        double p3 = Math.Max(p3a, p3b);
        bool bound3 = p3 < limitDb;
        double imd3 = (bound3 ? limitDb : p3) - toneMean;

        // 3) 5th order two spacings outboard. Optional — off-display is fine.
        double imd5 = double.NaN;
        bool bound5 = false;
        if (PeakAt(bins, 3 * pxA - 2 * pxB, prodHalf, out double p5a)
            && PeakAt(bins, 3 * pxB - 2 * pxA, prodHalf, out double p5b))
        {
            double p5 = Math.Max(p5a, p5b);
            bound5 = p5 < limitDb;
            imd5 = (bound5 ? limitDb : p5) - toneMean;
        }
        result = new ImdMeasurement(imd3, bound3, imd5, bound5);
        return true;
    }

    // The level a product must reach to be a measurement: the 95th percentile
    // of product-sized window maxima over line-free pixels, plus a margin.
    // Falls back to the old median rule when the display has too little
    // line-free spectrum to estimate it (and never goes below that rule).
    private static double NoiseLimit(
        ReadOnlySpan<float> bins, int loTonePx, int hiTonePx, int spacingPx, int prodHalf, double floorDb)
    {
        double medianRule = floorDb + MinProductAboveFloorDb;
        int w = 2 * prodHalf + 1;
        int exclLo = loTonePx - NoiseExclusionSpacings * spacingPx;
        int exclHi = hiTonePx + NoiseExclusionSpacings * spacingPx;
        var maxes = new List<double>();
        for (int s = 0; s + w <= bins.Length; s += w)
        {
            if (s < exclHi && s + w > exclLo) continue;   // overlaps the lines' neighbourhood
            double m = double.NegativeInfinity;
            bool any = false;
            for (int i = s; i < s + w; i++)
            {
                float v = bins[i];
                if (!float.IsFinite(v)) continue;
                any = true;
                if (v > m) m = v;
            }
            if (any) maxes.Add(m);
        }
        if (maxes.Count < MinNoiseWindows) return medianRule;
        maxes.Sort();
        int idx = Math.Clamp((int)Math.Ceiling(NoisePercentile * maxes.Count) - 1, 0, maxes.Count - 1);
        return Math.Max(maxes[idx] + ProductAboveNoiseMaxDb, medianRule);
    }

    private static bool ArgMaxNear(
        ReadOnlySpan<float> bins, float hzPerPixel, long centerHz, double targetHz, int half,
        out int peakPx, out double peakDb)
    {
        peakPx = -1;
        peakDb = double.NegativeInfinity;
        int n = bins.Length;
        int centerPx = (int)Math.Round(n / 2.0 + (targetHz - centerHz) / hzPerPixel);
        int from = centerPx - half, to = centerPx + half;
        if (from < 0 || to >= n) return false;
        for (int i = from; i <= to; i++)
        {
            float v = bins[i];
            if (!float.IsFinite(v)) continue;
            if (v > peakDb) { peakDb = v; peakPx = i; }
        }
        return peakPx >= 0;
    }

    private static bool PeakAt(ReadOnlySpan<float> bins, int centerPx, int half, out double peakDb)
    {
        peakDb = double.NegativeInfinity;
        int from = centerPx - half, to = centerPx + half;
        if (from < 0 || to >= bins.Length) return false;
        bool any = false;
        for (int i = from; i <= to; i++)
        {
            float v = bins[i];
            if (!float.IsFinite(v)) continue;
            any = true;
            if (v > peakDb) peakDb = v;
        }
        return any;
    }

    private static double MedianFloor(ReadOnlySpan<float> bins)
    {
        // Median of the finite bins — robust to the tones and products, which
        // occupy a handful of pixels out of the whole display.
        Span<float> tmp = bins.Length <= 4096 ? stackalloc float[bins.Length] : new float[bins.Length];
        int k = 0;
        foreach (float v in bins) if (float.IsFinite(v)) tmp[k++] = v;
        if (k == 0) return double.NegativeInfinity;
        var s = tmp.Slice(0, k);
        s.Sort();
        return s[k / 2];
    }
}
