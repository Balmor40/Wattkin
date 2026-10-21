using System.Windows;
using System.Windows.Controls;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.UI;

/// <summary>
/// The simple settings page: the everyday choices as Windows 11-style rows, one control each. It shares
/// the settings and hardware with the expert tabs; the settings window tells it when anything changed.
/// </summary>
public partial class SimpleSettings : UserControl
{
    private static readonly int[] Limits = [60, 70, 80, 90, 100];
    private bool _loading;
    private string _profileNames = "";
    private CalibrationSuggestion? _suggestion;
    private readonly ColorSwatches _keyboardColors;
    private readonly AutoOffDelayStepper _autoOffDelay = new();

    public SimpleSettings()
    {
        InitializeComponent();

        // A steady 48 px per wheel notch (Windows' usual three lines), not a whole group at a time.
        Scroller.PreviewMouseWheel += (_, e) =>
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - (e.Delta / 120.0 * 48));
            e.Handled = true;
        };
        foreach (var limit in Limits)
        {
            var button = Segment(Fmt.Percent(limit), "simpleLimit", limit);
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetChargeLimitAsync(limit, null);
                }
            };
            LimitPanel.Children.Add(button);
        }

        var levels = Fmt.KeyboardLevels();
        for (var i = 0; i < levels.Length; i++)
        {
            var level = i;
            var button = Segment(levels[i], "simpleKb", level);
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardLevelAsync(level);
                }
            };
            KeyboardPanel.Children.Add(button);
        }

        _keyboardColors = new ColorSwatches(KeyboardColorPanel, "simpleKbColor");
        AutoOffDelayHost.Child = _autoOffDelay.Control;
        Choices.FillTrayDisplay(TrayChoice);
        Choices.FillLanguages(LanguageChoice);

        foreach (var (text, mode) in new[] { (Strings.Simple_GpuIntegrated, FirmwareGpuMode.Integrated), (Strings.Simple_GpuHybrid, FirmwareGpuMode.Hybrid) })
        {
            var button = Segment(text, "simpleGpu", mode);
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    GpuPanel.IsEnabled = false;
                    var error = await App.Current.Hardware.RequestGpuModeAsync(mode);
                    GpuPanel.IsEnabled = true;
                    if (error is not null)
                    {
                        MessageBox.Show(Window.GetWindow(this), error, Strings.Common_Graphics, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    RefreshState();
                }
            };
            GpuPanel.Children.Add(button);
        }
    }

    /// <summary>"Expert view" at the bottom of the page.</summary>
    public event Action? ExpertRequested;

    private RadioButton Segment(string text, string group, object tag) =>
        new() { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = group, Tag = tag, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(6, 6, 6, 6) };

    /// <summary>Scrolled where <paramref name="old"/> was, once laid out: this page replaces it (in another language).</summary>
    public void KeepScrollOf(SimpleSettings old)
    {
        var offset = old.Scroller.VerticalOffset;
        Loaded += (_, _) => Scroller.ScrollToVerticalOffset(offset);
    }

    // ---- loading ------------------------------------------------------------------------------

    public void Load()
    {
        var s = App.Current.Store.Current;
        _loading = true;
        try
        {
            foreach (RadioButton b in LimitPanel.Children)
            {
                b.IsChecked = s.ChargeResumeBelow is null && s.ChargeLimit == (int)b.Tag;
            }

            LimitText.Text = LimitDescription(s);
            FullOnceToggle.IsChecked = s.FullChargeOnce;

            var names = string.Join("|", s.Profiles.Select(p => p.Name));
            if (names != _profileNames)
            {
                _profileNames = names;
                foreach (var box in new[] { AcProfile, DcProfile })
                {
                    Choices.FillProfiles(box, s.Profiles.Select(p => p.Name));
                }
            }

            Choices.Select(AcProfile, s.AcProfile ?? "");
            Choices.Select(DcProfile, s.BatteryProfile ?? "");
            AcText.Text = Describe(s.FindProfile(s.AcProfile));
            DcText.Text = Describe(s.FindProfile(s.BatteryProfile))
                + (s.AutoSwitchProfiles && s.LowBatteryThreshold > 0 && s.LowBatteryProfile is { } low ? " " + string.Format(Strings.Simple_LowBatterySwitch, Fmt.Percent(s.LowBatteryThreshold), low) : "");

            AutoOffToggle.IsChecked = s.KeyboardTimeout != KeyboardTimeout.AlwaysOn;
            _keyboardColors.Show(s.KeyboardColor);
            _autoOffDelay.Show(s.KeyboardAutoOffSeconds);
            Choices.Select(TrayChoice, s.TrayDisplay);
            Choices.Select(LanguageChoice, s.Language);
            TrayText.Text = TrayDescription(s.TrayDisplay, Elevation.IsElevated);
            WidgetToggle.IsChecked = App.Current.IsWidgetVisible;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string Describe(Profile? p) => p is null ? Strings.Simple_KeepsProfile : Fmt.Sentence(Fmt.ProfileSummary(p)) + ".";

    /// <param name="elevated">The CPU temperature comes from the EC, which only an administrator can read.</param>
    private static string TrayDescription(TrayDisplay display, bool elevated) => display switch
    {
        TrayDisplay.Logo => Strings.Simple_TrayLogo,
        TrayDisplay.CpuTemperature when !elevated => Strings.Simple_TrayTemperatureNeedsAdmin,
        TrayDisplay.CpuTemperature => Strings.Simple_TrayTemperature,
        TrayDisplay.CpuLoad => Strings.Simple_TrayLoad,
        _ => Strings.Simple_TrayWatts,
    };

    private static string LimitDescription(AppSettings s)
    {
        if (s.ChargeResumeBelow is { } resume)
        {
            return string.Format(Strings.Simple_LimitBand, Fmt.Percent(s.ChargeLimit), Fmt.Percent(resume));
        }

        if (!Limits.Contains(s.ChargeLimit))
        {
            return string.Format(Strings.Simple_LimitCustom, Fmt.Percent(s.ChargeLimit));
        }

        return s.ChargeLimit switch
        {
            <= 60 => Strings.Simple_Limit60,
            <= 80 => Strings.Simple_Limit80,
            < 100 => Strings.Simple_Limit90,
            _ => Strings.Simple_Limit100,
        };
    }

    public void SetCalibrationSuggestion(CalibrationSuggestion? suggestion)
    {
        _suggestion = suggestion;
        RefreshState();
    }

    public void RefreshStartup(bool enabled)
    {
        _loading = true;
        StartToggle.IsChecked = enabled;
        _loading = false;
    }

    /// <summary>Everything that follows the hardware: enabled states, the keyboard level, graphics, notices.</summary>
    public void RefreshState()
    {
        var app = App.Current;
        var state = app.Hardware.State;
        _loading = true;
        try
        {
            var chargeEditable = state.CanWrite && !app.Hardware.IsCalibrating;
            LimitPanel.IsEnabled = FullOnceToggle.IsEnabled = chargeEditable;
            foreach (RadioButton b in KeyboardPanel.Children)
            {
                b.IsChecked = state.KeyboardLevel == (int)b.Tag;
            }

            KeyboardPanel.IsEnabled = state.CanWrite && state.Caps?.HasKeyboardLight != false;
            var usbLight = state.Caps?.HasKeyboardColor != false;
            KeyboardColorRow.Visibility = AutoOffDelayHost.Visibility = usbLight ? Visibility.Visible : Visibility.Collapsed;
            KeyboardColorPanel.IsEnabled = state.CanWrite;
            AutoOffDelayHost.IsEnabled = state.CanWrite && app.Store.Current.KeyboardTimeout != KeyboardTimeout.AlwaysOn;
            AutoOffText.Text = usbLight ? Strings.Simple_AutoOffUsb : Strings.Simple_AutoOffEc;
            AutoOffToggle.IsEnabled = state.CanWrite && state.Caps?.HasKeyboardTimeout != false;
            AcProfile.IsEnabled = DcProfile.IsEnabled = state.CanWrite;

            RefreshGraphics(state);
            RefreshCalibration(state);
            RefreshNotice(state);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshGraphics(HardwareState state)
    {
        var gpu = state.Gpu;
        var available = state.Caps?.CanSwitchGpuMode == true && gpu is { SwitchSupported: true, IntegratedSupported: true } && gpu.Current != FirmwareGpuMode.Discrete;
        GraphicsGroup.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            return;
        }

        foreach (RadioButton b in GpuPanel.Children)
        {
            b.IsChecked = (FirmwareGpuMode)b.Tag == gpu!.Requested;
        }

        GpuText.Text = gpu!.SwitchPending
            ? gpu.Requested == FirmwareGpuMode.Hybrid ? Strings.Graphics_RtxBackAfterRestart : Strings.Graphics_RtxOffAfterRestart
            : gpu.Current == FirmwareGpuMode.Hybrid ? Strings.Simple_GpuHybridText : Strings.Simple_GpuIntegratedText;
        RestartButton.Visibility = gpu.SwitchPending ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshCalibration(HardwareState state)
    {
        var running = App.Current.Store.Current.Calibration is not null;
        if (running)
        {
            CalibrationRow.Visibility = Visibility.Visible;
            CalibrationTitle.Text = Strings.Simple_Calibrating;
            var c = state.Calibration;
            CalibrationText.Text = c is null ? Strings.Common_Starting : string.Format(Strings.Simple_CalibratingText, c.Message, Fmt.Duration(Fmt.Steady(c.TotalEta)));
            CalibrationBar.Visibility = Visibility.Visible;
            CalibrationBar.Value = c is null ? 0 : c.Phase == CalibrationPhase.Discharging
                ? Math.Clamp(((100 - (c.Percent ?? 100)) / Math.Max(1, 100 - c.TargetPercent) * 50) + 50, 50, 100)
                : Math.Clamp((c.Percent ?? 0) / 2, 0, 50);
            CalibrationButton.Content = Strings.Common_Cancel;
            CalibrationButton.IsEnabled = true;
        }
        else if (_suggestion is { } advice && state.Caps?.CanSimulateUnplug == true)
        {
            CalibrationRow.Visibility = Visibility.Visible;
            CalibrationTitle.Text = Strings.Simple_Recalibrate;
            CalibrationText.Text = string.Format(Strings.Simple_RecalibrateText, advice.NowWh, advice.TypicalWh);
            CalibrationBar.Visibility = Visibility.Collapsed;
            CalibrationButton.Content = Strings.Simple_Calibrate;
            CalibrationButton.IsEnabled = state.CanWrite;
        }
        else
        {
            CalibrationRow.Visibility = Visibility.Collapsed;
        }
    }

    private Action? _noticeAction;

    private void RefreshNotice(HardwareState state)
    {
        (string Title, string Text, string Button, Action Act)? notice = null;
        if (!Elevation.IsElevated)
        {
            notice = (Strings.Simple_NoticeNotAdmin, Strings.Simple_NoticeNotAdminText, Strings.Common_RestartAsAdmin, () => App.Current.RestartElevated());
        }
        else if (state.MsiSoftwareRunning)
        {
            notice = (Strings.Simple_NoticeMsi, Strings.Simple_NoticeMsiText, Strings.Simple_OpenApps, () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }));
        }
        else if (state.Caps is { Support: EcSupport.Unknown } && state.Problem is { } problem)
        {
            notice = (Strings.Simple_NoticeMonitoring, problem, Strings.Simple_Details, () => ExpertRequested?.Invoke());
        }

        NoticeGroup.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is { } n)
        {
            NoticeTitle.Text = n.Title;
            NoticeText.Text = n.Text;
            NoticeButton.Content = n.Button;
            _noticeAction = n.Act;
        }
    }

    /// <summary>Live: the battery health line.</summary>
    public void Update(Snapshot s)
    {
        if (App.Current.Monitor.BatteryInfo is { DesignedCapacityMWh: > 0 } info)
        {
            var health = 100.0 * info.FullChargedCapacityMWh / info.DesignedCapacityMWh;
            HealthPercent.Text = Fmt.Percent(health);
            HealthText.Text = string.Format(Strings.Simple_HealthText, info.FullChargedCapacityMWh / 1000.0, info.DesignedCapacityMWh / 1000.0, Fmt.Percent(s.Power.Percent));
        }

        if (App.Current.Store.Current.Calibration is not null)
        {
            RefreshCalibration(App.Current.Hardware.State);
        }
    }

    // ---- handlers -----------------------------------------------------------------------------

    private async void OnFullOnce(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetFullChargeOnceAsync(FullOnceToggle.IsChecked == true);

    private void OnProfileChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AcProfile.SelectedItem is null || DcProfile.SelectedItem is null)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.AutoSwitchProfiles = true;
            s.AcProfile = Choices.ProfileOf(AcProfile);
            s.BatteryProfile = Choices.ProfileOf(DcProfile);
        });
    }

    private async void OnAutoOff(object sender, RoutedEventArgs e) =>
        await App.Current.Hardware.SetKeyboardTimeoutAsync(AutoOffToggle.IsChecked == true ? KeyboardTimeout.AutoOff : KeyboardTimeout.AlwaysOn);

    private void OnTray(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && TrayChoice.SelectedItem is ComboBoxItem { Tag: TrayDisplay display })
        {
            App.Current.Store.Update(s => s.TrayDisplay = display);
        }
    }

    private void OnWidget(object sender, RoutedEventArgs e) => App.Current.SetWidgetVisible(WidgetToggle.IsChecked == true);

    private void OnLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LanguageChoice.SelectedItem is ComboBoxItem item)
        {
            App.Current.SetLanguage(item.Tag as string);
        }
    }

    private async void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        var wanted = StartToggle.IsChecked == true;
        if (!Elevation.IsElevated || !App.Current.Hardware.ControlsHardware)
        {
            RefreshStartup(!wanted);
            if (!Elevation.IsElevated && MessageBox.Show(Window.GetWindow(this), Strings.General_StartupNeedsAdmin, "Wattkin", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                App.Current.RestartElevated();
            }

            return;
        }

        StartToggle.IsEnabled = false;
        var error = await Task.Run(() => wanted ? StartupTask.Enable() : StartupTask.Disable());
        StartToggle.IsEnabled = true;
        if (error is not null)
        {
            MessageBox.Show(Window.GetWindow(this), error, Strings.Common_StartWithWindows, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStartup(await Task.Run(StartupTask.IsEnabled));
    }

    private void OnCalibration(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (App.Current.Store.Current.Calibration is not null)
        {
            if (MessageBox.Show(owner, Strings.Calib_ConfirmStop, Strings.Common_BatteryCalibration, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                App.Current.Hardware.CancelCalibration();
            }
        }
        else if (MessageBox.Show(owner, Strings.Calib_Confirm, Strings.Common_BatteryCalibration, MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK
                 && App.Current.Hardware.StartCalibration(fast: false) is { } error)
        {
            MessageBox.Show(owner, error, Strings.Common_BatteryCalibration, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshState();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), Strings.Graphics_RestartToSwitch, Strings.Common_Graphics, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            FirmwareGpuSwitch.ShutDownToApply();
        }
    }

    private void OnNotice(object sender, RoutedEventArgs e) => _noticeAction?.Invoke();

    private void OnExpert(object sender, RoutedEventArgs e) => ExpertRequested?.Invoke();
}
