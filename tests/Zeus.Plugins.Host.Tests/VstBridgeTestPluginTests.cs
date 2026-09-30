// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Real VST3 hosting against the in-tree test plug-in
/// (<c>native/zeus-vst-bridge/test-plugin</c>, built alongside the bridge by
/// CMake). Unlike <see cref="VstBridgeNativeRealTests"/>'s opt-in
/// ZEUS_VST_TEST_PATH case, this runs wherever the bridge is built — CI
/// included — so loading, bus negotiation and processing are verified on
/// every platform. Skips when the bridge or the test bundle is absent.
/// </summary>
public class VstBridgeTestPluginTests
{
    private const string GainUid = "5A455553544553544741494E00000001";
    private const string InvertUid = "5A45555354455354494E565400000002";

    private static string? FindTestPlugin()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_VST_TEST_PLUGIN");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "zeus-vst-bridge", "build",
                "test-plugin", "ZeusTestPlugin.vst3");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool BridgeBuilt()
    {
        string name =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libzeus-vst-bridge.dylib" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "libzeus-vst-bridge.so" :
            "zeus-vst-bridge.dll";
        return File.Exists(Path.Combine(AppContext.BaseDirectory, name));
    }

    private static readonly string? TestPlugin = FindTestPlugin();
    private static readonly bool Skip = TestPlugin is null || !BridgeBuilt();
    private const string SkipReason =
        "Bridge / ZeusTestPlugin.vst3 not built — run cmake under native/zeus-vst-bridge/ with the vst3sdk submodule initialised.";

    private static VstBridgeNative Bridge()
    {
        var b = new VstBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, b.Init(VstBridgeAbi.Current));
        return b;
    }

    private static float[] Ramp(int channels, int frames)
    {
        var x = new float[channels * frames];
        for (int i = 0; i < x.Length; i++) x[i] = (i % frames) / (float)frames - 0.5f;
        return x;
    }

    [SkippableFact]
    public void Describe_ListsBothEffectClasses()
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        try
        {
            var classes = VstBridgeNative.Scan(bridge, TestPlugin!);
            Assert.Equal(2, classes.Count);
            Assert.Contains(classes, c => c.Name == "Zeus Test Gain" && c.Uid == GainUid);
            Assert.Contains(classes, c => c.Name == "Zeus Test Invert" && c.Uid == InvertUid);
            Assert.All(classes, c => Assert.Equal("Zeus Test", c.Vendor));
        }
        finally { bridge.Shutdown(); }
    }

    // The plug-in declares a stereo sidechain input and refuses any
    // setBusArrangements call that doesn't name every bus — the load must
    // negotiate all buses and still come out unity at the default gain.
    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void Load_WithSidechainBus_ProcessesAtUnityGain(int channels)
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        try
        {
            const int frames = 256;
            Assert.Equal(VstBridgeStatus.Ok,
                bridge.LoadVst3(TestPlugin!, channels, 48000, frames, out var handle));
            try
            {
                var input = Ramp(channels, frames);
                var output = new float[input.Length];
                Assert.Equal(VstBridgeStatus.Ok, bridge.Process(handle, input, output, frames));
                for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i], 5);
                Assert.Equal(0, bridge.GetLatencySamples(handle));
            }
            finally { Assert.Equal(VstBridgeStatus.Ok, bridge.Unload(handle)); }
        }
        finally { bridge.Shutdown(); }
    }

    [SkippableFact]
    public void SetParameter_ReachesProcessor()
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        try
        {
            const int frames = 128;
            Assert.Equal(VstBridgeStatus.Ok,
                bridge.LoadVst3(TestPlugin!, 1, 48000, frames, out var handle));
            try
            {
                Assert.Equal(VstBridgeStatus.Ok, bridge.SetParameter(handle, 0, 1.0)); // gain x2
                var input = Ramp(1, frames);
                var output = new float[frames];
                Assert.Equal(VstBridgeStatus.Ok, bridge.Process(handle, input, output, frames));
                for (int i = 0; i < frames; i++) Assert.Equal(2f * input[i], output[i], 5);
            }
            finally { bridge.Unload(handle); }
        }
        finally { bridge.Shutdown(); }
    }

    [SkippableFact]
    public void LoadVst3Class_PicksTheNamedClass()
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        try
        {
            const int frames = 128;
            Assert.Equal(VstBridgeStatus.Ok,
                bridge.LoadVst3Class(TestPlugin!, InvertUid, 1, 48000, frames, out var handle));
            try
            {
                var input = Ramp(1, frames);
                var output = new float[frames];
                Assert.Equal(VstBridgeStatus.Ok, bridge.Process(handle, input, output, frames));
                for (int i = 0; i < frames; i++) Assert.Equal(-input[i], output[i], 5);
            }
            finally { bridge.Unload(handle); }

            Assert.Equal(VstBridgeStatus.NoAudioEffectClass,
                bridge.LoadVst3Class(TestPlugin!, "00000000000000000000000000000000", 1, 48000, frames, out _));
        }
        finally { bridge.Shutdown(); }
    }

    [SkippableFact]
    public void State_RoundTripsIntoAFreshInstance()
    {
        Xunit.Skip.If(Skip, SkipReason);
        var bridge = Bridge();
        try
        {
            const int frames = 128;
            Assert.Equal(VstBridgeStatus.Ok, bridge.LoadVst3(TestPlugin!, 1, 48000, frames, out var a));
            byte[] saved;
            try
            {
                bridge.SetParameter(a, 0, 0.75); // gain x1.5, applied by the next block
                var scratch = new float[frames];
                bridge.Process(a, Ramp(1, frames), scratch, frames);
                Assert.Equal(VstBridgeStatus.Ok, bridge.GetState(a, out saved));
                Assert.True(saved.Length > 12);
            }
            finally { bridge.Unload(a); }

            Assert.Equal(VstBridgeStatus.Ok, bridge.LoadVst3(TestPlugin!, 1, 48000, frames, out var b));
            try
            {
                Assert.Equal(VstBridgeStatus.Ok, bridge.SetState(b, saved));
                var input = Ramp(1, frames);
                var output = new float[frames];
                Assert.Equal(VstBridgeStatus.Ok, bridge.Process(b, input, output, frames));
                for (int i = 0; i < frames; i++) Assert.Equal(1.5f * input[i], output[i], 5);
                Assert.Equal(VstBridgeStatus.InvalidArguments, bridge.SetState(b, new byte[] { 1, 2, 3 }));
            }
            finally { bridge.Unload(b); }
        }
        finally { bridge.Shutdown(); }
    }
}
