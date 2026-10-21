using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Settings;
using Wattkin.UI;
using Xunit;

namespace Wattkin.Tests;

public sealed class TrayReadingTests
{
    [Fact]
    public void EachTrayChoiceShowsItsReadingOrTheLogo()
    {
        var snapshot = Snapshot(watts: 17.4, cpuTempC: 72, cpuLoad: 45.2);

        Assert.Null(TrayReading.From(TrayDisplay.Logo, snapshot));
        Assert.Equal(("17", "W"), Shown(TrayReading.From(TrayDisplay.Watts, snapshot)));
        Assert.Equal(("72", "°"), Shown(TrayReading.From(TrayDisplay.CpuTemperature, snapshot)));
        Assert.Equal(("45", "%"), Shown(TrayReading.From(TrayDisplay.CpuLoad, snapshot)));

        // No EC (not administrator) or no watts yet: the logo.
        var unread = Snapshot(watts: null, cpuTempC: null, cpuLoad: 0);
        Assert.Null(TrayReading.From(TrayDisplay.Watts, unread));
        Assert.Null(TrayReading.From(TrayDisplay.CpuTemperature, unread));
        Assert.Equal(("0", "%"), Shown(TrayReading.From(TrayDisplay.CpuLoad, unread)));
    }

    [Fact]
    public void TheTrayNumberOnlyMovesWhenTheReadingReallyMoved()
    {
        var temp = new TrayReading(TrayDisplay.CpuTemperature, 72);
        Assert.Equal(72, new TrayReading(TrayDisplay.CpuTemperature, 73).Settle(temp).Value);
        Assert.Equal(71, new TrayReading(TrayDisplay.CpuTemperature, 71).Settle(new TrayReading(TrayDisplay.CpuTemperature, 73)).Value);

        var load = new TrayReading(TrayDisplay.CpuLoad, 10);
        Assert.Equal(10, new TrayReading(TrayDisplay.CpuLoad, 12.5).Settle(load).Value);
        Assert.Equal(13, new TrayReading(TrayDisplay.CpuLoad, 13).Settle(load).Value);

        // Watts: 0.3 W, or 3% of a larger reading.
        Assert.Equal(5, new TrayReading(TrayDisplay.Watts, 5.2).Settle(new TrayReading(TrayDisplay.Watts, 5)).Value);
        Assert.Equal(40, new TrayReading(TrayDisplay.Watts, 41).Settle(new TrayReading(TrayDisplay.Watts, 40)).Value);
        Assert.Equal(42, new TrayReading(TrayDisplay.Watts, 42).Settle(new TrayReading(TrayDisplay.Watts, 40)).Value);

        // A new choice, or nothing shown before: the reading as it is.
        Assert.Equal(new TrayReading(TrayDisplay.CpuLoad, 72.5), new TrayReading(TrayDisplay.CpuLoad, 72.5).Settle(temp));
        Assert.Equal(new TrayReading(TrayDisplay.Watts, 9), new TrayReading(TrayDisplay.Watts, 9).Settle(null));
    }

    private static (string, string)? Shown(TrayReading? reading) => reading is { } r ? (r.Number, r.Unit) : null;

    private static Snapshot Snapshot(double? watts, int? cpuTempC, double cpuLoad)
    {
        var power = new PowerState(PowerSource.AcCpuOnly, watts, watts, watts, 5, 0, OnAc: true, Charging: false, 80, 48, 60, null, null, null, null);
        var sensors = new EcSensors(cpuTempC, null, null, null);
        return new Snapshot(DateTime.UtcNow, power, new CpuStat(cpuLoad, 0, 0, null, null, null, null), new MemoryStat(0, 0, 0, 0, 0), [], sensors, null, null, new DgpuStatus(DgpuState.None, null, []), [], []);
    }
}
