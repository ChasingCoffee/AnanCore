// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Per-plugin bypass: a bypassed plugin stays loaded and keeps its chain
/// position, its slot passes audio through untouched, the choice survives
/// every re-slot, and it is persisted. Plus the store behind it.
/// </summary>
public sealed class AudioPluginBridgePluginBypassTests
{
    private const string PluginId = "com.openhpsdr.zeus.vst.doubler";

    [Fact]
    public void BypassedPlugin_PassesAudioThrough_AndStaysSlotted()
    {
        using var fx = new Fixture();
        var (bridge, store) = NewBridge(fx.Service);
        var doubler = new DoublerPlugin();
        Attach(bridge, fx.Service, doubler);

        Assert.Equal(new[] { 2f, 4f }, Run(bridge, [1f, 2f]));

        Assert.True(bridge.SetPluginBypassed(PluginId, true));
        Assert.Equal(new[] { 1f, 2f }, Run(bridge, [1f, 2f]));
        Assert.Same(doubler, TxChain(bridge).GetSlot(0)); // still in position
        Assert.Contains(PluginId, bridge.BypassedPluginIds);
        Assert.Contains(PluginId, store.Bypassed);

        // Reorders / re-slots keep it bypassed.
        InvokePrivate(bridge, "ReapplySlotsUnderLock", []);
        Assert.Equal(new[] { 1f, 2f }, Run(bridge, [1f, 2f]));

        Assert.True(bridge.SetPluginBypassed(PluginId, false));
        Assert.Equal(new[] { 2f, 4f }, Run(bridge, [1f, 2f]));
        Assert.DoesNotContain(PluginId, store.Bypassed);
    }

    [Fact]
    public void SetPluginBypassed_UnknownPlugin_ReturnsFalse()
    {
        using var fx = new Fixture();
        var (bridge, store) = NewBridge(fx.Service);
        Assert.False(bridge.SetPluginBypassed("com.example.nope", true));
        Assert.Empty(store.Bypassed);
    }

    [Fact]
    public void StateStore_RoundTripsStateAndBypass()
    {
        var db = Path.Combine(Path.GetTempPath(), $"zeus-plugin-state-{Guid.NewGuid():N}.db");
        try
        {
            using (var s = new AudioPluginStateStore(NullLogger<AudioPluginStateStore>.Instance, db))
            {
                s.Save(PluginId, "vst3", [1, 2, 3]);
                s.Save(PluginId, "vst3", [4, 5]); // replace
                s.SetBypassed(PluginId, true);
                s.SetBypassed("com.other", true);
                s.SetBypassed("com.other", false);
                s.Save("com.huge", "au", new byte[AudioPluginStateStore.MaxStateBytes + 1]);
            }
            using (var s = new AudioPluginStateStore(NullLogger<AudioPluginStateStore>.Instance, db))
            {
                Assert.Equal(new byte[] { 4, 5 }, s.Load(PluginId));
                Assert.Null(s.Load("com.missing"));
                Assert.Null(s.Load("com.huge")); // over the limit: refused
                Assert.Equal(new[] { PluginId }, s.GetBypassedIds());
            }
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    // -- harness ---------------------------------------------------------

    private static (AudioPluginBridge, MemoryBypassStore) NewBridge(ChainOrderService chainOrder)
    {
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        var store = new MemoryBypassStore();
        SetPrivateField(bridge, "_chainOrder", chainOrder);
        SetPrivateField(bridge, "_bypassStore", store);
        return (bridge, store);
    }

    private static void Attach(AudioPluginBridge bridge, ChainOrderService order, IAudioPlugin plugin)
    {
        order.OnPluginAttached(PluginId, []);
        Assert.True(order.TrySetParked(PluginId, parked: false, out _));
        PrivateField<Dictionary<string, IAudioPlugin>>(bridge, "_idToPlugin")[PluginId] = plugin;
        PrivateField<Dictionary<string, string>>(bridge, "_txIdToSlotName")[PluginId] = "tx.post-leveler";
        InvokePrivate(bridge, "ApplyChainOrder", [Array.Empty<string>()]);
        Assert.Same(plugin, TxChain(bridge).GetSlot(0));
    }

    private static float[] Run(AudioPluginBridge bridge, float[] input)
    {
        var output = new float[input.Length];
        TxChain(bridge).Process(input, output, new AudioBlockContext(48000, 1, input.Length, 0, mox: true));
        return output;
    }

    private static AudioChain TxChain(AudioPluginBridge b) => PrivateField<AudioChain>(b, "_chain");

    private static void InvokePrivate(object target, string name, object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static T PrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private sealed class MemoryBypassStore : IPluginBypassStore
    {
        public readonly HashSet<string> Bypassed = new();
        public IReadOnlyCollection<string> GetBypassedIds() => Bypassed.ToArray();
        public void SetBypassed(string pluginId, bool bypassed)
        {
            if (bypassed) Bypassed.Add(pluginId); else Bypassed.Remove(pluginId);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), "zeus-plugin-bypass-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly ChainOrderStore _store;

        public Fixture()
        {
            _store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, _dbPath);
            Service = new ChainOrderService(_store, new StreamingHub(NullLogger<StreamingHub>.Instance),
                NullLogger<ChainOrderService>.Instance);
        }

        public ChainOrderService Service { get; }

        public void Dispose()
        {
            _store.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private sealed class DoublerPlugin : IAudioPlugin
    {
        public string DisplayName => "Doubler";
        public AudioPluginRequirements Requirements => new(48_000, 1, 1_024);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * 2f;
        }
    }
}
