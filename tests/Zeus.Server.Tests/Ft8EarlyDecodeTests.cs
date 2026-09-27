// SPDX-License-Identifier: GPL-2.0-or-later
//
// The early decode (DecoderPipeline.EarlyDecodeLeadMs): the slot is decoded
// 1 s before its boundary so the reply is staged before the keyer commits,
// and the full decode after the boundary only adds what that batch missed.

using System.Buffers.Binary;
using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class Ft8EarlyDecodeTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData", "ft8");

    [Fact]
    public void TheEarlyDecodeRunsOneSecondBeforeTheBoundary()
    {
        Assert.Equal(14_000, DecoderPipeline.EarlyDecodeAtMs(DigitalMode.Ft8));
        Assert.Equal(6_500, DecoderPipeline.EarlyDecodeAtMs(DigitalMode.Ft4));
    }

    [Theory]
    // FT8 slot 0 starts at 0 ms: early decode at 14 000, boundary at 15 000.
    [InlineData(13_950, false, 50)]      // just before the early decode
    [InlineData(10_000, false, 100)]     // far from it: capped, to notice mode changes
    [InlineData(14_020, false, 1)]       // overdue: go now
    [InlineData(14_960, true, 45)]       // early done: wake just past the boundary
    [InlineData(14_300, true, 100)]
    public void TheWatcherWakesForItsNextEvent(double nowMs, bool earlyDone, int expectedMs)
    {
        Assert.Equal(expectedMs, DecoderPipeline.WakeDelayMs(nowMs, 0, DigitalMode.Ft8, earlyDone));
    }

    [Fact]
    public void TheFullDecodeAddsOnlyWhatTheEarlyBatchMissed()
    {
        var published = new HashSet<string>(StringComparer.Ordinal) { "CQ K1ABC FN42", "G0XYZ K1ABC -12" };
        var found = new[]
        {
            Dto("CQ K1ABC FN42"), Dto("CQ W9XYZ EN52"), Dto("G0XYZ K1ABC -12"), Dto("CQ W9XYZ EN52"),
        };

        var fresh = DecoderPipeline.NotYetPublished(found, published);

        Assert.Equal(new[] { "CQ W9XYZ EN52" }, fresh.Select(d => d.Text));
        // What goes out is remembered, so a later pass cannot repeat it either.
        Assert.Contains("CQ W9XYZ EN52", published);
        Assert.Empty(DecoderPipeline.NotYetPublished(new[] { Dto("CQ W9XYZ EN52") }, published));
    }

    // The recorded 20 m slots, decoded as the early decode sees them (the audio
    // after its cut-off silent) and whole: the early batch must already carry
    // every message, so the full decode adds nothing on these slots and the TX
    // sequencer loses nothing by acting on it. The silent tail nudges the
    // estimates of about 1 % of messages by one step of the decoder's own
    // resolution (on 354 slots: frequency by one 3.125 Hz bin, dt by <= 0.08 s,
    // SNR mostly by 1 dB), so those are compared within that resolution and the
    // SNR not at all.
    [Theory]
    [InlineData("FT8")]
    [InlineData("FT4")]
    public void OnRecordedSlotsTheEarlyDecodeFindsWhatTheFullDecodeFinds(string protocol)
    {
        bool isFt4 = protocol == "FT4";
        var mode = isFt4 ? DigitalMode.Ft4 : DigitalMode.Ft8;
        var slots = Directory.GetFiles(Dir, $"*_{protocol}.f32").OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(slots);

        int total = 0;
        foreach (var f32 in slots)
        {
            var audio = ReadF32(f32);
            var full = Ft8Managed.Decode(audio, DecoderPipeline.DecodeRate, isFt4);

            var early = (float[])audio.Clone();
            int cut = DecoderPipeline.EarlyDecodeAtMs(mode) * DecoderPipeline.DecodeRate / 1000;
            Array.Clear(early, cut, early.Length - cut);
            var soFar = Ft8Managed.Decode(early, DecoderPipeline.DecodeRate, isFt4);

            var byText = soFar.GroupBy(d => d.Text).ToDictionary(g => g.Key, g => g.First());
            foreach (var d in full)
            {
                Assert.True(byText.TryGetValue(d.Text, out var e), $"{Path.GetFileName(f32)}: early decode missed '{d.Text}'");
                Assert.InRange(Math.Abs(d.DtSec - e!.DtSec), 0, 0.1);
                Assert.InRange(Math.Abs(d.FreqHz - e.FreqHz), 0, 4);
            }
            total += full.Count;
        }
        Assert.True(total > 0);
    }

    private static Ft8DecodeDto Dto(string text) => new() { Text = text, SnrDb = -10, DtSec = 0.1, FreqHz = 1500 };

    private static float[] ReadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var audio = new float[bytes.Length / 4];
        for (int i = 0; i < audio.Length; i++) audio[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return audio;
    }
}
