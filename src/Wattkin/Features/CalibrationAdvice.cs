using Wattkin.Monitoring;

namespace Wattkin.Features;

/// <summary>The gauge's capacity estimate fell well below its own recent level.</summary>
public sealed record CalibrationSuggestion(double TypicalWh, double NowWh);

/// <summary>
/// Decides when to suggest a calibration, so nobody has to judge it from the chart. A sudden drop in the
/// reported full-charge capacity is usually the gauge drifting between full discharges, not wear:
/// real wear takes months. Pure logic, unit tested.
/// </summary>
public static class CalibrationAdvice
{
    private const double DropFraction = 0.07;
    private const int MinimumDays = 5;
    private static readonly TimeSpan Baseline = TimeSpan.FromDays(60);

    /// <param name="history">Daily capacity points (local dates), as the capacity chart shows them.</param>
    /// <param name="lastCalibratedUtc">When the last calibration that finished successfully ended: readings before it don't count.</param>
    /// <param name="today">Local date; today's points are left out of the baseline.</param>
    public static CalibrationSuggestion? Check(IReadOnlyList<CapacityPoint> history, double? fullWhNow, DateTime? lastCalibratedUtc, DateTime today)
    {
        if (fullWhNow is not { } now || !double.IsFinite(now) || now <= 0)
        {
            return null;
        }

        // The gauge re-measured at the last calibration, so the baseline starts the day after it.
        var from = today.Date - Baseline;
        if (lastCalibratedUtc is { } calibrated && calibrated.ToLocalTime().Date.AddDays(1) > from)
        {
            from = calibrated.ToLocalTime().Date.AddDays(1);
        }

        var earlier = history
            .Where(p => p.Date.Date < today.Date && p.Date.Date >= from && double.IsFinite(p.FullWh) && p.FullWh > 0)
            .Select(p => p.FullWh)
            .Order()
            .ToList();
        if (earlier.Count < MinimumDays)
        {
            return null; // not enough history to call it a drop
        }

        var typical = earlier[earlier.Count / 2];
        return now < typical * (1 - DropFraction) ? new CalibrationSuggestion(typical, now) : null;
    }
}
