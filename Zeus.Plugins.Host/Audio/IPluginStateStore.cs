// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Where a hosted plugin's native state (its knob positions, as the VST3 / AU
/// bridge serialises them) is kept between sessions, keyed by the Zeus plugin
/// id. The server backs this with zeus-prefs.db; tests use an in-memory map.
/// </summary>
public interface IPluginStateStore
{
    /// <summary>The saved blob for <paramref name="pluginId"/>, or null.</summary>
    byte[]? Load(string pluginId);

    /// <summary>Save (replace) the blob for <paramref name="pluginId"/>.
    /// <paramref name="format"/> is the manifest's audio format ("vst3" / "au").</summary>
    void Save(string pluginId, string format, byte[] state);
}

/// <summary>
/// The operator's per-plugin bypass switches (a bypassed plugin stays loaded
/// and in the chain but passes audio straight through), keyed by plugin id.
/// </summary>
public interface IPluginBypassStore
{
    IReadOnlyCollection<string> GetBypassedIds();
    void SetBypassed(string pluginId, bool bypassed);
}
