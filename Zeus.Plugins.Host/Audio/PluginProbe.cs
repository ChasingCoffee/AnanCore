// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// The child-process side of plugin probing. The host binary runs
/// <c>OpenhpsdrZeus --plugin-probe …</c> (see <see cref="PluginProbeRunner"/>)
/// so that touching an unknown third-party plugin — reading its classes or
/// instantiating it — happens in a throw-away process: a plugin that crashes
/// or hangs takes the probe down, never the server.
///
/// Commands (after <see cref="Flag"/>):
/// <list type="bullet">
///   <item><c>describe &lt;path.vst3|path.clap&gt;</c> — the module's plug-ins.</item>
///   <item><c>load vst3|clap &lt;path&gt; [&lt;classUid | clap id&gt;]</c> — trial load:
///   instantiate, process one block, unload.</item>
///   <item><c>load au &lt;type:subtype:manufacturer&gt;</c> — the same for an Audio Unit.</item>
/// </list>
/// The result is one JSON line on stdout (<see cref="PluginProbeReply"/>),
/// prefixed with <see cref="ReplyMarker"/>.
/// </summary>
public static class PluginProbe
{
    public const string Flag = "--plugin-probe";

    /// <summary>Prefix of the reply line. Plugins print to stdout freely (some
    /// print a lot); only a line carrying this marker is the probe's answer.</summary>
    public const string ReplyMarker = "@@zeus-plugin-probe@@";

    // Trial-load geometry: the host's TX geometry (mono, 48 kHz).
    private const int ProbeChannels = 1;
    private const int ProbeSampleRate = 48000;
    private const int ProbeBlockSize = 1024;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Run a probe command; returns the process exit code (0 = ok).</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout)
    {
        PluginProbeReply reply;
        try
        {
            reply = args switch
            {
                ["describe", var path] => Describe(BridgeForPath(path), path),
                ["load", "vst3", var path] => TrialLoad(new VstBridgeNative(), path, null),
                ["load", "vst3", var path, var uid] => TrialLoad(new VstBridgeNative(), path, uid),
                ["load", "clap", var path] => TrialLoad(new ClapBridgeNative(), path, null),
                ["load", "clap", var path, var id] => TrialLoad(new ClapBridgeNative(), path, id),
                ["load", "au", var id] => TrialLoad(new AuBridgeNative(), id, null),
                _ => PluginProbeReply.Fail(VstBridgeStatus.InvalidArguments,
                    "usage: --plugin-probe describe <path> | load vst3|clap <path> [id] | load au <id>"),
            };
        }
        catch (Exception ex)
        {
            reply = PluginProbeReply.Fail(VstBridgeStatus.Other, $"{ex.GetType().Name}: {ex.Message}");
        }
        stdout.WriteLine(ReplyMarker + JsonSerializer.Serialize(reply, Json));
        stdout.Flush();
        return reply.Ok ? 0 : 1;
    }

    private static IVstBridgeNative BridgeForPath(string path) =>
        path.TrimEnd('/', '\\').EndsWith(".clap", StringComparison.OrdinalIgnoreCase)
            ? new ClapBridgeNative()
            : new VstBridgeNative();

    private static PluginProbeReply Describe(IVstBridgeNative bridge, string path)
    {
        var init = bridge.Init(VstBridgeAbi.Current);
        if (init != VstBridgeStatus.Ok) return PluginProbeReply.Fail(init, "bridge init failed");
        var status = bridge.Describe(path, out var json);
        if (status != VstBridgeStatus.Ok) return PluginProbeReply.Fail(status, "not a loadable VST3 on this platform");
        var classes = JsonSerializer.Deserialize<List<VstPluginDescriptor>>(json, Json) ?? [];
        return new PluginProbeReply { Ok = true, Classes = classes };
    }

    private static PluginProbeReply TrialLoad(IVstBridgeNative bridge, string identity, string? uid)
    {
        var init = bridge.Init(VstBridgeAbi.Current);
        if (init != VstBridgeStatus.Ok) return PluginProbeReply.Fail(init, "bridge init failed");
        var status = bridge.LoadVst3Class(identity, uid, ProbeChannels, ProbeSampleRate, ProbeBlockSize, out var handle);
        if (status != VstBridgeStatus.Ok || handle == 0)
            return PluginProbeReply.Fail(status, status switch
            {
                VstBridgeStatus.ActivateFailed => "the plugin would not activate as a mono effect (an instrument, or no audio input)",
                VstBridgeStatus.NoAudioEffectClass => "no audio effect in this plugin",
                VstBridgeStatus.NotAVst3 => "not a loadable plugin on this platform",
                VstBridgeStatus.FileNotFound => "plugin not found",
                _ => $"load failed (status {status})",
            });
        try
        {
            var input = new float[ProbeBlockSize];
            var output = new float[ProbeBlockSize];
            bridge.Process(handle, input, output, ProbeBlockSize);
            return new PluginProbeReply { Ok = true, LatencySamples = bridge.GetLatencySamples(handle) };
        }
        finally
        {
            bridge.Unload(handle);
        }
    }
}

/// <summary>The one JSON line a probe prints.</summary>
public sealed record PluginProbeReply
{
    public bool Ok { get; init; }
    public int Status { get; init; }
    public string? Error { get; init; }
    public List<VstPluginDescriptor>? Classes { get; init; }
    public int? LatencySamples { get; init; }

    public static PluginProbeReply Fail(int status, string error) =>
        new() { Ok = false, Status = status, Error = error };
}
