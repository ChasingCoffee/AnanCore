// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server;

// Persists each hosted VST3 / Audio Unit plugin's native state — the blob the
// bridge serialises from the plugin's own getState — keyed by Zeus plugin id,
// so a plugin comes back with the settings the operator left it at. The blob
// is opaque here: only the same plugin class can read it back.
public sealed class AudioPluginStateStore : IPluginStateStore, IPluginBypassStore, IDisposable
{
    // Guard against a runaway plugin: a few MB covers any audio effect's
    // state; anything larger is refused rather than bloating zeus-prefs.db.
    public const int MaxStateBytes = 8 * 1024 * 1024;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<AudioPluginStateEntry> _docs;
    private readonly ILiteCollection<AudioPluginBypassEntry> _bypass;
    private readonly ILogger<AudioPluginStateStore> _log;
    private readonly object _sync = new();

    public AudioPluginStateStore(ILogger<AudioPluginStateStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _docs = _dbLease.Database.GetCollection<AudioPluginStateEntry>("audio_plugin_state");
        _bypass = _dbLease.Database.GetCollection<AudioPluginBypassEntry>("audio_plugin_bypass");
    }

    public byte[]? Load(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return null;
        lock (_sync)
            return _docs.FindById(pluginId)?.State;
    }

    public void Save(string pluginId, string format, byte[] state)
    {
        if (string.IsNullOrEmpty(pluginId)) throw new ArgumentException("plugin id is required", nameof(pluginId));
        ArgumentNullException.ThrowIfNull(state);
        if (state.Length > MaxStateBytes)
        {
            _log.LogWarning("Not saving {Bytes}-byte state for {Id}: over the {Max}-byte limit.",
                state.Length, pluginId, MaxStateBytes);
            return;
        }
        lock (_sync)
        {
            _docs.Upsert(new AudioPluginStateEntry
            {
                Id = pluginId,
                Format = format,
                State = state,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
    }

    public bool Delete(string pluginId)
    {
        lock (_sync)
            return _docs.Delete(pluginId);
    }

    public IReadOnlyCollection<string> GetBypassedIds()
    {
        lock (_sync)
            return _bypass.FindAll().Select(e => e.Id).ToArray();
    }

    // Only bypassed plugins have a row; un-bypassing deletes it.
    public void SetBypassed(string pluginId, bool bypassed)
    {
        if (string.IsNullOrEmpty(pluginId)) throw new ArgumentException("plugin id is required", nameof(pluginId));
        lock (_sync)
        {
            if (bypassed) _bypass.Upsert(new AudioPluginBypassEntry { Id = pluginId, UpdatedUtc = DateTime.UtcNow });
            else _bypass.Delete(pluginId);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class AudioPluginBypassEntry
{
    public string Id { get; set; } = "";  // Zeus plugin id
    public DateTime UpdatedUtc { get; set; }
}

public sealed class AudioPluginStateEntry
{
    public string Id { get; set; } = "";          // Zeus plugin id
    public string Format { get; set; } = "vst3";  // "vst3" | "au"
    public byte[] State { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}
