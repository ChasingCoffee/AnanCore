// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class SlowPluginReportTests
{
    [Fact]
    public void Names_OnlyPluginsThatTookMoreThanTheirBudget()
    {
        var chain = new AudioChain();
        chain.SetSlot(0, new DelayPlugin("quick", TimeSpan.Zero));
        chain.SetSlot(1, new DelayPlugin("sluggish", TimeSpan.FromMilliseconds(30)));

        var block = new float[480]; // 10 ms at 48 kHz: budget 5 ms
        chain.Process(block, block, new AudioBlockContext(48000, 1, 480, 0, false));

        var slow = AudioPluginBridge.SlowPlugins(chain).ToList();
        Assert.Equal("sluggish", Assert.Single(slow).Plugin.DisplayName);
        Assert.Empty(AudioPluginBridge.SlowPlugins(chain)); // reported once per window
    }

    private sealed class DelayPlugin(string name, TimeSpan delay) : IAudioPlugin
    {
        public string DisplayName => name;
        public AudioPluginRequirements Requirements => new(48000, 1, 256);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            if (delay > TimeSpan.Zero) Thread.Sleep(delay);
            input.CopyTo(output);
        }
    }
}
