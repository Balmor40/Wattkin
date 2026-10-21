using Wattkin.Hardware;

namespace Wattkin.Features;

/// <summary>
/// What survives a restart of Wattkin (settings.json): the BIOS's power limits as captured in a Windows
/// session, and what Wattkin last wrote to the register. The file is the user's to edit, so neither is
/// trusted as it is (<see cref="PackagePowerLimit.IsPlausible"/>, <see cref="PackagePowerLimit.Restore"/>).
/// Also the firmware's own copy as last seen under each MSI mode, which is only ever shown.
/// </summary>
public sealed record PowerLimitMemory(ulong? BiosRaw, DateTime? BiosBootUtc, ulong? LastWrittenRaw, IReadOnlyDictionary<string, ulong>? FirmwareByMode = null);

/// <summary>
/// Keeps the processor's package power limits as the active profile wants them: its "Manual" PL1, PL2
/// and turbo time, or the BIOS's when it has none. The BIOS's are captured once, before Wattkin's first
/// write. Something else (another tool, the firmware after sleep) may rewrite the register, so manual
/// limits are checked soon after a change and every 30 s, and put back. Exiting, shutting
/// down and uninstalling put the BIOS's back. While ThrottleStop (or a similar tool) runs, the register
/// is left alone: two programs writing it would fight.
///
/// The capture has to survive a restart of Wattkin (after which the register holds Wattkin's own write,
/// not the BIOS's) without outliving the Windows session: with fast startup, Windows' uptime carries on
/// across a shutdown while the firmware resets the register at power-on, and the owner may have changed
/// the BIOS's limits in between. So the earlier capture is kept only when it's from this session and the
/// register still holds what Wattkin last wrote; otherwise the register is captured again.
///
/// MSI's modes don't touch the register but set the firmware's own copy (MCHBAR), which the processor
/// also keeps to (self-test of 2026-10-21: Balanced and Sport 40/45 W, Extreme 50/64 W). So the first
/// check after a profile change, once the firmware has settled, notes that copy under the MSI mode in
/// force: the editor shows it as Manual's ceiling.
///
/// Pure logic over <see cref="IPowerLimitRegister"/>, unit tested with a fake register; the real one is
/// checked by --selftest write. Thread-safe.
/// </summary>
public sealed class PowerLimitKeeper
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    /// <summary>The first check after a profile change: by then MSI's mode switch has set the firmware's copy (about 2 s).</summary>
    public static readonly TimeSpan CheckAfterChange = TimeSpan.FromSeconds(3);

    private const int MaxFixesLogged = 20;

    private readonly IPowerLimitRegister _register;
    private readonly Func<PowerLimitMemory> _load;
    private readonly Action<PowerLimitMemory> _save;
    private readonly Func<string?> _otherTool;
    private readonly Func<string?> _msiMode;
    private readonly DateTime _bootUtc;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();

    private bool _captured;
    private PowerLimitSetting? _wanted;
    private string _wantedBy = "";
    private DateTime _nextCheckUtc = DateTime.MaxValue;
    private bool _noteFirmware;
    private int _fixesLogged;
    private string? _toolLogged;

    /// <param name="otherTool">A sentence when another program that writes these limits is running, else null.</param>
    /// <param name="msiMode">The MSI mode in force (a <see cref="ShiftMode"/> name), or null when unknown.</param>
    /// <param name="bootUtc">When this Windows session started.</param>
    public PowerLimitKeeper(IPowerLimitRegister register, Func<PowerLimitMemory> load, Action<PowerLimitMemory> save, Func<string?> otherTool, Func<string?> msiMode, DateTime bootUtc, Func<DateTime>? utcNow = null)
    {
        _register = register;
        _load = load;
        _save = save;
        _otherTool = otherTool;
        _msiMode = msiMode;
        _bootUtc = bootUtc;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The BIOS's limits for this session once captured; null before, or when the saved capture made no sense.</summary>
    public ulong? Bios { get; private set; }

    /// <summary>The register could be read and the BIOS didn't lock it, the last time it was looked at.</summary>
    public bool Usable { get; private set; }

    /// <summary>The manual limits being kept in place, or null when the BIOS's apply.</summary>
    public PowerLimitSetting? Enforcing
    {
        get
        {
            lock (_sync)
            {
                return _wanted?.Clone();
            }
        }
    }

    /// <summary>Two times are the same Windows session (Wattkin's estimate of the boot time drifts by milliseconds).</summary>
    public static bool IsSameBoot(DateTime? saved, DateTime bootUtc) => saved is { } t && Math.Abs((t - bootUtc).TotalMinutes) < 2;

    /// <summary>
    /// A profile's limits: its manual ones, or the BIOS's when it has none. Manual limits that break the
    /// rules (<see cref="PowerLimitSetting.Problem"/>, with the processor's own maximum) aren't used:
    /// the BIOS's apply instead. While another tool runs, they're kept in mind and written once it's gone.
    /// </summary>
    public void Apply(PowerLimitSetting? limits, string profile)
    {
        lock (_sync)
        {
            if (limits?.Problem(_register.ReadMaxWatts()) is { } problem)
            {
                Log.Warn($"Not using {profile}'s power limits: {problem} The BIOS's apply instead.");
                limits = null;
            }

            _wanted = limits?.Clone();
            _wantedBy = profile;
            _fixesLogged = 0;
            _noteFirmware = true;
            _nextCheckUtc = _utcNow() + CheckAfterChange;
            if (OtherToolRunning() || !EnsureCaptured())
            {
                return;
            }

            if (_wanted is null)
            {
                PutBiosBack($"the BIOS's, for {profile}");
            }
            else if (!Enforce(profile))
            {
                Log.Warn($"{profile}'s power limits didn't hold: the register is left as it is until the next profile change.");
                _wanted = null;
            }
        }
    }

    /// <summary>
    /// Monitor thread: manual limits back in the register if something else changed them, soon after a
    /// profile change, then every 30 s. The BIOS's are never enforced: the firmware may change its own.
    /// The first check after a profile change also notes the firmware's copy under the MSI mode in force.
    /// </summary>
    public void Reassert()
    {
        lock (_sync)
        {
            var now = _utcNow();
            if (now < _nextCheckUtc)
            {
                return;
            }

            _nextCheckUtc = _wanted is null ? DateTime.MaxValue : now + CheckEvery;
            if (OtherToolRunning() || !EnsureCaptured())
            {
                return;
            }

            if (_noteFirmware)
            {
                _noteFirmware = false;
                NoteFirmwareCopy();
            }

            if (_wanted is null || _register.Read() is not { } current)
            {
                return;
            }

            if (PackagePowerLimit.SameLimits(current, PackagePowerLimit.Encode(current, _wanted, _register.Units)))
            {
                return;
            }

            if (_fixesLogged++ < MaxFixesLogged)
            {
                Log.Warn($"Power limits aren't {_wantedBy}'s ({PackagePowerLimit.Decode(current, _register.Units).Describe()}): writing them again.");
            }

            if (!Enforce("put back"))
            {
                Log.Warn($"{_wantedBy}'s power limits couldn't be put back: left as they are until the next profile change.");
                _wanted = null;
            }
        }
    }

    /// <summary>Exiting, shutting down or uninstalling: the BIOS's limits back, and nothing kept in place any more.</summary>
    public void RestoreBios(string why)
    {
        lock (_sync)
        {
            _wanted = null;
            _noteFirmware = false;
            _nextCheckUtc = DateTime.MaxValue;
            if (!OtherToolRunning() && EnsureCaptured())
            {
                PutBiosBack(why);
            }
        }
    }

    /// <summary>What the registers hold now, for the profile editor. Captures the BIOS's first if that hasn't happened yet.</summary>
    public CpuPowerStatus Status()
    {
        lock (_sync)
        {
            if (OtherToolRunning())
            {
                return CpuPowerStatus.Unavailable(_toolLogged!);
            }

            EnsureCaptured();
            var units = _register.Units;
            var msr = _register.Read();
            var locked = msr is { } m && PackagePowerLimit.IsLocked(m);
            Usable = msr is not null && !locked;
            return new CpuPowerStatus(
                Usable,
                locked ? Strings.Power_Locked : msr is null ? Strings.Power_Unreadable : null,
                units,
                msr is { } raw ? PackagePowerLimit.Decode(raw, units) : null,
                _register.ReadFirmware() is { } fw ? PackagePowerLimit.Decode(fw, units) : null,
                Bios is { } bios ? PackagePowerLimit.Decode(bios, units) : null,
                _register.ReadMaxWatts(),
                _load().FirmwareByMode?.ToDictionary(kv => kv.Key, kv => PackagePowerLimit.Decode(kv.Value, units)));
        }
    }

    // ---- under _sync --------------------------------------------------------------------------------

    /// <summary>False when the register can't be read (then nothing is captured, and nothing written).</summary>
    private bool EnsureCaptured()
    {
        if (_captured)
        {
            return true;
        }

        if (_register.Read() is not { } current)
        {
            return false;
        }

        Usable = !PackagePowerLimit.IsLocked(current);
        var memory = _load();
        if (IsSameBoot(memory.BiosBootUtc, _bootUtc) && memory.LastWrittenRaw is { } last && PackagePowerLimit.SameLimits(current, last))
        {
            // Wattkin's own write is still in place (it restarted within this session): the register isn't
            // the BIOS's, the earlier capture is.
            if (memory.BiosRaw is { } saved && PackagePowerLimit.IsPlausible(saved, _register.Units))
            {
                Bios = saved;
                Log.Info($"BIOS power limits for this session, captured earlier: {PackagePowerLimit.Decode(saved, _register.Units).Describe()}.");
            }
            else
            {
                Log.Warn("Power limits: the BIOS's limits saved in the settings make no sense, so they won't be put back.");
            }
        }
        else
        {
            // A new session, or the register no longer holds what Wattkin wrote: it holds the firmware's.
            Bios = current;
            _save(memory with { BiosRaw = current, BiosBootUtc = _bootUtc, LastWrittenRaw = null });
            Log.Info($"BIOS power limits for this session: {PackagePowerLimit.Decode(current, _register.Units).Describe()}.");
        }

        _captured = true;
        return true;
    }

    /// <summary>The manual limits written unless the register already holds them. False when they didn't hold.</summary>
    private bool Enforce(string why) =>
        _register.Read() is { } current && Write(PackagePowerLimit.Encode(current, _wanted!, _register.Units), why);

    private void PutBiosBack(string why)
    {
        if (Bios is { } bios && _register.Read() is { } current)
        {
            Write(PackagePowerLimit.Restore(current, bios), why);
        }
    }

    /// <summary>The firmware's copy noted under the MSI mode in force; settings.json is only rewritten when it changed.</summary>
    private void NoteFirmwareCopy()
    {
        if (_msiMode() is not { } mode || _register.ReadFirmware() is not { } copy)
        {
            return;
        }

        var memory = _load();
        if (memory.FirmwareByMode?.TryGetValue(mode, out var known) == true && known == copy)
        {
            return;
        }

        var byMode = new Dictionary<string, ulong>(memory.FirmwareByMode ?? new Dictionary<string, ulong>()) { [mode] = copy };
        _save(memory with { FirmwareByMode = byMode });
        Log.Info($"The firmware's own power limits under MSI's {mode} mode: {PackagePowerLimit.Decode(copy, _register.Units).Describe()}.");
    }

    /// <summary>A checked write, remembered so that a restart of Wattkin recognises its own value in the register.</summary>
    private bool Write(ulong value, string why)
    {
        if (!_register.WriteChecked(value, why))
        {
            return false;
        }

        var memory = _load();
        if (memory.LastWrittenRaw is not { } last || !PackagePowerLimit.SameLimits(last, value))
        {
            _save(memory with { LastWrittenRaw = value });
        }

        return true;
    }

    /// <summary>True while another program that writes these limits runs; logged once each time that changes.</summary>
    private bool OtherToolRunning()
    {
        var tool = _otherTool();
        if (tool != _toolLogged)
        {
            Log.Info(tool is null ? "Power limits: the other tool has stopped; Wattkin manages them again." : $"Power limits left alone: {tool}");
            _toolLogged = tool;
        }

        return tool is not null;
    }
}
