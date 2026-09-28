// SPDX-License-Identifier: GPL-2.0-or-later
//
// G2-Ultra front-panel LEDs, from radio state. LED 8 sits with the A/B button
// and lights while transmitting on VFO B (split) — the panel's main knob
// always tunes VFO A, so split is the lasting A/B state, and it is exactly
// when pressing A/B moves the transmit frequency.

using Xunit;
using Zeus.Contracts;
using Zeus.Server.FrontPanel;

namespace Zeus.Server.Tests;

public class G2PanelLedTests
{
    private static StateDto State(int txReceiverIndex = 0, bool splitEnabled = false,
        bool rit = false, bool xit = false, bool locked = false, bool ps = false) =>
        new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "test",
            VfoHz: 7_100_000,
            Mode: RxMode.LSB,
            FilterLowHz: -2900,
            FilterHighHz: -100,
            SampleRate: 192_000,
            TxReceiverIndex: txReceiverIndex,
            SplitEnabled: splitEnabled,
            RitEnabled: rit,
            XitEnabled: xit,
            VfoLocked: locked,
            PsEnabled: ps);

    private static bool Led(StateDto s, int led, bool mox = false, bool tun = false) =>
        G2FrontPanelService.LedStates(s, mox, tun).Single(x => x.Led == led).On;

    [Fact]
    public void AbLed_IsOff_InSimplex() => Assert.False(Led(State(), 8));

    [Fact]
    public void AbLed_Lights_WhenTransmittingOnVfoB() => Assert.True(Led(State(txReceiverIndex: 1), 8));

    [Fact]
    public void AbLed_Lights_ForRx1SplitProjection() => Assert.True(Led(State(splitEnabled: true), 8));

    [Fact]
    public void OtherLeds_FollowTheirState()
    {
        var s = State(rit: true, xit: true, locked: true, ps: true);
        Assert.True(Led(s, 1, mox: true));
        Assert.True(Led(s, 2, tun: true));
        Assert.True(Led(s, 3));
        Assert.True(Led(s, 6));
        Assert.True(Led(s, 7));
        Assert.True(Led(s, 9));
        Assert.False(Led(State(), 7));
    }

    [Fact]
    public void AtuLed_IsNeverDriven() =>
        Assert.DoesNotContain(G2FrontPanelService.LedStates(State(), false, false), x => x.Led == 4);
}
