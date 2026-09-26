// SPDX-License-Identifier: GPL-2.0-or-later
//
// Callsign → DXCC entity from AD1C's embedded country file (zeus-vkka).

using Zeus.Server.Hosting.Dxcc;

namespace Zeus.Server.Tests;

public sealed class DxccTableTests
{
    [Theory]
    [InlineData("EA5IUE", 281, "Spain")]
    [InlineData("EA8AR", 29, "Canary Islands")]
    [InlineData("K1ABC", 291, "United States")]
    [InlineData("DL1ABC", 230, "Fed. Rep. of Germany")]
    [InlineData("G4XYZ", 223, "England")]
    [InlineData("VK2ZZ", 150, "Australia")]
    [InlineData("ea5iue", 281, "Spain")]                 // case
    [InlineData("<EA8AR>", 29, "Canary Islands")]       // a resolved hashed call
    [InlineData("PJ4/K1ABC", 520, "Bonaire")]           // prefix before the call
    [InlineData("K1ABC/PJ4", 520, "Bonaire")]           // ... or after it
    [InlineData("DL1ABC/EA8", 29, "Canary Islands")]
    [InlineData("E7/K1ABC", 501, "Bosnia-Herzegovina")]
    [InlineData("EA5IUE/P", 281, "Spain")]              // suffixes that name no place
    [InlineData("EA5IUE/QRP", 281, "Spain")]
    [InlineData("K1ABC/4", 291, "United States")]        // a call-area digit
    [InlineData("IT9ABC", 248, "Italy")]                 // Sicily is WAE-only, not DXCC
    [InlineData("AO90A", 281, "Spain")]                  // an exact-call entry
    public void Lookup_FindsTheEntity(string call, int dxcc, string name)
    {
        var e = DxccTable.Default.Lookup(call);
        Assert.NotNull(e);
        Assert.Equal(dxcc, e!.Dxcc);
        Assert.Equal(name, e.Name);
    }

    [Theory]
    [InlineData("DL1ABC/MM")]                            // maritime mobile: no entity
    [InlineData("K1ABC/AM")]                             // aeronautical mobile
    [InlineData("<...>")]                                // unresolved hash
    [InlineData("")]
    [InlineData(null)]
    public void Lookup_HasNoEntity(string? call) => Assert.Null(DxccTable.Default.Lookup(call));

    [Fact]
    public void TheEmbeddedFile_IsLoadedWhole()
    {
        Assert.StartsWith("VER", DxccTable.Default.Version);
        // cty.csv lists ~340 DXCC entities; each must resolve, to itself, from
        // the first prefix it lists (the primary prefix is not always one in use:
        // Spratly's is 1S, its calls are 9M0/BM9S...).
        var csv = new StreamReader(typeof(DxccTable).Assembly.GetManifestResourceStream("Zeus.Dxcc.cty.csv")!).ReadToEnd();
        int n = 0;
        foreach (var line in csv.Split('\n').Where(l => l.Contains(',') && !l.StartsWith('*')))
        {
            var f = line.Trim().TrimEnd(';').Split(',');
            // (Some entities, e.g. the UN HQ, list only exact calls.)
            var tokens = f[9].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string first = (tokens.FirstOrDefault(p => !p.StartsWith('=')) ?? tokens[0]).TrimStart('=');
            first = first[..(first.IndexOfAny(['(', '[', '<', '{', '~']) is int c && c >= 0 ? c : first.Length)];
            var e = DxccTable.Default.Lookup(first);
            Assert.True(e?.Dxcc == int.Parse(f[2]), $"{f[1]}: prefix {first} resolves to {e?.Name ?? "nothing"}");
            n++;
        }
        Assert.True(n > 330, $"{n} entities");
    }
}
