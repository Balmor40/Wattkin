using System.IO;
using Wattkin.Hardware;
using Wattkin.Settings;
using Xunit;

namespace Wattkin.Tests;

public sealed class FanTableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wattkin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // The curves YAMDCC writes on the owner's Stealth 15M A11UEK (CurrentConfig.xml): "Romain High Perf"
    // for both fans, and "Romain".
    private static FanSteps HighPerfCpu() => new() { Speeds = [0, 0, 0, 30, 60, 82, 100], Up = [45, 57, 64, 71, 78, 85], Down = [39, 47, 50, 68, 75, 82] };
    private static FanSteps HighPerfGpu() => new() { Speeds = [0, 0, 30, 50, 65, 83, 100], Up = [50, 57, 64, 71, 78, 85], Down = [47, 54, 61, 68, 75, 82] };
    private static FanTable HighPerf() => new() { Cpu = HighPerfCpu(), Gpu = HighPerfGpu() };

    private static FanTable Romain() => new()
    {
        Cpu = new FanSteps { Speeds = [0, 0, 0, 30, 60, 75, 90], Up = [50, 57, 64, 71, 78, 85], Down = [47, 54, 61, 68, 75, 82] },
        Gpu = new FanSteps { Speeds = [0, 40, 48, 56, 64, 72, 80], Up = [50, 57, 64, 71, 78, 85], Down = [47, 54, 61, 68, 75, 82] },
    };

    [Fact]
    public void TheStealth15MGetsItsOwnMap()
    {
        var model = EcModels.Find("1563EMS1.115")!;
        Assert.Equal("Stealth 15M A11UEK (YAMDCC config)", model.Source);
        Assert.True(model.VerifiedOnWindows); // the self-test passed on the laptop
        Assert.Same(FanTableMap.MsiClassic, model.FanTable);
        Assert.Equal((byte)0xD7, model.ChargeAddress);
        Assert.Equal((byte)0xD2, model.ShiftAddress);
        Assert.Equal((byte)0xD4, model.FanModeAddress);
        Assert.Equal((byte)0xD3, model.KeyboardStateAddress); // what msi-ec's CONF_G2_10 lacks
        Assert.Equal((byte)0x98, model.CoolerBoostAddress);
        Assert.Equal(7, model.CoolerBoostBit);
        Assert.Equal((byte)0x68, model.CpuTempAddress);
        Assert.Equal((byte)0x80, model.GpuTempAddress);

        // The register checks accept what YAMDCC leaves there: limit 80%, High performance, Advanced fan mode.
        Assert.True(EmbeddedController.ChargeRegisterLooksRight(0xD0));
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(model, 0xC4));
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(model, 0xC0)); // YAMDCC's "Balanced"

        // Other firmwares of the family keep msi-ec's map, without whole tables.
        Assert.Equal("msi-ec CONF_G2_10", EcModels.Find("1563EMS1.111")!.Source);
        Assert.Null(EcModels.Find("1563EMS1.111")!.FanTable);
    }

    [Fact]
    public void TheRegistersAreYamdccs()
    {
        // CurrentConfig.xml, in decimal as YAMDCC writes them.
        var map = FanTableMap.MsiClassic;
        Assert.Equal([114, 115, 116, 117, 118, 119, 120], map.Cpu.Speeds.Select(b => (int)b));
        Assert.Equal([106, 107, 108, 109, 110, 111], map.Cpu.Up.Select(b => (int)b));
        Assert.Equal([122, 123, 124, 125, 126, 127], map.Cpu.Down.Select(b => (int)b));
        Assert.Equal([138, 139, 140, 141, 142, 143, 144], map.Gpu.Speeds.Select(b => (int)b));
        Assert.Equal([130, 131, 132, 133, 134, 135], map.Gpu.Up.Select(b => (int)b));
        Assert.Equal([146, 147, 148, 149, 150, 151], map.Gpu.Down.Select(b => (int)b));
        Assert.True(map.Cpu.DownIsOffset);
        Assert.Equal(19, map.Cpu.Addresses.Distinct().Count());
    }

    [Fact]
    public void DropBackTemperaturesAreWrittenAsOffsetsInYamdccsOrder()
    {
        var writes = FanTableMap.MsiClassic.Cpu.Encode(HighPerfCpu()).ToList();

        // The order of YAMDCC's service log: speed, then that step's up and down.
        Assert.Equal([0x72, 0x73, 0x6A, 0x7A, 0x74, 0x6B, 0x7B, 0x75, 0x6C, 0x7C, 0x76, 0x6D, 0x7D, 0x77, 0x6E, 0x7E, 0x78, 0x6F, 0x7F], writes.Select(w => (int)w.Address));
        Assert.Equal(((byte)0x6A, (byte)45), writes[2]);
        Assert.Equal(((byte)0x7A, (byte)6), writes[3]);  // back under 39°C: 45 − 39
        Assert.Equal(((byte)0x7C, (byte)14), writes[9]); // back under 50°C: 64 − 50

        var registers = writes.ToDictionary(w => w.Address, w => w.Value);
        var back = FanTableMap.MsiClassic.Cpu.Decode(a => registers.TryGetValue(a, out var v) ? v : null);
        Assert.NotNull(back);
        Assert.True(back.SameAs(HighPerfCpu()));

        // One unreadable register: no table.
        Assert.Null(FanTableMap.MsiClassic.Cpu.Decode(a => a == 0x7F ? null : (byte)1));
    }

    [Fact]
    public void TheOwnersCurvesPassTheSafetyRules()
    {
        Assert.Null(HighPerf().Problem(unrestricted: false));
        Assert.Null(Romain().Problem(unrestricted: false));
        Assert.Equal("off below 45°C · 30% from 64°C (back under 50°C) · 60% from 71°C (back under 68°C) · 82% from 78°C (back under 75°C) · 100% from 85°C (back under 82°C)", HighPerfCpu().Describe());
    }

    [Fact]
    public void TheHardRulesHoldEvenInExperimentalMode()
    {
        static FanTable With(Action<FanSteps> change)
        {
            var table = HighPerf();
            change(table.Cpu);
            return table;
        }

        Assert.NotNull(With(s => s.Up = [45, 57, 57, 71, 78, 85]).Problem(unrestricted: true));      // a step not hotter than the one before
        Assert.NotNull(With(s => s.Down = [45, 47, 50, 68, 75, 82]).Problem(unrestricted: true));    // drops back at its own start
        Assert.NotNull(With(s => s.Down = [39, 47, 50, 68, 75, 90]).Problem(unrestricted: true));    // drops back above its start
        Assert.NotNull(With(s => s.Down = [10, 47, 50, 68, 75, 82]).Problem(unrestricted: true));    // 35°C of hysteresis: a typo
        Assert.NotNull(With(s => s.Up = [15, 57, 64, 71, 78, 85]).Problem(unrestricted: true));      // below 20°C
        Assert.NotNull(With(s => s.Up = [45, 57, 64, 71, 78, 115]).Problem(unrestricted: true));     // above 110°C
        Assert.NotNull(With(s => s.Speeds = [0, 0, 0, 30, 60, 82, 160]).Problem(unrestricted: true)); // above 150%
        Assert.NotNull(With(s => s.Speeds = [0, 30, 60]).Problem(unrestricted: true));                // incomplete
        Assert.NotNull(new FanTable().Problem(unrestricted: true));
    }

    [Fact]
    public void TheSafetyRulesKeepTheFansAheadOfTheHeat()
    {
        var falling = HighPerf();
        falling.Cpu.Speeds = [0, 0, 40, 30, 60, 82, 100];
        Assert.NotNull(falling.Problem(unrestricted: false));
        Assert.Null(falling.Problem(unrestricted: true));

        var slowTop = HighPerf();
        slowTop.Gpu.Speeds = [0, 0, 30, 50, 65, 70, 70];
        Assert.NotNull(slowTop.Problem(unrestricted: false));
        Assert.Null(slowTop.Problem(unrestricted: true));

        var lateTop = HighPerf();
        lateTop.Cpu.Up = [45, 57, 64, 71, 78, 95];
        lateTop.Cpu.Down = [39, 47, 50, 68, 75, 92];
        Assert.NotNull(lateTop.Problem(unrestricted: false));
        Assert.Null(lateTop.Problem(unrestricted: true));

        var lateStart = HighPerf();
        lateStart.Gpu.Speeds = [0, 0, 0, 0, 0, 0, 100];
        Assert.NotNull(lateStart.Problem(unrestricted: false));
        Assert.Null(lateStart.Problem(unrestricted: true));

        // Off: everything off but the hottest step, which keeps the laptop's speed. Experimental only.
        var off = FanTable.Off(HighPerf());
        Assert.Equal([0, 0, 0, 0, 0, 0, 100], off.Cpu.Speeds);
        Assert.Equal(HighPerfCpu().Up, off.Cpu.Up);
        Assert.NotNull(off.Problem(unrestricted: false));
        Assert.Null(off.Problem(unrestricted: true));
    }

    [Fact]
    public void SpeedsOnlyProfilesRunAtTheTablesTemperatures()
    {
        var speeds = HighPerf().ToSpeeds();
        Assert.Equal([0, 0, 0, 30, 60, 82], speeds.Cpu);
        Assert.Equal([0, 0, 30, 50, 65, 83], speeds.Gpu);

        var table = HighPerf().WithSpeeds(new FanSpeeds { Cpu = [10, 20, 30, 40, 50, 60], Gpu = [15, 25, 35, 45, 55, 65] });
        Assert.Equal([10, 20, 30, 40, 50, 60, 100], table.Cpu.Speeds); // the hottest step stays the table's
        Assert.Equal(HighPerfCpu().Down, table.Cpu.Down);
        Assert.Equal([0, 0, 0, 30, 60, 82, 100], HighPerfCpu().Speeds); // a copy: the source is untouched
    }

    [Fact]
    public void AProfilesFanChoiceStandsForAWholeTable()
    {
        var settings = new AppSettings { FactoryFanTable = HighPerf() };
        settings.FanProfiles = [new FanProfile { Name = "Mine", Speeds = Romain().ToSpeeds(), Table = Romain() }, new FanProfile { Name = "Old", Speeds = FanSpeeds.Quiet() }];
        var profile = new Profile { Fan = FanMode.Custom };

        Assert.True(settings.ResolveFanTable(profile)!.SameAs(HighPerf()));
        profile.FanProfileName = FanProfile.LaptopDefault;
        Assert.True(settings.ResolveFanTable(profile)!.SameAs(HighPerf()));
        profile.FanProfileName = FanProfile.Off;
        Assert.True(settings.ResolveFanTable(profile)!.SameAs(FanTable.Off(HighPerf())));
        profile.FanProfileName = "Mine";
        Assert.True(settings.ResolveFanTable(profile)!.SameAs(Romain()));
        profile.FanProfileName = "Old"; // made before whole tables: its speeds at the laptop's temperatures
        Assert.True(settings.ResolveFanTable(profile)!.SameAs(HighPerf().WithSpeeds(FanSpeeds.Quiet())));
        profile.FanProfileName = "Gone";
        Assert.True(settings.ResolveFanTable(profile)!.SameAs(HighPerf()));

        // No table of the laptop's yet: the speeds-only path decides.
        Assert.Null(new AppSettings().ResolveFanTable(new Profile { Fan = FanMode.Custom }));
    }

    [Fact]
    public void TablesSurviveTheSettingsFileAndBrokenOnesAreDropped()
    {
        Directory.CreateDirectory(_dir);
        var broken = HighPerf();
        broken.Cpu.Up = [85, 78, 71, 64, 57, 45];
        new SettingsStore(_dir).Update(s =>
        {
            s.FactoryFanTable = Romain();
            s.FanProfiles = [new FanProfile { Name = "Mine", Speeds = HighPerf().ToSpeeds(), Table = HighPerf() }, new FanProfile { Name = "Broken", Speeds = HighPerf().ToSpeeds(), Table = broken }];
        });

        var loaded = new SettingsStore(_dir).Current;
        Assert.True(loaded.FactoryFanTable!.SameAs(Romain()));
        Assert.True(loaded.FindFanProfile("Mine")!.Table!.SameAs(HighPerf()));
        Assert.Null(loaded.FindFanProfile("Broken")!.Table); // dropped, its speeds kept
        Assert.Equal(HighPerf().ToSpeeds().Cpu, loaded.FindFanProfile("Broken")!.Speeds.Cpu);
    }

    [Fact]
    public void YamdccsConfigIsReadForTheSelfTest()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "CurrentConfig.xml");
        File.WriteAllText(file, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <YAMDCC_Config Ver="1">
              <Model>Stealth 15M A11UEK</Model>
              <FirmVer>1563EMS1.115</FirmVer>
              <OffsetDT>true</OffsetDT>
              <FanConfs>
                <FanConf><Name>CPU Fan</Name><CurveSel>1</CurveSel><FanCurveConfs>{Curve("Default", Romain().Cpu)}{Curve("Romain High Perf", HighPerfCpu())}</FanCurveConfs></FanConf>
                <FanConf><Name>GPU Fan</Name><CurveSel>1</CurveSel><FanCurveConfs>{Curve("Default", Romain().Gpu)}{Curve("Romain High Perf", HighPerfGpu())}</FanCurveConfs></FanConf>
              </FanConfs>
            </YAMDCC_Config>
            """);

        var table = SelfTest.YamdccTable(out var names, file);
        Assert.NotNull(table);
        Assert.True(table.SameAs(HighPerf()));
        Assert.Equal("CPU \"Romain High Perf\", GPU \"Romain High Perf\"", names);
        Assert.Null(SelfTest.YamdccTable(out _, Path.Combine(_dir, "missing.xml")));
    }

    private static string Curve(string name, FanSteps steps) =>
        $"<FanCurveConf><Name>{name}</Name><TempThresholds>"
        + string.Concat(Enumerable.Range(0, FanSteps.Count).Select(i =>
            $"<TempThreshold><UpThreshold>{(i == 0 ? 0 : steps.Up[i - 1])}</UpThreshold><DownThreshold>{(i == 0 ? 0 : steps.Down[i - 1])}</DownThreshold><FanSpeed>{steps.Speeds[i]}</FanSpeed></TempThreshold>"))
        + "</TempThresholds></FanCurveConf>";
}
