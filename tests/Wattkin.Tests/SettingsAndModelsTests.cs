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
    public void ADamagedFanProfileNeverReachesTheController()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            { "ProfilesVersion": 1,
              "Profiles": [ { "Name": "Mine", "Fan": "Custom", "FanProfileName": "Broken" } ],
              "FanProfiles": [
                { "Name": "Broken", "Speeds": { "Cpu": [0, 500], "Gpu": [] } },
                { "Name": "Off", "Speeds": { "Cpu": [0, 0, 0, 0, 0, 0], "Gpu": [0, 0, 0, 0, 0, 0] } },
                { "Name": "Fine", "Speeds": { "Cpu": [0, 0, 40, 55, 75, 100], "Gpu": [0, 40, 60, 80, 100, 100] } },
                { "Name": "fine", "Speeds": { "Cpu": [0, 0, 40, 55, 75, 100], "Gpu": [0, 40, 60, 80, 100, 100] } } ],
              "FactoryFanCurve": { "Cpu": [1], "Gpu": [1] } }
            """);
        var settings = new SettingsStore(_dir).Current;
        Assert.Equal(["Fine"], settings.FanProfiles.Select(f => f.Name)); // broken, built-in name and repeat dropped
        Assert.Null(settings.FindProfile("Mine")!.FanProfileName);          // back to the laptop's own curve
        Assert.Null(settings.FactoryFanCurve);
    }

    [Fact]
    public void EachFanChoiceStandsForTheRightSpeeds()
    {
        var factory = new FanSpeeds { Cpu = [0, 50, 55, 70, 80, 130], Gpu = [0, 60, 80, 130, 130, 130] };
        var settings = new AppSettings { FactoryFanCurve = factory, FanProfiles = FanProfile.Defaults() };
        FanSpeeds? Speeds(string? name) => settings.ResolveFanCurve(new Profile { Fan = FanMode.Custom, FanProfileName = name }).Speeds;

        Assert.Same(factory, Speeds(null));
        Assert.Same(factory, Speeds(FanProfile.LaptopDefault));
        Assert.Same(factory, Speeds("Deleted since"));
        Assert.Equal(FanSpeeds.Quiet().Cpu, Speeds("quiet")!.Cpu); // names are case-insensitive

        // Off: refused by the safety rules until the user accepts the experimental mode.
        var (off, unrestricted) = settings.ResolveFanCurve(new Profile { Fan = FanMode.Custom, FanProfileName = FanProfile.Off });
        Assert.All(off!.Cpu.Concat(off.Gpu), s => Assert.Equal(0, s));
        Assert.NotNull(off.Problem(unrestricted));
        settings.UnrestrictedFanCurves = true;
        (off, unrestricted) = settings.ResolveFanCurve(new Profile { Fan = FanMode.Custom, FanProfileName = FanProfile.Off });
        Assert.Null(off!.Problem(unrestricted));
    }

    [Fact]
    public void ProfilesFollowARenamedOrDeletedFanProfile()
    {
        var settings = new AppSettings { Profiles = [new Profile { Name = "A", Fan = FanMode.Custom, FanProfileName = "Quiet" }] };
        settings.RenameFanProfile("Quiet", "Whisper");
        Assert.Equal("Whisper", settings.FindProfile("A")!.FanProfileName);
        settings.RenameFanProfile("whisper", null);
        Assert.Null(settings.FindProfile("A")!.FanProfileName);
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
        Assert.Equal(1, settings.ProfilesVersion);

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

        // Settings from before this was remembered: the charger profile, as before.
        settings.ActiveProfileOnAc = null;
        Assert.Equal("Balanced", HardwareService.StartupProfile(settings, onAc: true));

        // Without automatic switching, always the picked profile.
        settings.AutoSwitchProfiles = false;
        Assert.Equal("Quiet", HardwareService.StartupProfile(settings, onAc: false));
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

    private static Snapshot Full(double percent)
    {
        var power = new PowerState(PowerSource.AcCpuOnly, 5, 5, 5, 5, 0, OnAc: true, Charging: false, percent, percent * 0.6, 60, null, null, null, null);
        return new Snapshot(DateTime.UtcNow, power, new CpuStat(0, 0, 0, null, null, null, null), new MemoryStat(0, 0, 0, 0, 0), [], null, null, null, new DgpuStatus(DgpuState.None, null, []), [], []);
    }
}
