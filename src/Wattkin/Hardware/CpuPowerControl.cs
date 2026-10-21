namespace Wattkin.Hardware;

/// <summary>
/// What the power-limit registers hold now, for the editor: the MSR, the firmware's copy (as set by the
/// MSI mode in force), the BIOS's capture, and the firmware's copy as last seen under each MSI mode.
/// </summary>
public sealed record CpuPowerStatus(
    bool Available,
    string? Problem,
    RaplUnits Units,
    PackageLimits? Msr,
    PackageLimits? Firmware,
    PackageLimits? Bios,
    double? MaxWatts,
    IReadOnlyDictionary<string, PackageLimits>? FirmwareByMode = null)
{
    public static CpuPowerStatus Unavailable(string problem) => new(false, problem, RaplUnits.Typical, null, null, null, null);
}

/// <summary>
/// The package power-limit register as <see cref="Features.PowerLimitKeeper"/> and the self-test use it:
/// <see cref="CpuPowerControl"/> through PawnIO on the laptop, a fake in the unit tests.
/// </summary>
public interface IPowerLimitRegister
{
    RaplUnits Units { get; }

    /// <summary>MSR_PKG_POWER_LIMIT (0x610), or null when it can't be read.</summary>
    ulong? Read();

    /// <summary>Writes the register as given; false when the driver refused. Callers go through <see cref="PowerLimitRegister.WriteChecked"/>.</summary>
    bool Write(ulong value);

    /// <summary>The firmware's copy (MCHBAR + 0x59A0), or null when it can't be read.</summary>
    ulong? ReadFirmware();

    /// <summary>The most the package may be given (MSR_PKG_POWER_INFO), when the processor reports it.</summary>
    double? ReadMaxWatts();
}

public static class PowerLimitRegister
{
    /// <summary>
    /// Writes the whole register and reads it back; logged. Nothing is written when the register already
    /// holds these limits, and nothing when it can't be read or the BIOS locked it (bit 63).
    /// </summary>
    public static bool WriteChecked(this IPowerLimitRegister register, ulong value, string why)
    {
        if (register.Read() is not { } current)
        {
            Log.Warn("Power limits: the register couldn't be read; nothing written.");
            return false;
        }

        if (PackagePowerLimit.SameLimits(current, value))
        {
            return true;
        }

        if (PackagePowerLimit.IsLocked(current))
        {
            Log.Warn("Power limits: the BIOS locked the register; nothing written.");
            return false;
        }

        var written = register.Write(value);
        var ok = written && register.Read() is { } back && PackagePowerLimit.SameLimits(back, value);
        Log.Info($"Power limits ({why}): {PackagePowerLimit.Decode(current, register.Units).Describe()} -> {PackagePowerLimit.Decode(value, register.Units).Describe()} (0x{value:X16}) -> {(ok ? "ok" : written ? "FAILED (didn't read back)" : "FAILED (refused)")}");
        return ok;
    }
}

/// <summary>
/// The processor's package power limits through PawnIO (<see cref="PawnIo"/>): MSR_PKG_POWER_LIMIT
/// (0x610) read and written with the IntelMSR module, the firmware's MMIO copy (MCHBAR + 0x59A0) only
/// read, with IntelMCHBAR. The processor follows the lower of the two, so Wattkin can bring the limits
/// below what the firmware set, not above it.
/// </summary>
internal sealed class CpuPowerControl : IPowerLimitRegister, IDisposable
{
    public const uint MsrPowerUnit = 0x606;
    public const uint MsrPowerLimit = 0x610;
    public const uint MsrPowerInfo = 0x614;
    public const ulong MmioPowerLimit = 0x59A0;

    private readonly PawnIo _msr;
    private readonly PawnIo? _mchbar;

    private CpuPowerControl(PawnIo msr, PawnIo? mchbar, RaplUnits units)
    {
        _msr = msr;
        _mchbar = mchbar;
        Units = units;
    }

    public RaplUnits Units { get; }

    /// <summary>Null with a reason when PawnIO isn't installed, isn't reachable (no admin) or this isn't an Intel processor.</summary>
    public static CpuPowerControl? Open(out string? error)
    {
        if (PawnIo.Open("IntelMSR", out error) is not { } msr)
        {
            return null;
        }

        if (msr.Execute("ioctl_read_msr", [MsrPowerUnit], 1) is not [var unit])
        {
            msr.Dispose();
            error = Strings.Power_UnitsUnreadable;
            return null;
        }

        var mchbar = PawnIo.Open("IntelMCHBAR", out var mchbarError); // optional: only shows the firmware's copy
        if (mchbar is null)
        {
            Log.Info($"PawnIO: the firmware's copy of the power limits can't be read ({mchbarError}).");
        }

        var control = new CpuPowerControl(msr, mchbar, RaplUnits.From(unit));
        var now = control.Read() is { } raw ? PackagePowerLimit.Decode(raw, control.Units).Describe() : "unreadable";
        Log.Info($"PawnIO {PawnIo.InstalledVersion}: power limits {now}.");
        return control;
    }

    public ulong? Read() => _msr.Execute("ioctl_read_msr", [MsrPowerLimit], 1) is [var value] ? value : null;

    public bool Write(ulong value) => _msr.Execute("ioctl_write_msr", [MsrPowerLimit, value], 0) is not null;

    public ulong? ReadFirmware() => _mchbar?.Execute("ioctl_read_qword", [MmioPowerLimit], 1) is [var value] ? value : null;

    /// <summary>MSR_PKG_POWER_INFO bits 32–46: the most the package may be given, when the processor reports it.</summary>
    public double? ReadMaxWatts() =>
        _msr.Execute("ioctl_read_msr", [MsrPowerInfo], 1) is [var info] && ((info >> 32) & 0x7FFF) is var max and > 0 ? max * Units.WattsPerStep : null;

    public void Dispose()
    {
        _msr.Dispose();
        _mchbar?.Dispose();
    }
}
