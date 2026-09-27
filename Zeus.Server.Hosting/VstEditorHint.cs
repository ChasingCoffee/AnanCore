// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// VstEditorHint — picks the operator-facing message when a plugin editor can't
// be opened because no host will load the plugin in the current mode. Two cases:
//   * VST processing mode selected but the out-of-process engine isn't routing.
//   * Native processing mode while the in-process bridge won't host the plugin
//     (TX native load stays opt-in — a crashing in-process TX VST takes the
//     radio down).
// In both, the bare in-process bridge would surface a "set ZEUS_ENABLE_VST_LOAD=1"
// hint — a developer-only escape hatch that points a new operator at the wrong
// (and unsafe) fix. ANAN Core has retired the out-of-process engine, so the
// only remaining guidance is about the in-process bridge: a plugin that is not
// hosted is either parked or failed to load.

namespace Zeus.Server;

internal static class VstEditorHint
{
    /// <summary>
    /// The operator-facing error to return when an editor open can't succeed, or
    /// <c>null</c> when the open should be attempted normally — i.e. the engine
    /// is routing, or Native mode with the in-process bridge able to host the
    /// plugin (<paramref name="nativeLoadEnabled"/> is true).
    /// </summary>
    /// <param name="nativeLoadEnabled">Whether the in-process bridge will host
    /// this plugin in Native mode. RX VSTs load in-process by default; TX VSTs
    /// only when <c>ZEUS_ENABLE_VST_LOAD=1</c>. When false, the in-process editor
    /// can't open, so point the operator at the out-of-process engine instead.</param>
    public static string? EngineUnavailableMessage(
        AudioProcessingMode mode, bool engineActive, bool engineInstalled,
        bool nativeLoadEnabled = true)
    {
        // The out-of-process engine is retired in ANAN Core, so no message may
        // send the operator to it: no "Download VST Engine", no "switch to VST
        // mode". mode / engineActive / engineInstalled are kept in the signature
        // for the existing callers; only an engine that is somehow routing still
        // short-circuits, and it never is.
        if (engineActive)
            return null;
        // The in-process bridge will host this plugin: attempt the open normally.
        if (nativeLoadEnabled)
            return null;
        // Not hosted in-process: the plugin is parked, or it failed to load.
        return "This VST isn't loaded, so there is no editor to open. Move it into the "
            + "active chain; if it is already there, it failed to load — check that it is "
            + "a VST3 audio effect built for this computer.";
    }
}
