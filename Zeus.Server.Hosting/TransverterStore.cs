// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text.Json;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the transverter band list (one JSON document in zeus-prefs.db) and
/// applies it to <see cref="Transverters"/>. Constructed eagerly at startup so
/// the bands are live before the first tune.
/// </summary>
public sealed class TransverterStore : IDisposable
{
    private const string Key = "default";
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<TransverterEntry> _coll;
    private readonly object _sync = new();

    public TransverterStore()
    {
        var dbPath = PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _coll = _dbLease.Database.GetCollection<TransverterEntry>("transverters");
        _coll.EnsureIndex(x => x.Key, unique: true);
        Transverters.Apply(Get().Bands);
    }

    public TransverterSettingsDto Get()
    {
        lock (_sync)
        {
            var e = _coll.FindOne(x => x.Key == Key);
            if (e?.Json is not { Length: > 0 } json) return new TransverterSettingsDto([]);
            try
            {
                var bands = System.Text.Json.JsonSerializer.Deserialize<List<TransverterBandDto>>(json) ?? [];
                return new TransverterSettingsDto(bands);
            }
            catch (JsonException)
            {
                return new TransverterSettingsDto([]);
            }
        }
    }

    /// <summary>Validate, persist and apply. Returns an error, or null on success.</summary>
    public string? Set(IReadOnlyList<TransverterBandDto> bands)
    {
        var clean = bands.Select(b => b with { Name = b.Name?.Trim() ?? "" }).ToList();
        if (Transverters.Validate(clean) is string err) return err;
        lock (_sync)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(clean);
            var e = _coll.FindOne(x => x.Key == Key);
            if (e is null) _coll.Insert(new TransverterEntry { Key = Key, Json = json });
            else { e.Json = json; _coll.Update(e); }
        }
        Transverters.Apply(clean);
        return null;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class TransverterEntry
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
}
