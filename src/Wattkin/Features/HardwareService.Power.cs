using System.Diagnostics;
using Wattkin.Hardware;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.Features;

/// <summary>
/// The processor's package power limits (a profile's "Manual" power limits), through PawnIO. The rules
/// (BIOS capture, re-checks, restore on exit, hands off while ThrottleStop runs) live in
/// <see cref="PowerLimitKeeper"/>; this connects it to the driver and to settings.json.
/// </summary>
public sealed partial class HardwareService
{
    /// <summary>Programs that write the same register; Wattkin leaves it alone while one runs (they'd fight over it).</summary>
    internal static readonly string[] PowerLimitTools = ["ThrottleStop"];

    private CpuPowerControl? _cpuPower;
    private PowerLimitKeeper? _powerLimits;
    private string? _cpuPowerError;

    /// <summary>When this Windows session started, as far as Windows' uptime knows (fast startup doesn't reset it).</summary>
    internal static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>A sentence for the user while ThrottleStop (or a similar tool) runs, else null.</summary>
    internal static string? PowerLimitToolRunning() =>
        PowerLimitTools.FirstOrDefault(n => Process.GetProcessesByName(n).Length > 0) is { } name
            ? string.Format(Strings.Power_ToolRunning, name)
            : null;

    internal static PowerLimitMemory PowerLimitMemoryOf(AppSettings s) =>
        new(s.BiosPowerLimitRaw, s.BiosPowerLimitBootUtc, s.LastWrittenPowerLimitRaw, s.FirmwarePowerLimits);

    /// <summary>A keeper for the uninstaller: reads the saved capture, saves nothing.</summary>
    internal static PowerLimitKeeper ReadOnlyPowerLimitKeeper(IPowerLimitRegister register, AppSettings settings) =>
        new(register, () => PowerLimitMemoryOf(settings), _ => { }, PowerLimitToolRunning, () => null, BootTimeUtc);

    private void InitializeCpuPower()
    {
        _cpuPower = CpuPowerControl.Open(out _cpuPowerError);
        if (_cpuPower is null)
        {
            Log.Info($"Manual power limits unavailable: {_cpuPowerError}");
            return;
        }

        // The MSI mode is read on the monitor thread, under the hardware lock, like every EC read.
        _powerLimits = new PowerLimitKeeper(_cpuPower, () => PowerLimitMemoryOf(_store.Current), SavePowerLimitMemory, PowerLimitToolRunning, () => _ec?.GetShiftMode()?.ToString(), BootTimeUtc);
    }

    private void SavePowerLimitMemory(PowerLimitMemory memory) => _store.Update(s =>
    {
        s.BiosPowerLimitRaw = memory.BiosRaw;
        s.BiosPowerLimitBootUtc = memory.BiosBootUtc;
        s.LastWrittenPowerLimitRaw = memory.LastWrittenRaw;
        s.FirmwarePowerLimits = memory.FirmwareByMode is { } byMode ? new Dictionary<string, ulong>(byMode) : null;
    });

    /// <summary>Under the hardware lock, after MSI's mode: the profile's manual limits, or the BIOS's when it has none.</summary>
    private void ApplyPowerLimits(Profile profile) => _powerLimits?.Apply(profile.PowerLimits, profile.Name);

    /// <summary>Monitor thread, under the lock: a profile's manual limits stay in place.</summary>
    private void ReassertPowerLimits() => _powerLimits?.Reassert();

    /// <summary>On exit and shutdown: the BIOS's limits back.</summary>
    private void RestoreBiosPowerLimit() => _powerLimits?.RestoreBios("the BIOS's, on exit");

    /// <summary>What the power-limit registers hold now, for the profile editor.</summary>
    public CpuPowerStatus ReadCpuPower() =>
        _powerLimits?.Status()
        ?? CpuPowerStatus.Unavailable(_cpuPowerError ?? (Elevation.IsElevated ? Strings.Power_NotConnected : Strings.Power_NeedsAdmin));

    /// <summary>Manual power limits can be used (PawnIO there, register not locked).</summary>
    public bool HasManualPowerLimits => _powerLimits is { Usable: true };
}
