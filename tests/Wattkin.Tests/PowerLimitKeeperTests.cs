using Wattkin.Features;
using Wattkin.Hardware;
using Xunit;

namespace Wattkin.Tests;

/// <summary>The rules that keep a profile's manual power limits in place, against a fake register and clock.</summary>
public class PowerLimitKeeperTests
{
    // A BIOS's limits: PL1 30 W for 28 s, PL2 64 W, both clamped (decoded in TheTestBiosIsWhatItSays).
    private const ulong Bios = 0x00438200_00DD80F0;
    private const ulong Lock = 1UL << 63;

    private static readonly RaplUnits Units = RaplUnits.Typical;
    private static readonly PowerLimitSetting Manual = new() { Pl1Watts = 15, Pl2Watts = 25, TurboSeconds = 8 };

    private readonly FakeRegister _register = new(Bios);
    private readonly DateTime _boot = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
    private readonly List<ulong?> _registerAtEachSave = [];
    private PowerLimitMemory _memory = new(null, null, null);
    private DateTime _now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private string? _otherTool;
    private string? _msiMode = nameof(ShiftMode.Balanced);

    private PowerLimitKeeper Keeper(DateTime? boot = null) => new(
        _register,
        () => _memory,
        m =>
        {
            _memory = m;
            _registerAtEachSave.Add(_register.Value);
        },
        () => _otherTool,
        () => _msiMode,
        boot ?? _boot,
        () => _now);

    // The firmware's copy under MSI's Balanced (as read by the self-test of 2026-10-21) and Extreme (its
    // 50/64 W from the same report, in the same layout).
    private const ulong FirmwareBalanced = 0x0042816800DD8140;
    private const ulong FirmwareExtreme = 0x0042820000DD8190;

    private static ulong ManualOn(ulong current) => PackagePowerLimit.Encode(current, Manual, Units);

    private static ulong With(int pl1, int pl2, double seconds) =>
        PackagePowerLimit.Encode(Bios, new PowerLimitSetting { Pl1Watts = pl1, Pl2Watts = pl2, TurboSeconds = seconds }, Units);

    private PackageLimits Now => PackagePowerLimit.Decode(_register.Value!.Value, Units);

    [Fact]
    public void TheTestBiosIsWhatItSays()
    {
        Assert.Equal(new PackageLimits(30, true, 28, 64, true, false), PackagePowerLimit.Decode(Bios, Units));
    }

    // ---- applying a profile ---------------------------------------------------------------------------

    [Fact]
    public void AManualProfileWritesItsLimitsAndLeavesTheRestOfTheRegister()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        Assert.Equal(new PackageLimits(15, true, 8, 25, true, false), Now);
        Assert.Equal(ManualOn(Bios), _register.Value);
        Assert.Single(_register.Writes);
        Assert.True(keeper.Enforcing!.SameAs(Manual));
    }

    [Fact]
    public void TheBiosLimitsAreCapturedBeforeTheFirstWrite()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        Assert.Equal(Bios, keeper.Bios);
        Assert.Equal(Bios, _memory.BiosRaw);
        Assert.Equal(_boot, _memory.BiosBootUtc);
        Assert.Equal(Bios, _registerAtEachSave[0]); // saved while the register still held them
        Assert.Equal(ManualOn(Bios), _memory.LastWrittenRaw);
    }

    [Fact]
    public void AProfileWithoutManualPutsTheBiosLimitsBack()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        keeper.Apply(null, "Balanced");
        Assert.Equal(Bios, _register.Value);
        Assert.Null(keeper.Enforcing);
        Assert.Equal(Bios, _memory.LastWrittenRaw);
    }

    [Fact]
    public void ProfilesWithoutManualWriteNothingWhileTheBiosLimitsAreInPlace()
    {
        var keeper = Keeper();
        keeper.Apply(null, "Balanced");
        keeper.Apply(null, "Battery saver");
        _now += TimeSpan.FromMinutes(5);
        keeper.Reassert();
        Assert.Empty(_register.Writes);
    }

    [Fact]
    public void ApplyingTheSameLimitsAgainWritesNothing()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        var saves = _registerAtEachSave.Count;
        keeper.Apply(Manual, "Cool");
        _now += TimeSpan.FromMinutes(1);
        keeper.Reassert();
        Assert.Single(_register.Writes);
        Assert.Equal(saves, _registerAtEachSave.Count); // settings.json isn't rewritten for nothing
    }

    [Fact]
    public void SwitchingBetweenManualProfilesWritesEachOnesLimits()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        keeper.Apply(new PowerLimitSetting { Pl1Watts = 35, Pl2Watts = 45, TurboSeconds = 28 }, "Fast");
        Assert.Equal(new PackageLimits(35, true, 28, 45, true, false), Now);
        Assert.Equal(35, keeper.Enforcing!.Pl1Watts);
    }

    [Fact]
    public void LimitsThatBreakTheRulesAreNeverWritten()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        keeper.Apply(new PowerLimitSetting { Pl1Watts = 30, Pl2Watts = 20, TurboSeconds = 8 }, "Broken"); // PL2 under PL1
        Assert.Equal(Bios, _register.Value); // the BIOS's instead
        Assert.Null(keeper.Enforcing);

        // More than the processor says it can take (MSR_PKG_POWER_INFO).
        _register.MaxWatts = 20;
        keeper.Apply(Manual, "Cool"); // PL2 25 W
        Assert.Equal(Bios, _register.Value);
        Assert.Null(keeper.Enforcing);
        Assert.Equal(2, _register.Writes.Count); // the first manual write, then the BIOS's back
    }

    // ---- keeping them in place ------------------------------------------------------------------------

    [Fact]
    public void SomethingElseChangingTheManualLimitsIsUndoneAtTheNextCheck()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");

        _register.Value = Bios; // the firmware, after MSI's mode switch
        keeper.Reassert();
        Assert.Equal(Bios, _register.Value); // not yet: the first check is 3 s after the change
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Equal(ManualOn(Bios), _register.Value);

        // Then every 30 s.
        _register.Value = Bios;
        _now += TimeSpan.FromSeconds(29);
        keeper.Reassert();
        Assert.Equal(Bios, _register.Value);
        _now += TimeSpan.FromSeconds(1);
        keeper.Reassert();
        Assert.Equal(ManualOn(Bios), _register.Value);
    }

    [Fact]
    public void TheBiosLimitsAreNeverEnforced()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        keeper.Apply(null, "Balanced");
        var other = With(40, 50, 16); // the firmware's own change, e.g. another MSI mode
        _register.Value = other;
        _now += TimeSpan.FromMinutes(5);
        keeper.Reassert();
        Assert.Equal(other, _register.Value);
    }

    [Fact]
    public void ALockedRegisterIsNeverWritten()
    {
        _register.Value = Bios | Lock;
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        Assert.Null(keeper.Enforcing);
        _now += TimeSpan.FromMinutes(1);
        keeper.Reassert();
        keeper.Apply(null, "Balanced");
        keeper.RestoreBios("exit");
        Assert.Empty(_register.Writes);
        Assert.False(keeper.Usable);

        var status = keeper.Status();
        Assert.False(status.Available);
        Assert.Contains("locked", status.Problem);
        Assert.True(status.Msr!.Value.Locked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWriteThatDoesntHoldIsNotRetried(bool driverRefuses)
    {
        _register.RefusesWrites = driverRefuses;
        _register.WritesDontHold = !driverRefuses;
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        Assert.Null(keeper.Enforcing);
        var attempts = _register.Writes.Count;
        _now += TimeSpan.FromMinutes(5);
        keeper.Reassert();
        Assert.Equal(attempts, _register.Writes.Count);
        Assert.Null(_memory.LastWrittenRaw); // never taken for Wattkin's own value
    }

    [Fact]
    public void AnUnreadableRegisterIsLeftAloneUntilItCanBeRead()
    {
        _register.Value = null;
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        keeper.Reassert();
        Assert.Empty(_register.Writes);
        Assert.Null(_memory.BiosRaw);
        Assert.False(keeper.Status().Available);

        // Readable again (the driver came back): captured first, then the profile's limits.
        _register.Value = Bios;
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Equal(Bios, _memory.BiosRaw);
        Assert.Equal(ManualOn(Bios), _register.Value);
    }

    [Fact]
    public void WhileThrottleStopRunsTheRegisterIsLeftAlone()
    {
        _otherTool = "ThrottleStop is running and sets the same limits: close it and take it out of Windows startup to use Manual.";
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        _now += TimeSpan.FromMinutes(1);
        keeper.Reassert();
        keeper.RestoreBios("exit");
        keeper.Apply(Manual, "Cool");
        Assert.Empty(_register.Writes);
        Assert.Null(_memory.BiosRaw); // ThrottleStop's values are never taken for the BIOS's

        var status = keeper.Status();
        Assert.False(status.Available);
        Assert.Equal(_otherTool, status.Problem);

        // Closed: the profile's limits go in at the next check.
        _otherTool = null;
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Equal(ManualOn(Bios), _register.Value);
        Assert.Equal(Bios, _memory.BiosRaw);
    }

    // ---- exit and restarts ----------------------------------------------------------------------------

    [Fact]
    public void ExitingPutsTheBiosLimitsBackAndStopsKeepingThem()
    {
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        keeper.RestoreBios("exit");
        Assert.Equal(Bios, _register.Value);
        Assert.Null(keeper.Enforcing);

        var other = With(40, 50, 16);
        _register.Value = other;
        _now += TimeSpan.FromMinutes(5);
        keeper.Reassert();
        Assert.Equal(other, _register.Value);
    }

    [Fact]
    public void ARestartOfWattkinKeepsTheFirstCapture()
    {
        Keeper().Apply(Manual, "Cool"); // then Wattkin stops without putting anything back (killed, crashed)

        var restarted = Keeper();
        restarted.Apply(null, "Balanced");
        Assert.Equal(Bios, restarted.Bios);
        Assert.Equal(Bios, _register.Value);
        Assert.Equal(Bios, _memory.BiosRaw);
    }

    [Fact]
    public void ARestartAfterANormalExitFindsTheSameBiosLimits()
    {
        var first = Keeper();
        first.Apply(Manual, "Cool");
        first.RestoreBios("exit");

        var restarted = Keeper();
        restarted.Apply(Manual, "Cool");
        Assert.Equal(Bios, restarted.Bios);
        restarted.RestoreBios("exit");
        Assert.Equal(Bios, _register.Value);
    }

    [Fact]
    public void ANewWindowsSessionCapturesAgain()
    {
        Keeper().Apply(Manual, "Cool");

        // Rebooted, and the BIOS now holds exactly these limits: the old capture belongs to another session.
        var next = Keeper(boot: _boot.AddHours(10));
        next.Apply(null, "Balanced");
        Assert.Equal(ManualOn(Bios), next.Bios);
        Assert.Equal(ManualOn(Bios), _register.Value);
        Assert.Equal(_boot.AddHours(10), _memory.BiosBootUtc);
    }

    [Fact]
    public void AfterAFastStartupTheFirmwaresLimitsAreCapturedAgain()
    {
        Keeper().Apply(Manual, "Cool");

        // Shut down with fast startup: Windows' uptime carries on, but the firmware reset the register at
        // power-on, with the owner's new BIOS setting.
        var raised = With(35, 64, 28);
        _register.Value = raised;
        var after = Keeper(); // same boot time as far as Windows knows
        after.Apply(Manual, "Cool");
        after.Apply(null, "Balanced");
        Assert.Equal(raised, after.Bios);
        Assert.Equal(raised, _register.Value);
        Assert.Equal(raised, _memory.BiosRaw);
    }

    [Fact]
    public void ASavedCaptureNeverBringsTheLockBitOrReservedBits()
    {
        Keeper().Apply(Manual, "Cool"); // the register holds Wattkin's write: the saved capture will be used
        _memory = _memory with { BiosRaw = Bios | Lock | 0xFF00_0000UL };

        var keeper = Keeper();
        keeper.Apply(null, "Balanced");
        Assert.Equal(Bios, _register.Value);
        Assert.False(PackagePowerLimit.IsLocked(_register.Value!.Value));
    }

    [Fact]
    public void ASavedCaptureThatMakesNoSenseIsNotPutBack()
    {
        Keeper().Apply(Manual, "Cool");
        _memory = _memory with { BiosRaw = With(1, 2, 8) }; // edited in settings.json: 1 W would cripple the laptop

        var keeper = Keeper();
        keeper.Apply(null, "Balanced");
        keeper.RestoreBios("exit");
        Assert.Null(keeper.Bios);
        Assert.Single(_register.Writes);
        Assert.Equal(ManualOn(Bios), _register.Value);
    }

    [Fact]
    public void TheSessionIsRecognisedDespiteASmallDrift()
    {
        Assert.True(PowerLimitKeeper.IsSameBoot(_boot.AddSeconds(30), _boot));
        Assert.True(PowerLimitKeeper.IsSameBoot(_boot.AddSeconds(-90), _boot));
        Assert.False(PowerLimitKeeper.IsSameBoot(_boot.AddMinutes(5), _boot));
        Assert.False(PowerLimitKeeper.IsSameBoot(null, _boot));
    }

    // ---- the firmware's ceiling under each MSI mode ---------------------------------------------------

    [Fact]
    public void TheFirmwaresCeilingIsNotedUnderTheMsiModeOnceItHasSettled()
    {
        Assert.Equal(new PackageLimits(40, true, 28, 45, true, false), PackagePowerLimit.Decode(FirmwareBalanced, Units));
        Assert.Equal(new PackageLimits(50, true, 28, 64, true, false), PackagePowerLimit.Decode(FirmwareExtreme, Units));

        _register.Firmware = FirmwareExtreme; // what the previous mode left: the firmware takes a moment
        var keeper = Keeper();
        keeper.Apply(null, "Balanced");
        keeper.Reassert();
        Assert.Null(_memory.FirmwareByMode);

        _register.Firmware = FirmwareBalanced;
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Equal(FirmwareBalanced, _memory.FirmwareByMode!["Balanced"]);

        _msiMode = nameof(ShiftMode.Turbo);
        _register.Firmware = FirmwareExtreme;
        keeper.Apply(Manual, "Fast");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Equal(new Dictionary<string, ulong> { ["Balanced"] = FirmwareBalanced, ["Turbo"] = FirmwareExtreme }, _memory.FirmwareByMode);

        // The editor gets them decoded: Balanced's is Manual's ceiling.
        Assert.Equal(new PackageLimits(40, true, 28, 45, true, false), keeper.Status().FirmwareByMode!["Balanced"]);
    }

    [Fact]
    public void TheCeilingIsNotedOncePerProfileChangeAndSavedOnlyWhenItChanged()
    {
        _register.Firmware = FirmwareBalanced;
        var keeper = Keeper();
        keeper.Apply(Manual, "Cool");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        var saves = _registerAtEachSave.Count;

        // Later checks keep the manual limits but don't note again.
        _register.Firmware = FirmwareExtreme;
        _now += TimeSpan.FromMinutes(2);
        keeper.Reassert();
        Assert.Equal(FirmwareBalanced, _memory.FirmwareByMode!["Balanced"]);

        // The same profile again, same copy: settings.json isn't rewritten.
        _register.Firmware = FirmwareBalanced;
        keeper.Apply(Manual, "Cool");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Equal(saves, _registerAtEachSave.Count);
    }

    [Fact]
    public void NothingIsNotedWithoutAModeACopyOrWhileThrottleStopRuns()
    {
        _register.Firmware = FirmwareBalanced;
        _msiMode = null; // the controller couldn't say
        var keeper = Keeper();
        keeper.Apply(null, "Balanced");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Null(_memory.FirmwareByMode);

        _msiMode = nameof(ShiftMode.Balanced);
        _register.Firmware = null; // IntelMCHBAR missing
        keeper.Apply(null, "Balanced");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Null(_memory.FirmwareByMode);

        _register.Firmware = FirmwareBalanced;
        _otherTool = "ThrottleStop is running."; // it may write the firmware's copy too
        keeper.Apply(null, "Balanced");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Null(_memory.FirmwareByMode);

        // Exiting cancels a pending note.
        _otherTool = null;
        keeper.Apply(null, "Balanced");
        keeper.RestoreBios("exit");
        _now += PowerLimitKeeper.CheckAfterChange;
        keeper.Reassert();
        Assert.Null(_memory.FirmwareByMode);
    }

    // ---- what the editor shows ------------------------------------------------------------------------

    [Fact]
    public void TheStatusShowsWhatIsInForce()
    {
        var firmware = With(35, 45, 28);
        _register.Firmware = firmware;
        _register.MaxWatts = 64;
        var keeper = Keeper();
        Assert.Equal(PackagePowerLimit.Decode(Bios, Units), keeper.Status().Bios); // reading captures, writes nothing
        Assert.Empty(_register.Writes);

        keeper.Apply(Manual, "Cool");
        var status = keeper.Status();
        Assert.True(status.Available);
        Assert.Null(status.Problem);
        Assert.Equal(new PackageLimits(15, true, 8, 25, true, false), status.Msr);
        Assert.Equal(PackagePowerLimit.Decode(Bios, Units), status.Bios);
        Assert.Equal(PackagePowerLimit.Decode(firmware, Units), status.Firmware);
        Assert.Equal(64.0, status.MaxWatts);
        Assert.True(keeper.Usable);

        _register.Firmware = null;
        Assert.Null(keeper.Status().Firmware); // IntelMCHBAR missing: the rest still works
    }

    // ---- the checked write ----------------------------------------------------------------------------

    [Fact]
    public void TheCheckedWriteReadsBackAndRefusesALockedOrUnreadableRegister()
    {
        var register = new FakeRegister(Bios);
        Assert.True(register.WriteChecked(ManualOn(Bios), "test"));
        Assert.Equal(ManualOn(Bios), register.Value);

        Assert.True(register.WriteChecked(ManualOn(Bios), "test")); // already there: nothing written
        Assert.Single(register.Writes);

        var locked = new FakeRegister(Bios | Lock);
        Assert.False(locked.WriteChecked(ManualOn(Bios), "test"));
        Assert.True(locked.WriteChecked(Bios, "test")); // holds those limits already
        Assert.Empty(locked.Writes);

        Assert.False(new FakeRegister(null).WriteChecked(Bios, "test"));

        var stubborn = new FakeRegister(Bios) { WritesDontHold = true };
        Assert.False(stubborn.WriteChecked(ManualOn(Bios), "test"));
        Assert.Single(stubborn.Writes);
    }

    /// <summary>MSR 0x610 and its neighbours, in memory. A locked register is never written by the keeper, so the fake doesn't model the lock.</summary>
    private sealed class FakeRegister(ulong? value) : IPowerLimitRegister
    {
        public ulong? Value { get; set; } = value;
        public ulong? Firmware { get; set; }
        public double? MaxWatts { get; set; }

        /// <summary>The driver takes the write but the register keeps its value.</summary>
        public bool WritesDontHold { get; set; }

        /// <summary>The driver refuses the write.</summary>
        public bool RefusesWrites { get; set; }

        public List<ulong> Writes { get; } = [];

        public RaplUnits Units => RaplUnits.Typical;

        public ulong? Read() => Value;

        public bool Write(ulong v)
        {
            if (RefusesWrites)
            {
                return false;
            }

            Writes.Add(v);
            if (!WritesDontHold)
            {
                Value = v;
            }

            return true;
        }

        public ulong? ReadFirmware() => Firmware;

        public double? ReadMaxWatts() => MaxWatts;
    }
}
