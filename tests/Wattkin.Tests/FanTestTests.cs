using Wattkin.Features;
using Xunit;

namespace Wattkin.Tests;

public class FanTestTests
{
    private static List<FanReading> Readings(params (int Fan1, int Fan2)[] values) => [.. values.Select(v => new FanReading(v.Fan1, v.Fan2))];

    [Fact]
    public void FansStillSpeedingUpAreNotSettled()
    {
        // Cooler Boost ramping: each second faster than the last.
        Assert.False(FanTest.Settled(Readings((3500, 3400), (4300, 4200), (5000, 4900), (5600, 5500))));
        Assert.False(FanTest.Settled(Readings((6000, 5900), (6010, 5900), (6000, 5905))));
    }

    [Fact]
    public void FourCloseReadingsOfBothFansAreSettled()
    {
        Assert.True(FanTest.Settled(Readings((3500, 3400), (6050, 5880), (6010, 5900), (6000, 5910), (6040, 5890))));

        // One fan still moving keeps the test going.
        Assert.False(FanTest.Settled(Readings((6050, 5000), (6010, 5300), (6000, 5600), (6040, 5850))));

        // A single fan (the other reads 0) settles on its own.
        Assert.True(FanTest.Settled(Readings((6050, 0), (6010, 0), (6000, 0), (6040, 0))));
    }

    [Fact]
    public void TachGlitchesDontCountAsSpeeds()
    {
        Assert.Equal(0, FanTest.Plausible(null));
        Assert.Equal(0, FanTest.Plausible(0));
        Assert.Equal(0, FanTest.Plausible(48000));
        Assert.Equal(5900, FanTest.Plausible(5900));
    }

    [Fact]
    public void TopAndAverageAreTakenPerFan()
    {
        var readings = Readings((5800, 6100), (6000, 5900));
        Assert.Equal(new FanReading(6000, 6100), FanTest.Top(readings));
        Assert.Equal(new FanReading(5900, 6000), FanTest.Average(readings));
        Assert.Equal(default, FanTest.Top([]));
    }
}
