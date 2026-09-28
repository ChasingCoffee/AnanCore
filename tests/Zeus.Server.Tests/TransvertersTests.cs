// SPDX-License-Identifier: GPL-2.0-or-later
//
// Transverters (piHPSDR model): the operator tunes RF, the radio is sent
// RF - (LO + LO error), and nothing above the radio's 60 MHz ever reaches it.

using System.Text.RegularExpressions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

// Transverters holds global state that BandUtils.AllBands / FreqToBand read,
// so these run alone rather than beside tests that enumerate the band list.
[CollectionDefinition("Transverters", DisableParallelization = true)]
public sealed class TransvertersCollection { }

[Collection("Transverters")]
public sealed class TransvertersTests : IDisposable
{
    // 13 cm from a 28 MHz-ish IF: RF 2300-2310 MHz, LO 2272 MHz -> IF 28-38 MHz.
    private static TransverterBandDto Cm13(bool enabled = true, long loError = 0) =>
        new(Id: 1, Enabled: enabled, Name: "13cm", MinHz: 2_300_000_000, MaxHz: 2_310_000_000,
            LoHz: 2_272_000_000, LoErrorHz: loError);

    public TransvertersTests() => Transverters.Apply([]);
    public void Dispose() => Transverters.Apply([]);

    [Fact]
    public void Hf_IsUntouched()
    {
        Transverters.Apply([Cm13()]);
        Assert.Equal(7_180_000, Transverters.HardwareHz(7_180_000));
        Assert.Equal(50_125_000, Transverters.HardwareHz(50_125_000));
    }

    [Fact]
    public void Rf_ConvertsToIf_WithLoError()
    {
        Transverters.Apply([Cm13()]);
        Assert.Equal(28_100_000, Transverters.HardwareHz(2_300_100_000));
        Transverters.Apply([Cm13(loError: 1_500)]);
        Assert.Equal(28_101_500, Transverters.HardwareHz(2_300_100_000));
    }

    [Fact]
    public void JustOutsideTheBand_StillConverts_ForCtunAndCwOffsets()
    {
        Transverters.Apply([Cm13()]);
        // DDC centre parked 500 kHz below the band start (CTUN) still uses the band's LO.
        Assert.Equal(27_500_000, Transverters.HardwareHz(2_299_500_000));
    }

    [Fact]
    public void NothingAbove60MHz_EverReachesTheRadio()
    {
        Transverters.Apply([Cm13()]);
        foreach (long hz in new long[] { 60_000_001, 144_200_000, 2_305_000_000, 5_760_000_000, 10_000_000_000, long.MaxValue })
            Assert.InRange(Transverters.HardwareHz(hz), 0, Transverters.MaxRadioHz);
        Transverters.Apply([]);
        Assert.Equal(Transverters.MaxRadioHz, Transverters.HardwareHz(2_305_000_000));
    }

    [Fact]
    public void Tuning_AllowsTheRadiosRangeAndEnabledBandsOnly()
    {
        Transverters.Apply([Cm13()]);
        Assert.True(Transverters.IsTunable(14_200_000));
        Assert.True(Transverters.IsTunable(2_305_000_000));
        Assert.False(Transverters.IsTunable(144_200_000));
        Assert.Equal(2_305_000_000, Transverters.ClampTune(2_305_000_000));
        Assert.Equal(Transverters.MaxRadioHz, Transverters.ClampTune(144_200_000));
        Transverters.Apply([Cm13(enabled: false)]);
        Assert.False(Transverters.IsTunable(2_305_000_000));
    }

    [Fact]
    public void BandNaming_FeedsThePerBandTables()
    {
        Transverters.Apply([Cm13()]);
        Assert.Equal("13cm", BandUtils.FreqToBand(2_305_000_000));
        Assert.Equal("20m", BandUtils.FreqToBand(14_200_000));
        Assert.Contains("13cm", BandUtils.AllBands);
        Assert.Contains("40m", BandUtils.AllBands);
        Transverters.Apply([]);
        Assert.DoesNotContain("13cm", BandUtils.AllBands);
    }

    [Fact]
    public void Validation_RejectsWhatCouldPutTheRadioOffFrequency()
    {
        Assert.Null(Transverters.Validate([Cm13()]));
        Assert.Contains("above 60 MHz", Transverters.Validate([Cm13() with { MinHz = 50_000_000 }]));
        Assert.Contains("outside the radio", Transverters.Validate([Cm13() with { LoHz = 2_200_000_000 }]));
        Assert.Contains("10 GHz", Transverters.Validate([Cm13() with { MaxHz = 10_500_000_000, LoHz = 10_470_000_000 }]));
        Assert.Contains("clashes", Transverters.Validate([Cm13() with { Name = "20m" }]));
        var near = Cm13() with { Id = 2, Name = "13cm-b", MinHz = 2_311_000_000, MaxHz = 2_320_000_000, LoHz = 2_292_000_000 };
        Assert.Contains("2 MHz apart", Transverters.Validate([Cm13(), near]));
        var many = Enumerable.Range(1, 17).Select(i => Cm13(enabled: false) with { Id = i, Name = $"x{i}" }).ToList();
        Assert.Contains("at most 16", Transverters.Validate(many));
    }
}

/// <summary>
/// Every frequency written to the radio must go through Transverters.HardwareHz.
/// A write that bypasses it would put the radio on the RF value (clamped) instead
/// of the IF — silently off frequency on a transverter band. This scans the
/// server for every protocol-client frequency write, so a write added later
/// without the conversion fails here, not on the air.
/// </summary>
public sealed class TransverterWriteSiteTests
{
    private static readonly Regex Write = new(
        @"\.(SetVfoAHz|SetVfoBHz|SetTxDucFrequency|SetExtraReceiverFreqHz|SetDdcFrequency)\(",
        RegexOptions.Compiled);

    // ZeusHost's raw Saturn DDC diagnostic endpoint takes a hardware frequency
    // by design and is the only intentional exception.
    private static readonly string[] Exempt = ["ZeusHost.cs"];

    [Fact]
    public void EveryRadioFrequencyWrite_GoesThroughHardwareHz()
    {
        var dir = Path.Combine(FindRepoRoot(), "Zeus.Server.Hosting");
        var missing = new List<string>();
        int writes = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (Exempt.Contains(Path.GetFileName(file))) continue;
            string src = File.ReadAllText(file);
            foreach (Match m in Write.Matches(src))
            {
                int lineStart = src.LastIndexOf('\n', m.Index) + 1;
                string lineHead = src[lineStart..m.Index];
                if (lineHead.TrimStart().StartsWith("//") || lineHead.Contains("void ")) continue;
                int end = src.IndexOf(';', m.Index);
                string stmt = src[m.Index..(end < 0 ? src.Length : end)];
                writes++;
                if (!stmt.Contains("Transverters.HardwareHz("))
                {
                    int line = src[..m.Index].Count(c => c == '\n') + 1;
                    missing.Add($"{Path.GetFileName(file)}:{line}: {stmt.Split('\n')[0].Trim()}");
                }
            }
        }
        Assert.True(writes >= 10, $"scan found only {writes} writes — the scan itself is broken");
        Assert.True(missing.Count == 0, "frequency writes bypassing Transverters.HardwareHz:\n" + string.Join("\n", missing));
    }

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Zeus.Server.Hosting", "Transverters.cs")))
                return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
