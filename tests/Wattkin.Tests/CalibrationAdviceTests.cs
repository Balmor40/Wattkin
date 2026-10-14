using Wattkin.Features;
using Wattkin.Monitoring;
using Xunit;

namespace Wattkin.Tests;

public class CalibrationAdviceTests
{
    private static readonly DateTime Today = new(2026, 10, 14);

    private static List<CapacityPoint> Steady(double wh, int days) =>
        [.. Enumerable.Range(1, days).Select(d => new CapacityPoint(Today.AddDays(-d), wh, 80.3))];

    [Fact]
    public void SuggestsWhenTheReadingDropsWellBelowItsRecentLevel()
    {
        // This laptop: about 65 Wh for months, then 58.4 Wh today.
        var advice = CalibrationAdvice.Check(Steady(65, 30), 58.4, null, Today);
        Assert.NotNull(advice);
        Assert.Equal(65, advice!.TypicalWh, 1);
    }

    [Fact]
    public void SmallDipsAndShortHistoriesAreLeftAlone()
    {
        Assert.Null(CalibrationAdvice.Check(Steady(65, 30), 62, null, Today));
        Assert.Null(CalibrationAdvice.Check(Steady(65, 3), 50, null, Today));
        Assert.Null(CalibrationAdvice.Check(Steady(65, 30), null, null, Today));
    }

    [Fact]
    public void TheBaselineStartsAfterTheLastCalibration()
    {
        // A calibration re-measured 65 Wh down to 59 Wh ten days ago: 59 is the new normal.
        var calibrated = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var history = Steady(65, 30).Where(p => p.Date <= calibrated.ToLocalTime().Date).ToList();
        history.AddRange(Steady(59, 9));
        Assert.Null(CalibrationAdvice.Check(history, 59, calibrated, Today));

        // Just after a calibration there's too little new history to call anything a drop.
        Assert.Null(CalibrationAdvice.Check(Steady(65, 30), 58.4, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc), Today));

        // A calibration long ago doesn't hide a new drop.
        Assert.NotNull(CalibrationAdvice.Check(Steady(65, 30), 58.4, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), Today));
    }

    [Fact]
    public void TodaysPointsDoNotHideTheDrop()
    {
        var history = Steady(65, 30);
        history.Add(new CapacityPoint(Today, 58.4, 80.3));
        Assert.NotNull(CalibrationAdvice.Check(history, 58.4, null, Today));
    }
}
