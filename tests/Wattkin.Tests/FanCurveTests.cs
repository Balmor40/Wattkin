using Wattkin.Hardware;
using Xunit;

namespace Wattkin.Tests;

public class FanCurveTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split('-').Select(h => Convert.ToByte(h, 16)), .. new byte[23]];

    // Get_Temperature(n) / Get_Fan(n) as the Prestige 16 Studio (EC 1594EMS1.113) returned them.
    private static readonly byte[] CpuTemps = Bytes("00-5F-64-3C-46-50-55-5A");
    private static readonly byte[] CpuSpeeds = Bytes("32-00-32-37-46-50-82-82");
    private static readonly byte[] GpuTemps = Bytes("00-57-5F-2D-3C-4B-50-55");
    private static readonly byte[] GpuSpeeds = Bytes("00-00-3C-50-82-82-82-82");

    [Fact]
    public void ReadsTheTablesInTheEcsOrder()
    {
        var cpu = FanCurve.Parse(CpuTemps, CpuSpeeds)!;
        Assert.Equal([60, 70, 80, 85, 90, 95], cpu.Temps);
        Assert.Equal([0, 50, 55, 70, 80, 130, 130], cpu.Speeds);
        Assert.True(cpu.Sane);
        Assert.Equal("off below 60°C · 50% from 60°C · 55% from 70°C · 70% from 80°C · 80% from 85°C · 130% from 90°C", cpu.Describe());

        var gpu = FanCurve.Parse(GpuTemps, GpuSpeeds)!;
        Assert.Equal([45, 60, 75, 80, 85, 87], gpu.Temps);
        Assert.True(gpu.Sane);
        Assert.Equal("off below 45°C · 60% from 45°C · 80% from 60°C · 130% from 75°C", gpu.Describe());
    }

    [Fact]
    public void TablesThatCouldOverheatAreRejected()
    {
        // Top step below 100%: the fan would never run flat out.
        Assert.False(new FanCurve([60, 70, 80, 85, 90, 95], [0, 20, 30, 40, 50, 60, 70]).Sane);

        // Speeds dropping as it gets hotter.
        Assert.False(new FanCurve([60, 70, 80, 85, 90, 95], [0, 50, 55, 30, 80, 130, 130]).Sane);

        // An empty table (all zero), as a reset controller might hold.
        Assert.False(FanCurve.Parse(new byte[31], new byte[31])!.Sane);

        Assert.Null(FanCurve.Parse(null, CpuSpeeds));
    }
}
