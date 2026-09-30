using Xunit;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// The radio's own keyer (P1 cw_enable in register 0x0f, P2 TxSpecific byte 5)
// is armed only in CWU/CWL, and never while a host CW source is keying.
public class RadioKeyerArmTests
{
    [Theory]
    [InlineData(RxMode.USB, false)]
    [InlineData(RxMode.LSB, false)]
    [InlineData(RxMode.DIGU, false)]
    [InlineData(RxMode.DIGL, false)]
    [InlineData(RxMode.AM, false)]
    [InlineData(RxMode.FM, false)]
    [InlineData(RxMode.FreeDv, false)]
    [InlineData(RxMode.CWU, true)]
    [InlineData(RxMode.CWL, true)]
    public void ArmedOnlyInCw(RxMode mode, bool expected)
    {
        Assert.Equal(expected, RadioService.RadioKeyerArmed(mode, hostCwKeying: false));
    }

    [Theory]
    [InlineData(RxMode.CWU)]
    [InlineData(RxMode.CWL)]
    public void DisarmedWhileHostCwKeys(RxMode mode)
    {
        Assert.False(RadioService.RadioKeyerArmed(mode, hostCwKeying: true));
    }
}
