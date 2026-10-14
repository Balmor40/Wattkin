using System.Runtime.InteropServices;
using Wattkin.Native;
using Wattkin.Settings;

namespace Wattkin.Platform;

/// <summary>
/// Windows power mode (Settings > Power) and processor power-plan knobs. Everything Wattkin changes is
/// remembered first, keyed by power plan, so "Restore" and uninstall can put the user's values back.
/// </summary>
public static class WindowsPower
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BatterySubgroup = new("e73a048d-bf27-4f12-9731-8b2076e8891f");
    private static readonly Guid CriticalBatteryLevel = new("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469");

    private const uint BoostDisabled = 0;
    private const uint BoostAggressive = 2;

    // Only these settings are ever written back from saved originals (the file is user-writable).
    private static readonly Dictionary<string, Guid> RestorableSettings = new()
    {
        ["boost"] = BoostMode,
        ["maxstate"] = MaxProcessorState,
    };

    private static readonly bool HasPerSourceModes =
        NativeLibrary.TryLoad("powrprof.dll", out var lib) && NativeLibrary.TryGetExport(lib, "PowerSetUserConfiguredACPowerMode", out _);

    /// <summary>
    /// Sets Windows' power mode for the current power source only (Windows 11 24H2 keeps separate
    /// plugged-in and on-battery modes; the profile for the other source sets that one when it applies).
    /// </summary>
    public static void SetMode(WindowsPowerMode mode, bool onAc, AppSettings settings)
    {
        if (mode == WindowsPowerMode.Unchanged)
        {
            return;
        }

        var guid = mode switch
        {
            WindowsPowerMode.BestEfficiency => BestEfficiency,
            WindowsPowerMode.BestPerformance => BestPerformance,
            _ => Balanced,
        };

        var key = $"mode:{(onAc ? "ac" : "dc")}";
        if (!settings.OriginalPowerModes.ContainsKey(key) && GetModeGuid(onAc) is { } original)
        {
            settings.OriginalPowerModes[key] = original;
        }

        uint status;
        if (HasPerSourceModes)
        {
            status = onAc ? PowrProf.PowerSetUserConfiguredACPowerMode(guid) : PowrProf.PowerSetUserConfiguredDCPowerMode(guid);
        }
        else
        {
            status = PowrProf.PowerSetActiveOverlayScheme(guid);
        }

        if (status != 0)
        {
            Log.Warn($"Setting the Windows power mode failed (0x{status:X}).");
        }
    }

    public static WindowsPowerMode GetMode(bool onAc) => GetModeGuid(onAc) switch
    {
        null => WindowsPowerMode.Unchanged,
        var g when g == BestEfficiency => WindowsPowerMode.BestEfficiency,
        var g when g == BestPerformance => WindowsPowerMode.BestPerformance,
        _ => WindowsPowerMode.Balanced,
    };

    private static Guid? GetModeGuid(bool onAc)
    {
        Guid guid;
        uint status;
        if (HasPerSourceModes)
        {
            status = onAc ? PowrProf.PowerGetUserConfiguredACPowerMode(out guid) : PowrProf.PowerGetUserConfiguredDCPowerMode(out guid);
        }
        else
        {
            status = PowrProf.PowerGetEffectiveOverlayScheme(out guid);
        }

        return status == 0 ? guid : null;
    }

    /// <summary>
    /// Turbo boost on/off in the active power plan (both AC and DC). Off caps the CPU at its base
    /// clock, the single biggest battery saver for light work.
    /// </summary>
    public static void SetCpuBoost(bool enabled, AppSettings settings)
    {
        if (!TryActiveScheme(out var scheme))
        {
            return;
        }

        CaptureOriginal(scheme, BoostMode, settings, "boost");
        var on = settings.OriginalPowerSettings.TryGetValue(Key(scheme, "boost", "ac"), out var original) && original != BoostDisabled ? original : BoostAggressive;
        WriteProcessor(scheme, BoostMode, enabled ? on : BoostDisabled, settings, "boost");
    }

    public static void SetMaxCpuPercent(int percent, AppSettings settings)
    {
        if (TryActiveScheme(out var scheme))
        {
            WriteProcessor(scheme, MaxProcessorState, (uint)Math.Clamp(percent, 5, 100), settings, "maxstate");
        }
    }

    public static bool? GetCpuBoost(bool onAc)
    {
        if (!TryActiveScheme(out var scheme))
        {
            return null;
        }

        var status = onAc
            ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, BoostMode, out var value)
            : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, BoostMode, out value);
        return status == 0 ? value != BoostDisabled : null;
    }

    /// <summary>Windows' "critical battery level" on battery, in percent.</summary>
    public static int CriticalBatteryPercent()
    {
        if (TryActiveScheme(out var scheme) && PowrProf.PowerReadDCValueIndex(IntPtr.Zero, scheme, BatterySubgroup, CriticalBatteryLevel, out var value) == 0 && value <= 50)
        {
            return (int)value;
        }

        return 5;
    }

    /// <summary>Puts every processor setting and power mode back to what it was before Wattkin changed it.</summary>
    public static void RestoreOriginals(AppSettings settings)
    {
        MigrateLegacyKeys(settings); // an uninstall can run before a newer version ever started
        var touched = new HashSet<Guid>();
        foreach (var (key, value) in settings.OriginalPowerSettings)
        {
            // key = "{scheme}:{setting}:{ac|dc}"
            var parts = key.Split(':');
            if (parts.Length != 3 || !Guid.TryParse(parts[0], out var scheme) || !RestorableSettings.TryGetValue(parts[1], out var setting) || value > 100)
            {
                continue;
            }

            var status = parts[2] == "ac"
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value);
            if (status == 0)
            {
                touched.Add(scheme);
            }
        }

        if (TryActiveScheme(out var active) && touched.Contains(active))
        {
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, active);
        }

        foreach (var (key, guid) in settings.OriginalPowerModes)
        {
            if (guid != BestEfficiency && guid != Balanced && guid != BestPerformance)
            {
                continue;
            }

            if (key == "mode:ac")
            {
                if (HasPerSourceModes)
                {
                    PowrProf.PowerSetUserConfiguredACPowerMode(guid);
                }
                else
                {
                    PowrProf.PowerSetActiveOverlayScheme(guid);
                }
            }
            else if (key == "mode:dc" && HasPerSourceModes)
            {
                PowrProf.PowerSetUserConfiguredDCPowerMode(guid);
            }
        }

        settings.OriginalPowerSettings.Clear();
        settings.OriginalPowerModes.Clear();
    }

    private static string Key(Guid scheme, string setting, string source) => $"{scheme}:{setting}:{source}";

    private static bool IsLegacyKey(string key) => key.Count(c => c == ':') == 1;

    public static bool HasLegacyKeys(AppSettings settings) => settings.OriginalPowerSettings.Keys.Any(IsLegacyKey);

    /// <summary>Earlier builds saved "boost:ac" without the plan; they were taken from the plan active then.</summary>
    public static void MigrateLegacyKeys(AppSettings settings)
    {
        var legacy = settings.OriginalPowerSettings.Where(kv => IsLegacyKey(kv.Key)).ToList();
        if (legacy.Count == 0 || !TryActiveScheme(out var scheme))
        {
            return;
        }

        foreach (var (key, value) in legacy)
        {
            settings.OriginalPowerSettings.Remove(key);
            settings.OriginalPowerSettings.TryAdd($"{scheme}:{key}", value);
        }
    }

    private static void WriteProcessor(Guid scheme, Guid setting, uint value, AppSettings settings, string key)
    {
        CaptureOriginal(scheme, setting, settings, key);
        if (Read(scheme, setting) == (value, value))
        {
            return; // already set; don't touch the power plan
        }

        PowrProf.PowerWriteACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value);
        PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, value);
        PowrProf.PowerSetActiveScheme(IntPtr.Zero, scheme);
    }

    /// <summary>Remembers the plan's own AC/DC values the first time Wattkin touches a setting in that plan.</summary>
    private static void CaptureOriginal(Guid scheme, Guid setting, AppSettings settings, string key)
    {
        if (!settings.OriginalPowerSettings.ContainsKey(Key(scheme, key, "ac")) && Read(scheme, setting) is { } values)
        {
            settings.OriginalPowerSettings[Key(scheme, key, "ac")] = values.Ac;
            settings.OriginalPowerSettings[Key(scheme, key, "dc")] = values.Dc;
        }
    }

    private static (uint Ac, uint Dc)? Read(Guid scheme, Guid setting) =>
        PowrProf.PowerReadACValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, out var ac) == 0
        && PowrProf.PowerReadDCValueIndex(IntPtr.Zero, scheme, ProcessorSubgroup, setting, out var dc) == 0
            ? (ac, dc)
            : null;

    private static bool TryActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowrProf.PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return false;
        }

        scheme = Marshal.PtrToStructure<Guid>(pointer);
        Marshal.FreeHGlobal(pointer); // LocalFree on Windows
        return true;
    }
}
