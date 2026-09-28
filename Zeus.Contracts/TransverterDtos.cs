// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Contracts;

/// <summary>
/// One transverter band, piHPSDR's model (band.c: frequencyMin/Max,
/// frequencyLO, errorLO). The operator tunes RF (MinHz..MaxHz); the radio is
/// sent RF - (LoHz + LoErrorHz). Everything else — PA disable, open-collector
/// bits, TX/RX antenna, the XVTR input — comes from the existing per-band
/// tables, keyed by <see cref="Name"/>.
/// </summary>
public sealed record TransverterBandDto(
    int Id,
    bool Enabled = false,
    string Name = "",
    long MinHz = 0,
    long MaxHz = 0,
    long LoHz = 0,
    long LoErrorHz = 0);

/// <summary>GET/PUT /api/radio/transverters.</summary>
public sealed record TransverterSettingsDto(IReadOnlyList<TransverterBandDto> Bands);
