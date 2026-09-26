// SPDX-License-Identifier: GPL-2.0-or-later
//
// Managed FT8/FT4 port (zeus-wi0p). The pack/encode and unpack paths are
// integer code, so they must match ft8_lib exactly: the expected values are
// the native library's own output, frozen into TestData/ft8 before native/ft8
// was retired (see docs/designs/ft8-managed-port.md). Runs everywhere.

using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Ft8;

namespace Zeus.Server.Tests;

public sealed class Ft8ManagedTests
{
    private static string TestData(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "ft8", name);

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    private static string Digits(ReadOnlySpan<byte> b)
    {
        var c = new char[b.Length];
        for (int i = 0; i < b.Length; i++) c[i] = (char)('0' + b[i]);
        return new string(c);
    }

    public static TheoryData<string, int, string, string, string> EncoderVectors()
    {
        var d = new TheoryData<string, int, string, string, string>();
        foreach (var line in File.ReadAllLines(TestData("encoder-native.tsv")))
        {
            var f = line.Split('\t');
            d.Add(f[0], int.Parse(f[1]), f[2], f[3], f[4]);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(EncoderVectors))]
    public void Encoder_MatchesNative(string message, int rc, string payload, string ft8Tones, string ft4Tones)
    {
        // ft8_lib's quirks were fixed on purpose after the port (zeus-sk6o);
        // those messages have their own expectations below.
        if (FixedQuirks.Any(q => (string)q[0] == message)) return;

        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(rc, (int)FtxMessage.Encode(message, null, p));
        if (rc != 0) return;

        Assert.Equal(payload, Hex(p));

        var t8 = new byte[FtxConstants.Ft8Nn];
        FtxEncoder.Ft8Tones(p, t8);
        Assert.Equal(ft8Tones, Digits(t8));

        var t4 = new byte[FtxConstants.Ft4Nn];
        FtxEncoder.Ft4Tones(p, t4);
        Assert.Equal(ft4Tones, Digits(t4));
    }

    /// <summary>Messages the native encoder got wrong (zeus-sk6o), with what
    /// the far end now reads — null when the message is refused. The comment
    /// says what ft8_lib sent instead.</summary>
    public static TheoryData<string, string?> FixedQuirks => new()
    {
        // An unrecognised third field became a report of +00 (or worse).
        { "K1ABC W9XYZ HELLO", null },                  // was K1ABC W9XYZ +00
        { "K1ABC W9XYZ AA", null },                     // was +00
        { "K1ABC W9XYZ FN4", null },                    // was +00
        { "K1ABC W9XYZ SS99", null },                   // was +00 (S is not a grid letter)
        { "K1ABC W9XYZ R-", null },                     // was R+00
        { "K1ABC W9XYZ -99", null },                    // was "K1ABC W9XYZ RR36"
        { "K1ABC W9XYZ R-99", null },                   // was "K1ABC W9XYZ R RR36"
        { "K1ABC W9XYZ +99", null },                    // beyond +49
        { "K1ABC W9XYZ R+50", null },                   // beyond +49
        { "K1ABC W9XYZ ABCDEFGHIJKLMNOPQRSTUV", null }, // was +00, token truncated
        // Short enough for free text, which is what was typed.
        { "K1ABC W9XYZ R", "K1ABC W9XYZ R" },           // was R+00
        { "K1ABC W9XYZ -", "K1ABC W9XYZ -" },           // was +00
        { "K1ABC W9XYZ +", "K1ABC W9XYZ +" },           // was +00
        { "K1ABC W9XYZ 5", "K1ABC W9XYZ 5" },           // was +05: a report needs its sign
        { "HELLO WORLD", "HELLO WORLD" },               // was "<...> <...>"
        // Bracketed (hashed) calls could not be encoded at all.
        { "<W9XYZ> PJ4/K1ABC RR73", "<...> PJ4/K1ABC RR73" },
        { "<W9XYZ> PJ4/K1ABC RRR", "<...> PJ4/K1ABC RRR" },
        { "<W9XYZ> PJ4/K1ABC 73", "<...> PJ4/K1ABC 73" },
        { "<W9XYZ> PJ4/K1ABC", "<...> PJ4/K1ABC" },
        { "PJ4/K1ABC <W9XYZ> RR73", "PJ4/K1ABC <...> RR73" },
        // A non-standard call now goes in full (type 4) when it can.
        { "E7/K1ABC W9XYZ", "E7/K1ABC <...>" },         // was "<...> W9XYZ"
        { "K1ABC PJ4/W9XYZ", "<...> PJ4/W9XYZ" },       // was "K1ABC <...>"
        // Mixed /R and /P cannot be sent; ft8_lib sent something else.
        { "K1ABC/R W9XYZ/P EN37", null },               // was "<...> W9XYZ/P"
        { "K1ABC/P W9XYZ/R EN37", null },               // was "<...> W9XYZ/R"
        { "K1ABCDEFGHIJKL W9XYZ EN37", null },          // was "<...> W9XYZ EN37", call truncated
        // Case and spacing are normalised, as WSJT-X does.
        { "cq k1abc fn42", "CQ K1ABC FN42" },           // was refused
        { " K1ABC W9XYZ EN37", "K1ABC W9XYZ EN37" },    // was refused
        { "CQ  K1ABC FN42", "CQ K1ABC FN42" },          // was "CQ  K1ABC FN42" (an empty CQ modifier)
    };

    [Theory]
    [MemberData(nameof(FixedQuirks))]
    public void Encoder_FixedQuirks(string message, string? readsAs)
    {
        var p = new byte[FtxMessage.PayloadBytes];
        var rc = FtxMessage.Encode(message, null, p);
        if (readsAs is null)
        {
            Assert.NotEqual(FtxMessageRc.Ok, rc);
            return;
        }
        Assert.Equal(FtxMessageRc.Ok, rc);
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, null, out string text));
        Assert.Equal(readsAs, text);
    }

    // The QSO messages the sequencer builds for a compound call still go out as
    // before: the compound call hashed into a standard message, which the far
    // end resolves because its own call is in its table.
    [Theory]
    [InlineData("PJ4/K1ABC EA5IUE -10", "<PJ4/K1ABC> EA5IUE -10")]
    [InlineData("EA5IUE PJ4/K1ABC R-12", "EA5IUE <PJ4/K1ABC> R-12")]
    [InlineData("EA5IUE/QRP K1ABC -10", "<EA5IUE/QRP> K1ABC -10")]
    public void CompoundCallReports_ResolveAtAStationThatKnowsTheCall(string message, string readsAs)
    {
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode(message, null, p));
        Assert.Equal(1, (p[9] >> 3) & 7);                          // a standard message
        var table = new FtxCallsignTable();
        string compound = message.Split(' ').First(c => c.Contains('/'));
        FtxMessage.SaveCallsign(table, compound, out _);              // the far end knows it
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string text));
        Assert.Equal(readsAs, text);
    }

    [Fact]
    public void Unpack_MatchesNativeOnRandomPayloads()
    {
        var lines = File.ReadAllLines(TestData("unpack-native.tsv"));
        Assert.True(lines.Length >= 1000);
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            var p = Convert.FromHexString(f[0]);
            var rc = FtxMessage.Decode(p, null, out string text);
            Assert.True(int.Parse(f[1]) == (int)rc, $"{f[0]}: rc {(int)rc}, native {f[1]}");
            if (rc == FtxMessageRc.Ok) Assert.True(f[2] == text, $"{f[0]}: '{text}', native '{f[2]}'");
        }
    }

    [Theory]
    [InlineData("CQ EA5IUE IM76")]
    [InlineData("EA5IUE W1AW -15")]
    [InlineData("W1AW EA5IUE R-09")]
    [InlineData("EA5IUE W1AW RR73")]
    [InlineData("CQ POTA EA5IUE IM76")]
    [InlineData("CQ 123 K1ABC FN42")]
    [InlineData("PA3XYZ/P GM4ABC/P JO22")]
    [InlineData("TNX BOB 73 GL")]
    public void Encode_ThenDecode_RoundTrips(string message)
    {
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode(message, null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, null, out string text));
        Assert.Equal(message, text);
    }

    // A non-standard call is sent in full once; later messages carry only its
    // hash, which the session-long table resolves.
    [Fact]
    public void CallsignTable_ResolvesAHashedCallFromAnEarlierMessage()
    {
        var table = new FtxCallsignTable();
        var p = new byte[FtxMessage.PayloadBytes];

        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("K1ABC <PJ4/W9XYZ> -10", null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string before));
        Assert.Equal("K1ABC <...> -10", before);

        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("CQ PJ4/W9XYZ", null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string cq));
        Assert.Equal("CQ PJ4/W9XYZ", cq);

        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("K1ABC <PJ4/W9XYZ> -10", null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string after));
        Assert.Equal("K1ABC <PJ4/W9XYZ> -10", after);
    }

    [Fact]
    public void Crc_OfAnEncodedMessage_ChecksOut()
    {
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("CQ K1ABC FN42", null, p));
        var a91 = new byte[FtxConstants.LdpcKBytes];
        FtxCrc.Add(p, a91);
        ushort sent = FtxCrc.Extract(a91);
        a91[9] &= 0xF8;
        a91[10] = 0;
        a91[11] = 0;
        Assert.Equal(sent, FtxCrc.Compute(a91, 96 - 14));
    }

    [Theory]
    [InlineData(0.5f, 0.5204998778)]
    [InlineData(1.0f, 0.8427007929)]
    [InlineData(2.0f, 0.9953222650)]
    [InlineData(2.9f, 0.9999589021)]
    [InlineData(3.1f, 0.9999883513)]
    [InlineData(4.0f, 0.9999999846)]
    [InlineData(-1.0f, -0.8427007929)]
    [InlineData(-3.5f, -0.9999992569)]
    [InlineData(0.0f, 0.0)]
    public void Erf_MatchesReferenceValues(float x, double want) =>
        Assert.Equal(want, FtxSynth.Erf(x), 1e-7);

    [Fact]
    public void Synth_RefusesWhatTheEncoderRefuses()
    {
        Assert.Null(FtxSynth.Synth("THIS IS TOO LONG FOR FREE TEXT", false, 1500f, 48000, null, out var rc));
        Assert.Equal(FtxMessageRc.ErrorType, rc);
    }

    // ---- decoder -----------------------------------------------------------

    /// <summary>A slot with several transmissions at chosen levels, offsets
    /// and frequencies, in Gaussian noise (fixed seed).</summary>
    internal static float[] SynthSlot(bool isFt4, int rate, int seed, double noiseRms,
                                      params (string msg, float hz, float dt, float amp)[] txs)
    {
        int slot = (int)((isFt4 ? 7.5 : 15.0) * rate);
        var audio = new float[slot];
        var rng = new Random(seed);
        for (int i = 0; i < slot; i += 2)
        {
            // Box-Muller
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1)) * noiseRms;
            audio[i] = (float)(r * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < slot) audio[i + 1] = (float)(r * Math.Sin(2 * Math.PI * u2));
        }
        foreach (var (msg, hz, dt, amp) in txs)
        {
            var w = FtxSynth.Synth(msg, isFt4, hz, rate, null, out var rc)!;
            Assert.Equal(FtxMessageRc.Ok, rc);
            int start = (int)(dt * rate);
            for (int i = 0; i < w.Length; i++)
            {
                int j = start + i;
                if (j >= 0 && j < slot) audio[j] += amp * w[i];
            }
        }
        return audio;
    }

    public static TheoryData<bool, int> DecoderSlots => new()
    {
        { false, 1 }, { false, 2 }, { false, 3 }, { true, 4 }, { true, 5 },
    };

    // The native decoder's output on these slots, frozen before native/ft8
    // was retired: same messages, frequencies, dt, SNR and sync score.
    [Theory]
    [MemberData(nameof(DecoderSlots))]
    public void Decoder_MatchesFrozenNativeOnSynthesizedSlots(bool isFt4, int seed)
    {
        var audio = DecoderSlotAudio(isFt4, seed);
        var managed = Ft8Managed.ToDtos(FtxDecoder.Decode(audio, 48000, isFt4, null)).Select(Ft8GoldenTests.Line).ToList();

        string prefix = $"{(isFt4 ? "FT4" : "FT8")}\t{seed}\t";
        var native = Ft8GoldenTests.FirstOfEachText(File.ReadAllLines(TestData("synthetic-native.tsv"))
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[prefix.Length..]));

        Assert.True(native.Count >= 3);
        Assert.Equal(native, managed);
    }

    /// <summary>The 48 kHz slot a DecoderSlots row stands for.</summary>
    internal static float[] DecoderSlotAudio(bool isFt4, int seed)
    {
        const int rate = 48000;
        float t0 = isFt4 ? 0.3f : 0.5f;
        return SynthSlot(isFt4, rate, seed, 0.1,
            ("CQ EA5IUE IM76", 600f + 7 * seed, t0, 0.20f),
            ("W1AW EA5IUE R-09", 900f, t0 + 0.1f, 0.05f),
            ("EA5IUE W1AW RR73", 1210f, t0 - 0.2f, 0.02f),
            ("CQ DX K1ABC FN42", 1523f, t0 + 0.4f, 0.012f),
            ("K1ABC W9XYZ EN37", 1876f + seed, t0, 0.008f),
            ("W9XYZ K1ABC -11", 2140f, t0 + 0.8f, 0.006f),
            ("CQ POTA EA5HYW IM98", 2400f, t0 - 0.4f, 0.004f),
            ("TNX BOB 73 GL", 350f, t0 + 0.2f, 0.003f));
    }

    // FT8 carries no country: the sender's DXCC entity comes from its callsign.
    [Theory]
    [InlineData("CQ EA8AR IL18", "Canary Islands", 29)]
    [InlineData("EA5IUE PJ4/K1ABC R-12", "Bonaire", 520)]
    [InlineData("K1ABC EA5IUE -10", "Spain", 281)]
    [InlineData("<...> PD0OKP JO32", "Netherlands", 263)]
    [InlineData("TNX BOB 73 GL", null, null)]
    public void Decodes_CarryTheSendersDxccEntity(string text, string? country, int? dxcc)
    {
        var dto = Ft8Managed.ToDtos([new FtxDecode(-10, 0.5f, 1500f, 20, text)]).Single();
        Assert.Equal(country, dto.Country);
        Assert.Equal(dxcc, dto.Dxcc);
    }
}
