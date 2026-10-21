using System.IO;
using Wattkin.Hardware;
using Wattkin.Platform;
using Wattkin.Settings;
using Wattkin.UI;
using Xunit;

namespace Wattkin.Tests;

public sealed class PowerLimitTests : IDisposable
{
    // MSR 0x610 as ThrottleStop 9.7 wrote it on the owner's Stealth 15M (ThrottleStop.ini PowerLimitEDX/EAX):
    // its Performance, Game and Battery profiles.
    private const ulong Performance = 0x004380E0_009B8078;
    private const ulong Game = 0x00428200_00DD81B8;
    private const ulong Battery = 0x00438078_005B8050;

    private const ulong Lock = 1UL << 63;

    // What Wattkin never sets: bits 24–31 (reserved), PL2's window (49–55), 56–62 (reserved) and the lock.
    private const ulong Untouched = 0xFF00_0000UL | 0xFFFE_0000_0000_0000UL;

    private static readonly RaplUnits Units = RaplUnits.Typical;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wattkin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static PowerLimitSetting Limits(int pl1, int pl2, double seconds) => new() { Pl1Watts = pl1, Pl2Watts = pl2, TurboSeconds = seconds };

    // ---- the register ---------------------------------------------------------------------------------

    [Fact]
    public void TheRegisterReadsLikeThrottleStopShowsIt()
    {
        Assert.Equal(new PackageLimits(15, true, 12, 28, true, false), PackagePowerLimit.Decode(Performance, Units));
        Assert.Equal(new PackageLimits(55, true, 28, 64, true, false), PackagePowerLimit.Decode(Game, Units));
        Assert.Equal(new PackageLimits(10, true, 10, 15, true, false), PackagePowerLimit.Decode(Battery, Units));
        Assert.Equal("PL1 15 W for 12 s · PL2 28 W", PackagePowerLimit.Decode(Performance, Units).Describe());
        Assert.Equal("PL1 off for 12 s · PL2 28 W · locked", PackagePowerLimit.Decode((Performance & ~(1UL << 15)) | Lock, Units).Describe());
    }

    [Fact]
    public void TheUnitsComeFromTheProcessor()
    {
        // MSR_RAPL_POWER_UNIT on Intel client processors: 1/8 W, 1/2^14 J, 1/1024 s.
        Assert.Equal(RaplUnits.Typical, RaplUnits.From(0x000A0E03));
        Assert.Equal(new RaplUnits(1.0 / 16, 1.0 / 512), RaplUnits.From(0x00090E04));

        // With other units the same watts and seconds land in other raw values, and read back the same.
        var fine = new RaplUnits(1.0 / 16, 1.0 / 512);
        var raw = PackagePowerLimit.Encode(0, Limits(25, 35, 28), fine);
        Assert.Equal(25UL * 16, raw & 0x7FFF);
        Assert.Equal(new PackageLimits(25, true, 28, 35, true, false), PackagePowerLimit.Decode(raw, fine));
    }

    [Fact]
    public void EncodingSetsTheLimitsAndKeepsTheRest()
    {
        var limits = Limits(25, 35, 28);
        var raw = PackagePowerLimit.Encode(Performance, limits, Units);
        Assert.Equal(new PackageLimits(25, true, 28, 35, true, false), PackagePowerLimit.Decode(raw, Units));
        Assert.Equal((Performance >> 49) & 0x7F, (raw >> 49) & 0x7F); // PL2's own window stays
        Assert.NotEqual(0UL, raw & (1UL << 16));                      // PL1 clamped: a low limit really holds
        Assert.NotEqual(0UL, raw & (1UL << 48));
        Assert.True(PackagePowerLimit.SameLimits(raw, PackagePowerLimit.Encode(Game, limits, Units)));
        Assert.False(PackagePowerLimit.SameLimits(raw, Performance));

        // Wattkin never sets the lock bit, and a register the BIOS locked is recognised.
        Assert.False(PackagePowerLimit.IsLocked(raw));
        Assert.True(PackagePowerLimit.IsLocked(Performance | Lock));
        Assert.True(PackagePowerLimit.IsLocked(PackagePowerLimit.Encode(Performance | Lock, limits, Units))); // nor clears it
    }

    [Fact]
    public void EveryOfferedTurboTimeAndWattageSurvivesTheRoundTrip()
    {
        foreach (var seconds in PackagePowerLimit.TurboTimeChoices(Units))
        {
            foreach (var watts in new[] { PowerLimitSetting.MinWatts, 15, 28, 35, 64, PowerLimitSetting.MaxWatts })
            {
                var raw = PackagePowerLimit.Encode(Game, Limits(watts, watts, seconds), Units);
                Assert.Equal(new PackageLimits(watts, true, seconds, watts, true, false), PackagePowerLimit.Decode(raw, Units));
            }
        }
    }

    [Fact]
    public void TheFieldsNeverSpillIntoTheirNeighbours()
    {
        // Out-of-range watts (the rules stop them earlier) are held to their own 15 bits.
        var everythingElse = ulong.MaxValue & ~Lock;
        var raw = PackagePowerLimit.Encode(everythingElse, Limits(5000, -10, 28), Units);
        Assert.Equal(0x7FFFUL, raw & 0x7FFF);
        Assert.Equal(0UL, (raw >> 32) & 0x7FFF);
        Assert.Equal(everythingElse & Untouched, raw & Untouched);

        raw = PackagePowerLimit.Encode(0, Limits(25, 35, 28), Units);
        Assert.Equal(0UL, raw & Untouched);
    }

    [Fact]
    public void TurboTimesAreTheOnesTheRegisterCanHold()
    {
        Assert.Equal((14, 3), PackagePowerLimit.NearestTimeWindow(28, Units)); // 2^14 · 1.75 / 1024
        Assert.Equal((13, 2), PackagePowerLimit.NearestTimeWindow(12, Units));
        Assert.Equal(12, PackagePowerLimit.TimeWindow(13, 2, Units));

        var choices = PackagePowerLimit.TurboTimeChoices(Units);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128], choices);
    }

    [Fact]
    public void ATurboTimeBetweenTwoStepsGoesToTheNearestAndATieToTheShorter()
    {
        Assert.Equal(10, Window(9.5));
        Assert.Equal(8, Window(9));   // 8 and 10 are as near: the shorter burst
        Assert.Equal(28, Window(30)); // 28 and 32
        Assert.Equal(128, Window(130));
        Assert.Equal(28, PackagePowerLimit.Decode(PackagePowerLimit.Encode(Game, Limits(20, 30, 29), Units), Units).TurboSeconds);

        static double Window(double seconds)
        {
            var (y, z) = PackagePowerLimit.NearestTimeWindow(seconds, Units);
            return PackagePowerLimit.TimeWindow(y, z, Units);
        }
    }

    [Fact]
    public void SameLimitsLooksOnlyAtWhatWattkinSets()
    {
        Assert.True(PackagePowerLimit.SameLimits(Performance, Performance | Lock | 0xFF00_0000UL | (0x7FUL << 49)));
        Assert.False(PackagePowerLimit.SameLimits(Performance, Performance + 1));            // PL1 1/8 W higher
        Assert.False(PackagePowerLimit.SameLimits(Performance, Performance ^ (1UL << 16)));  // PL1's clamp
        Assert.False(PackagePowerLimit.SameLimits(Performance, Performance ^ (1UL << 47)));  // PL2's enable
        Assert.False(PackagePowerLimit.SameLimits(Performance, Performance ^ (1UL << 17)));  // PL1's window
    }

    [Fact]
    public void ASavedCaptureGoesBackWithoutTheLockOrReservedBits()
    {
        var tampered = Game | Lock | 0xFF00_0000UL | (0x7FUL << 56);
        var restored = PackagePowerLimit.Restore(Performance, tampered);
        Assert.True(PackagePowerLimit.SameLimits(restored, Game));
        Assert.False(PackagePowerLimit.IsLocked(restored));
        Assert.Equal(Performance & Untouched, restored & Untouched);
    }

    [Fact]
    public void OnlyPlausibleCapturesArePutBack()
    {
        Assert.True(PackagePowerLimit.IsPlausible(Game, Units));
        Assert.True(PackagePowerLimit.IsPlausible(Battery, Units));
        Assert.True(PackagePowerLimit.IsPlausible(Game | (0x7FFFUL << 32), Units)); // firmware's "unlimited" PL2, 4095 W
        Assert.False(PackagePowerLimit.IsPlausible(PackagePowerLimit.Encode(Game, Limits(1, 25, 8), Units), Units));
        Assert.False(PackagePowerLimit.IsPlausible(PackagePowerLimit.Encode(Game, Limits(15, 4, 8), Units), Units));
        Assert.False(PackagePowerLimit.IsPlausible(0, Units));
    }

    // ---- the rules ------------------------------------------------------------------------------------

    [Fact]
    public void ManualLimitsKeepTheirRules()
    {
        Assert.Null(Limits(15, 25, 8).Problem());
        Assert.Null(Limits(PowerLimitSetting.MinWatts, PowerLimitSetting.MinWatts, PowerLimitSetting.MinTurboSeconds).Problem());
        Assert.Null(Limits(PowerLimitSetting.MaxWatts, PowerLimitSetting.MaxWatts, PowerLimitSetting.MaxTurboSeconds).Problem());
        Assert.Null(Limits(30, 30, 28).Problem());                 // PL2 may equal PL1

        Assert.Contains("PL2 (bursts) can't be lower", Limits(30, 20, 8).Problem());
        Assert.Contains("between 5 and 120 W", Limits(4, 20, 8).Problem());
        Assert.Contains("between 5 and 120 W", Limits(15, 121, 8).Problem());
        Assert.Contains("between 5 and 120 W", Limits(-1, 20, 8).Problem());
        Assert.Contains("turbo time", Limits(15, 25, 0).Problem());
        Assert.Contains("turbo time", Limits(15, 25, 0.5).Problem());
        Assert.Contains("turbo time", Limits(15, 25, 129).Problem());
        Assert.Contains("turbo time", Limits(15, 25, double.NaN).Problem());
        Assert.Contains("turbo time", Limits(15, 25, double.PositiveInfinity).Problem());
    }

    [Fact]
    public void TheProcessorsOwnMaximumCapsTheLimits()
    {
        Assert.Null(Limits(15, 64, 8).Problem(maxWatts: 64));
        Assert.Contains("between 5 and 64 W", Limits(15, 65, 8).Problem(maxWatts: 64));
        Assert.Contains("between 5 and 64 W", Limits(15, 65, 8).Problem(maxWatts: 64.9)); // whole watts under it

        // A maximum that makes no sense (or none reported) leaves the usual 120 W.
        foreach (var odd in new double?[] { null, 0, 3, 120, 250 })
        {
            Assert.Null(Limits(15, 120, 8).Problem(odd));
        }
    }

    [Fact]
    public void LimitsAboveTheFirmwaresCopyAreFlaggedButAllowed()
    {
        // The firmware's copy under MSI's Balanced on the Stealth 15M (self-test of 2026-10-21).
        var firmware = PackagePowerLimit.Decode(0x0042816800DD8140, Units);
        Assert.Equal(new PackageLimits(40, true, 28, 45, true, false), firmware);
        Assert.Null(Limits(30, 40, 28).AboveFirmware(firmware));
        Assert.Null(Limits(40, 45, 28).AboveFirmware(firmware));
        Assert.Null(Limits(55, 64, 28).AboveFirmware(null)); // not known yet

        Assert.Equal("PL1 is above the 40 W the firmware allows under Balanced: the processor stays under it.", Limits(41, 45, 28).AboveFirmware(firmware));
        Assert.Equal("PL2 is above the 45 W the firmware allows under Balanced: the processor stays under it.", Limits(30, 50, 28).AboveFirmware(firmware));
        Assert.Equal("PL1 and PL2 are above what the firmware allows under Balanced (40 W and 45 W): the processor stays under those.", Limits(55, 64, 28).AboveFirmware(firmware));

        // A limit the firmware's copy doesn't enable doesn't cap anything.
        Assert.Null(Limits(30, 50, 28).AboveFirmware(firmware with { Pl2Enabled = false }));

        // A warning, not a refusal.
        Assert.Null(Limits(55, 64, 28).Problem());
    }

    [Fact]
    public void ManualLimitsAreCopiedNotShared()
    {
        var limits = Limits(15, 25, 8);
        var copy = limits.Clone();
        Assert.True(copy.SameAs(limits));
        copy.TurboSeconds = 16;
        Assert.False(copy.SameAs(limits));
        Assert.Equal(8, limits.TurboSeconds);
        Assert.False(limits.SameAs(null));
        Assert.Equal("PL1 15 W · PL2 25 W · turbo 8 s", limits.Describe());
    }

    // ---- profiles and settings ------------------------------------------------------------------------

    [Fact]
    public void ProfilesKeepTheirProcessorSettingsWithinRange()
    {
        var s = new AppSettings();
        s.Profiles[0].PowerLimits = Limits(40, 20, 28); // broken: dropped, the BIOS's apply
        s.Profiles[1].PowerLimits = Limits(15, 25, 8);
        s.Profiles[1].EnergyPreference = 150;
        s.Profiles[1].MaxFrequencyMHz = 100;
        s.Profiles[2].MaxFrequencyMHz = -5;
        s.Profiles[2].EnergyPreference = -3;
        s.Profiles[3].MaxFrequencyMHz = 99999;
        s.Normalize();
        Assert.Null(s.Profiles[0].PowerLimits);
        Assert.Equal(ShiftMode.Eco, s.Profiles[0].Performance);
        Assert.Equal(15, s.Profiles[1].PowerLimits!.Pl1Watts);
        Assert.Equal(100, s.Profiles[1].EnergyPreference);
        Assert.Equal(400, s.Profiles[1].MaxFrequencyMHz);
        Assert.Equal(0, s.Profiles[2].MaxFrequencyMHz); // no cap
        Assert.Equal(0, s.Profiles[2].EnergyPreference);
        Assert.Equal(8000, s.Profiles[3].MaxFrequencyMHz);
        Assert.Null(s.Profiles[3].EnergyPreference); // never set: left alone

        var copy = s.Profiles[1].Clone();
        copy.PowerLimits!.Pl1Watts = 20;
        Assert.Equal(15, s.Profiles[1].PowerLimits!.Pl1Watts); // a deep copy
    }

    [Fact]
    public void ManualLimitsEppAndFrequencySurviveARestart()
    {
        var store = new SettingsStore(_dir);
        store.Update(s =>
        {
            s.Profiles[3].Performance = ShiftMode.Balanced;
            s.Profiles[3].PowerLimits = Limits(35, 45, 28);
            s.Profiles[3].EnergyPreference = 33;
            s.Profiles[3].MaxFrequencyMHz = 0;
            s.Profiles[0].MaxFrequencyMHz = 2200;
            s.BiosPowerLimitRaw = Game;
            s.BiosPowerLimitBootUtc = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
            s.LastWrittenPowerLimitRaw = Performance;
            s.FirmwarePowerLimits = new() { ["Balanced"] = 0x0042816800DD8140, ["Turbo"] = Game };
        });

        var reloaded = new SettingsStore(_dir).Current;
        Assert.Equal(new Dictionary<string, ulong> { ["Balanced"] = 0x0042816800DD8140, ["Turbo"] = Game }, reloaded.FirmwarePowerLimits);
        Assert.True(reloaded.Profiles[3].PowerLimits!.SameAs(Limits(35, 45, 28)));
        Assert.Equal(ShiftMode.Balanced, reloaded.Profiles[3].Performance);
        Assert.Equal(33, reloaded.Profiles[3].EnergyPreference);
        Assert.Equal(0, reloaded.Profiles[3].MaxFrequencyMHz);
        Assert.Equal(2200, reloaded.Profiles[0].MaxFrequencyMHz);
        Assert.Null(reloaded.Profiles[1].PowerLimits);
        Assert.Null(reloaded.Profiles[1].EnergyPreference);
        Assert.Equal(Game, reloaded.BiosPowerLimitRaw);
        Assert.Equal(new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc), reloaded.BiosPowerLimitBootUtc!.Value.ToUniversalTime());
        Assert.Equal(Performance, reloaded.LastWrittenPowerLimitRaw);
    }

    [Fact]
    public void ManualLimitsEditedInTheFileStillKeepTheRules()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            { "Profiles": [
                { "Name": "Hot", "Performance": "Balanced", "PowerLimits": { "Pl1Watts": 200, "Pl2Watts": 250, "TurboSeconds": 28 }, "EnergyPreference": 900 },
                { "Name": "Fine", "Performance": "Balanced", "PowerLimits": { "Pl1Watts": 20, "Pl2Watts": 30, "TurboSeconds": 16 } }
            ] }
            """);
        var settings = new SettingsStore(_dir).Current;
        Assert.Null(settings.FirmwarePowerLimits); // none noted yet
        Assert.Null(settings.FindProfile("Hot")!.PowerLimits);
        Assert.Equal(100, settings.FindProfile("Hot")!.EnergyPreference);
        Assert.True(settings.FindProfile("Fine")!.PowerLimits!.SameAs(Limits(20, 30, 16)));
    }

    [Fact]
    public void OnlyMsiModesKeepANotedFirmwareCeiling()
    {
        var s = new AppSettings { FirmwarePowerLimits = new() { ["Balanced"] = 0x0042816800DD8140, ["Warp"] = Game, ["3"] = Game } };
        s.Normalize();
        Assert.Equal(["Balanced"], s.FirmwarePowerLimits!.Keys);

        s = new AppSettings { FirmwarePowerLimits = new() { ["Warp"] = Game } };
        s.Normalize();
        Assert.Null(s.FirmwarePowerLimits);
    }

    [Fact]
    public void ProfileTextsSayWhatManualAndThePlanSettingsDo()
    {
        var manual = new Profile { Name = "Cool", Performance = ShiftMode.Balanced, PowerLimits = Limits(15, 25, 8), Fan = FanMode.Auto, CpuBoost = true, MaxFrequencyMHz = 2200, EnergyPreference = 85 };
        Assert.Equal($"CPU 15 W, bursts 25 W  ·  auto fans  ·  turbo on  ·  up to {2.2:0.0#} GHz  ·  EPP 85", Fmt.ProfileDetails(manual));
        Assert.Equal($"processor held to 15 W, clock capped at {2.2:0.0#} GHz, favours efficiency", Fmt.ProfileSummary(manual));

        // Without Manual, MSI's mode is shown; no cap and a performance-leaning EPP stay out of the summary.
        var plain = new Profile { Name = "Fast", Performance = ShiftMode.Turbo, Fan = FanMode.Auto, MaxFrequencyMHz = 0, EnergyPreference = 20 };
        Assert.Equal("CPU Extreme  ·  auto fans  ·  no clock cap  ·  EPP 20", Fmt.ProfileDetails(plain));
        Assert.Equal("highest power limits", Fmt.ProfileSummary(plain));
    }

    // ---- the power plan -------------------------------------------------------------------------------

    [Fact]
    public void SavedPlanOriginalsOnlyRestoreSettingsWattkinWritesInTheirRange()
    {
        var scheme = Guid.NewGuid();
        var epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        var freq = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");

        Assert.True(WindowsPower.TryParseOriginal($"{scheme}:epp:ac", 100, out var s, out var setting, out var onAc));
        Assert.Equal((scheme, epp, true), (s, setting, onAc));
        Assert.True(WindowsPower.TryParseOriginal($"{scheme}:freqmax:dc", 4400, out _, out setting, out onAc));
        Assert.Equal((freq, false), (setting, onAc));
        Assert.True(WindowsPower.TryParseOriginal($"{scheme}:freqmax1:ac", 0, out _, out _, out _));
        Assert.True(WindowsPower.TryParseOriginal($"{scheme}:epp1:dc", 0, out _, out _, out _));
        Assert.True(WindowsPower.TryParseOriginal($"{scheme}:boost:ac", 2, out _, out _, out _));

        Assert.False(WindowsPower.TryParseOriginal($"{scheme}:epp:ac", 101, out _, out _, out _));
        Assert.False(WindowsPower.TryParseOriginal($"{scheme}:freqmax:ac", 10001, out _, out _, out _));
        Assert.False(WindowsPower.TryParseOriginal($"{scheme}:maxstate:ac", 101, out _, out _, out _));
        Assert.False(WindowsPower.TryParseOriginal($"{scheme}:something:ac", 1, out _, out _, out _));
        Assert.False(WindowsPower.TryParseOriginal($"{scheme}:epp:both", 50, out _, out _, out _));
        Assert.False(WindowsPower.TryParseOriginal("not-a-guid:epp:ac", 50, out _, out _, out _));
        Assert.False(WindowsPower.TryParseOriginal("epp:ac", 50, out _, out _, out _));
    }

    [Fact]
    public void MeasuringLiftsEveryCapThePlanHas()
    {
        var scheme = Guid.NewGuid();
        var plan = new ProcessorPlan(scheme, 0, 1, 80, 60, (2200, 1800), (85, 100));
        Assert.Equal(new ProcessorPlan(scheme, 2, 2, 100, 100, (0, 0), (0, 0)), WindowsPower.Unlimited(plan));

        // A plan without a frequency cap or energy preference setting gets none written.
        Assert.Equal(new ProcessorPlan(scheme, 2, 2, 100, 100, null, null), WindowsPower.Unlimited(plan with { Freq = null, Epp = null }));
    }
}
