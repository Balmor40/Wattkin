using Wattkin.Features;
using Xunit;

namespace Wattkin.Tests;

public class ChargePolicyTests
{
    private static ChargeDecision Decide(int limit, int? resume, double percent, bool onAc = true, bool charging = false, bool holding = false, bool full = false, bool drain = false) =>
        ChargePolicy.Decide(limit, resume, full, drain, holding, new ChargeInputs(percent, onAc, charging));

    [Fact]
    public void DefaultBandWritesTheLimitAndLetsTheEcDoTheRest()
    {
        var d = Decide(80, null, 75, charging: true);
        Assert.Equal(80, d.EcLimit);
        Assert.False(d.SimulateUnplug);
        Assert.False(d.BandHolding);
    }

    [Fact]
    public void ResumeEqualToEcHysteresisIsTreatedAsDefault()
    {
        Assert.Equal(80, Decide(80, 70, 72).EcLimit);
    }

    [Fact]
    public void WideBandChargesToTopThenHoldsUntilBelowResume()
    {
        // Charging up to 90.
        var charging = Decide(90, 70, 85, charging: true);
        Assert.Equal(90, charging.EcLimit);
        Assert.False(charging.BandHolding);

        // Reached the top: hold with the EC limit at resume + 10 so it won't restart until < 70.
        var top = Decide(90, 70, 90, charging: false);
        Assert.True(top.BandHolding);
        Assert.Equal(80, top.EcLimit);

        // Drifted down to 75 while holding: still holding.
        var drifted = Decide(90, 70, 75, holding: true);
        Assert.True(drifted.BandHolding);
        Assert.Equal(80, drifted.EcLimit);

        // EC started charging below 70: switch back to the full band top.
        var restarted = Decide(90, 70, 69, charging: true, holding: true);
        Assert.False(restarted.BandHolding);
        Assert.Equal(90, restarted.EcLimit);
    }

    [Theory]
    [InlineData(75, 74)]
    [InlineData(75, 75)]
    [InlineData(75, 60)]
    public void BandsNarrowerThanTheEcGapFallBackToTheEcDefault(double percent, int resumeRequested)
    {
        // The EC's 10% gap is the narrowest band; the stop limit is simply written as-is.
        var d = Decide(80, resumeRequested, percent);
        Assert.Equal(80, d.EcLimit);
        Assert.False(d.BandHolding);
    }

    [Fact]
    public void WideBandNeverHoldsAboveItsTop()
    {
        foreach (var percent in new[] { 5.0, 50, 70, 89, 90, 95 })
        {
            Assert.True(Decide(90, 70, percent, holding: true).EcLimit <= 90);
            Assert.True(Decide(90, 70, percent, charging: true).EcLimit <= 90);
        }
    }

    [Fact]
    public void FullChargeOnceGoesTo100ThenReturnsToLimit()
    {
        var charging = Decide(80, null, 85, charging: true, full: true);
        Assert.Equal(100, charging.EcLimit);
        Assert.False(charging.FullChargeDone);

        // Gauges commonly report 98–99% when the EC says the battery is full.
        var done = Decide(80, null, 98.6, charging: false, full: true);
        Assert.True(done.FullChargeDone);
        Assert.Equal(80, done.EcLimit);
    }

    [Fact]
    public void FullChargeOnceExplainsWhenTheEcWontTopUp()
    {
        var d = Decide(80, null, 93, charging: false, full: true);
        Assert.Equal(100, d.EcLimit);
        Assert.False(d.FullChargeDone);
        Assert.Contains("below 90%", d.Status);
    }

    [Fact]
    public void DrainSimulatesUnplugUntilLimitThenStops()
    {
        var draining = Decide(80, null, 92, drain: true);
        Assert.True(draining.SimulateUnplug);
        Assert.False(draining.DrainDone);

        var done = Decide(80, null, 80, drain: true);
        Assert.False(done.SimulateUnplug);
        Assert.True(done.DrainDone);
    }

    [Fact]
    public void DrainNeverGoesBelowSafetyFloor()
    {
        var d = Decide(10, null, 14, drain: true);
        Assert.False(d.SimulateUnplug);
        Assert.True(d.DrainDone);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(100, 100)]
    [InlineData(120, 100)]
    public void LimitIsClampedToWhatTheEcAccepts(int requested, int expected)
    {
        Assert.Equal(expected, Decide(requested, null, 50).EcLimit);
    }
}
