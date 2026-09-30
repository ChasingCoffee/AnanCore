// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// <see cref="IVstBridgeNative"/> for CLAP plug-ins: a P/Invoke façade over
/// <c>native/zeus-vst-bridge/include/zclap.h</c>. The CLAP host lives in the
/// same native library as the VST3 host (so it is found by the same loader),
/// with its own <c>zclap_*</c> entry points. Status codes are the VST3
/// bridge's (<see cref="VstBridgeStatus"/>).
///
/// Mapping: <see cref="LoadVst3"/> / <see cref="LoadVst3Class"/> load from a
/// <c>.clap</c> path, the class UID being the CLAP plug-in id;
/// <see cref="Describe"/> lists a module's plug-ins in the same JSON shape
/// as the VST3 scanner, with the CLAP feature list in <c>category</c>.
/// </summary>
public sealed partial class ClapBridgeNative : IVstBridgeNative
{
    static ClapBridgeNative()
    {
        VstBridgeNativeLoader.EnsureResolverRegistered();
    }

    public ClapBridgeNative()
    {
        VstBridgeNativeLoader.EnsureResolverRegistered();
    }

    // The abi argument is the VST3 ABI the caller knows; the CLAP host checks
    // its own.
    public int Init(int abi) => zclap_init(ClapBridgeAbi.Current);

    public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        => zclap_load(path, null, channels, sampleRate, blockSize, out handle);

    public int LoadVst3Class(string path, string? classUid, int channels, int sampleRate, int blockSize, out nint handle)
        => zclap_load(path, string.IsNullOrEmpty(classUid) ? null : classUid, channels, sampleRate, blockSize, out handle);

    public unsafe int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        fixed (float* pIn = input)
        fixed (float* pOut = output)
            return zclap_process(handle, pIn, pOut, frames);
    }

    public int SetParameter(nint handle, uint paramId, double normalized) => zclap_set_param(handle, paramId, normalized);

    public int Unload(nint handle) => zclap_unload(handle);

    public int Shutdown() => zclap_shutdown();

    public int GetLatencySamples(nint handle) => zclap_get_latency_samples(handle);

    public int EditorOpen(nint handle, string title) => zclap_editor_open(handle, title);

    public int EditorClose(nint handle) => zclap_editor_close(handle);

    public bool EditorIsOpen(nint handle) => zclap_editor_is_open(handle) != 0;

    public int Describe(string path, out string json)
    {
        var buf = new byte[64 * 1024];
        int status = zclap_describe(path, buf, buf.Length, out int len);
        if (status != VstBridgeStatus.Ok) { json = "[]"; return status; }
        if (len >= buf.Length)
        {
            buf = new byte[len + 1];
            status = zclap_describe(path, buf, buf.Length, out len);
            if (status != VstBridgeStatus.Ok) { json = "[]"; return status; }
        }
        json = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length));
        return VstBridgeStatus.Ok;
    }

    public int GetState(nint handle, out byte[] state) =>
        NativeState.Read((byte[]? buf, int cap, out int len) => zclap_get_state(handle, buf, cap, out len), out state);

    public unsafe int SetState(nint handle, ReadOnlySpan<byte> state)
    {
        fixed (byte* p = state)
            return zclap_set_state(handle, p, state.Length);
    }

    // --- P/Invoke imports ---------------------------------------------------

    private const string Lib = VstBridgeNative.LibraryName;

    [LibraryImport(Lib, EntryPoint = "zclap_init")]
    private static partial int zclap_init(int abi);

    [LibraryImport(Lib, EntryPoint = "zclap_shutdown")]
    private static partial int zclap_shutdown();

    [LibraryImport(Lib, EntryPoint = "zclap_describe", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zclap_describe(string path, [Out] byte[] outJson, int outCap, out int outLen);

    [LibraryImport(Lib, EntryPoint = "zclap_load", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zclap_load(string path, string? pluginId, int channels, int sampleRate, int blockSize, out nint handle);

    [LibraryImport(Lib, EntryPoint = "zclap_process")]
    private static unsafe partial int zclap_process(nint handle, float* input, float* output, int frames);

    [LibraryImport(Lib, EntryPoint = "zclap_set_param")]
    private static partial int zclap_set_param(nint handle, uint paramId, double normalized);

    [LibraryImport(Lib, EntryPoint = "zclap_unload")]
    private static partial int zclap_unload(nint handle);

    [LibraryImport(Lib, EntryPoint = "zclap_get_latency_samples")]
    private static partial int zclap_get_latency_samples(nint handle);

    [LibraryImport(Lib, EntryPoint = "zclap_get_state")]
    private static partial int zclap_get_state(nint handle, [Out] byte[]? outBuf, int cap, out int outLen);

    [LibraryImport(Lib, EntryPoint = "zclap_set_state")]
    private static unsafe partial int zclap_set_state(nint handle, byte* data, int len);

    [LibraryImport(Lib, EntryPoint = "zclap_editor_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int zclap_editor_open(nint handle, string title);

    [LibraryImport(Lib, EntryPoint = "zclap_editor_close")]
    private static partial int zclap_editor_close(nint handle);

    [LibraryImport(Lib, EntryPoint = "zclap_editor_is_open")]
    private static partial int zclap_editor_is_open(nint handle);
}

/// <summary>CLAP host ABI version; mirrors <c>ZCLAP_ABI</c> in zclap.h.</summary>
public static class ClapBridgeAbi
{
    // v1: initial CLAP hosting.
    public const int Current = 1;
}
