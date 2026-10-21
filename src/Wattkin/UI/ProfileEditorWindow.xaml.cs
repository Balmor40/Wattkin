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

    public ProfileEditorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.SetDarkTitleBar(new WindowInteropHelper(this).Handle);
        TableEditor.Changed += OnCurveChanged;
        SpeedsEditor.Changed += OnCurveChanged;
        CoolerBoostBox.Click += (_, _) => ShowFanHint();
        BuildChoices();
        _ = ReadFirmwareAsync();
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

    private bool FullTables => _firmwareTable is not null;

    private FanTable? LaptopTable => App.Current.Store.Current.FactoryFanTable?.Clone() ?? _firmwareTable?.Clone();

    private FanSpeeds? LaptopSpeeds => App.Current.Store.Current.FactoryFanCurve?.Clone() ?? (_firmwareCurves is { } curves ? FanSpeeds.From(curves) : null);

    private async Task ReadFirmwareAsync()
    {
        (_firmwareCurves, _firmwareTable) = await Task.Run(() => (App.Current.Hardware.ReadFanCurves(), App.Current.Hardware.ReadFullFanTable()));
        _firmwareRead = true;
        ShowCurve(App.Current.Store.Current.Profiles[_slot]);
    }

    // ---- choices ----------------------------------------------------------------------------------

    private void BuildChoices()
    {
        var caps = App.Current.Hardware.State.Caps;
        ShiftBox.Items.Add(Item("Eco: lowest limits (MSI Super Battery)", ShiftMode.Eco));
        ShiftBox.Items.Add(Item("Balanced (MSI Balanced / Silent)", ShiftMode.Balanced));
        if (caps?.HasSportMode != false)
        {
            ShiftBox.Items.Add(Item("Sport: between Balanced and Extreme (YAMDCC's Balanced)", ShiftMode.Sport));
        }

        ShiftBox.Items.Add(Item("Extreme: highest limits (MSI Extreme Performance)", ShiftMode.Turbo));

        TurboBox.Items.Add(Item("Leave as is", null));
        TurboBox.Items.Add(Item("On", true));
        TurboBox.Items.Add(Item("Off: base clock only, much cooler and quieter", false));

        MaxCpuBox.Items.Add(Item("Leave as is", null));
        foreach (var value in new[] { 100, 99, 90, 80, 70, 60, 50 })
        {
            MaxCpuBox.Items.Add(Item(value == 99 ? "99% (no turbo)" : $"{value}%", (int?)value));
        }

        RefreshBox.Items.Add(Item("Leave as is", null));
        foreach (var hz in DisplayModes.SupportedRefreshRates())
        {
            RefreshBox.Items.Add(Item($"{hz} Hz", (int?)hz));
        }

        KeyboardBox.Items.Add(Item("Leave as is", null));
        string[] levels = ["Off", "Low", "Mid", "High"];
        for (var i = 0; i < levels.Length; i++)
        {
            KeyboardBox.Items.Add(Item(levels[i], (int?)i));
        }

        foreach (var (text, mode) in new (string, FanMode?)[] { ("Auto", FanMode.Auto), ("Silent", FanMode.Silent), ("Custom curve", FanMode.Custom), ("Leave as is", null) })
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
            Title = $"Edit profile: {profile.Name}";
            SlotTitle.Text = $"Profile {_slot + 1} of {AppSettings.ProfileSlots}";
            NameBox.Text = profile.Name;
            Select(ShiftBox, profile.Performance, t => $"{t} (not offered by this laptop)");
            Select(TurboBox, profile.CpuBoost);
            Select(MaxCpuBox, profile.MaxCpuPercent);
            Select(RefreshBox, profile.RefreshRate, t => $"{t} Hz (built-in screen off right now)");
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
            : CoolerBoostBox.IsChecked == true ? "The fans run flat out while this profile is on."
            : mode switch
            {
                FanMode.Auto => "The laptop's own fan behaviour (the firmware decides).",
                FanMode.Silent => "The laptop's own quieter behaviour (the firmware decides).",
                FanMode.Custom => "The curve below: each fan's speed at each step, and when it steps up and back down.",
                _ => "This profile leaves the fans as they are.",
            };

        CurvePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        LaptopCurveButton.IsEnabled = HasOwnCurveNow;
        CurveIntro.Text = !_firmwareRead ? "Reading the laptop's fan table…"
            : !CurveAvailable ? "Custom curves need administrator rights and a fan table Wattkin can read on this laptop."
            : HasOwnCurveNow ? "This profile's own curve."
            : "This profile uses the laptop's curve, shown below. Change anything to give it a curve of its own.";
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

        if (MessageBox.Show(this,
                "This is experimental.\n\nNormally Wattkin makes sure the fans keep ahead of the heat: speeds never drop as it gets hotter, the fans run by the step before the hottest, and fast at the hottest. Allowing this lets you save curves that let the laptop run hot enough to slow itself down (thermal throttling).\n\nKeep an eye on your temperatures in the flyout.",
                "Fans", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
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
        var problem = name.Length == 0 ? "Give the profile a name."
            : !string.Equals(settings.UniqueName(name, _slot), name, StringComparison.OrdinalIgnoreCase) ? $"Another profile is already called \"{name}\"."
            : CurveProblemText();
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var profile = current.Clone();
        profile.Name = name;
        profile.Performance = TagOf<ShiftMode>(ShiftBox);
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
        if (MessageBox.Show(this, $"Put this profile back to how Wattkin ships it, \"{fresh.Name}\" included?\n\n{Fmt.ProfileDetails(fresh)}", "Reset profile", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
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
