// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugins.Host;

/// <summary>
/// The standard per-OS install folders for audio plug-ins — what the Audio
/// Suite's one-click "Scan VSTs" sweeps. Folders that do not exist are left
/// in: the scan treats a missing folder as simply absent, and listing it tells
/// the operator where plug-ins are expected.
/// </summary>
public static class PluginSearchPaths
{
    /// <summary>VST3 locations from the VST3 SDK's "Plug-in Locations" spec,
    /// plus Windows' long-standing <c>C:\VST PLUGINS</c> manual-install
    /// convention (Zeus's historical scan default).</summary>
    public static IReadOnlyList<string> DefaultVst3Directories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Distinct(
                Path.Combine(string.IsNullOrEmpty(common) ? @"C:\Program Files\Common Files" : common, "VST3"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs", "Common", "VST3"),
                @"C:\VST PLUGINS");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Distinct(
                "/Library/Audio/Plug-Ins/VST3",
                string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3"));
        }
        return Distinct(
            string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".vst3"),
            "/usr/lib/vst3",
            "/usr/local/lib/vst3");
    }

    /// <summary>CLAP locations from the CLAP spec's "Plug-in location"
    /// section (entry.h).</summary>
    public static IReadOnlyList<string> DefaultClapDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Distinct(
                Path.Combine(string.IsNullOrEmpty(common) ? @"C:\Program Files\Common Files" : common, "CLAP"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Programs", "Common", "CLAP"));
        }
        if (OperatingSystem.IsMacOS())
        {
            return Distinct(
                "/Library/Audio/Plug-Ins/CLAP",
                string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Library", "Audio", "Plug-Ins", "CLAP"));
        }
        return Distinct(
            string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".clap"),
            "/usr/lib/clap");
    }

    /// <summary>Every standard plug-in folder the one-click scan sweeps:
    /// VST3 then CLAP.</summary>
    public static IReadOnlyList<string> DefaultPluginDirectories() =>
        Distinct([.. DefaultVst3Directories(), .. DefaultClapDirectories()]);

    private static IReadOnlyList<string> Distinct(params string?[] paths) =>
        paths.Where(p => !string.IsNullOrEmpty(p))
             .Select(p => p!)
             .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
             .ToList();
}
