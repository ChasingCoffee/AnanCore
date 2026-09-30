// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Settings persistence, class selection and the load guard in
/// <see cref="VstHostAudioPlugin"/>, against a fake bridge whose "state" is a
/// byte array. Shares the LoadSensitive collection with the other tests that
/// flip the native-load override.
/// </summary>
[Collection("LoadSensitive")]
public class VstHostAudioPluginStateTests : IDisposable
{
    private readonly string _vst3;

    public VstHostAudioPluginStateTests()
    {
        VstHostAudioPlugin.NativeLoadEnabledOverride = true;
        _vst3 = Path.Combine(Path.GetTempPath(), $"zeus-state-{Guid.NewGuid():N}.vst3");
        File.WriteAllText(_vst3, "stub");
    }

    public void Dispose()
    {
        VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        File.Delete(_vst3);
    }

    private VstHostAudioPlugin Plugin(StatefulBridge bridge, MemoryStateStore? store, string? uid = null,
                                      IPluginLoadGuard? guard = null) =>
        new(bridge,
            new AudioBlock { Vst3Path = _vst3, Vst3Uid = uid, Slot = "tx.post-leveler", Channels = 1, SampleRate = 48000 },
            Path.GetTempPath(), "Fx", pluginId: "com.test.fx", stateStore: store, loadGuard: guard);

    [Fact]
    public async Task Load_RestoresSavedSettings()
    {
        var bridge = new StatefulBridge();
        var store = new MemoryStateStore();
        store.Save("com.test.fx", "vst3", [7, 7, 7]);
        var plugin = Plugin(bridge, store);

        await plugin.InitializeAudioAsync(new Host(), default);

        Assert.Equal(new byte[] { 7, 7, 7 }, bridge.State);
    }

    [Fact]
    public async Task SaveStateIfChanged_WritesOnlyWhenTheStateChanged()
    {
        var bridge = new StatefulBridge { State = [1] };
        var store = new MemoryStateStore();
        var plugin = Plugin(bridge, store);
        await plugin.InitializeAudioAsync(new Host(), default);

        Assert.True(plugin.SaveStateIfChanged());
        Assert.False(plugin.SaveStateIfChanged());   // unchanged: no write
        bridge.State = [2];                          // the operator turned a knob
        Assert.True(plugin.SaveStateIfChanged());
        Assert.Equal(new byte[] { 2 }, store.Load("com.test.fx"));
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task RestoredState_IsNotRewrittenUnchanged()
    {
        var bridge = new StatefulBridge();
        var store = new MemoryStateStore();
        store.Save("com.test.fx", "vst3", [5]);
        var writes = store.Writes;
        var plugin = Plugin(bridge, store);
        await plugin.InitializeAudioAsync(new Host(), default);

        Assert.False(plugin.SaveStateIfChanged());
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public async Task CloseEditor_And_Shutdown_KeepTheLatestSettings()
    {
        var bridge = new StatefulBridge { State = [1] };
        var store = new MemoryStateStore();
        var plugin = Plugin(bridge, store);
        await plugin.InitializeAudioAsync(new Host(), default);

        bridge.State = [3];
        plugin.CloseEditor();
        Assert.Equal(new byte[] { 3 }, store.Load("com.test.fx"));

        bridge.State = [4];
        await plugin.ShutdownAudioAsync(default);
        Assert.Equal(new byte[] { 4 }, store.Load("com.test.fx"));
        Assert.Equal(1, bridge.UnloadCount);
    }

    [Fact]
    public void RestoreState_WhileUnloaded_IsKeptForTheNextLoad()
    {
        var store = new MemoryStateStore();
        var plugin = Plugin(new StatefulBridge(), store);

        Assert.True(plugin.RestoreState([9, 9]));
        Assert.Equal(new byte[] { 9, 9 }, store.Load("com.test.fx"));
    }

    [Fact]
    public async Task ClassUid_SelectsTheClassToLoad()
    {
        var bridge = new StatefulBridge();
        await Plugin(bridge, store: null, uid: "ABCDEF").InitializeAudioAsync(new Host(), default);
        Assert.Equal("ABCDEF", bridge.LastClassUid);

        var plain = new StatefulBridge();
        await Plugin(plain, store: null).InitializeAudioAsync(new Host(), default);
        Assert.Null(plain.LastClassUid);
        Assert.True(plain.PlainLoad);
    }

    [Fact]
    public async Task LoadGuard_Refusal_PreventsTheNativeLoad()
    {
        var bridge = new StatefulBridge();
        var plugin = Plugin(bridge, store: null, guard: new DenyGuard());

        var ex = await Assert.ThrowsAsync<PluginLoadException>(
            () => plugin.InitializeAudioAsync(new Host(), default));
        Assert.Contains("crashed its probe", ex.Message);
        Assert.False(bridge.Loaded);
    }

    [SkippableFact]
    public async Task OpenEditor_OnAPluginLoadedBeforeTheUiLoop_ReloadsItWithItsSettings()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "the reload applies to macOS editors only");
        var bridge = new StatefulBridge { State = [7, 7, 7] };
        bridge.EditorRefused.Add(0x1234); // the instance restored at startup
        var plugin = Plugin(bridge, new MemoryStateStore());
        await plugin.InitializeAudioAsync(new Host(), default);

        Assert.True(plugin.OpenEditor());

        Assert.Equal(2, bridge.LoadCount);
        Assert.Equal(new nint[] { 0x1234 }, bridge.Unloaded);  // the old instance went
        Assert.Contains((nint)0x1235, bridge.SetStateOn);       // the new one got its settings
        Assert.Equal(new byte[] { 7, 7, 7 }, bridge.State);

        var input = new float[] { 0.5f, -0.5f };
        var output = new float[2];
        plugin.Process(input, output, new AudioBlockContext(48000, 1, 2, 0, false));
        Assert.Equal(input, output); // audio runs through the new instance
    }

    [SkippableFact]
    public async Task OpenEditor_ReloadsAtMostOnce()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "the reload applies to macOS editors only");
        var bridge = new StatefulBridge();
        bridge.EditorRefused.UnionWith([(nint)0x1234, 0x1235, 0x1236]); // never supported
        var plugin = Plugin(bridge, null);
        await plugin.InitializeAudioAsync(new Host(), default);

        Assert.False(plugin.OpenEditor());
        Assert.False(plugin.OpenEditor());
        Assert.Equal(2, bridge.LoadCount);
    }

    private sealed class DenyGuard : IPluginLoadGuard
    {
        public (bool Allowed, string? Reason) Check(string format, string identity, string? classUid) =>
            (false, "the plugin crashed its probe process");
    }

    private sealed class MemoryStateStore : IPluginStateStore
    {
        private readonly Dictionary<string, byte[]> _map = new();
        public int Writes;
        public byte[]? Load(string pluginId) => _map.GetValueOrDefault(pluginId);
        public void Save(string pluginId, string format, byte[] state) { _map[pluginId] = state; Writes++; }
    }

    private sealed class StatefulBridge : IVstBridgeNative
    {
        public byte[] State = [];
        public bool Loaded;
        public bool PlainLoad;
        public string? LastClassUid;
        public int UnloadCount;
        public int LoadCount;
        public nint NextHandle = 0x1234;
        public readonly List<nint> Unloaded = [];
        public readonly List<nint> SetStateOn = [];
        // Handles whose editor the bridge refuses as "not implemented" (a
        // macOS plugin loaded before the UI loop ran).
        public readonly HashSet<nint> EditorRefused = [];

        public int Init(int abi) => VstBridgeStatus.Ok;
        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            PlainLoad = true;
            Loaded = true;
            LoadCount++;
            handle = NextHandle++;
            return VstBridgeStatus.Ok;
        }
        public int LoadVst3Class(string path, string? classUid, int channels, int sampleRate, int blockSize, out nint handle)
        {
            LastClassUid = classUid;
            Loaded = true;
            LoadCount++;
            handle = NextHandle++;
            return VstBridgeStatus.Ok;
        }
        public int GetState(nint handle, out byte[] state) { state = State.ToArray(); return VstBridgeStatus.Ok; }
        public int SetState(nint handle, ReadOnlySpan<byte> state)
        {
            SetStateOn.Add(handle);
            State = state.ToArray();
            return VstBridgeStatus.Ok;
        }
        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            input.CopyTo(output);
            return VstBridgeStatus.Ok;
        }
        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;
        public int Unload(nint handle) { UnloadCount++; Unloaded.Add(handle); Loaded = false; return VstBridgeStatus.Ok; }
        public int Shutdown() => VstBridgeStatus.Ok;
        public int EditorOpen(nint handle, string title) =>
            EditorRefused.Contains(handle) ? VstBridgeStatus.NotImplemented : VstBridgeStatus.Ok;
        public int EditorClose(nint handle) => VstBridgeStatus.Ok;
        public bool EditorIsOpen(nint handle) => false;
    }

    private sealed class Host : IAudioHost
    {
        public int CurrentSampleRate => 48000;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => 1024;
        public string Slot => "tx.post-leveler";
    }
}
