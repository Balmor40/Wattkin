using System.Globalization;
using Wattkin.Monitoring;
using Wattkin.Settings;

namespace Wattkin.UI;

/// <summary>
/// The number drawn as the tray icon: the laptop's watts, the CPU temperature (from the EC, so only
/// with administrator rights) or the CPU load (Windows' "% Processor Utility", as Task Manager shows).
/// </summary>
internal readonly record struct TrayReading(TrayDisplay Display, double Value)
{
    /// <summary>The reading <paramref name="display"/> asks for; null for the logo, or when there's no reading (the logo then).</summary>
    public static TrayReading? From(TrayDisplay display, Snapshot snapshot) => display switch
    {
        TrayDisplay.Watts => snapshot.Power.WattsNow is { } w ? new TrayReading(display, w) : null,
        TrayDisplay.CpuTemperature => snapshot.Sensors?.CpuTempC is { } t ? new TrayReading(display, t) : null,
        TrayDisplay.CpuLoad => new TrayReading(display, snapshot.Cpu.UsagePercent),
        _ => null,
    };

    /// <summary>Whole numbers only ("17", "72", "45"); the flyout and the tooltip keep the decimal.</summary>
    public string Number => Math.Round(Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture);

    /// <summary>What follows the number in the icon: a small W or %, or a degree sign.</summary>
    public string Unit => Display switch
    {
        TrayDisplay.CpuTemperature => "°",
        TrayDisplay.CpuLoad => "%",
        _ => "W",
    };

    /// <summary>
    /// Every change redraws the taskbar (and wakes the display pipeline), so the icon keeps
    /// <paramref name="shown"/> until the reading really moved: at least 0.3 W or 3% of the watts,
    /// 2 °C, or 3 points of load.
    /// </summary>
    public TrayReading Settle(TrayReading? shown)
    {
        if (shown is not { } s || s.Display != Display)
        {
            return this;
        }

        var step = Display switch
        {
            TrayDisplay.CpuTemperature => 2,
            TrayDisplay.CpuLoad => 3,
            _ => Math.Max(0.3, s.Value * 0.03),
        };
        return Math.Abs(Value - s.Value) < step ? s : this;
    }
}
