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

/// <summary>
/// One of the four profile slots: a bundle of independent knobs, applied together. Null means "leave
/// this alone". The slots keep their place (see <see cref="AppSettings.ProfileSlots"/>); their names
/// and knobs are the user's, and each can be reset to <see cref="Default"/>.
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "Profile";

    /// <summary>The firmware's CPU power limits (MSI's shift mode).</summary>
    public ShiftMode Performance { get; set; } = ShiftMode.Balanced;

    /// <summary>Null = leave the fan mode alone (e.g. keep a custom curve set by MSI Center). Written even when null, so that survives a restart.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public FanMode? Fan { get; set; } = FanMode.Auto;

    /// <summary>
    /// With <see cref="FanMode.Custom"/>, on a laptop where Wattkin writes whole fan tables: this
    /// profile's own table, temperatures included. Null (with <see cref="CurveSpeeds"/> null too): the
    /// laptop's default curve.
    /// </summary>
    public FanTable? CurveTable { get; set; }

    /// <summary>
    /// With <see cref="FanMode.Custom"/>, where only speeds can be set: this profile's speeds at the
    /// laptop's temperature steps. Kept in step with <see cref="CurveTable"/>'s first six speeds.
    /// </summary>
    public FanSpeeds? CurveSpeeds { get; set; }

    /// <summary>Fans at full speed (MSI's Cooler Boost), whatever <see cref="Fan"/> says.</summary>
    public bool CoolerBoost { get; set; }

    public bool? CpuBoost { get; set; }
    public int? MaxCpuPercent { get; set; }
    public int? RefreshRate { get; set; }
    public int? KeyboardLevel { get; set; }

    /// <summary>Settings files before 1.3 named a shared fan profile here; <see cref="AppSettings.Normalize"/> turns it into this profile's own curve.</summary>
    public string? FanProfileName { get; set; }

    /// <summary>The profile has a curve of its own (else Custom means the laptop's default curve).</summary>
    [JsonIgnore]
    public bool HasOwnCurve => CurveTable is not null || CurveSpeeds is not null;

    public Profile Clone()
    {
        var copy = (Profile)MemberwiseClone();
        copy.CurveTable = CurveTable?.Clone();
        copy.CurveSpeeds = CurveSpeeds?.Clone();
        return copy;
    }

    /// <summary>What slot <paramref name="slot"/> (0–3) holds out of the box, and after a reset.</summary>
    public static Profile Default(int slot) => slot switch
    {
        0 => new() { Name = "Battery saver", Performance = ShiftMode.Eco, Fan = FanMode.Auto, CpuBoost = false },
        1 => new() { Name = "Balanced", Performance = ShiftMode.Balanced, Fan = FanMode.Auto, CpuBoost = true },

        // The controller's own fan table: on the Prestige 16 Studio it keeps the fans off up to 60°C
        // (Auto ran them at ~3,000–3,600 rpm at 45°C, off at 42°C). Laptops without a usable
        // table get Silent instead (see HardwareService.ApplyFans).
        2 => new() { Name = "Quiet", Performance = ShiftMode.Balanced, Fan = FanMode.Custom, CpuBoost = true },
        _ => new() { Name = "Performance", Performance = ShiftMode.Turbo, Fan = FanMode.Auto, CpuBoost = true },
    };

    public static List<Profile> Defaults() => [.. Enumerable.Range(0, AppSettings.ProfileSlots).Select(Default)];
}

/// <summary>
/// A named fan curve shared between profiles, as settings files before 1.3 kept them. Only read, by
/// <see cref="AppSettings.Normalize"/>, which copies each one into the profiles that used it.
/// </summary>
public sealed class FanProfile
{
    public const string LaptopDefault = "Laptop default";
    public const string Off = "Off";

    public string Name { get; set; } = "Fan profile";
    public FanSpeeds Speeds { get; set; } = new();
    public FanTable? Table { get; set; }

    public static FanSpeeds OffSpeeds() => new() { Cpu = new int[FanSpeeds.Steps], Gpu = new int[FanSpeeds.Steps] };
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

    // Profiles: always four slots (see Normalize).
    public const int ProfileSlots = 4;
    public List<Profile> Profiles { get; set; } = Profile.Defaults();
    /// <summary>Which changes to the built-in profiles this file has had (see <see cref="Normalize"/>).</summary>
    public int ProfilesVersion { get; set; }
    public string ActiveProfile { get; set; } = "Balanced";
    /// <summary>Whether the charger was connected when <see cref="ActiveProfile"/> was applied (null: unknown).</summary>
    public bool? ActiveProfileOnAc { get; set; }

    // Automatic switching: each rule names a profile, or null to leave the profile alone. Nulls are
    // written to the file, so "leave alone" survives a restart instead of coming back as the default.
    public bool AutoSwitchProfiles { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AcProfile { get; set; } = "Balanced";
    /// <summary>On battery, above <see cref="LowBatteryThreshold"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BatteryProfile { get; set; } = "Balanced";
    /// <summary>On battery, switch to <see cref="LowBatteryProfile"/> at or below this %. 0 = off.</summary>
    public int LowBatteryThreshold { get; set; } = 30;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LowBatteryProfile { get; set; } = "Battery saver";

    // Keyboard, LEDs, hotkeys
    public int? KeyboardLevel { get; set; }
    public KeyboardTimeout KeyboardTimeout { get; set; } = KeyboardTimeout.AlwaysOn;
    public bool SyncMuteLeds { get; set; } = true;
    public bool HandleHotkeys { get; set; } = true;
    public bool ShowOsd { get; set; } = true;

    // Settings window: the simple view hides the fine-grained controls (they keep their values).
    public bool ExpertView { get; set; }

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

    // Fan curves: each profile holds its own (Profile.CurveTable / CurveSpeeds). The laptop's own speeds
    // are kept the first time they're read (curves overwrite the controller's copy), and whether the
    // user accepted the experimental mode (curves that let the laptop run hot).
    public FanSpeeds? FactoryFanCurve { get; set; }
    public bool UnrestrictedFanCurves { get; set; }

    /// <summary>Settings files before 1.3 kept shared fan curves here; <see cref="Normalize"/> copies them into the profiles that used them.</summary>
    public List<FanProfile>? FanProfiles { get; set; }

    /// <summary>
    /// The controller's whole fan table the first time Wattkin read it, on a laptop where it writes
    /// whole tables: "Laptop default", and what uninstalling puts back. If another tool (YAMDCC) had
    /// written its own table before, that's the one kept.
    /// </summary>
    public FanTable? FactoryFanTable { get; set; }

    // The battery in the laptop (Windows' unique ID for it, and what it last held when full) and when a
    // different one was first seen: capacity history and calibration advice start again from then.
    public string? BatteryIdentity { get; set; }
    public uint? BatteryFullMWh { get; set; }
    public DateTime? BatteryChangedUtc { get; set; }

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
    /// The fan speeds a profile's fan choice stands for (null: hand the controller back the table it
    /// holds), and whether they may break the safety rules: only a curve of the profile's own, after
    /// the user accepted the experimental mode. The laptop's own curve always keeps the rules.
    /// </summary>
    public (FanSpeeds? Speeds, bool Unrestricted) ResolveFanCurve(Profile profile) =>
        profile.HasOwnCurve ? (profile.CurveSpeeds ?? profile.CurveTable!.ToSpeeds(), UnrestrictedFanCurves) : (FactoryFanCurve, false);

    /// <summary>
    /// Where the laptop takes whole fan tables: the table a profile's fan choice stands for. Speeds
    /// without a table run at the laptop's own temperatures. Null when no table of the laptop's is known
    /// yet (the speeds-only path is used then).
    /// </summary>
    public FanTable? ResolveFanTable(Profile profile) =>
        profile.CurveTable ?? (profile.CurveSpeeds is { } speeds ? FactoryFanTable?.WithSpeeds(speeds) : FactoryFanTable);

    /// <summary>
    /// The profile the automatic rules pick for this power state (on the charger; on battery above the
    /// threshold; at or below it), or null when the matching rule leaves the profile alone.
    /// </summary>
    public string? AutoProfile(bool onAc, double? percent) =>
        !AutoSwitchProfiles ? null
        : onAc ? AcProfile
        : LowBatteryThreshold > 0 && percent is { } p && p <= LowBatteryThreshold ? LowBatteryProfile
        : BatteryProfile;

    /// <summary>Puts <paramref name="profile"/> in slot <paramref name="slot"/>; the rules and the active profile follow a new name. Returns the name it got.</summary>
    public string ReplaceProfile(int slot, Profile profile)
    {
        var oldName = Profiles[slot].Name;
        profile.Name = UniqueName(profile.Name.Trim(), slot);
        Profiles = [.. Profiles.Select((p, i) => i == slot ? profile : p)];
        if (!string.Equals(oldName, profile.Name, StringComparison.Ordinal))
        {
            string? Follow(string? name) => string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase) ? profile.Name : name;
            ActiveProfile = Follow(ActiveProfile)!;
            AcProfile = Follow(AcProfile);
            BatteryProfile = Follow(BatteryProfile);
            LowBatteryProfile = Follow(LowBatteryProfile);
        }

        return profile.Name;
    }

    /// <summary><paramref name="name"/>, or "name 2", "name 3"… if another slot has it.</summary>
    public string UniqueName(string name, int slot)
    {
        var taken = Profiles.Where((_, i) => i != slot).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = name.Length == 0 ? Profile.Default(slot).Name : name;
        var unique = baseName;
        for (var i = 2; taken.Contains(unique); i++)
        {
            unique = $"{baseName} {i}";
        }

        return unique;
    }

    /// <summary>
    /// Repairs anything a hand-edited or damaged file could contain: nulls, out-of-range numbers and
    /// enum values the hardware layer must never see. The elevated process reads this file.
    /// </summary>
    public void Normalize()
    {
        // Exactly four slots: unreadable entries are dropped, extra ones (older versions could add
        // profiles) ignored, and missing slots get their defaults. Names stay unique.
        var kept = (Profiles ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name)).ToList();
        if (kept.Count > ProfileSlots)
        {
            Log.Warn($"Only {ProfileSlots} profiles are kept; ignored: {string.Join(", ", kept.Skip(ProfileSlots).Select(p => p.Name))}.");
        }

        Profiles = [.. kept.Take(ProfileSlots)];
        for (var slot = Profiles.Count; slot < ProfileSlots; slot++)
        {
            Profiles = [.. Profiles, Profile.Default(slot)];
        }

        // A repeated name gets a number; the earlier slot (the user's own, before filled-in defaults) keeps it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Profiles)
        {
            var baseName = p.Name.Trim();
            var name = baseName;
            for (var i = 2; !seen.Add(name); i++)
            {
                name = $"{baseName} {i}";
            }

            p.Name = name;
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

        // 2: shared fan profiles became each profile's own curve. "Laptop default" (or a name that's
        // gone) is the laptop's curve; "Off" all speeds off.
        if (ProfilesVersion < 2)
        {
            foreach (var p in Profiles.Where(p => p.FanProfileName is not null))
            {
                if (string.Equals(p.FanProfileName, FanProfile.Off, StringComparison.OrdinalIgnoreCase))
                {
                    p.CurveSpeeds = FanProfile.OffSpeeds();
                    p.CurveTable = FactoryFanTable is { } factory ? FanTable.Off(factory) : null;
                }
                else if (FanProfiles?.FirstOrDefault(f => f is not null && string.Equals(f.Name, p.FanProfileName, StringComparison.OrdinalIgnoreCase)) is { } shared)
                {
                    p.CurveSpeeds = shared.Speeds;
                    p.CurveTable = shared.Table;
                }
            }

            ProfilesVersion = 2;
        }

        FanProfiles = null;
        foreach (var p in Profiles)
        {
            p.FanProfileName = null;
            if (!Enum.IsDefined(p.Performance))
            {
                p.Performance = ShiftMode.Balanced;
            }

            if (p.Fan is { } f && !Enum.IsDefined(f))
            {
                p.Fan = FanMode.Auto;
            }

            p.MaxCpuPercent = p.MaxCpuPercent is { } m ? Math.Clamp(m, 5, 100) : null;
            p.KeyboardLevel = p.KeyboardLevel is { } k ? Math.Clamp(k, 0, 3) : null;
            p.RefreshRate = p.RefreshRate is > 0 and < 1000 ? p.RefreshRate : null;

            // A malformed curve never reaches the controller: dropped, so the profile falls back to the
            // laptop's curve (or, for a table, to its speeds at the laptop's temperatures).
            if (p.CurveTable?.Problem(unrestricted: true) is not null)
            {
                p.CurveTable = null;
            }

            if (p.CurveSpeeds?.Problem(unrestricted: true) is not null)
            {
                p.CurveSpeeds = null;
            }

            p.CurveSpeeds ??= p.CurveTable?.ToSpeeds();
        }

        if (FactoryFanCurve?.Problem(unrestricted: true) is not null)
        {
            FactoryFanCurve = null;
        }

        if (FactoryFanTable?.Problem(unrestricted: true) is not null)
        {
            FactoryFanTable = null;
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
        ActiveProfile = FindProfile(ActiveProfile)?.Name ?? Profiles[0].Name;

        // A rule naming a profile that's gone leaves the profile alone.
        AcProfile = FindProfile(AcProfile)?.Name;
        BatteryProfile = FindProfile(BatteryProfile)?.Name;
        LowBatteryProfile = FindProfile(LowBatteryProfile)?.Name;
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
