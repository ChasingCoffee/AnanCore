// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Host.Audio;

namespace Zeus.Server.Tests;

/// <summary>
/// Plugin probing through the real OpenhpsdrZeus host binary (in this test
/// project's output) against the in-tree test VST3, whose ZEUS_TEST_VST_FAULT
/// modes make it crash or hang on demand. Skips where the bridge or the test
/// plug-in hasn't been built. Not parallel: the fault mode is an environment
/// variable the probe child inherits.
/// </summary>
[Collection("PluginProbe")]
public class PluginProbeTests : IDisposable
{
    private const string InvertUid = "5A45555354455354494E565400000002";

    private static readonly string? Plugin = FindTestPlugin();
    private static readonly PluginProbeRunner? Runner = CreateRunner();

    private static string? FindTestPlugin()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "zeus-vst-bridge", "build",
                "test-plugin", "ZeusTestPlugin.vst3");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static PluginProbeRunner? CreateRunner()
    {
        var exe = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "OpenhpsdrZeus.exe" : "OpenhpsdrZeus");
        if (!File.Exists(exe) || Plugin is null) return null;
        var runner = new PluginProbeRunner(exe)
        {
            DescribeTimeout = TimeSpan.FromSeconds(20),
            LoadTimeout = TimeSpan.FromSeconds(20),
        };
        // Usable only if this output's bridge can actually read the test plug-in.
        return runner.Describe(Plugin).Ok ? runner : null;
    }

    private const string SkipReason =
        "Probe host, native bridge or ZeusTestPlugin.vst3 not available — build native/zeus-vst-bridge with cmake.";

    public PluginProbeTests() => Environment.SetEnvironmentVariable("ZEUS_TEST_VST_FAULT", null);

    public void Dispose() => Environment.SetEnvironmentVariable("ZEUS_TEST_VST_FAULT", null);

    [SkippableFact]
    public void Describe_ReturnsEveryEffectClass()
    {
        Skip.If(Runner is null, SkipReason);
        var result = Runner!.Describe(Plugin!);
        Assert.True(result.Ok, result.Message);
        var names = result.Reply!.Classes!.Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "Zeus Test Gain", "Zeus Test Invert" }, names);
    }

    [SkippableFact]
    public void TrialLoad_OfAChosenClass_Succeeds()
    {
        Skip.If(Runner is null, SkipReason);
        var result = Runner!.TrialLoad("vst3", Plugin!, InvertUid);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, result.Reply!.LatencySamples);
    }

    [SkippableFact]
    public void Describe_OfAPluginThatCrashes_ReportsCrashed()
    {
        Skip.If(Runner is null, SkipReason);
        Environment.SetEnvironmentVariable("ZEUS_TEST_VST_FAULT", "crash-scan");
        var result = Runner!.Describe(Plugin!);
        Assert.Equal(ProbeOutcome.Crashed, result.Outcome);
    }

    [SkippableFact]
    public void Describe_OfAPluginThatHangs_TimesOutAndIsKilled()
    {
        Skip.If(Runner is null, SkipReason);
        Environment.SetEnvironmentVariable("ZEUS_TEST_VST_FAULT", "hang-scan");
        var quick = new PluginProbeRunner(Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "OpenhpsdrZeus.exe" : "OpenhpsdrZeus"))
        {
            DescribeTimeout = TimeSpan.FromSeconds(3),
        };
        var started = DateTime.UtcNow;
        var result = quick.Describe(Plugin!);
        Assert.Equal(ProbeOutcome.TimedOut, result.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [SkippableFact]
    public void LoadGuard_RefusesACrashingPlugin_AndRemembersIt()
    {
        Skip.If(Runner is null, SkipReason);
        var cache = new PluginProbeCache(file: null);
        var guard = new ProbingPluginLoadGuard(Runner, cache);

        Environment.SetEnvironmentVariable("ZEUS_TEST_VST_FAULT", "crash-process");
        var (allowed, reason) = guard.Check("vst3", Plugin!, InvertUid);
        Assert.False(allowed);
        Assert.Contains("crashed", reason);

        // The verdict is cached for this plugin version: no second probe (the
        // fault is gone now, so a fresh probe would pass).
        Environment.SetEnvironmentVariable("ZEUS_TEST_VST_FAULT", null);
        Assert.False(guard.Check("vst3", Plugin!, InvertUid).Allowed);

        // Another class of the same file is probed on its own.
        Assert.True(guard.Check("vst3", Plugin!, null).Allowed);
    }

    [Fact]
    public void LoadGuard_WithoutAProbeHost_AllowsEverything()
    {
        var guard = new ProbingPluginLoadGuard(runner: null, new PluginProbeCache(file: null));
        Assert.True(guard.Check("vst3", "/no/such/plugin.vst3", null).Allowed);
    }

    [Fact]
    public void ProbeCache_PersistsAndInvalidatesOnStampChange()
    {
        var file = Path.Combine(Path.GetTempPath(), $"zeus-probe-cache-{Guid.NewGuid():N}.json");
        try
        {
            var a = new PluginProbeCache(file);
            a.Put("k", new PluginProbeCache.Entry("stamp1", ProbeOutcome.Crashed, "boom", DateTime.UtcNow));

            var b = new PluginProbeCache(file);
            Assert.Equal(ProbeOutcome.Crashed, b.Get("k", "stamp1")!.Outcome);
            Assert.Null(b.Get("k", "stamp2")); // the plugin changed: probe again
            b.Forget("k");
            Assert.Null(new PluginProbeCache(file).Get("k", "stamp1"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

[CollectionDefinition("PluginProbe", DisableParallelization = true)]
public class PluginProbeCollection { }
