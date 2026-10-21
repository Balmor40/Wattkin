using System.Globalization;

namespace Wattkin.Hardware;

/// <summary>What one unit of the RAPL registers is worth (MSR 0x606): watts and seconds per step.</summary>
public readonly record struct RaplUnits(double WattsPerStep, double SecondsPerStep)
{
    /// <summary>Intel client processors: 1/8 W and 1/1024 s (what ThrottleStop's numbers on the Stealth 15M decode with).</summary>
    public static readonly RaplUnits Typical = new(1.0 / 8, 1.0 / 1024);

    /// <summary>MSR_RAPL_POWER_UNIT: power unit in bits 0–3, time unit in bits 16–19, both 1/2^n.</summary>
    public static RaplUnits From(ulong msr606) => new(1.0 / (1L << (int)(msr606 & 0xF)), 1.0 / (1L << (int)((msr606 >> 16) & 0xF)));
}

/// <summary>Package power limits as the register holds them, in watts and seconds.</summary>
public readonly record struct PackageLimits(double Pl1Watts, bool Pl1Enabled, double TurboSeconds, double Pl2Watts, bool Pl2Enabled, bool Locked)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"PL1 {(Pl1Enabled ? $"{Pl1Watts:0.#} W" : "off")} for {TurboSeconds:0.##} s · PL2 {(Pl2Enabled ? $"{Pl2Watts:0.#} W" : "off")}{(Locked ? " · locked" : "")}");
}

/// <summary>
/// MSR_PKG_POWER_LIMIT (0x610) and its firmware copy at MCHBAR + 0x59A0 (Intel SDM vol. 4; the 12th Gen
/// datasheet for the MMIO copy; same layout on Tiger Lake). Low half: PL1 in bits 0–14, enable 15,
/// clamp 16, time window 17–23 (2^Y · (1 + Z/4) time units, Y bits 17–21, Z bits 22–23). High half:
/// PL2 in bits 32–46, enable 47, clamp 48, PL2's window 49–55. Bit 63 locks the register until reset.
/// The processor follows the lower of the MSR and the MMIO copy. Pure logic, unit tested.
/// </summary>
public static class PackagePowerLimit
{
    private const ulong LockBit = 1UL << 63;

    // The fields Wattkin sets (PL1, its enable, clamp and window; PL2, its enable and clamp).
    private const ulong OwnedMask = 0x00FF_FFFFUL | (0x1FFFFUL << 32);

    public static bool IsLocked(ulong raw) => (raw & LockBit) != 0;

    public static PackageLimits Decode(ulong raw, RaplUnits units) => new(
        (raw & 0x7FFF) * units.WattsPerStep,
        (raw & (1UL << 15)) != 0,
        TimeWindow((int)((raw >> 17) & 0x1F), (int)((raw >> 22) & 0x3), units),
        ((raw >> 32) & 0x7FFF) * units.WattsPerStep,
        (raw & (1UL << 47)) != 0,
        IsLocked(raw));

    /// <summary>
    /// <paramref name="current"/> with PL1, PL2 and PL1's window replaced, both limits enabled and
    /// clamped (allowed to hold the processor under its base clock, so a low PL1 really holds). PL2's own
    /// window and the lock bit stay as they are.
    /// </summary>
    public static ulong Encode(ulong current, PowerLimitSetting limits, RaplUnits units)
    {
        var pl1 = (ulong)Math.Clamp((long)Math.Round(limits.Pl1Watts / units.WattsPerStep), 0, 0x7FFF);
        var pl2 = (ulong)Math.Clamp((long)Math.Round(limits.Pl2Watts / units.WattsPerStep), 0, 0x7FFF);
        var (y, z) = NearestTimeWindow(limits.TurboSeconds, units);
        var low = pl1 | (1UL << 15) | (1UL << 16) | ((ulong)y << 17) | ((ulong)z << 22);
        var high = (pl2 | (1UL << 15) | (1UL << 16)) << 32;
        return (current & ~OwnedMask) | low | high;
    }

    /// <summary>The fields <see cref="Encode"/> sets agree (what a read-back must show).</summary>
    public static bool SameLimits(ulong a, ulong b) => (a & OwnedMask) == (b & OwnedMask);

    /// <summary>
    /// <paramref name="current"/> with the fields <see cref="Encode"/> sets taken from <paramref name="saved"/>:
    /// how a capture kept in settings.json (a file the user can edit) goes back, so it can never bring
    /// the lock bit or a reserved bit to the register.
    /// </summary>
    public static ulong Restore(ulong current, ulong saved) => (current & ~OwnedMask) | (saved & OwnedMask);

    /// <summary>
    /// A saved capture worth putting back: both limits at least <see cref="PowerLimitSetting.MinWatts"/>,
    /// as low as a profile could set anyway. No upper bound: firmware often leaves PL2 at the register's
    /// maximum (4095 W, "unlimited"), and the processor's own thermal limit holds whatever the register says.
    /// </summary>
    public static bool IsPlausible(ulong raw, RaplUnits units) =>
        Decode(raw, units) is { Pl1Watts: >= PowerLimitSetting.MinWatts, Pl2Watts: >= PowerLimitSetting.MinWatts };

    public static double TimeWindow(int y, int z, RaplUnits units) => Math.Pow(2, y) * (1 + (z / 4.0)) * units.SecondsPerStep;

    /// <summary>The window the register can hold that's closest to <paramref name="seconds"/>.</summary>
    public static (int Y, int Z) NearestTimeWindow(double seconds, RaplUnits units)
    {
        var best = (Y: 0, Z: 0);
        var bestError = double.MaxValue;
        for (var y = 0; y < 32; y++)
        {
            for (var z = 0; z < 4; z++)
            {
                var error = Math.Abs(TimeWindow(y, z, units) - seconds);
                if (error < bestError)
                {
                    bestError = error;
                    best = (y, z);
                }
            }
        }

        return best;
    }

    /// <summary>Whole-second windows the register can hold exactly, 1–128 s: what the editor offers.</summary>
    public static IReadOnlyList<int> TurboTimeChoices(RaplUnits units) =>
        [.. Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(0, 4).Select(z => TimeWindow(y, z, units)))
            .Where(t => t is >= 1 and <= 128 && Math.Abs(t - Math.Round(t)) < 1e-9)
            .Select(t => (int)Math.Round(t)).Distinct().Order()];
}

/// <summary>
/// A profile's own package power limits ("Manual"): PL1, the sustained limit; PL2, the burst limit;
/// and how long PL2 may run before PL1 takes over (PL1's time window, ThrottleStop's "turbo time").
/// </summary>
public sealed class PowerLimitSetting
{
    public const int MinWatts = 5;
    public const int MaxWatts = 120;
    public const double MinTurboSeconds = 1;
    public const double MaxTurboSeconds = 128;

    public int Pl1Watts { get; set; } = 25;
    public int Pl2Watts { get; set; } = 35;
    public double TurboSeconds { get; set; } = 28;

    public PowerLimitSetting Clone() => (PowerLimitSetting)MemberwiseClone();

    public bool SameAs(PowerLimitSetting? other) =>
        other is not null && other.Pl1Watts == Pl1Watts && other.Pl2Watts == Pl2Watts && other.TurboSeconds.Equals(TurboSeconds);

    /// <summary>
    /// What's wrong, or null: PL1 and PL2 of 5–120 W (and at most what the processor reports it can
    /// take, <paramref name="maxWatts"/>, when it reports it), PL2 no lower than PL1, 1–128 s.
    /// </summary>
    public string? Problem(double? maxWatts = null)
    {
        var top = maxWatts is > MinWatts and < MaxWatts ? (int)maxWatts.Value : MaxWatts;
        if (Pl1Watts < MinWatts || Pl1Watts > top || Pl2Watts < MinWatts || Pl2Watts > top)
        {
            return string.Format(Strings.Power_WattsRange, MinWatts, top);
        }

        if (Pl2Watts < Pl1Watts)
        {
            return Strings.Power_Pl2BelowPl1;
        }

        return TurboSeconds is < MinTurboSeconds or > MaxTurboSeconds or double.NaN
            ? string.Format(Strings.Power_TurboRange, MinTurboSeconds, MaxTurboSeconds)
            : null;
    }

    /// <summary>
    /// A warning when these limits are above the firmware's own copy (<paramref name="firmware"/>, MCHBAR,
    /// as MSI's Balanced mode sets it), which the processor also follows: it keeps to the lower of the
    /// two, and Wattkin only writes the MSR. Null when they fit, or when the copy isn't known. The limits
    /// are still saved.
    /// </summary>
    public string? AboveFirmware(PackageLimits? firmware)
    {
        if (firmware is not { } fw)
        {
            return null;
        }

        var pl1 = fw.Pl1Enabled && Pl1Watts > fw.Pl1Watts;
        var pl2 = fw.Pl2Enabled && Pl2Watts > fw.Pl2Watts;
        return (pl1, pl2) switch
        {
            (true, true) => string.Format(Strings.Power_BothAboveFirmware, fw.Pl1Watts, fw.Pl2Watts),
            (true, false) => string.Format(Strings.Power_Pl1AboveFirmware, fw.Pl1Watts),
            (false, true) => string.Format(Strings.Power_Pl2AboveFirmware, fw.Pl2Watts),
            _ => null,
        };
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"PL1 {Pl1Watts} W · PL2 {Pl2Watts} W · turbo {TurboSeconds:0.##} s");
}
