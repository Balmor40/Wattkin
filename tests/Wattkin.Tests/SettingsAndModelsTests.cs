using System.IO;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Settings;
using Xunit;

namespace Wattkin.Tests;

public sealed class SettingsAndModelsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wattkin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SharedFanProfilesFromOlderVersionsBecomeEachProfilesOwnCurve()
    {
        // The shape of a 1.2 settings file: shared fan profiles picked by name, Windows power modes.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            { "ProfilesVersion": 1,
              "Profiles": [
                { "Name": "Battery saver", "Performance": "Eco", "Fan": "Auto", "WindowsMode": "BestEfficiency", "CpuBoost": false },
                { "Name": "Mine", "Fan": "Custom", "FanProfileName": "fine" },
                { "Name": "Broken one", "Fan": "Custom", "FanProfileName": "Broken" },
                { "Name": "Hot", "Fan": "Custom", "FanProfileName": "Off" } ],
              "FanProfiles": [
                { "Name": "Broken", "Speeds": { "Cpu": [0, 500], "Gpu": [] } },
                { "Name": "Fine", "Speeds": { "Cpu": [0, 0, 40, 55, 75, 100], "Gpu": [0, 40, 60, 80, 100, 100] } } ],
              "FactoryFanCurve": { "Cpu": [1], "Gpu": [1] } }
            """);
        var store = new SettingsStore(_dir);
        var settings = store.Current;
        Assert.Equal(2, settings.ProfilesVersion);
        Assert.Null(settings.FanProfiles);
        Assert.All(settings.Profiles, p => Assert.Null(p.FanProfileName));
        Assert.Equal([0, 0, 40, 55, 75, 100], settings.FindProfile("Mine")!.CurveSpeeds!.Cpu); // names are case-insensitive
        Assert.False(settings.FindProfile("Broken one")!.HasOwnCurve);                        // a broken curve: the laptop's
        Assert.All(settings.FindProfile("Hot")!.CurveSpeeds!.Cpu, s => Assert.Equal(0, s));    // Off: all off
        Assert.Null(settings.FactoryFanCurve);

        // Saved again, the file no longer names shared fan profiles or Windows power modes.
        store.Update(_ => { });
        var saved = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("FanProfiles", saved);
        Assert.DoesNotContain("FanProfileName", saved);
        Assert.DoesNotContain("WindowsMode", saved);
    }

    [Fact]
    public void EachProfileUsesItsOwnCurveOrTheLaptops()
    {
        var factory = new FanSpeeds { Cpu = [0, 50, 55, 70, 80, 130], Gpu = [0, 60, 80, 130, 130, 130] };
        var settings = new AppSettings { FactoryFanCurve = factory };
        Assert.Same(factory, settings.ResolveFanCurve(new Profile { Fan = FanMode.Custom }).Speeds);
        Assert.False(settings.ResolveFanCurve(new Profile { Fan = FanMode.Custom }).Unrestricted); // the laptop's curve keeps the rules

        // Its own curve: all off is refused by the safety rules until the user accepts the experimental mode.
        var off = new Profile { Fan = FanMode.Custom, CurveSpeeds = FanProfile.OffSpeeds() };
        var (speeds, unrestricted) = settings.ResolveFanCurve(off);
        Assert.All(speeds!.Cpu.Concat(speeds.Gpu), s => Assert.Equal(0, s));
        Assert.NotNull(speeds.Problem(unrestricted));
        settings.UnrestrictedFanCurves = true;
        (speeds, unrestricted) = settings.ResolveFanCurve(off);
        Assert.Null(speeds!.Problem(unrestricted));
    }

    [Fact]
    public void ThereAreAlwaysFourProfilesWithDistinctNames()
    {
        var s = new AppSettings { Profiles = [new Profile { Name = "Mine" }, new Profile { Name = "mine " }] };
        s.Normalize();
        Assert.Equal(["Mine", "mine 2", "Quiet", "Performance"], s.Profiles.Select(p => p.Name)); // missing slots get their defaults

        // More than four (older versions could add profiles): the first four are kept.
        s.Profiles = [.. Enumerable.Range(1, 6).Select(i => new Profile { Name = $"P{i}" })];
        s.Normalize();
        Assert.Equal(["P1", "P2", "P3", "P4"], s.Profiles.Select(p => p.Name));
        Assert.Equal("P1", s.ActiveProfile); // the active one was gone
    }

    [Fact]
    public void RenamingOrResettingASlotCarriesTheRulesAlong()
    {
        var s = new AppSettings();
        s.Normalize();
        s.ActiveProfile = "Quiet";
        s.AcProfile = "Quiet";
        s.LowBatteryProfile = "Battery saver";

        var renamed = s.Profiles[2].Clone();
        renamed.Name = "Silence";
        Assert.Equal("Silence", s.ReplaceProfile(2, renamed));
        Assert.Equal("Silence", s.ActiveProfile);
        Assert.Equal("Silence", s.AcProfile);
        Assert.Equal("Battery saver", s.LowBatteryProfile);

        // A name another slot has gets a number.
        var clash = s.Profiles[2].Clone();
        clash.Name = "balanced";
        Assert.Equal("balanced 2", s.ReplaceProfile(2, clash));

        // Reset: the slot's own default, name included.
        Assert.Equal("Quiet", s.ReplaceProfile(2, Profile.Default(2)));
        Assert.Equal("Quiet", s.AcProfile);
        Assert.Equal(FanMode.Custom, s.Profiles[2].Fan);
        Assert.False(s.Profiles[2].HasOwnCurve);
    }

    [Fact]
    public void LeaveAsIsSurvivesARestart()
    {
        var store = new SettingsStore(_dir);
        store.Update(s =>
        {
            s.AcProfile = null;
            s.Profiles[3].Fan = null;
            s.Profiles[3].Performance = null; // power limits left to the BIOS or ThrottleStop
            s.Profiles[3].CpuBoost = null;
        });
        var reloaded = new SettingsStore(_dir).Current;
        Assert.Null(reloaded.AcProfile);
        Assert.Null(reloaded.Profiles[3].Fan);
        Assert.Null(reloaded.Profiles[3].Performance);
        Assert.Null(reloaded.Profiles[3].CpuBoost);
        Assert.Equal(ShiftMode.Eco, reloaded.Profiles[0].Performance);
        Assert.Equal("Balanced", reloaded.BatteryProfile);

        // A profile that leaves everything alone says so.
        Assert.Equal("leaves everything as it is", Wattkin.UI.Fmt.ProfileDetails(new Profile { Performance = null, Fan = null }));
    }

    [Fact]
    public void ABatterySwapIsSeenEvenWhenTheBatteryHasNoSerial()
    {
        // The Prestige 16 Studio's battery: no serial, so a replacement has the same "unique" ID.
        BatteryInfo Battery(uint fullMWh, string id = "MSIBIF0_9") => new(80256, fullMWh, 0, "LION", "MSI", "BIF0_9", id);

        Assert.False(BatterySwap.IsNewBattery(null, null, Battery(58216)));            // first run: just remember it
        Assert.False(BatterySwap.IsNewBattery("MSIBIF0_9", 58216, Battery(58216)));     // same battery
        Assert.False(BatterySwap.IsNewBattery("MSIBIF0_9", 58216, Battery(64900)));     // a calibration putting a few Wh back
        Assert.True(BatterySwap.IsNewBattery("MSIBIF0_9", 58216, Battery(79800)));      // a new battery, same ID
        Assert.True(BatterySwap.IsNewBattery("MSIBIF0_9", 58216, Battery(58000, "SMP|1234"))); // a different ID
    }

    [Fact]
    public void QuietMovesToTheFanCurveOnceUnlessTheUserChangedIt()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "Profiles": [ { "Name": "Quiet", "Performance": "Balanced", "Fan": "Silent" }, { "Name": "Balanced", "Fan": "Silent" } ] }""");
        var settings = new SettingsStore(_dir).Current;
        Assert.Equal(FanMode.Custom, settings.FindProfile("Quiet")!.Fan);
        Assert.Equal(FanMode.Silent, settings.FindProfile("Balanced")!.Fan); // only Quiet
        Assert.Equal(2, settings.ProfilesVersion);

        // Already moved once: the user's later choice stays.
        File.WriteAllText(path, """{ "ProfilesVersion": 1, "Profiles": [ { "Name": "Quiet", "Fan": "Silent" } ] }""");
        Assert.Equal(FanMode.Silent, new SettingsStore(_dir).Current.FindProfile("Quiet")!.Fan);

        // A Quiet the user set to something else is left alone.
        File.WriteAllText(path, """{ "Profiles": [ { "Name": "Quiet", "Fan": "Auto" } ] }""");
        Assert.Equal(FanMode.Auto, new SettingsStore(_dir).Current.FindProfile("Quiet")!.Fan);
    }

    [Fact]
    public void ARestartKeepsThePickedProfileUnlessTheChargerChanged()
    {
        var settings = new AppSettings { AcProfile = "Balanced", BatteryProfile = "Battery saver", ActiveProfile = "Quiet", ActiveProfileOnAc = true };
        Assert.Equal("Quiet", HardwareService.StartupProfile(settings, onAc: true));

        // Unplugged while Wattkin wasn't running: the battery profile, as if it happened live.
        Assert.Equal("Battery saver", HardwareService.StartupProfile(settings, onAc: false));

        // ...or the low-battery one when it's that low.
        settings.LowBatteryProfile = "Performance";
        Assert.Equal("Performance", HardwareService.StartupProfile(settings, onAc: false, percent: 20));

        // Settings from before this was remembered: the charger profile, as before.
        settings.ActiveProfileOnAc = null;
        Assert.Equal("Balanced", HardwareService.StartupProfile(settings, onAc: true));

        // A rule that leaves the profile alone keeps the picked one.
        settings.AcProfile = null;
        Assert.Equal("Quiet", HardwareService.StartupProfile(settings, onAc: true));

        // Without automatic switching, always the picked profile.
        settings.AutoSwitchProfiles = false;
        Assert.Equal("Quiet", HardwareService.StartupProfile(settings, onAc: false));
    }

    [Fact]
    public void TheAutomaticRulesPickByPowerSourceAndBatteryLevel()
    {
        var s = new AppSettings { AcProfile = "Performance", BatteryProfile = "Balanced", LowBatteryProfile = "Battery saver", LowBatteryThreshold = 30 };
        Assert.Equal("Performance", s.AutoProfile(onAc: true, percent: 10));
        Assert.Equal("Balanced", s.AutoProfile(onAc: false, percent: 31));
        Assert.Equal("Battery saver", s.AutoProfile(onAc: false, percent: 30));
        Assert.Equal("Balanced", s.AutoProfile(onAc: false, percent: null)); // level unknown: not "low"

        s.LowBatteryThreshold = 0; // no threshold: one battery rule
        Assert.Equal("Balanced", s.AutoProfile(onAc: false, percent: 5));

        s.BatteryProfile = null; // leave as is
        Assert.Null(s.AutoProfile(onAc: false, percent: 50));

        s.AutoSwitchProfiles = false;
        Assert.Null(s.AutoProfile(onAc: true, percent: 50));
    }

    [Fact]
    public void VerifiedFirmwareIsFoundAndMarkedVerified()
    {
        var model = EcModels.Find("1594EMS1.113");
        Assert.NotNull(model);
        Assert.True(model!.VerifiedOnWindows);
        Assert.Equal((byte)0xD7, model.ChargeAddress);
        Assert.Equal((byte)0xD2, model.ShiftAddress);
        Assert.Equal((byte)0xC2, model.ShiftEco);
        Assert.Equal((byte)0xC1, model.ShiftBalanced);
        Assert.Equal((byte)0xC4, model.ShiftTurbo);
    }

    [Fact]
    public void OnlyOneModelIsVerifiedAndUnknownFirmwareIsNotMatched()
    {
        Assert.Single(EcModels.All, m => m.VerifiedOnWindows);
        Assert.True(EcModels.All.Length >= 15);
        Assert.Null(EcModels.Find("NOPE0000.000"));
    }

    [Fact]
    public void NoFirmwareStringAppearsInTwoModels()
    {
        var all = EcModels.All.SelectMany(m => m.Firmware).Select(f => f.ToUpperInvariant()).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void NormalizeRepairsOutOfRangeValues()
    {
        var s = new AppSettings { ChargeLimit = 250, ChargeResumeBelow = -4, LowBatteryThreshold = 200, WidgetOpacity = double.NaN, KeyboardLevel = 9 };
        s.Profiles = [new Profile { Name = "x", MaxCpuPercent = 400, KeyboardLevel = -1, RefreshRate = 0 }];
        s.Normalize();
        Assert.Equal(100, s.ChargeLimit);
        Assert.Equal(0, s.ChargeResumeBelow);
        Assert.Equal(90, s.LowBatteryThreshold);
        Assert.Equal(0.92, s.WidgetOpacity);
        Assert.Equal(3, s.KeyboardLevel);
        Assert.Equal(100, s.Profiles[0].MaxCpuPercent);
        Assert.Equal(0, s.Profiles[0].KeyboardLevel);
        Assert.Null(s.Profiles[0].RefreshRate);
    }

    [Fact]
    public void IntegerEnumValuesInTheFileAreRejectedNotWrittenToHardware()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "Profiles": [ { "Name": "Evil", "Performance": 7 } ] }""");
        var store = new SettingsStore(_dir);
        Assert.DoesNotContain(store.Current.Profiles, p => p.Name == "Evil");
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.broken")));
    }

    [Fact]
    public void SaveKeepsABackupAndLoadFallsBackToIt()
    {
        var store = new SettingsStore(_dir);
        store.Update(s => s.ChargeLimit = 70);
        store.Update(s => s.ChargeLimit = 60);
        File.WriteAllText(store.FilePath, "{ not json");
        var reloaded = new SettingsStore(_dir);
        Assert.Equal(70, reloaded.Current.ChargeLimit);
    }

    [Fact]
    public void ReadOnlyStoreKeepsChangesInMemoryAndNeverTouchesTheFiles()
    {
        var owner = new SettingsStore(_dir);
        owner.Update(s => s.ChargeLimit = 70);
        File.WriteAllText(owner.FilePath + ".bak", "{ not json");
        var before = File.ReadAllText(owner.FilePath);

        var preview = new SettingsStore(_dir, readOnly: true);
        preview.Update(s => s.ChargeLimit = 55);
        Assert.Equal(55, preview.Current.ChargeLimit);
        Assert.Equal(before, File.ReadAllText(owner.FilePath));
        Assert.Equal(70, new SettingsStore(_dir).Current.ChargeLimit);
    }

    [Fact]
    public void ASecondLaunchShowsTheRunningCopyInsteadOfStarting()
    {
        var name = @"Local\WattkinTest-" + Guid.NewGuid().ToString("N");
        using var running = Wattkin.Platform.SingleInstance.Acquire(name, waitForPrevious: false);
        Assert.NotNull(running);
        using var shown = new ManualResetEventSlim();
        running!.Listen(onShow: shown.Set, onExit: () => { });

        Assert.Null(Wattkin.Platform.SingleInstance.Acquire(name, waitForPrevious: false));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void FullChargeOnceOnlyCompletesAfterTwoConfirmingPasses()
    {
        var store = new SettingsStore(_dir);
        store.Update(s =>
        {
            s.ChargeLimit = 80;
            s.FullChargeOnce = true;
        });
        var controller = new ChargeController(store);

        var first = controller.Decide(Full(98), simulatingNow: false);
        Assert.Equal(100, first!.Value.Limit);
        Assert.True(store.Current.FullChargeOnce);

        var second = controller.Decide(Full(98), simulatingNow: false);
        Assert.Equal(80, second!.Value.Limit);
        Assert.False(store.Current.FullChargeOnce);
    }

    [Fact]
    public void DrainStopsImmediatelyButClearsTheSettingOnlyWhenConfirmed()
    {
        var store = new SettingsStore(_dir);
        store.Update(s =>
        {
            s.ChargeLimit = 80;
            s.DrainToLimit = true;
        });
        var controller = new ChargeController(store);

        Assert.True(controller.Decide(Full(90), simulatingNow: true)!.Value.SimulateUnplug);
        Assert.False(controller.Decide(Full(80), simulatingNow: true)!.Value.SimulateUnplug);
        Assert.True(store.Current.DrainToLimit);
        Assert.False(controller.Decide(Full(80), simulatingNow: false)!.Value.SimulateUnplug);
        Assert.False(store.Current.DrainToLimit);
    }

    [Fact]
    public void TheOldWattsSwitchBecomesTheTrayChoice()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "TrayShowsWatts": false }""");
        Assert.Equal(TrayDisplay.Logo, new SettingsStore(_dir).Current.TrayDisplay);

        File.WriteAllText(path, """{ "TrayShowsWatts": true }""");
        var store = new SettingsStore(_dir);
        Assert.Equal(TrayDisplay.Watts, store.Current.TrayDisplay);

        // Saved again, only the new choice is in the file, and it survives a restart.
        store.Update(s => s.TrayDisplay = TrayDisplay.CpuTemperature);
        Assert.DoesNotContain("TrayShowsWatts", File.ReadAllText(path));
        Assert.Equal(TrayDisplay.CpuTemperature, new SettingsStore(_dir).Current.TrayDisplay);
    }

    private static Snapshot Full(double percent)
    {
        var power = new PowerState(PowerSource.AcCpuOnly, 5, 5, 5, 5, 0, OnAc: true, Charging: false, percent, percent * 0.6, 60, null, null, null, null);
        return new Snapshot(DateTime.UtcNow, power, new CpuStat(0, 0, 0, null, null, null, null), new MemoryStat(0, 0, 0, 0, 0), [], null, null, null, new DgpuStatus(DgpuState.None, null, []), [], []);
    }
}
