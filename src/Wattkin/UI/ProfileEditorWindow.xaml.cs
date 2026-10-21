using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Wattkin.Hardware;
using Wattkin.Platform;
using Wattkin.Settings;
using static Wattkin.UI.Choices;

namespace Wattkin.UI;

/// <summary>
/// Everything one profile slot sets: its name, the processor's power limits, turbo and maximum state,
/// the fans (with a curve of its own), the screen's refresh rate and the keyboard light. Opened by the
/// gear on a profile button; one at a time.
/// </summary>
public partial class ProfileEditorWindow : Window
{
    private int _slot;
    private bool _loading;

    // The laptop's tables, read once: the curve editor's steps and "Use the laptop's curve".
    private FanCurves? _firmwareCurves;
    private FanTable? _firmwareTable;
    private bool _firmwareRead;

    // The curve as edited here: Edited = the user changed it (saved as the profile's own);
    // UseLaptop = the user asked for the laptop's curve (the profile's own is dropped on save).
    private bool _curveEdited;
    private bool _useLaptopCurve;

    // "Manual" in the power-limits list: PL1, PL2 and the turbo time, written through PawnIO.
    private const string ManualTag = "manual";
    private readonly NumberStepper _pl1 = new(PowerLimitSetting.MinWatts, PowerLimitSetting.MaxWatts, 25);
    private readonly NumberStepper _pl2 = new(PowerLimitSetting.MinWatts, PowerLimitSetting.MaxWatts, 35);
    private CpuPowerStatus? _cpuPower;

    public ProfileEditorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.SetDarkTitleBar(new WindowInteropHelper(this).Handle);
        TableEditor.Changed += OnCurveChanged;
        SpeedsEditor.Changed += OnCurveChanged;
        CoolerBoostBox.Click += (_, _) => ShowFanHint();
        Pl1Host.Content = _pl1;
        Pl2Host.Content = _pl2;
        _pl1.ValueChanged += ShowManualStatus;
        _pl2.ValueChanged += ShowManualStatus;
        BuildChoices();
        _ = ReadFirmwareAsync();
        _ = ReadCpuPowerAsync();
    }

    public int Slot => _slot;

    /// <summary>Shows slot <paramref name="slot"/>; unsaved changes to another slot are dropped.</summary>
    public void Edit(int slot)
    {
        _slot = slot;
        Load(App.Current.Store.Current.Profiles[slot]);
    }

    /// <summary>Screenshots without administrator rights (--render): the curve editor on the table the settings kept.</summary>
    internal void PreviewTable(FanTable table)
    {
        _firmwareTable = table;
        _firmwareRead = true;
        ShowCurve(App.Current.Store.Current.Profiles[_slot]);
    }

    /// <summary>Screenshots (--render): "Manual" chosen, to show its panel.</summary>
    internal void PreviewManual() => Select(ShiftBox, ManualTag);

    private bool FullTables => _firmwareTable is not null;

    private FanTable? LaptopTable => App.Current.Store.Current.FactoryFanTable?.Clone() ?? _firmwareTable?.Clone();

    private FanSpeeds? LaptopSpeeds => App.Current.Store.Current.FactoryFanCurve?.Clone() ?? (_firmwareCurves is { } curves ? FanSpeeds.From(curves) : null);

    private async Task ReadFirmwareAsync()
    {
        (_firmwareCurves, _firmwareTable) = await Task.Run(() => (App.Current.Hardware.ReadFanCurves(), App.Current.Hardware.ReadFullFanTable()));
        _firmwareRead = true;
        ShowCurve(App.Current.Store.Current.Profiles[_slot]);
    }

    /// <summary>The power-limit registers now: the turbo times the register can hold, and what's in force.</summary>
    private async Task ReadCpuPowerAsync()
    {
        _cpuPower = await Task.Run(App.Current.Hardware.ReadCpuPower);
        var turbo = TagOf<int>(TurboTimeBox);
        FillTurboTimes(_cpuPower.Units);
        Select(TurboTimeBox, turbo);
        ShowManualStatus();
    }

    private void FillTurboTimes(RaplUnits units)
    {
        TurboTimeBox.Items.Clear();
        foreach (var seconds in PackagePowerLimit.TurboTimeChoices(units))
        {
            TurboTimeBox.Items.Add(Item(string.Format(Strings.Unit_Seconds, seconds), seconds));
        }
    }

    /// <summary>The turbo time the list offers that's closest to <paramref name="seconds"/>.</summary>
    private int NearestTurboTime(double seconds) =>
        TurboTimeBox.Items.Cast<ComboBoxItem>().Select(i => (int)i.Tag).MinBy(t => Math.Abs(t - seconds));

    private void OnShiftChanged(object sender, SelectionChangedEventArgs e)
    {
        ManualPanel.Visibility = TagOf<string>(ShiftBox) == ManualTag ? Visibility.Visible : Visibility.Collapsed;
        ShowManualStatus();
    }

    /// <summary>
    /// The firmware's own copy of the limits under MSI's Balanced, which Manual uses: the processor keeps
    /// to the lower of it and Manual's. Noted by the app a few seconds after a profile change in Balanced.
    /// </summary>
    private PackageLimits? ManualCeiling =>
        _cpuPower?.FirmwareByMode?.TryGetValue(nameof(ShiftMode.Balanced), out var ceiling) == true ? ceiling : null;

    private void ShowManualStatus()
    {
        // Above the firmware's copy the processor stays at the firmware's limits; saved anyway.
        var warning = TagOf<string>(ShiftBox) == ManualTag ? ManualLimits().AboveFirmware(ManualCeiling) : null;
        ManualWarning.Text = warning ?? "";
        ManualWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;

        if (_cpuPower is not { } power)
        {
            ManualStatus.Text = Strings.Editor_ReadingLimits;
            return;
        }

        if (!power.Available)
        {
            ManualStatus.Text = string.Format(Strings.Editor_LimitsUnavailable, power.Problem);
            return;
        }

        var lines = new List<string>();
        if (power.Msr is { } now)
        {
            lines.Add(string.Format(Strings.Editor_LimitsNow, Fmt.Limits(now)));
        }

        if (power.Bios is { } bios)
        {
            lines.Add(string.Format(Strings.Editor_LimitsBios, Fmt.Limits(bios)));
        }

        lines.Add(ManualCeiling is { } ceiling ? string.Format(Strings.Editor_ManualCeiling, Fmt.Limits(ceiling)) : Strings.Editor_ManualCeilingUnknown);
        ManualStatus.Text = string.Join(" ", lines);
    }

    /// <summary>What's wrong with the manual limits as set, or null (also when Manual isn't chosen).</summary>
    private string? ManualProblem() =>
        TagOf<string>(ShiftBox) == ManualTag ? ManualLimits().Problem(_cpuPower?.MaxWatts) : null;

    private PowerLimitSetting ManualLimits() => new() { Pl1Watts = _pl1.Value, Pl2Watts = _pl2.Value, TurboSeconds = TagOf<int>(TurboTimeBox) };

    // ---- choices ----------------------------------------------------------------------------------

    private void BuildChoices()
    {
        var caps = App.Current.Hardware.State.Caps;
        ShiftBox.Items.Add(Item(Strings.Editor_ShiftLeave, null));
        ShiftBox.Items.Add(Item(Strings.Editor_ShiftEco, ShiftMode.Eco));
        ShiftBox.Items.Add(Item(Strings.Editor_ShiftBalanced, ShiftMode.Balanced));
        if (caps?.HasSportMode != false)
        {
            ShiftBox.Items.Add(Item(Strings.Editor_ShiftSport, ShiftMode.Sport));
        }

        ShiftBox.Items.Add(Item(Strings.Editor_ShiftExtreme, ShiftMode.Turbo));
        ShiftBox.Items.Add(Item(Strings.Editor_ShiftManual, ManualTag));
        FillTurboTimes(RaplUnits.Typical);

        MaxFreqBox.Items.Add(Item(Strings.Common_LeaveAsIs, null));
        MaxFreqBox.Items.Add(Item(Strings.Editor_NoCap, (int?)0));
        foreach (var mhz in new[] { 1500, 1800, 2000, 2200, 2400, 2600, 2800, 3000, 3300, 3600, 4000, 4400 })
        {
            MaxFreqBox.Items.Add(Item(Fmt.Gigahertz(mhz), (int?)mhz));
        }

        EppBox.Items.Add(Item(Strings.Common_LeaveAsIs, null));
        foreach (var (value, text) in new[] { (0, Strings.Editor_EppPerformance), (20, "20"), (33, "33"), (50, Strings.Editor_EppBalanced), (70, "70"), (85, "85"), (100, Strings.Editor_EppEfficiency) })
        {
            EppBox.Items.Add(Item(text, (int?)value));
        }

        TurboBox.Items.Add(Item(Strings.Common_LeaveAsIs, null));
        TurboBox.Items.Add(Item(Strings.Editor_TurboOn, true));
        TurboBox.Items.Add(Item(Strings.Editor_TurboOff, false));

        MaxCpuBox.Items.Add(Item(Strings.Common_LeaveAsIs, null));
        foreach (var value in new[] { 100, 99, 90, 80, 70, 60, 50 })
        {
            MaxCpuBox.Items.Add(Item(value == 99 ? string.Format(Strings.Editor_MaxStateNoTurbo, Fmt.Percent(value)) : Fmt.Percent(value), (int?)value));
        }

        RefreshBox.Items.Add(Item(Strings.Common_LeaveAsIs, null));
        foreach (var hz in DisplayModes.SupportedRefreshRates())
        {
            RefreshBox.Items.Add(Item($"{hz} Hz", (int?)hz));
        }

        KeyboardBox.Items.Add(Item(Strings.Common_LeaveAsIs, null));
        var levels = Fmt.KeyboardLevels();
        for (var i = 0; i < levels.Length; i++)
        {
            KeyboardBox.Items.Add(Item(levels[i], (int?)i));
        }

        foreach (var (text, mode) in new (string, FanMode?)[] { (Strings.Fan_Auto, FanMode.Auto), (Strings.Fan_Silent, FanMode.Silent), (Strings.Editor_FanCustom, FanMode.Custom), (Strings.Common_LeaveAsIs, null) })
        {
            var button = new RadioButton { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = "fanmode", Tag = mode, Margin = new Thickness(2, 0, 2, 0) };
            button.Checked += (_, _) =>
            {
                if (!_loading)
                {
                    ShowFanHint();
                }
            };
            FanModePanel.Children.Add(button);
        }
    }

    private FanMode? SelectedFanMode => FanModePanel.Children.OfType<RadioButton>().FirstOrDefault(b => b.IsChecked == true)?.Tag as FanMode?;

    private bool FanModeChosen => FanModePanel.Children.OfType<RadioButton>().Any(b => b.IsChecked == true);

    // ---- load -------------------------------------------------------------------------------------

    private void Load(Profile profile)
    {
        _loading = true;
        try
        {
            Title = string.Format(Strings.Editor_TitleNamed, profile.Name);
            SlotTitle.Text = string.Format(Strings.Editor_Slot, _slot + 1, AppSettings.ProfileSlots);
            NameBox.Text = profile.Name;
            if (profile.PowerLimits is { } limits)
            {
                Select(ShiftBox, ManualTag);
                _pl1.Value = limits.Pl1Watts;
                _pl2.Value = limits.Pl2Watts;
                Select(TurboTimeBox, NearestTurboTime(limits.TurboSeconds));
            }
            else
            {
                Select(ShiftBox, profile.Performance, t => string.Format(Strings.Editor_NotOffered, Fmt.Shift((ShiftMode)t)));
                Select(TurboTimeBox, NearestTurboTime(28));
            }

            ManualPanel.Visibility = profile.PowerLimits is null ? Visibility.Collapsed : Visibility.Visible;
            Select(TurboBox, profile.CpuBoost);
            Select(MaxCpuBox, profile.MaxCpuPercent);
            Select(MaxFreqBox, profile.MaxFrequencyMHz, t => Fmt.Gigahertz((int)t));
            Select(EppBox, profile.EnergyPreference, t => $"{t}");
            Select(RefreshBox, profile.RefreshRate, t => string.Format(Strings.Editor_RefreshUnavailable, t));
            Select(KeyboardBox, profile.KeyboardLevel);
            foreach (RadioButton button in FanModePanel.Children)
            {
                button.IsChecked = Equals(button.Tag as FanMode?, profile.Fan);
            }

            CoolerBoostBox.IsChecked = profile.CoolerBoost;
            UnrestrictedBox.IsChecked = App.Current.Store.Current.UnrestrictedFanCurves;
            _curveEdited = false;
            _useLaptopCurve = false;
            ShowCurve(profile);
        }
        finally
        {
            _loading = false;
        }

        ShowFanHint();
    }

    /// <summary>The profile's own curve, or the laptop's when it has none, in whichever editor this laptop takes.</summary>
    private void ShowCurve(Profile profile)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            TableEditor.Visibility = FullTables ? Visibility.Visible : Visibility.Collapsed;
            SpeedsEditor.Visibility = FullTables || _firmwareCurves is null ? Visibility.Collapsed : Visibility.Visible;
            if (FullTables)
            {
                TableEditor.Show(profile.CurveTable?.Clone() ?? (profile.CurveSpeeds is { } speeds ? LaptopTable!.WithSpeeds(speeds) : LaptopTable!));
            }
            else if (_firmwareCurves is { } curves && (profile.CurveSpeeds?.Clone() ?? LaptopSpeeds) is { } shown)
            {
                SpeedsEditor.Show(curves, shown);
            }
        }
        finally
        {
            _loading = wasLoading;
        }

        ShowFanHint();
    }

    // ---- fans -------------------------------------------------------------------------------------

    private bool CurveAvailable => FullTables || _firmwareCurves is not null;

    private bool HasOwnCurveNow => _curveEdited || (!_useLaptopCurve && App.Current.Store.Current.Profiles[_slot].HasOwnCurve);

    private void ShowFanHint()
    {
        var mode = SelectedFanMode;
        var custom = FanModeChosen && mode == FanMode.Custom;
        FanHint.Text = !FanModeChosen ? ""
            : CoolerBoostBox.IsChecked == true ? Strings.Editor_FanHintBoost
            : mode switch
            {
                FanMode.Auto => Strings.Editor_FanHintAuto,
                FanMode.Silent => Strings.Editor_FanHintSilent,
                FanMode.Custom => Strings.Editor_FanHintCustom,
                _ => Strings.Editor_FanHintLeave,
            };

        CurvePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        LaptopCurveButton.IsEnabled = HasOwnCurveNow;
        CurveIntro.Text = !_firmwareRead ? Strings.Editor_CurveReading
            : !CurveAvailable ? Strings.Editor_CurveUnavailable
            : HasOwnCurveNow ? Strings.Editor_CurveOwn
            : Strings.Editor_CurveLaptop;
        UpdateProblem();
    }

    private void OnCurveChanged()
    {
        if (_loading)
        {
            return;
        }

        _curveEdited = true;
        _useLaptopCurve = false;
        ShowFanHint();
    }

    private void OnLaptopCurve(object sender, RoutedEventArgs e)
    {
        _curveEdited = false;
        _useLaptopCurve = true;
        ShowCurve(new Profile());
    }

    private void UpdateProblem()
    {
        var problem = CurveProblemText();
        CurveProblem.Text = problem ?? "";
        CurveProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What's wrong with the curve as set (only one the profile would own), or null.</summary>
    private string? CurveProblemText()
    {
        if (!_curveEdited || !CurveAvailable)
        {
            return null;
        }

        var unrestricted = App.Current.Store.Current.UnrestrictedFanCurves;
        return FullTables ? TableEditor.Problem(unrestricted) : SpeedsEditor.Problem(unrestricted);
    }

    private void OnUnrestricted(object sender, RoutedEventArgs e)
    {
        if (UnrestrictedBox.IsChecked == true)
        {
            UnrestrictedBox.IsChecked = ConfirmExperimental();
        }
        else
        {
            App.Current.Store.Update(s => s.UnrestrictedFanCurves = false);
            Log.Info("Fans: back to the safety rules (curves that break them use Silent instead).");
        }

        UpdateProblem();
    }

    /// <summary>The experimental warning. True when the user accepts (remembered) or already had.</summary>
    private bool ConfirmExperimental()
    {
        if (App.Current.Store.Current.UnrestrictedFanCurves)
        {
            return true;
        }

        if (MessageBox.Show(this, Strings.Editor_ExperimentalWarning, Strings.Common_Fans, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return false;
        }

        App.Current.Store.Update(s => s.UnrestrictedFanCurves = true);
        Log.Info("Fans: the user accepted the experimental mode (curves that let the laptop run hot).");
        return true;
    }

    // ---- save, reset ------------------------------------------------------------------------------

    /// <summary>The profile as set here, or null after telling the user what to fix.</summary>
    private Profile? Collect()
    {
        var settings = App.Current.Store.Current;
        var current = settings.Profiles[_slot];
        var name = NameBox.Text.Trim();
        var problem = name.Length == 0 ? Strings.Editor_NeedsName
            : !string.Equals(settings.UniqueName(name, _slot), name, StringComparison.OrdinalIgnoreCase) ? string.Format(Strings.Editor_NameTaken, name)
            : ManualProblem() ?? CurveProblemText();
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Strings.Common_Profile, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var profile = current.Clone();
        profile.Name = name;
        if (TagOf<string>(ShiftBox) == ManualTag)
        {
            // MSI's Balanced: its own ceiling (firmware copy 40/45 W on the Stealth 15M) is above the ~32 W
            // the cooling holds, and only Eco holds the processor lower (10 W).
            profile.Performance = ShiftMode.Balanced;
            profile.PowerLimits = ManualLimits();
        }
        else
        {
            profile.Performance = (ShiftBox.SelectedItem as ComboBoxItem)?.Tag as ShiftMode?;
            profile.PowerLimits = null;
        }

        profile.MaxFrequencyMHz = (MaxFreqBox.SelectedItem as ComboBoxItem)?.Tag as int?;
        profile.EnergyPreference = (EppBox.SelectedItem as ComboBoxItem)?.Tag as int?;
        profile.CpuBoost = (TurboBox.SelectedItem as ComboBoxItem)?.Tag as bool?;
        profile.MaxCpuPercent = (MaxCpuBox.SelectedItem as ComboBoxItem)?.Tag as int?;
        profile.RefreshRate = (RefreshBox.SelectedItem as ComboBoxItem)?.Tag as int?;
        profile.KeyboardLevel = (KeyboardBox.SelectedItem as ComboBoxItem)?.Tag as int?;
        if (FanModeChosen)
        {
            profile.Fan = SelectedFanMode;
        }

        profile.CoolerBoost = CoolerBoostBox.IsChecked == true;
        if (_useLaptopCurve)
        {
            profile.CurveTable = null;
            profile.CurveSpeeds = null;
        }
        else if (_curveEdited && FullTables)
        {
            profile.CurveTable = TableEditor.Value;
            profile.CurveSpeeds = profile.CurveTable.ToSpeeds();
        }
        else if (_curveEdited)
        {
            profile.CurveSpeeds = SpeedsEditor.Value;
            profile.CurveTable = current.CurveTable?.WithSpeeds(SpeedsEditor.Value);
        }

        return profile;
    }

    /// <summary>
    /// Saves the slot. The profile in use gets its new settings straight away; another one only when
    /// it's next picked (by a click or a rule), so editing never switches profiles.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (Collect() is not { } profile)
        {
            return false;
        }

        var name = "";
        App.Current.Store.Update(s => name = s.ReplaceProfile(_slot, profile));
        Log.Info($"Profile {_slot + 1} saved: {name} · {Fmt.ProfileDetails(profile)}");
        if (string.Equals(App.Current.Store.Current.ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
        {
            await App.Current.Hardware.ApplyProfileAsync(name);
        }

        return true;
    }

    /// <summary>Saves and stays open, showing the profile as saved (its curve is now its own).</summary>
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync())
        {
            Load(App.Current.Store.Current.Profiles[_slot]);
        }
    }

    private async void OnApplyAndClose(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync())
        {
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        var fresh = Profile.Default(_slot);
        if (MessageBox.Show(this, string.Format(Strings.Editor_ConfirmReset, fresh.Name, Fmt.ProfileDetails(fresh)), Strings.Editor_ResetTitle, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var wasActive = string.Equals(App.Current.Store.Current.ActiveProfile, App.Current.Store.Current.Profiles[_slot].Name, StringComparison.OrdinalIgnoreCase);
        var name = "";
        App.Current.Store.Update(s => name = s.ReplaceProfile(_slot, fresh));
        Log.Info($"Profile {_slot + 1} reset to its default: {name}.");
        Load(App.Current.Store.Current.Profiles[_slot]);
        if (wasActive)
        {
            await App.Current.Hardware.ApplyProfileAsync(name);
        }
    }
}
