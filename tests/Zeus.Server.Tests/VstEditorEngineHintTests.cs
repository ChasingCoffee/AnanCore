// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core has retired the out-of-process VST engine: VST3 plugins run in the
// in-process bridge, in Native mode. So opening a plugin editor must never send
// the operator to the engine — no "Download VST Engine", no "switch to VST
// mode" — and never to the developer-only ZEUS_ENABLE_VST_LOAD hatch. When the
// bridge will host the plugin, the open proceeds normally; when it won't (the
// plugin is parked, or failed to load), the message says so plainly.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class VstEditorEngineHintTests
{
    [Theory]
    [InlineData(AudioProcessingMode.Native, false)]
    [InlineData(AudioProcessingMode.Native, true)]
    [InlineData(AudioProcessingMode.Vst, false)]
    [InlineData(AudioProcessingMode.Vst, true)]
    public void InProcessHostable_NeverGuards(AudioProcessingMode mode, bool engineInstalled)
    {
        Assert.Null(VstEditorHint.EngineUnavailableMessage(
            mode, engineActive: false, engineInstalled, nativeLoadEnabled: true));
    }

    [Theory]
    [InlineData(AudioProcessingMode.Native, false)]
    [InlineData(AudioProcessingMode.Native, true)]
    [InlineData(AudioProcessingMode.Vst, false)]
    [InlineData(AudioProcessingMode.Vst, true)]
    public void NotHosted_ExplainsParkedOrFailed_NeverPointsAtEngine(
        AudioProcessingMode mode, bool engineInstalled)
    {
        var msg = VstEditorHint.EngineUnavailableMessage(
            mode, engineActive: false, engineInstalled, nativeLoadEnabled: false);

        Assert.NotNull(msg);
        Assert.Contains("isn't loaded", msg);
        Assert.Contains("active chain", msg);
        Assert.DoesNotContain("Download VST Engine", msg);
        Assert.DoesNotContain("VST mode", msg);
        Assert.DoesNotContain("processing mode", msg);
        Assert.DoesNotContain("ZEUS_ENABLE_VST_LOAD", msg);
    }

    [Fact]
    public void EngineRouting_NoGuard()
    {
        Assert.Null(VstEditorHint.EngineUnavailableMessage(
            AudioProcessingMode.Vst, engineActive: true, engineInstalled: true,
            nativeLoadEnabled: false));
    }
}
