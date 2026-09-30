// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Real CLAP hosting through <see cref="ClapBridgeNative"/> against the
/// in-tree test CLAP (built next to the bridge by CMake), plus a real scan of
/// the fixtures folder holding both test plug-ins. Skips where the bridge or
/// the fixtures haven't been built.
/// </summary>
public class ClapBridgeTestPluginTests
{
    private const string GainId = "org.openhpsdr.zeus.test.gain";
    private const string InvertId = "org.openhpsdr.zeus.test.invert";

    private static string? FixturesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "zeus-vst-bridge", "build", "test-plugin");
            if (Directory.Exists(Path.Combine(candidate, "ZeusTestPlugin.clap")) ||
                File.Exists(Path.Combine(candidate, "ZeusTestPlugin.clap")))
                return candidate;
        }
        return null;
    }

    private static bool BridgeBuilt() => File.Exists(Path.Combine(AppContext.BaseDirectory,
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libzeus-vst-bridge.dylib" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "libzeus-vst-bridge.so" :
        "zeus-vst-bridge.dll"));

    private static readonly string? Fixtures = FixturesDir();
    private static readonly string? Clap = Fixtures is null ? null : Path.Combine(Fixtures, "ZeusTestPlugin.clap");
    private static readonly bool Skip = Clap is null || !BridgeBuilt();
    private const string SkipReason = "Bridge / ZeusTestPlugin.clap not built — run cmake under native/zeus-vst-bridge/.";

    private static ClapBridgeNative Bridge()
    {
        var b = new ClapBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, b.Init(VstBridgeAbi.Current));
        return b;
    }

    private static float[] Ramp(int channels, int frames)
    {
        var x = new float[channels * frames];
        for (int i = 0; i < x.Length; i++) x[i] = (i % frames) / (float)frames - 0.5f;
        return x;
    }

    private static void AssertGain(ClapBridgeNative bridge, nint handle, int channels, int frames, float k)
    {
        var input = Ramp(channels, frames);
        var output = new float[input.Length];
        Assert.Equal(VstBridgeStatus.Ok, bridge.Process(handle, input, output, frames));
        for (int i = 0; i < input.Length; i++) Assert.Equal(k * input[i], output[i], 5);
    }

    [SkippableFact]
    public void Describe_ListsEveryPlugin_WithFeatures()
    {
        Xunit.Skip.If(Skip, SkipReason);
        var classes = VstBridgeNative.Scan(Bridge(), Clap!);
        Assert.Equal(3, classes.Count);
        Assert.Contains(classes, c => c.Uid == GainId && c.Name == "Zeus Test Gain" && c.Category.Contains("audio-effect"));
        Assert.Contains(classes, c => c.Uid == "org.openhpsdr.zeus.test.synth" && c.Category.Contains("instrument"));
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void Load_Process_And_SelectById(int channels)
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        Assert.Equal(VstBridgeStatus.Ok, bridge.LoadVst3(Clap!, channels, 48000, 256, out var gain));
        try { AssertGain(bridge, gain, channels, 256, 1f); }
        finally { bridge.Unload(gain); }

        Assert.Equal(VstBridgeStatus.Ok, bridge.LoadVst3Class(Clap!, InvertId, channels, 48000, 256, out var inv));
        try { AssertGain(bridge, inv, channels, 256, -1f); }
        finally { bridge.Unload(inv); }
    }

    [SkippableFact]
    public void State_And_Params_RoundTrip()
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        Assert.Equal(VstBridgeStatus.Ok, bridge.LoadVst3Class(Clap!, GainId, 1, 48000, 128, out var a));
        byte[] saved;
        try
        {
            Assert.Equal(VstBridgeStatus.Ok, bridge.SetParameter(a, 0, 0.75)); // plain 0..2 -> 1.5
            AssertGain(bridge, a, 1, 128, 1.5f);
            Assert.Equal(VstBridgeStatus.Ok, bridge.GetState(a, out saved));
        }
        finally { bridge.Unload(a); }

        Assert.Equal(VstBridgeStatus.Ok, bridge.LoadVst3Class(Clap!, GainId, 1, 48000, 128, out var b));
        try
        {
            Assert.Equal(VstBridgeStatus.Ok, bridge.SetState(b, saved));
            AssertGain(bridge, b, 1, 128, 1.5f);
        }
        finally { bridge.Unload(b); }
    }

    [SkippableFact]
    public async Task Scan_RegistersVst3AndClapEffects_SkippingInstruments()
    {
        Xunit.Skip.If(Skip || !Directory.Exists(Path.Combine(Fixtures!, "ZeusTestPlugin.vst3")), SkipReason);
        var root = Path.Combine(Path.GetTempPath(), "zeus-clapscan-" + Guid.NewGuid().ToString("N"));
        var store = new PluginSettingsStore(Path.Combine(root, "settings.db"));
        var manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: store,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = Path.Combine(root, "plugins") });
        try
        {
            var scanner = new VstDirectoryScanService(manager, Path.Combine(root, "plugins"),
                NullLogger<VstDirectoryScanService>.Instance);
            var result = await scanner.ScanAsync(Fixtures!, "tx", default);

            var names = result.Registered.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "Zeus Test Gain", "Zeus Test Gain (CLAP)", "Zeus Test Invert", "Zeus Test Invert (CLAP)",
            }, names);

            var clap = result.Registered.Single(r => r.Name == "Zeus Test Invert (CLAP)");
            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "plugins", clap.Id, "plugin.json")));
            var audio = manifest.RootElement.GetProperty("audio");
            Assert.Equal("clap", audio.GetProperty("format").GetString());
            Assert.Equal(InvertId, audio.GetProperty("vst3Uid").GetString());
        }
        finally
        {
            await manager.DisposeAsync();
            store.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [SkippableTheory]
    [InlineData("clap", new[] { "Zeus Test Gain (CLAP)", "Zeus Test Invert (CLAP)" })]
    [InlineData("vst3", new[] { "Zeus Test Gain", "Zeus Test Invert" })]
    public async Task Scan_WithAFormat_RegistersOnlyThatFormat(string format, string[] expected)
    {
        Xunit.Skip.If(Skip || !Directory.Exists(Path.Combine(Fixtures!, "ZeusTestPlugin.vst3")), SkipReason);
        var root = Path.Combine(Path.GetTempPath(), "zeus-clapscan-" + Guid.NewGuid().ToString("N"));
        var store = new PluginSettingsStore(Path.Combine(root, "settings.db"));
        var manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: store,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = Path.Combine(root, "plugins") });
        try
        {
            var scanner = new VstDirectoryScanService(manager, Path.Combine(root, "plugins"),
                NullLogger<VstDirectoryScanService>.Instance);
            var result = await scanner.ScanAsync(Fixtures!, "tx", default, format);

            var names = result.Registered.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, names);
        }
        finally
        {
            await manager.DisposeAsync();
            store.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
