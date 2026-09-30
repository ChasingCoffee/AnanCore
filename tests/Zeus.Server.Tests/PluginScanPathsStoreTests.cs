// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Host;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class PluginScanPathsStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"zeus-scanpaths-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_db); } catch { /* ignore */ }
    }

    [Fact]
    public void Unset_UsesTheStandardFolders()
    {
        using var store = new PluginScanPathsStore(_db);
        var p = store.Get();
        Assert.Equal(PluginSearchPaths.DefaultVst3Directories(), p.Vst3);
        Assert.Equal(PluginSearchPaths.DefaultClapDirectories(), p.Clap);
        Assert.False(p.Vst3Custom);
        Assert.False(p.ClapCustom);
    }

    [Fact]
    public void Set_PersistsCleanedLists_AndEmptyMeansDefaults()
    {
        using (var store = new PluginScanPathsStore(_db))
            store.Set([" /a/vst3 ", "", "/b/vst3", "/b/vst3"], null);

        using var reopened = new PluginScanPathsStore(_db);
        var p = reopened.Get();
        Assert.Equal(new[] { "/a/vst3", "/b/vst3" }, p.Vst3);
        Assert.True(p.Vst3Custom);
        Assert.Equal(PluginSearchPaths.DefaultClapDirectories(), p.Clap);
        Assert.False(p.ClapCustom);

        p = reopened.Set([], ["/c/clap"]);
        Assert.False(p.Vst3Custom);
        Assert.Equal(new[] { "/c/clap" }, p.Clap);
    }
}
