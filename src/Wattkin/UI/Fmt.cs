using System.Globalization;
using Wattkin.Hardware;
using Wattkin.Monitoring;

namespace Wattkin.UI;

internal static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public static string Watts(double? watts) => watts is null ? "—" : WattsNumber(watts) + " W";

    /// <summary>One decimal below 10 W, whole watts above. Rounded first, so 9.97 shows as "10", not "10.0".</summary>
    public static string WattsNumber(double? watts)
    {
        if (watts is not { } w || !double.IsFinite(w))
        {
            return "—";
        }

        var tenth = Math.Round(w, 1, MidpointRounding.AwayFromZero);
        return tenth < 10 ? tenth.ToString("0.0", C) : Math.Round(w, MidpointRounding.AwayFromZero).ToString("0", C);
    }

    /// <summary>For display only: the per-user folders as %APPDATA% / %LOCALAPPDATA%, so screenshots don't show the account name.</summary>
    public static string FriendlyPath(string path)
    {
        foreach (var (folder, name) in new[] { (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"), (Environment.SpecialFolder.ApplicationData, "%APPDATA%") })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return name + path[root.Length..];
            }
        }

        return path;
    }

    /// <summary>Time estimates in steps that don't flicker: 5 minutes above an hour (above 10 minutes when coarse).</summary>
    public static TimeSpan? Steady(TimeSpan? span, bool coarse = false) =>
        span is { } v && v.TotalMinutes >= (coarse ? 10 : 60) ? TimeSpan.FromMinutes(Math.Round(v.TotalMinutes / 5) * 5) : span;

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t)
        {
            return "—";
        }

        if (t.TotalMinutes < 1)
        {
            return Strings.Unit_UnderAMinute;
        }

        if (t.TotalHours < 1)
        {
            return string.Format(Strings.Unit_Minutes, (int)t.TotalMinutes);
        }

        return t.Minutes == 0 ? string.Format(Strings.Unit_Hours, (int)t.TotalHours) : string.Format(Strings.Unit_HoursMinutes, (int)t.TotalHours, t.Minutes);
    }

    /// <summary>A short wait in seconds: "45 s", "1 min", "1 min 30 s".</summary>
    public static string Delay(int seconds) => (seconds / 60, seconds % 60) switch
    {
        (0, var s) => string.Format(Strings.Unit_Seconds, s),
        (var m, 0) => string.Format(Strings.Unit_Minutes, m),
        (var m, var s) => string.Format(Strings.Unit_MinutesSeconds, m, s),
    };

    public static string Percent(double? value, string format = "0") => value is { } v ? string.Format(Strings.Unit_Percent, v.ToString(format, C)) : "—";

    /// <summary>"80%" (French: "80 %").</summary>
    public static string Percent(int value) => string.Format(Strings.Unit_Percent, value);

    public static string GHz(double mhz) => mhz <= 0 ? "—" : (mhz / 1000).ToString("0.00", C) + " GHz";

    public static string Bytes(double bytes)
    {
        var units = Strings.Unit_ByteUnits.Split(' ');
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }

        return bytes.ToString(bytes >= 100 || i == 0 ? "0" : "0.0", C) + " " + units[i];
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Temp(int? celsius) => celsius is { } c ? string.Format(Strings.Unit_Celsius, c) : "—";

    public static string Temp(double? celsius) => celsius is { } c ? string.Format(Strings.Unit_Celsius, c.ToString("0", C)) : "—";

    public static string Rpm(int? rpm) => rpm switch
    {
        null => "—",
        0 => Strings.Fmt_FanOff,
        _ => rpm.Value.ToString("N0", C),
    };

    /// <summary>Both fans: "off" when both are stopped, else "2,400 / off rpm".</summary>
    public static string Fans(int? fan1, int? fan2) => (fan1, fan2) switch
    {
        (null, null) => "—",
        (0, 0) => Strings.Fmt_FanOff,
        _ => string.Format(Strings.Unit_FanPair, Rpm(fan1), Rpm(fan2)),
    };

    public static string PowerHeadline(PowerState p) => p.Source switch
    {
        PowerSource.BatteryMeasured => Strings.Fmt_HeadlineMeasured,
        PowerSource.AcEstimated => Strings.Fmt_HeadlineEstimated,
        PowerSource.AcCpuOnly => Strings.Fmt_HeadlineCpuOnly,
        _ => "",
    };

    /// <param name="coarse">Round times to 5 minutes (the tray tooltip: fewer pointless updates).</param>
    public static string BatteryLine(PowerState p, string chargeStatus, bool coarse = false)
    {
        if (p.Percent is null)
        {
            return Strings.Fmt_NoBatteryData;
        }

        if (p.Source == PowerSource.BatteryMeasured)
        {
            return p.TimeToEmpty is { } t ? string.Format(Strings.Fmt_TimeLeft, Duration(Steady(t, coarse))) : Strings.Fmt_Estimating;
        }

        if (p.Charging)
        {
            return p.TimeToTarget is { } t ? string.Format(Strings.Fmt_ChargingToLimit, Duration(Steady(t, coarse))) : Strings.Fmt_Charging;
        }

        return string.IsNullOrEmpty(chargeStatus) ? Strings.Fmt_PluggedIn : chargeStatus;
    }

    public static string Dgpu(DgpuStatus status) => status.State switch
    {
        DgpuState.None => Strings.Fmt_DgpuNone,
        DgpuState.RemovedByFirmware => Strings.Fmt_DgpuRemoved,
        DgpuState.DisabledInWindows => Strings.Fmt_DgpuDisabled,
        DgpuState.Sleeping => Strings.Fmt_DgpuAsleep,
        DgpuState.Active => Strings.Fmt_DgpuAwake,
        _ => Strings.Fmt_DgpuUnknown,
    };

    /// <summary>Package power limits as the editor shows them: "PL1 15 W for 28 s · PL2 25 W" (<see cref="PackageLimits.Describe"/> is the log's).</summary>
    public static string Limits(PackageLimits limits)
    {
        string Watts(bool enabled, double watts) => enabled ? $"{watts.ToString("0.#", C)} W" : Strings.Fmt_LimitOff;
        var text = string.Format(Strings.Fmt_Limits, Watts(limits.Pl1Enabled, limits.Pl1Watts), limits.TurboSeconds.ToString("0.##", C), Watts(limits.Pl2Enabled, limits.Pl2Watts));
        return limits.Locked ? string.Format(Strings.Fmt_LimitsLocked, text) : text;
    }

    /// <summary>The parts that exist, as a meter bar's right-hand text: "4.5 W  ·  1.20 GHz  ·  46°C".</summary>
    public static string Join(params string?[] parts) =>
        string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p) && p != "—"));

    /// <summary>"whole laptop, …" → "Whole laptop, …".</summary>
    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Everything a profile sets, one item each: what the flyout shows under the profile buttons.</summary>
    public static string ProfileDetails(Settings.Profile p)
    {
        var parts = new List<string>();
        if (p.PowerLimits is { } limits)
        {
            parts.Add(string.Format(Strings.Fmt_DetailManual, limits.Pl1Watts, limits.Pl2Watts));
        }
        else if (p.Performance is { } shift)
        {
            parts.Add(string.Format(Strings.Fmt_DetailShift, Shift(shift)));
        }

        if (FanWords(p) is { } fans)
        {
            parts.Add(fans);
        }

        if (p.CpuBoost is { } boost)
        {
            parts.Add(boost ? Strings.Fmt_DetailTurboOn : Strings.Fmt_DetailTurboOff);
        }

        if (p.MaxCpuPercent is { } max)
        {
            parts.Add(string.Format(Strings.Fmt_DetailMaxCpu, Percent(max)));
        }

        if (p.MaxFrequencyMHz is { } mhz)
        {
            parts.Add(mhz == 0 ? Strings.Fmt_DetailNoClockCap : string.Format(Strings.Fmt_DetailClockCap, Gigahertz(mhz)));
        }

        if (p.EnergyPreference is { } epp)
        {
            parts.Add($"EPP {epp}");
        }

        if (p.RefreshRate is { } hz)
        {
            parts.Add(string.Format(Strings.Fmt_ScreenHz, hz));
        }

        if (p.KeyboardLevel is { } level)
        {
            parts.Add(string.Format(Strings.Fmt_DetailKeyboard, KeyboardLevel(level).ToLower(CultureInfo.CurrentCulture)));
        }

        return parts.Count == 0 ? Strings.Fmt_LeavesEverything : string.Join("  ·  ", parts);
    }

    /// <summary>A profile's fan setting in a few words; null when it leaves the fans alone.</summary>
    public static string? FanWords(Settings.Profile p) => p switch
    {
        { CoolerBoost: true } => Strings.Fmt_FansFull,
        { Fan: FanMode.Auto } => Strings.Fmt_FansAuto,
        { Fan: FanMode.Silent } => Strings.Fmt_FansSilent,
        { Fan: FanMode.Custom, HasOwnCurve: true } => Strings.Fmt_FansOwnCurve,
        { Fan: FanMode.Custom } => Strings.Fmt_FansLaptopCurve,
        _ => null,
    };

    /// <summary>What a profile changes, in plain words, built from its settings so it stays true after editing.</summary>
    public static string ProfileSummary(Settings.Profile p)
    {
        var parts = new List<string>();
        if (p.PowerLimits is { } limits)
        {
            parts.Add(string.Format(Strings.Fmt_SummaryManual, limits.Pl1Watts));
        }
        else if (p.Performance is { } shift)
        {
            parts.Add(shift switch
            {
                ShiftMode.Eco => Strings.Fmt_SummaryEco,
                ShiftMode.Sport => Strings.Fmt_SummarySport,
                ShiftMode.Turbo => Strings.Fmt_SummaryExtreme,
                _ => Strings.Fmt_SummaryBalanced,
            });
        }

        if (p.Fan != FanMode.Auto || p.CoolerBoost)
        {
            if (FanWords(p) is { } fans)
            {
                parts.Add(fans);
            }
        }

        if (p.CpuBoost == false)
        {
            parts.Add(Strings.Fmt_SummaryNoTurbo);
        }

        if (p.MaxCpuPercent is { } max and < 100)
        {
            parts.Add(string.Format(Strings.Fmt_SummaryMaxCpu, Percent(max)));
        }

        if (p.MaxFrequencyMHz is { } mhz and > 0)
        {
            parts.Add(string.Format(Strings.Fmt_SummaryClockCap, Gigahertz(mhz)));
        }

        if (p.EnergyPreference is { } epp and >= 60)
        {
            parts.Add(Strings.Fmt_SummaryEfficiency);
        }

        if (p.RefreshRate is { } hz)
        {
            parts.Add(string.Format(Strings.Fmt_ScreenHz, hz));
        }

        if (p.KeyboardLevel is { } level)
        {
            parts.Add(level == 0 ? Strings.Fmt_SummaryKeyboardOff : Strings.Fmt_SummaryKeyboardOn);
        }

        return parts.Count == 0 ? Strings.Fmt_LeavesEverything : string.Join(", ", parts);
    }

    /// <summary>A clock cap in MHz as the profile lists show it: "2.4 GHz", "1.85 GHz".</summary>
    public static string Gigahertz(int mhz) => (mhz / 1000.0).ToString("0.0#", C) + " GHz";

    public static string Shift(ShiftMode? mode) => mode switch
    {
        ShiftMode.Eco => Strings.Shift_Eco,
        ShiftMode.Balanced => Strings.Shift_Balanced,
        ShiftMode.Sport => Strings.Shift_Sport,
        ShiftMode.Turbo => Strings.Shift_Extreme,
        _ => "—",
    };

    /// <summary>A calibration's phase as the history lists it (settings keep <see cref="Features.CalibrationPhase"/> names).</summary>
    public static string PhaseName(string? phase) => phase switch
    {
        nameof(Features.CalibrationPhase.ChargingToFull) => Strings.CalibPhase_ChargingToFull,
        nameof(Features.CalibrationPhase.DrainingForTopUp) => Strings.CalibPhase_DrainingForTopUp,
        nameof(Features.CalibrationPhase.Discharging) => Strings.CalibPhase_Discharging,
        nameof(Features.CalibrationPhase.Done) => Strings.CalibPhase_Done,
        nameof(Features.CalibrationPhase.Failed) => Strings.CalibPhase_Failed,
        nameof(Features.CalibrationPhase.Cancelled) => Strings.CalibPhase_Cancelled,
        _ => phase ?? "?",
    };

    /// <summary>The keyboard light's levels, 0–3: Off, Low, Mid, High.</summary>
    public static string KeyboardLevel(int level) => level switch
    {
        0 => Strings.Common_LevelOff,
        1 => Strings.Common_LevelLow,
        2 => Strings.Common_LevelMid,
        _ => Strings.Common_LevelHigh,
    };

    /// <summary>The keyboard light's levels in order, for the segment rows and lists.</summary>
    public static string[] KeyboardLevels() => [.. Enumerable.Range(0, 4).Select(KeyboardLevel)];
}
