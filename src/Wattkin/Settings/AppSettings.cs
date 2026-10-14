using System.Text.Json;
using System.Text.Json.Serialization;
using Wattkin.Hardware;

namespace Wattkin.Settings;

public enum KeyboardTimeout
{
    AlwaysOn,
    AutoOff,
    AutoOffOnBattery,
}

public enum WindowsPowerMode
{
    Unchanged,
    BestEfficiency,
    Balanced,
    BestPerformance,
}

/// <summary>A bundle of independent knobs. Null means "leave this alone".</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Profile";
    public ShiftMode Performance { get; set; } = ShiftMode.Balanced;
    /// <summary>Null = leave the fan mode alone (e.g. keep a custom curve set by MSI Center).</summary>
    public FanMode? Fan { get; set; } = FanMode.Auto;
    public bool CoolerBoost { get; set; }
    public WindowsPowerMode WindowsMode { get; set; } = WindowsPowerMode.Balanced;
    public bool? CpuBoost { get; set; }
    public int? MaxCpuPercent { get; set; }
    public int? RefreshRate { get; set; }
    public int? KeyboardLevel { get; set; }

    public Profile Clone() => (Profile)MemberwiseClone();

    public static List<Profile> Defaults() =>
    [
        new()
        {
            Name = "Battery saver",
            Performance = ShiftMode.Eco,
            Fan = FanMode.Auto,
            WindowsMode = WindowsPowerMode.BestEfficiency,
            CpuBoost = false,
        },
        new()
        {
            Name = "Balanced",
            Performance = ShiftMode.Balanced,
            Fan = FanMode.Auto,
            WindowsMode = WindowsPowerMode.Balanced,
            CpuBoost = true,
        },
        new()
        {
            // The controller's own fan table: on the Prestige 16 Studio it keeps the fans off up to 60°C
            // (Auto ran them at ~3,000–3,600 rpm at 45°C, off at 42°C). Laptops without a usable
            // table get Silent instead (see HardwareService.ApplyFans).
            Name = "Quiet",
            Performance = ShiftMode.Balanced,
            Fan = FanMode.Custom,
            WindowsMode = WindowsPowerMode.Balanced,
            CpuBoost = true,
        },
        new()
        {
            Name = "Performance",
            Performance = ShiftMode.Turbo,
            Fan = FanMode.Auto,
            WindowsMode = WindowsPowerMode.BestPerformance,
            CpuBoost = true,
        },
    ];
}

public sealed class CalibrationRecord
{
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Phase { get; set; } = "Charging";
    public uint FullChargeBeforeMWh { get; set; }
    public uint? FullChargeAfterMWh { get; set; }
    public int LimitBefore { get; set; } = 80;
    public bool FastDischarge { get; set; }
    public string? Outcome { get; set; }
}

public sealed class AppSettings
{
    // Battery
    public int ChargeLimit { get; set; } = 80;
    /// <summary>Resume charging below this %. Null = the EC default (limit − 10).</summary>
    public int? ChargeResumeBelow { get; set; }
    public bool FullChargeOnce { get; set; }
    public bool DrainToLimit { get; set; }
    public bool ChargeBandHolding { get; set; }
    public CalibrationRecord? Calibration { get; set; }
    public List<CalibrationRecord> CalibrationHistory { get; set; } = [];
    public bool BatteryLogging { get; set; } = true;

    // Profiles
    public List<Profile> Profiles { get; set; } = Profile.Defaults();
    /// <summary>Which changes to the built-in profiles this file has had (see <see cref="Normalize"/>).</summary>
    public int ProfilesVersion { get; set; }
    public string ActiveProfile { get; set; } = "Balanced";
    /// <summary>Whether the charger was connected when <see cref="ActiveProfile"/> was applied (null: unknown).</summary>
    public bool? ActiveProfileOnAc { get; set; }
    public bool AutoSwitchProfiles { get; set; } = true;
    public string AcProfile { get; set; } = "Balanced";
    public string BatteryProfile { get; set; } = "Balanced";
    /// <summary>On battery, switch to <see cref="LowBatteryProfile"/> at or below this %. 0 = off.</summary>
    public int LowBatteryThreshold { get; set; } = 30;
    public string LowBatteryProfile { get; set; } = "Battery saver";

    // Keyboard, LEDs, hotkeys
    public int? KeyboardLevel { get; set; }
    public KeyboardTimeout KeyboardTimeout { get; set; } = KeyboardTimeout.AlwaysOn;
    public bool SyncMuteLeds { get; set; } = true;
    public bool HandleHotkeys { get; set; } = true;
    public bool ShowOsd { get; set; } = true;

    // Settings window: the simple view hides the fine-grained controls (they keep their values).
    public bool ExpertView { get; set; }

    // Updates from GitHub releases (signed); see Updater.
    public bool AutoUpdate { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }

    // Tray / widget
    public bool TrayShowsWatts { get; set; } = true;
    public bool WidgetEnabled { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public bool WidgetTopmost { get; set; }
    public bool WidgetCompact { get; set; }
    public double WidgetOpacity { get; set; } = 0.92;

    // Each fan's top speed, measured with Cooler Boost (once, automatically) or the fastest seen since:
    // the flyout's fan bar is drawn against it.
    public int? Fan1MaxRpm { get; set; }
    public int? Fan2MaxRpm { get; set; }
    public DateTime? FanTopSpeedMeasuredUtc { get; set; }
    public int FanTopSpeedAttempts { get; set; }

    // Learned power model
    public double? LearnedRestOfSystemWatts { get; set; }
    public int LearnedRestOfSystemSamples { get; set; }

    // Windows settings captured before Wattkin first changed them: "{plan}:{setting}:{ac|dc}" and "mode:{ac|dc}".
    public Dictionary<string, uint> OriginalPowerSettings { get; set; } = [];
    public Dictionary<string, Guid> OriginalPowerModes { get; set; } = [];

    /// <summary>
    /// False until the app has once read the laptop's own settings (needs admin). Until then nothing
    /// is pushed to the hardware, so a first non-admin launch can't impose defaults later.
    /// </summary>
    public bool HardwareAdopted { get; set; }

    /// <summary>
    /// Profiles is only ever replaced, never mutated in place, so readers on other threads can
    /// enumerate the list they got without locking.
    /// </summary>
    public Profile? FindProfile(string? name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Repairs anything a hand-edited or damaged file could contain: nulls, out-of-range numbers and
    /// enum values the hardware layer must never see. The elevated process reads this file.
    /// </summary>
    public void Normalize()
    {
        Profiles = Profiles is { Count: > 0 } ? [.. Profiles.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))] : Profile.Defaults();
        if (Profiles.Count == 0)
        {
            Profiles = Profile.Defaults();
        }

        foreach (var p in Profiles)
        {
            if (!Enum.IsDefined(p.Performance))
            {
                p.Performance = ShiftMode.Balanced;
            }

            if (p.Fan is { } f && !Enum.IsDefined(f))
            {
                p.Fan = FanMode.Auto;
            }

            if (!Enum.IsDefined(p.WindowsMode))
            {
                p.WindowsMode = WindowsPowerMode.Unchanged;
            }

            p.MaxCpuPercent = p.MaxCpuPercent is { } m ? Math.Clamp(m, 5, 100) : null;
            p.KeyboardLevel = p.KeyboardLevel is { } k ? Math.Clamp(k, 0, 3) : null;
            p.RefreshRate = p.RefreshRate is > 0 and < 1000 ? p.RefreshRate : null;
        }

        // 1: Quiet moved from Silent to the fan curve. Only a Quiet still on the old built-in fan
        // setting is moved; one the user changed is left alone.
        if (ProfilesVersion < 1)
        {
            if (FindProfile("Quiet") is { Fan: FanMode.Silent, CoolerBoost: false } quiet)
            {
                quiet.Fan = FanMode.Custom;
            }

            ProfilesVersion = 1;
        }

        ChargeLimit = Math.Clamp(ChargeLimit, 10, 100);
        ChargeResumeBelow = ChargeResumeBelow is { } r ? Math.Clamp(r, 0, 99) : null;
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 0, 90);
        KeyboardLevel = KeyboardLevel is { } kb ? Math.Clamp(kb, 0, 3) : null;
        if (!Enum.IsDefined(KeyboardTimeout))
        {
            KeyboardTimeout = KeyboardTimeout.AlwaysOn;
        }

        WidgetOpacity = double.IsFinite(WidgetOpacity) ? Math.Clamp(WidgetOpacity, 0.3, 1) : 0.92;
        CalibrationHistory ??= [];
        OriginalPowerSettings ??= [];
        OriginalPowerModes ??= [];
        ActiveProfile ??= Profiles[0].Name;
        AcProfile ??= ActiveProfile;
        BatteryProfile ??= ActiveProfile;
        LowBatteryProfile ??= ActiveProfile;
        if (LearnedRestOfSystemWatts is { } w && (!double.IsFinite(w) || w is < 0 or > 60))
        {
            LearnedRestOfSystemWatts = null;
            LearnedRestOfSystemSamples = 0;
        }
    }
}

/// <summary>
/// Loads and saves settings.json in %APPDATA%\Wattkin. Saves are flushed to disk and the previous
/// file is kept as settings.json.bak, so a power cut mid-save can't lose everything.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    /// <param name="readOnly">Changes stay in memory: for a copy that doesn't own the settings (screenshots, measurements).</param>
    public SettingsStore(string? directory = null, bool readOnly = false)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wattkin");
        FilePath = Path.Combine(Directory, "settings.json");
        ReadOnly = readOnly;
        Current = Load();
    }

    public string Directory { get; }
    public string FilePath { get; }
    public bool ReadOnly { get; }
    public AppSettings Current { get; private set; }

    public event Action? Changed;

    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            Save();
        }

        Changed?.Invoke();
    }

    public void Save()
    {
        if (ReadOnly)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var temp = FilePath + ".tmp";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Current, Options);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, FilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                Log.Error("Could not save settings", ex);
            }
        }
    }

    private AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) is { } settings)
                {
                    settings.Normalize();
                    if (path != FilePath)
                    {
                        Log.Warn("settings.json was unreadable; restored the previous copy.");
                    }

                    return settings;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Error($"Settings file {Path.GetFileName(path)} unreadable", ex);
                try
                {
                    if (!ReadOnly)
                    {
                        File.Copy(path, path + ".broken", overwrite: true);
                    }
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }
}
