// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Plugins.Host;

namespace Zeus.Server;

// The folders the Audio Suite's Scan VST3 / Scan CLAP buttons sweep, set by
// the operator in "Set paths". They are paths on the SERVER's file system, so
// they live here rather than in the browser. A format with nothing saved uses
// the OS's standard folders (PluginSearchPaths).
public sealed class PluginScanPathsStore : IDisposable
{
    private const string DocId = "scan-paths";

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<PluginScanPathsEntry> _docs;
    private readonly object _sync = new();

    public PluginScanPathsStore(string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _docs = _dbLease.Database.GetCollection<PluginScanPathsEntry>("audio_plugin_scan_paths");
    }

    public PluginScanPaths Get()
    {
        PluginScanPathsEntry? e;
        lock (_sync) e = _docs.FindById(DocId);
        return new PluginScanPaths(
            e?.Vst3 is { Count: > 0 } v ? v : PluginSearchPaths.DefaultVst3Directories(),
            e?.Clap is { Count: > 0 } c ? c : PluginSearchPaths.DefaultClapDirectories(),
            e?.Vst3 is { Count: > 0 },
            e?.Clap is { Count: > 0 });
    }

    /// <summary>Save the folder lists; an empty or null list means "the standard folders".</summary>
    public PluginScanPaths Set(IEnumerable<string>? vst3, IEnumerable<string>? clap)
    {
        lock (_sync)
        {
            _docs.Upsert(new PluginScanPathsEntry
            {
                Id = DocId,
                Vst3 = Clean(vst3),
                Clap = Clean(clap),
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        return Get();
    }

    private static List<string> Clean(IEnumerable<string>? paths) =>
        (paths ?? [])
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

    public void Dispose() => _dbLease.Dispose();
}

/// <param name="Vst3Custom">True when the VST3 list was set by the operator.</param>
/// <param name="ClapCustom">True when the CLAP list was set by the operator.</param>
public sealed record PluginScanPaths(
    IReadOnlyList<string> Vst3, IReadOnlyList<string> Clap, bool Vst3Custom, bool ClapCustom);

public sealed class PluginScanPathsEntry
{
    public string Id { get; set; } = "";
    public List<string> Vst3 { get; set; } = [];
    public List<string> Clap { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}
