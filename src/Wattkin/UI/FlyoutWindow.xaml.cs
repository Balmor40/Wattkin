using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.UI;

public partial class FlyoutWindow : Window
{
    private static readonly int[] LimitChoices = [60, 70, 80, 90, 100];
    private readonly AutoOffDelayStepper _autoOffDelay = new();
    private ColorSwatches? _keyboardColors;
    private bool _updating;

    private DateTime _hiddenAtUtc;

    private Rect _anchorArea;
    private bool _discarded;

    public FlyoutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(new WindowInteropHelper(this).Handle, toolWindow: true);
        Deactivated += (_, _) => HideFlyout();
        ProfilePanel.Picked += profile => ProfileDetails.Text = Fmt.ProfileDetails(profile);

        // Alt+F4 hides instead of destroying the one flyout the app reuses (until it's discarded).
        Closing += (_, e) =>
        {
            if (!_discarded)
            {
                e.Cancel = true;
                HideFlyout();
            }
        };

        // Release live updates however the window got hidden.
        IsVisibleChanged += (_, _) =>
        {
            App.Current.Monitor.Demand(this, fast: IsVisible);
            if (IsVisible)
            {
                RefreshControls();
            }
            else
            {
                _hiddenAtUtc = DateTime.UtcNow;
                MemoryTrim.Soon();
            }
        };

        // Content can grow after showing (e.g. a banner appears); keep the bottom edge anchored.
        SizeChanged += (_, _) => Anchor();
        App.Current.Hardware.StateChanged += OnChanged;
        App.Current.Store.Changed += OnChanged;
        BuildStaticSegments();
    }

    /// <summary>Closes it for good: the app builds a new one (in another language).</summary>
    public void Discard()
    {
        _discarded = true;
        App.Current.Hardware.StateChanged -= OnChanged;
        App.Current.Store.Changed -= OnChanged;
        Close();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(RefreshIfVisible);

    private void RefreshIfVisible()
    {
        if (IsVisible)
        {
            RefreshControls();
        }
    }

    public void ShowNearTray()
    {
        // A click on the tray icon right after the flyout lost focus would otherwise re-open it.
        if (DateTime.UtcNow - _hiddenAtUtc < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        RefreshControls();
        Opacity = 0;
        Show();
        if (App.Current.Monitor.Latest is { } latest)
        {
            Update(latest);
        }

        _anchorArea = TrayWorkArea();
        MaxHeight = Math.Max(300, _anchorArea.Height - 24);
        UpdateLayout();
        Anchor();
        Opacity = 1;
        Activate();
    }

    public void HideFlyout()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Bottom-right (or next to the taskbar wherever it is) of the monitor the cursor is on, in this window's DIPs.</summary>
    private Rect TrayWorkArea()
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        return new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY);
    }

    private void Anchor()
    {
        if (!IsVisible || _anchorArea.IsEmpty)
        {
            return;
        }

        const double gap = 12;
        Left = Math.Max(_anchorArea.Left + gap, _anchorArea.Right - ActualWidth - gap);
        Top = Math.Max(_anchorArea.Top + gap, _anchorArea.Bottom - ActualHeight - gap);
    }

    public void Update(Snapshot s)
    {
        if (!IsVisible)
        {
            return;
        }

        var app = App.Current;
        var p = s.Power;
        var info = app.Monitor.Info;
        ModelText.Text = info?.Model.Replace("MSI ", "") ?? "";

        WattsText.Text = Fmt.WattsNumber(p.WattsNow);
        WattsCaption.Text = p.Source switch
        {
            PowerSource.BatteryMeasured => Strings.Common_OnBattery,
            PowerSource.AcEstimated => Strings.Flyout_OnChargerEstimated,
            PowerSource.AcCpuOnly => Strings.Flyout_OnChargerCpuOnly,
            _ => "",
        };
        PercentText.Text = Fmt.Percent(p.Percent);
        BatteryLine.Text = p switch
        {
            { Percent: null } => "",
            { Source: PowerSource.BatteryMeasured, TimeToEmpty: { } left } => string.Format(Strings.Fmt_TimeLeft, Fmt.Duration(Fmt.Steady(left))),
            { Source: PowerSource.BatteryMeasured } => Strings.Fmt_Estimating,
            { Charging: true, TimeToTarget: { } full } => string.Format(Strings.Flyout_ChargingFor, Fmt.Duration(Fmt.Steady(full))),
            { Charging: true } => Strings.Fmt_Charging,
            { UnpluggedRuntimeEstimate: { } est } => string.Format(Strings.Flyout_OnBatteryEstimate, Fmt.Duration(Fmt.Steady(est))),
            _ => Strings.Fmt_PluggedIn,
        };
        HeadlineDetails.Text = HeadlineTooltip(s, app);
        Spark.SetPoints(app.History.Snapshot());

        // CPU: usage on the left; package watts, clocks and temperature on the right, inside the bar.
        var cpu = s.Cpu;
        var temp = s.Sensors?.CpuTempC is { } t ? Fmt.Temp(t) : Fmt.Temp(s.ThermalZoneC);
        CpuBar.Set(cpu.UsagePercent / 100, Fmt.Percent(cpu.UsagePercent), Fmt.Join(p.CpuPackageWatts is { } pkg ? Fmt.Watts(pkg) : null, Fmt.GHz(cpu.AverageMHz), temp));

        // Fans: the faster one, against its measured top speed (or the fastest it has been seen to spin).
        var settings = app.Store.Current;
        if (s.Sensors is { } sensors && (sensors.Fan1Rpm is not null || sensors.Fan2Rpm is not null))
        {
            var (rpm, max) = (sensors.Fan1Rpm ?? 0) >= (sensors.Fan2Rpm ?? 0) ? (sensors.Fan1Rpm ?? 0, settings.Fan1MaxRpm) : (sensors.Fan2Rpm ?? 0, settings.Fan2MaxRpm);
            FanBar.Set(rpm / (double)Math.Max(max ?? 0, MinimumFanMax), rpm > 0 ? string.Format(Strings.Unit_Rpm, Fmt.Rpm(rpm)) : Strings.Flyout_FansStopped);
        }
        else
        {
            FanBar.Set(null, Elevation.IsElevated ? "—" : Strings.Flyout_NeedsAdmin);
        }

        var mem = s.Memory;
        RamBar.Set(mem.LoadPercent / 100.0, string.Format(Strings.Flyout_RamUsed, Fmt.Bytes(mem.UsedBytes), Fmt.Bytes(mem.TotalBytes)), Fmt.Percent(mem.LoadPercent));

        var read = s.Disks.Sum(d => d.ReadBytesPerSec);
        var write = s.Disks.Sum(d => d.WriteBytesPerSec);
        var busy = s.Disks.Count > 0 ? s.Disks.Max(d => d.ActivePercent) : 0;
        DiskBar.Set(busy / 100, $"↓ {Fmt.Rate(read)}   ↑ {Fmt.Rate(write)}", Fmt.Percent(busy));

        UpdateGpus(s);
        ChargeStatus.Text = app.Hardware.State.Calibration?.Message ?? "";
        ChargeStatus.Visibility = ChargeStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Integrated GPU first, then the discrete one; the RTX shows its state when it isn't working.</summary>
    private void UpdateGpus(Snapshot s)
    {
        var integrated = s.Gpus.FirstOrDefault(g => !g.Adapter.Discrete);
        var discrete = s.Gpus.FirstOrDefault(g => g.Adapter.Discrete);
        Gpu1Label.Text = integrated?.Adapter.ShortName ?? "iGPU";
        Gpu1Bar.Set(integrated?.Percent / 100, integrated is null ? "—" : Fmt.Percent(integrated.Percent), integrated?.Watts is { } igpuWatts ? Fmt.Watts(igpuWatts) : "");

        var hasDiscrete = s.Dgpu.State != DgpuState.None;
        Gpu2Label.Visibility = Gpu2Bar.Visibility = hasDiscrete ? Visibility.Visible : Visibility.Collapsed;
        Gpu1Bar.Margin = new Thickness(0, 0, 0, hasDiscrete ? 6 : 0);
        Gpu1Label.Margin = new Thickness(0, 0, 10, Gpu1Bar.Margin.Bottom);
        Gpu2Label.Text = (discrete?.Adapter.Name ?? s.Dgpu.Name ?? "").Contains("RTX", StringComparison.OrdinalIgnoreCase) ? "RTX" : "dGPU";
        var gpuTemp = s.Sensors?.GpuTempC is { } gt ? Fmt.Temp(gt) : null;
        switch (s.Dgpu.State)
        {
            case DgpuState.Active:
                Gpu2Bar.Set((discrete?.Percent ?? 0) / 100, Fmt.Percent(discrete?.Percent ?? 0), Fmt.Join(discrete?.Watts is { } w ? Fmt.Watts(w) : null, gpuTemp));
                break;
            case DgpuState.Sleeping:
                Gpu2Bar.Set(0, Strings.Fmt_DgpuAsleep);
                break;
            case DgpuState.RemovedByFirmware:
                Gpu2Bar.Set(0, Strings.Flyout_DgpuOffIntegrated);
                break;
            case DgpuState.DisabledInWindows:
                Gpu2Bar.Set(0, Strings.Fmt_DgpuDisabled);
                break;
            default:
                Gpu2Bar.Set(discrete?.Percent / 100, discrete is null ? "—" : Fmt.Percent(discrete.Percent));
                break;
        }
    }

    /// <summary>The detail the headline no longer shows: where the number comes from and how it splits.</summary>
    private static string HeadlineTooltip(Snapshot s, App app)
    {
        var p = s.Power;
        var lines = new List<string> { Fmt.Sentence(Fmt.PowerHeadline(p)) };
        if (p.CpuPackageWatts is { } pkg)
        {
            lines.Add(string.Format(Strings.Flyout_TipCpu, Fmt.Watts(pkg), Fmt.Watts(s.Cpu.CoresWatts), Fmt.Watts(s.Cpu.GraphicsWatts)));
            if (p.Source == PowerSource.BatteryMeasured && p.SystemWatts is { } sys)
            {
                lines.Add(string.Format(Strings.Flyout_TipRest, Fmt.Watts(Math.Max(0, sys - pkg))));
            }
        }

        if (p.AverageWatts is { } avg)
        {
            lines.Add(string.Format(Strings.Flyout_TipAverage, Fmt.Watts(avg)));
        }

        if (p.Session is { AverageWatts: { } sessionAvg } session)
        {
            lines.Add(string.Format(Strings.Flyout_TipSession, Fmt.Duration(session.Duration), Fmt.Watts(sessionAvg)));
        }

        var status = app.Hardware.State.ChargeStatus;
        if (status.Length > 0)
        {
            lines.Add(status);
        }

        return string.Join("\n", lines.Where(l => l.Length > 0));
    }

    /// <summary>Until a fan's top speed is measured (or seen), treat this as its top speed (a typical laptop maximum).</summary>
    private const int MinimumFanMax = 5000;

    private RadioButton Segment(string text, string group, object tag) =>
        new() { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = group, Tag = tag, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(4, 6, 4, 6) };

    private void BuildStaticSegments()
    {
        // 60–100% share the row equally; "Custom…" gets a little more room for its longer label.
        for (var i = 0; i <= LimitChoices.Length; i++)
        {
            LimitPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == LimitChoices.Length ? 1.5 : 1, GridUnitType.Star) });
        }

        for (var i = 0; i < LimitChoices.Length; i++)
        {
            var limit = LimitChoices[i];
            var button = Segment(Fmt.Percent(limit), "limit", limit);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await App.Current.Hardware.SetChargeLimitAsync(limit, null);
                }
            };
            Grid.SetColumn(button, i);
            LimitPanel.Children.Add(button);
        }

        var custom = Segment(Strings.Flyout_CustomLimit, "limit", -1);
        custom.Click += (_, _) => App.Current.OpenSettings("battery", expert: true);
        Grid.SetColumn(custom, LimitChoices.Length);
        LimitPanel.Children.Add(custom);

        var levels = Fmt.KeyboardLevels();
        for (var i = 0; i < levels.Length; i++)
        {
            var level = i;
            var button = Segment(levels[i], "kb", level);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await App.Current.Hardware.SetKeyboardLevelAsync(level);
                }
            };
            KeyboardPanel.Children.Add(button);
        }

        _keyboardColors = new ColorSwatches(KeyboardColorPanel, "flyoutKbColor");
        foreach (var (text, timeout) in new[] { (Strings.Flyout_AutoOffNever, KeyboardTimeout.AlwaysOn), (Strings.Flyout_AutoOffAlways, KeyboardTimeout.AutoOff), (Strings.Common_OnBattery, KeyboardTimeout.AutoOffOnBattery) })
        {
            var button = Segment(text, "kbTimeout", timeout);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await App.Current.Hardware.SetKeyboardTimeoutAsync(timeout);
                }
            };
            KeyboardTimeoutPanel.Children.Add(button);
        }

        AutoOffDelayHost.Child = _autoOffDelay.Control;

        foreach (var (text, mode) in new[] { (Strings.Flyout_GpuIntegrated, FirmwareGpuMode.Integrated), (Strings.Flyout_GpuHybrid, FirmwareGpuMode.Hybrid) })
        {
            var button = Segment(text, "gpu", mode);
            button.Checked += async (_, _) =>
            {
                if (!_updating)
                {
                    await RequestGpuMode(mode);
                }
            };
            GraphicsPanel.Children.Add(button);
        }
    }

    /// <summary>Queues MSI's firmware switch; it takes effect at the next full shutdown or restart.</summary>
    private async Task RequestGpuMode(FirmwareGpuMode mode)
    {
        GraphicsPanel.IsEnabled = false;
        var error = await App.Current.Hardware.RequestGpuModeAsync(mode);
        GraphicsPanel.IsEnabled = true;
        if (error is not null)
        {
            GraphicsHint.Text = error;
        }

        RefreshControls();
    }

    private void OnRestartForGpu(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        if (MessageBox.Show(Strings.Graphics_RestartToSwitch, "Wattkin", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            FirmwareGpuSwitch.ShutDownToApply();
        }
    }
    private void RefreshControls()
    {
        var app = App.Current;
        var settings = app.Store.Current;
        var state = app.Hardware.State;
        _updating = true;
        try
        {
            // Until the EC has been read (needs admin) the real settings are unknown: highlight nothing.
            var known = state.Available && settings.HardwareAdopted;
            ProfilePanel.Refresh(settings, state);
            ProfileDetails.Text = known && settings.FindProfile(settings.ActiveProfile) is { } active ? Fmt.ProfileDetails(active) : "";
            ProfileDetails.Visibility = ProfileDetails.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var isPreset = settings.ChargeResumeBelow is null && LimitChoices.Contains(settings.ChargeLimit) && !settings.FullChargeOnce;
            foreach (RadioButton button in LimitPanel.Children)
            {
                var tag = (int)button.Tag;
                button.IsChecked = known && (isPreset ? tag == settings.ChargeLimit : tag == -1);
                button.IsEnabled = (state.CanWrite && !app.Hardware.IsCalibrating) || tag == -1;
            }

            foreach (RadioButton button in KeyboardPanel.Children)
            {
                button.IsChecked = state.KeyboardLevel == (int)button.Tag;
                button.IsEnabled = state.CanWrite;
            }

            // Colour and the delay only where the light is over USB (auto-off is Wattkin's own there).
            var usbLight = state.Caps?.HasKeyboardColor != false;
            KeyboardColorPanel.Visibility = AutoOffDelayHost.Visibility = usbLight ? Visibility.Visible : Visibility.Collapsed;
            KeyboardColorPanel.IsEnabled = state.CanWrite;
            _keyboardColors?.Show(settings.KeyboardColor);
            KeyboardTimeoutSection.Visibility = state.Caps?.HasKeyboardTimeout != false ? Visibility.Visible : Visibility.Collapsed;
            foreach (RadioButton button in KeyboardTimeoutPanel.Children)
            {
                button.IsChecked = known && (KeyboardTimeout)button.Tag == settings.KeyboardTimeout;
                button.IsEnabled = state.CanWrite;
            }

            _autoOffDelay.Show(settings.KeyboardAutoOffSeconds);
            AutoOffDelayHost.IsEnabled = state.CanWrite && settings.KeyboardTimeout != KeyboardTimeout.AlwaysOn;

            RefreshGraphics(state);

            ShowBanner(state);
            WidgetButton.Foreground = app.IsWidgetVisible ? (System.Windows.Media.Brush)FindResource("Lc.Accent") : (System.Windows.Media.Brush)FindResource("Lc.SubText");
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshGraphics(HardwareState state)
    {
        var gpu = state.Gpu;
        var available = state.Caps?.CanSwitchGpuMode == true && gpu is { SwitchSupported: true, IntegratedSupported: true } && gpu.Current != FirmwareGpuMode.Discrete;
        GraphicsSection.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            return;
        }

        foreach (RadioButton button in GraphicsPanel.Children)
        {
            button.IsChecked = (FirmwareGpuMode)button.Tag == gpu!.Requested;
        }

        // Only something to say while a switch is waiting for the restart.
        GraphicsHint.Text = gpu!.SwitchPending
            ? gpu.Requested == FirmwareGpuMode.Hybrid ? Strings.Graphics_RtxBackAfterRestart : Strings.Graphics_RtxOffAfterRestart
            : "";
        GraphicsHint.Visibility = RestartButton.Visibility = gpu.SwitchPending ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum BannerAction
    {
        None,
        Elevate,
        ShowMsiHelp,
        SaveReport,
    }

    private BannerAction _bannerAction;

    private void ShowBanner(HardwareState state)
    {
        (string? text, _bannerAction) = (Elevation.IsElevated, state) switch
        {
            (false, _) => (Strings.Flyout_BannerNotAdmin, BannerAction.Elevate),
            (_, { MsiSoftwareRunning: true }) => (Strings.Flyout_BannerMsi, BannerAction.ShowMsiHelp),
            // Hardware controls are off: a one-click report is what someone needs to help.
            (_, { Problem: { } problem, CanWrite: false }) => (problem, BannerAction.SaveReport),
            _ => ((string?)null, BannerAction.None),
        };

        Banner.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = text;
        BannerButton.Content = _bannerAction switch
        {
            BannerAction.Elevate => Strings.Common_RestartAsAdmin,
            BannerAction.ShowMsiHelp => Strings.Flyout_ShowMeHow,
            BannerAction.SaveReport => Strings.Flyout_SaveReport,
            _ => null,
        };
        BannerButton.Visibility = _bannerAction == BannerAction.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnBannerAction(object sender, RoutedEventArgs e)
    {
        switch (_bannerAction)
        {
            case BannerAction.Elevate:
                App.Current.RestartElevated();
                break;
            case BannerAction.ShowMsiHelp:
                App.Current.OpenSettings("general");
                break;
            case BannerAction.SaveReport:
                BannerButton.IsEnabled = false;
                BannerButton.Content = Strings.Common_ReadingHardware;
                await SaveReportToDesktopAsync();
                BannerButton.IsEnabled = true;
                BannerButton.Content = Strings.Flyout_SaveReport;
                break;
        }
    }

    /// <summary>Saves the read-only report on the desktop and shows it in Explorer, ready to attach to an issue.</summary>
    private static async Task SaveReportToDesktopAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Wattkin-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        try
        {
            await Task.Run(() => Diagnostics.Run(path));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostics failed", ex);
            MessageBox.Show(ex.Message, Strings.Common_Diagnostics, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    private void OnWidget(object sender, RoutedEventArgs e)
    {
        App.Current.SetWidgetVisible(!App.Current.IsWidgetVisible);
        RefreshControls();
    }
}
