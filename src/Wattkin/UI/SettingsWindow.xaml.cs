using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;
using static Wattkin.UI.Choices;

namespace Wattkin.UI;

public partial class SettingsWindow : Window
{
    private bool _loading;
    private bool _expert;
    private IReadOnlyList<CapacityPoint> _history = [];
    private ColorSwatches? _kbColors;
    private readonly AutoOffDelayStepper _kbDelay = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Simple.ExpertRequested += () => SetView(expert: true, save: true);
        SourceInitialized += (_, _) => WindowEffects.SetDarkTitleBar(new WindowInteropHelper(this).Handle);
        var app = App.Current;
        app.Hardware.StateChanged += OnHardwareChanged;
        app.Hardware.FanTestChanged += OnFanTestChanged;
        app.Store.Changed += OnStoreChanged;
        app.Monitor.Demand(this, fast: true);

        _registersTimer.Tick += async (_, _) => await RefreshRegistersAsync();
        Closed += (_, _) =>
        {
            _registersTimer.Stop();
            app.Hardware.StateChanged -= OnHardwareChanged;
            app.Hardware.FanTestChanged -= OnFanTestChanged;
            app.Store.Changed -= OnStoreChanged;
            app.Monitor.Demand(this, fast: false);
        };

        BuildChoices();
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == Tabs)
            {
                DemandUpdates();
            }
        };
        SetView(app.Store.Current.ExpertView, save: false);
        LoadSettings(full: true);
        RefreshFromState();
        ShowStatus(FanTestText, app.Hardware.FanTestStatus);
        if (app.Monitor.Latest is { } latest)
        {
            Update(latest);
        }

        _ = LoadHistoryAsync();
        _ = RefreshStartupTaskAsync();
        app.Hardware.SafeRefreshStateAsync();
    }

    private void OnHardwareChanged() => Dispatcher.BeginInvoke(RefreshFromState);

    /// <summary>
    /// Live readings while open; per-app CPU only while the System tab (Expert) is showing, and the
    /// controller registers only while the Debug tab is.
    /// </summary>
    private void DemandUpdates()
    {
        App.Current.Monitor.Demand(this, fast: true, processes: _expert && Tabs.SelectedItem is TabItem { Tag: "system" });
        if (_expert && Tabs.SelectedItem is TabItem { Tag: "debug" })
        {
            if (!_registersTimer.IsEnabled)
            {
                _registersTimer.Start();
                _ = RefreshRegistersAsync();
            }
        }
        else
        {
            _registersTimer.Stop();
        }
    }

    /// <summary>Background changes refresh the mirror controls, never an edit in progress.</summary>
    private void OnStoreChanged() => Dispatcher.BeginInvoke(() => LoadSettings(full: false));

    private async Task RefreshStartupTaskAsync()
    {
        var enabled = await Task.Run(StartupTask.IsEnabled);
        _loading = true;
        StartWithWindows.IsChecked = enabled;
        _loading = false;
        Simple.RefreshStartup(enabled);
    }

    public void SelectTab(string? tag)
    {
        if (tag is null)
        {
            return;
        }

        foreach (TabItem item in Tabs.Items)
        {
            if ((string)item.Tag == tag && item.Visibility == Visibility.Visible)
            {
                Tabs.SelectedItem = item;
            }
        }
    }

    /// <summary>Simple: the everyday settings on one page. Expert: every control, in tabs.</summary>
    public void SetView(bool expert, bool save)
    {
        _expert = expert;
        _loading = true;
        try
        {
            (expert ? ExpertViewButton : SimpleViewButton).IsChecked = true;
        }
        finally
        {
            _loading = false;
        }

        Tabs.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        Simple.Visibility = expert ? Visibility.Collapsed : Visibility.Visible;
        DemandUpdates();

        if (save && App.Current.Store.Current.ExpertView != expert)
        {
            App.Current.Store.Update(s => s.ExpertView = expert);
        }
    }

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SetView(ExpertViewButton.IsChecked == true, save: true);
        }
    }

    // ---- choices -----------------------------------------------------------------------------

    private void BuildChoices()
    {
        string[] levels = ["Off", "Low", "Mid", "High"];
        LowThreshold.Items.Add(Item("Off", 0));
        foreach (var value in new[] { 10, 15, 20, 25, 30, 40, 50 })
        {
            LowThreshold.Items.Add(Item($"≤ {value}%", value));
        }

        for (var i = 0; i < levels.Length; i++)
        {
            var level = i;
            var button = new RadioButton { Content = levels[i], Style = (Style)FindResource("Lc.Segment"), GroupName = "kbs", Tag = level };
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardLevelAsync(level);
                }
            };
            KbLevelPanel.Children.Add(button);
        }

        _kbColors = new ColorSwatches(KbColorPanel, "kbc");

        foreach (var (text, force) in new[] { ("Profile", FanForce.None), ("Auto", FanForce.Auto), ("Silent", FanForce.Silent), ("Curve", FanForce.Curve), ("Full speed", FanForce.Full) })
        {
            var button = new RadioButton { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = "fanforce", Tag = force };
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.ForceFansAsync(force);
                }
            };
            FanForcePanel.Children.Add(button);
        }

        KbDelayHost.Child = _kbDelay.Control;
        (string Text, KeyboardTimeout Value)[] timeouts = [("Always on", KeyboardTimeout.AlwaysOn), ("Auto-off", KeyboardTimeout.AutoOff), ("Auto-off on battery only", KeyboardTimeout.AutoOffOnBattery)];
        foreach (var (text, value) in timeouts)
        {
            var button = new RadioButton { Content = text, Style = (Style)FindResource("Lc.Segment"), GroupName = "kbt", Tag = value };
            button.Checked += async (_, _) =>
            {
                if (!_loading)
                {
                    await App.Current.Hardware.SetKeyboardTimeoutAsync(value);
                }
            };
            KbTimeoutPanel.Children.Add(button);
        }
    }

    private string _profileNames = "";
    private bool _limitEdited;

    /// <summary>One line per profile under the buttons: everything it sets, its fans included.</summary>
    private void ShowProfileSummaries(AppSettings s)
    {
        ProfileButtonsPanel.Refresh(s, App.Current.Hardware.State);
        ProfileSummaries.Children.Clear();
        foreach (var p in s.Profiles)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontSize = 12 };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Lc.SubText");
            var name = new Run(p.Name) { FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextElement.ForegroundProperty, "Lc.Text");
            line.Inlines.Add(name);
            line.Inlines.Add(new Run("   " + Fmt.ProfileDetails(p)));
            ProfileSummaries.Children.Add(line);
        }
    }

    /// <param name="full">Also reload the charge-limit sliders (opening, or after the user saved).</param>
    private void LoadSettings(bool full)
    {
        _loading = true;
        try
        {
            var s = App.Current.Store.Current;
            if (full || !_limitEdited)
            {
                StopSlider.Value = s.ChargeLimit;
                CustomResume.IsChecked = s.ChargeResumeBelow is not null;
                ResumeSlider.Value = s.ChargeResumeBelow ?? ChargePolicy.MaxResumeFor(s.ChargeLimit);
                _limitEdited = false;
                UpdateLimitText();
            }

            FullOnce.IsChecked = s.FullChargeOnce;
            Drain.IsChecked = s.DrainToLimit;

            var names = string.Join("|", s.Profiles.Select(p => p.Name));
            if (full || names != _profileNames)
            {
                _profileNames = names;
                foreach (var box in new[] { AcProfile, DcProfile, LowProfile })
                {
                    FillProfiles(box, s.Profiles.Select(p => p.Name));
                }
            }

            ShowProfileSummaries(s);
            AutoSwitch.IsChecked = s.AutoSwitchProfiles;
            Select(AcProfile, s.AcProfile ?? "");
            Select(DcProfile, s.BatteryProfile ?? "");
            Select(LowProfile, s.LowBatteryProfile ?? "");
            Select(LowThreshold, s.LowBatteryThreshold);
            DcLabel.Text = s.LowBatteryThreshold > 0 ? $"On battery, above {s.LowBatteryThreshold}%" : "On battery";
            LowProfile.IsEnabled = s.LowBatteryThreshold > 0;

            foreach (RadioButton b in KbTimeoutPanel.Children)
            {
                b.IsChecked = (KeyboardTimeout)b.Tag == s.KeyboardTimeout;
            }

            _kbColors?.Show(s.KeyboardColor);
            _kbDelay.Show(s.KeyboardAutoOffSeconds);
            KbDelayHost.IsEnabled = s.KeyboardTimeout != KeyboardTimeout.AlwaysOn;
            Hotkeys.IsChecked = s.HandleHotkeys;
            Osd.IsChecked = s.ShowOsd;
            MuteLeds.IsChecked = s.SyncMuteLeds;

            TrayWatts.IsChecked = s.TrayShowsWatts;
            WidgetToggle.IsChecked = App.Current.IsWidgetVisible;
            BatteryLogToggle.IsChecked = s.BatteryLogging;
            CalibrationFast.IsChecked = s.Calibration?.FastDischarge ?? false;

            Simple.Load();
            CalibrationHistory.Text = s.CalibrationHistory.Count == 0
                ? "No calibrations yet."
                : "Past runs:\n" + string.Join("\n", s.CalibrationHistory.Take(5).Select(r =>
                    $"{r.StartedUtc.ToLocalTime():d MMM yyyy HH:mm}  {r.Phase}  {r.FullChargeBeforeMWh / 1000.0:0.0} Wh → {(r.FullChargeAfterMWh is { } a ? $"{a / 1000.0:0.0} Wh" : "?")}  {r.Outcome}"));
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- live updates ----------------------------------------------------------------------------

    public void Update(Snapshot s)
    {
        var app = App.Current;
        var info = app.Monitor.BatteryInfo;
        if (info is not null)
        {
            DesignText.Text = $"{info.DesignedCapacityMWh / 1000.0:0.0} Wh";
            FullText.Text = $"{info.FullChargedCapacityMWh / 1000.0:0.0} Wh";
            HealthText.Text = info.DesignedCapacityMWh > 0 ? $"{100.0 * info.FullChargedCapacityMWh / info.DesignedCapacityMWh:0.0}%" : "—";
            CyclesText.Text = info.CycleCount > 0 ? info.CycleCount.ToString() : "not reported";
            var p = s.Power;
            BatteryDetails.Text = $"{info.Manufacturer ?? "?"} {info.DeviceName ?? ""} · {info.Chemistry} · now {Fmt.Percent(p.Percent, "0.0")} ({p.RemainingWh:0.0} Wh)"
                + (p.BatteryRateWatts switch
                {
                    > 0.05 and var r => $" · charging {r:0.0} W",
                    < -0.05 and var r => $" · discharging {-r:0.0} W",
                    not null => " · not charging",
                    _ => "",
                })
                + (s.BatteryTempC is { } bt ? $" · {bt:0}°C" : "");
        }

        RefreshCalibration();
        UpdateLive(s);
        DgpuNow.Text = (s.Dgpu.Name ?? "NVIDIA GPU") + ": " + Fmt.Dgpu(s.Dgpu);
        DgpuExplain.Text = s.Dgpu.State switch
        {
            DgpuState.RemovedByFirmware => "The firmware is in Integrated-only mode, so the RTX is powered off and invisible to Windows. Use 'Switch to Hybrid' below to get it back (needs a restart).",
            DgpuState.DisabledInWindows => "Disabled in Windows. That stops it powering down properly, so it uses more power than when it's simply asleep.",
            DgpuState.Sleeping => "Hybrid mode, idle: powered down, using practically no power. Apps that need it wake it, and it sleeps again afterwards.",
            DgpuState.Active => "Hybrid mode and powered up. Something is using it or just used it. Its power isn't included in the CPU figure.",
            _ => "",
        };
        DisabledCard.Visibility = s.Dgpu.State == DgpuState.DisabledInWindows ? Visibility.Visible : Visibility.Collapsed;
        ReEnableButton.IsEnabled = Elevation.IsElevated;
        UpdateDebug(s);
        Simple.Update(s);
    }

    private void UpdateDebug(Snapshot s)
    {
        var settings = App.Current.Store.Current;
        DebugFansNow.Text = s.Sensors is { } sensors
            ? $"Fan 1 {Fmt.Rpm(sensors.Fan1Rpm),7} rpm   Fan 2 {Fmt.Rpm(sensors.Fan2Rpm),7} rpm   CPU {Fmt.Temp(sensors.CpuTempC)}   GPU {Fmt.Temp(sensors.GpuTempC)}"
            : Elevation.IsElevated ? "Reading…" : "Fan speeds need administrator rights.";
        DebugTopSpeed.Text = settings.FanTopSpeedMeasuredUtc is { } measured
            ? $"Top speed {Fmt.Rpm(settings.Fan1MaxRpm)} / {Fmt.Rpm(settings.Fan2MaxRpm)} rpm, measured {measured.ToLocalTime():d MMM HH:mm}."
            : settings.Fan1MaxRpm is not null || settings.Fan2MaxRpm is not null
                ? $"Top speed not measured yet. Fastest seen: {Fmt.Rpm(settings.Fan1MaxRpm)} / {Fmt.Rpm(settings.Fan2MaxRpm)} rpm."
                : "Top speed not measured yet.";
        ShowStatus(OnBatteryText, App.Current.Hardware.RunningFromBatteryUntilUtc is { } until
            ? $"Running from the battery: {Math.Max(0, (until - DateTime.UtcNow).TotalSeconds):0} s left · {Fmt.Watts(s.Power.WattsNow)} {(s.Power.Source == PowerSource.BatteryMeasured ? "measured" : "(waiting for the battery to report)")}"
            : "");
    }

    /// <summary>A status line takes no room while it has nothing to say.</summary>
    private static void ShowStatus(TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFanTestChanged() => Dispatcher.BeginInvoke(() =>
    {
        ShowStatus(FanTestText, App.Current.Hardware.FanTestStatus);
        RefreshDebugControls(App.Current.Hardware.State);
    });

    /// <summary>The Debug tab's buttons follow what the hardware can do and whether a test is running.</summary>
    private void RefreshDebugControls(HardwareState state)
    {
        var hardware = App.Current.Hardware;
        var fans = state.CanWrite && state.Caps?.HasCoolerBoost == true;
        _loading = true;
        try
        {
            foreach (RadioButton b in FanForcePanel.Children)
            {
                var force = (FanForce)b.Tag;
                b.IsChecked = force == hardware.ForcedFans;
                b.IsEnabled = fans && (!hardware.FanTestRunning || force == FanForce.None);
                b.Visibility = force == FanForce.Curve && state.Caps?.HasFanCurves != true ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            _loading = false;
        }

        MeasureFansButton.IsEnabled = CompareFansButton.IsEnabled = fans && !hardware.FanTestRunning;
        var battery = state.CanWrite && state.Caps?.CanSimulateUnplug == true && !hardware.IsCalibrating;
        RunOnBatteryButton.IsEnabled = battery && hardware.RunningFromBatteryUntilUtc is null;
        StopOnBatteryButton.IsEnabled = hardware.RunningFromBatteryUntilUtc is not null;
        TestLightsButton.IsEnabled = state.CanWrite;
    }

    private readonly System.Windows.Threading.DispatcherTimer _registersTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _readingRegisters;

    private async Task RefreshRegistersAsync()
    {
        if (_readingRegisters)
        {
            return;
        }

        _readingRegisters = true;
        try
        {
            RegistersText.Text = await Task.Run(App.Current.Hardware.ReadRegisters);
        }
        catch (Exception ex)
        {
            Log.Error("Reading registers failed", ex);
        }
        finally
        {
            _readingRegisters = false;
        }
    }

    private async void OnMeasureFans(object sender, RoutedEventArgs e)
    {
        ShowStatus(FanTestText, "Starting…");
        ShowStatus(FanTestText, await App.Current.Hardware.MeasureFanTopSpeedAsync());
    }

    private async void OnCompareFans(object sender, RoutedEventArgs e)
    {
        ShowStatus(FanTestText, "Starting…");
        ShowStatus(FanTestText, await App.Current.Hardware.CompareFanModesAsync());
    }

    private void OnRunOnBattery(object sender, RoutedEventArgs e) => RunOnBattery(TimeSpan.FromMinutes(2));

    private void OnStopOnBattery(object sender, RoutedEventArgs e) => RunOnBattery(TimeSpan.Zero);

    private void RunOnBattery(TimeSpan duration)
    {
        if (App.Current.Hardware.RunFromBattery(duration) is { } error)
        {
            MessageBox.Show(this, error, "Wattkin", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RefreshDebugControls(App.Current.Hardware.State);
    }

    private async void OnTestLights(object sender, RoutedEventArgs e)
    {
        TestLightsButton.IsEnabled = false;
        await App.Current.Hardware.TestLightsAsync();
        TestLightsButton.IsEnabled = true;
    }

    private void OnTestOsd(object sender, RoutedEventArgs e) => App.Current.ShowOsd("Test message from Wattkin");

    private void UpdateLive(Snapshot s)
    {
        var sb = new StringBuilder();
        var p = s.Power;
        sb.AppendLine($"Power      {Fmt.Watts(p.WattsNow)} now, {Fmt.Watts(p.AverageWatts)} average for time estimates · {Fmt.PowerHeadline(p)}");
        sb.AppendLine($"           CPU package {Fmt.Watts(p.CpuPackageWatts)} · cores {Fmt.Watts(s.Cpu.CoresWatts)} · graphics {Fmt.Watts(s.Cpu.GraphicsWatts)}");
        if (App.Current.Monitor.Power.RestOfSystemWatts is { } rest)
        {
            sb.AppendLine($"           learned rest-of-system draw {Fmt.Watts(rest)} ({App.Current.Monitor.Power.RestOfSystemSamples} on-battery samples)");
        }

        sb.AppendLine($"Battery    {Fmt.Percent(p.Percent, "0.0")} · {p.RemainingWh:0.00} / {p.FullWh:0.00} Wh · rate {p.BatteryRateWatts:+0.00;-0.00;0} W");
        sb.AppendLine($"CPU        {s.Cpu.UsagePercent:0}% · avg {Fmt.GHz(s.Cpu.AverageMHz)} · fastest core {Fmt.GHz(s.Cpu.MaxCoreMHz)}"
            + (s.Cpu.PerformanceCoreMHz is { } pc ? $" · P-cores {Fmt.GHz(pc)}" : "")
            + (s.Cpu.EfficiencyCoreMHz is { } ec ? $" · E-cores {Fmt.GHz(ec)}" : ""));
        sb.AppendLine($"Temps      CPU {Fmt.Temp(s.Sensors?.CpuTempC)} · GPU {Fmt.Temp(s.Sensors?.GpuTempC)} · ACPI zone {Fmt.Temp(s.ThermalZoneC)}");
        sb.AppendLine($"Fans       {Fmt.Fans(s.Sensors?.Fan1Rpm, s.Sensors?.Fan2Rpm)}");
        sb.AppendLine($"Memory     {Fmt.Bytes(s.Memory.UsedBytes)} / {Fmt.Bytes(s.Memory.TotalBytes)} ({s.Memory.LoadPercent}%) · committed {Fmt.Bytes(s.Memory.CommitUsedBytes)} / {Fmt.Bytes(s.Memory.CommitLimitBytes)}");
        foreach (var d in s.Disks)
        {
            sb.AppendLine($"{d.Name,-10} read {Fmt.Rate(d.ReadBytesPerSec)} · write {Fmt.Rate(d.WriteBytesPerSec)} · {d.ActivePercent:0}% busy");
        }

        sb.Append($"GPU        {Fmt.Dgpu(s.Dgpu)}");
        LiveText.Text = sb.ToString();
        ProcessText.Text = s.TopProcesses.Count == 0
            ? "Measuring…"
            : string.Join("\n", s.TopProcesses.Take(8).Select(x => $"{(x.Processes > 1 ? $"{x.Name} ({x.Processes})" : x.Name),-34} {x.CpuPercent,5:0.0}%"));

        // Static facts: rebuilt every 10 s at most (display-mode enumeration isn't free).
        if (DateTime.UtcNow - _infoUpdatedUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _infoUpdatedUtc = DateTime.UtcNow;
        var info = App.Current.Monitor.Info;
        var state = App.Current.Hardware.State;
        var battery = App.Current.Monitor.BatteryInfo;
        InfoText.Text =
            $"Model      {info?.Model}\n" +
            $"BIOS       {info?.BiosVersion} · EC {state.EcFirmware ?? "(needs administrator)"} · MSI WMI {state.WmiVersion ?? "?"}\n" +
            $"CPU        {info?.CpuName} · {info?.PerformanceThreads} P-threads + {info?.EfficiencyThreads} E-threads\n" +
            $"Memory     {Fmt.Bytes(info?.InstalledMemoryBytes ?? 0)} {info?.MemoryType} · running {info?.MemoryConfiguredMTs} MT/s (rated {info?.MemoryRatedMTs}) · {info?.MemoryPart}\n" +
            $"Battery    {battery?.Manufacturer} {battery?.DeviceName} · {battery?.Chemistry} · design {battery?.DesignedCapacityMWh / 1000.0:0.0} Wh\n" +
            $"Display    {DisplayModes.CurrentRefreshRate()} Hz (available: {string.Join(", ", DisplayModes.SupportedRefreshRates())})";
    }

    private void RefreshFromState()
    {
        var state = App.Current.Hardware.State;
        _loading = true;
        try
        {
            ChargeStatusText.Text = (state.ChargeStatus, state.EcChargeLimit) switch
            {
                ({ Length: > 0 } status, { } ec) => $"{status} (controller set to {ec}%)",
                (_, { } ec) => $"The laptop's controller is set to stop at {ec}%.",
                (var status, null) => status,
            };
            foreach (RadioButton b in KbLevelPanel.Children)
            {
                b.IsChecked = state.KeyboardLevel == (int)b.Tag;
                b.IsEnabled = state.CanWrite;
            }

            foreach (RadioButton b in KbTimeoutPanel.Children)
            {
                b.IsEnabled = state.CanWrite;
            }

            ProfileButtonsPanel.Refresh(App.Current.Store.Current, state);

            WebcamToggle.IsChecked = state.Webcam;
            WebcamToggle.IsEnabled = state.CanWrite;
            // Null = this model has no EC webcam switch (EC 0x2F bit1 clear), or not connected yet.
            DevicesCard.Visibility = state.Webcam is null ? Visibility.Collapsed : Visibility.Visible;
            var caps = state.Caps;
            KbTimeoutPanel.IsEnabled = caps?.HasKeyboardTimeout != false;
            KbColorSection.Visibility = KbDelaySection.Visibility = caps?.HasKeyboardColor != false ? Visibility.Visible : Visibility.Collapsed;
            KbColorPanel.IsEnabled = KbDelaySection.IsEnabled = state.CanWrite;
            KbTimeoutHint.Text = caps?.HasKeyboardColor == true
                ? "The keyboard controller's own auto-off doesn't reach these keys, so Wattkin does it: the light goes off when the laptop's keyboard hasn't been used for the time above, and comes back on with its next key press. Another keyboard doesn't wake it. Wattkin only notices that a key was pressed, never which one. While Windows is locked the light stays on, so you can see the keys for your password."
                : "Auto-off is built into the keyboard controller: the light goes off after about 10 seconds without typing and comes back on with a key press. The controller turns auto-off back on every boot, so Wattkin re-applies your choice at startup and after sleep.";

            // Calibration owns charging until it finishes or is cancelled.
            var chargeEditable = state.CanWrite && !App.Current.Hardware.IsCalibrating;
            ApplyLimit.IsEnabled = StopSlider.IsEnabled = CustomResume.IsEnabled = chargeEditable;
            FullOnce.IsEnabled = chargeEditable;
            Drain.IsEnabled = chargeEditable && caps?.CanSimulateUnplug == true;
            Drain.Visibility = caps is { CanSimulateUnplug: false } ? Visibility.Collapsed : Visibility.Visible;

            SupportText.Text = caps switch
            {
                null => Elevation.IsElevated ? "Reading the laptop's firmware…" : "Hardware support: needs administrator rights to check.",
                { Support: EcSupport.Verified } => $"Hardware support: verified on this firmware ({caps.MapSource}).",
                { Support: EcSupport.Community } => $"Hardware support: msi-ec register map ({caps.MapSource}), sanity-checked on this laptop at startup. If anything misbehaves, please report it with a diagnostics report.",
                { Support: EcSupport.Standard } => "Hardware support: MSI's standard layout (the registers MSI Center uses on all recent MSI laptops), sanity-checked on this laptop at startup. Please report how it works with a diagnostics report.",
                _ => state.Problem ?? "Hardware support: monitoring only on this firmware.",
            };
            if (state.Gpu is { } gpu)
            {
                FirmwareMode.Text = $"Current: {Describe(gpu.Current)}" + (gpu.SwitchPending ? $" · switching to {Describe(gpu.Requested)} at the next full shutdown" : "");
                ModeHybrid.IsEnabled = state.Caps?.CanSwitchGpuMode == true && gpu.SwitchSupported && gpu.Requested != FirmwareGpuMode.Hybrid;
                ModeIntegrated.IsEnabled = state.Caps?.CanSwitchGpuMode == true && gpu.SwitchSupported && gpu.IntegratedSupported && gpu.Requested != FirmwareGpuMode.Integrated;
            }
            else
            {
                FirmwareMode.Text = state.GpuProblem ?? (Elevation.IsElevated ? "Reading…" : "Needs administrator rights.");
                ModeHybrid.IsEnabled = ModeIntegrated.IsEnabled = false;
            }

            ElevationText.Text = Elevation.IsElevated
                ? "Running as administrator: hardware controls are available."
                : "Running without administrator rights: monitoring only.";
            ElevateButton.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
            MsiStatus.Text = state.MsiSoftwareRunning
                ? "MSI Center components are running right now. They can overwrite Wattkin's settings."
                : "No MSI Center components are running.";
            AboutText.Text = $"Wattkin {typeof(App).Assembly.GetName().Version?.ToString(3)} · EC {state.EcFirmware ?? "?"} · settings: {Fmt.FriendlyPath(App.Current.Store.FilePath)}";
        }
        finally
        {
            _loading = false;
        }

        RefreshCalibration();
        UpdateCalibrationSuggestion();
        RefreshDebugControls(state);
        Simple.RefreshState();
    }

    private static string Describe(FirmwareGpuMode mode) => mode switch
    {
        FirmwareGpuMode.Hybrid => "Hybrid (RTX available, sleeps when idle)",
        FirmwareGpuMode.Integrated => "Integrated only (RTX powered off)",
        _ => "Discrete only",
    };

    private void RefreshCalibration()
    {
        var state = App.Current.Hardware.State;
        var running = App.Current.Store.Current.Calibration is not null;
        CalibrationStart.IsEnabled = state.CanWrite && !running && state.Caps?.CanSimulateUnplug == true;
        CalibrationCancel.IsEnabled = running;
        if (state.Calibration is { } c && running)
        {
            CalibrationStatus.Text = c.Message;
            var percent = c.Percent ?? 0;
            CalibrationBar.Value = c.Phase == CalibrationPhase.Discharging ? Math.Clamp((100 - percent) / (100 - c.TargetPercent) * 50 + 50, 50, 100) : Math.Clamp(percent / 2, 0, 50);
            CalibrationEta.Text = $"Running for {Fmt.Duration(c.Elapsed)} · this step ~{Fmt.Duration(c.PhaseEta)} · finished in ~{Fmt.Duration(c.TotalEta)}";
        }
        else if (running)
        {
            CalibrationStatus.Text = "Starting…";
        }
        else
        {
            CalibrationStatus.Text = state.Calibration?.Message ?? "Not running.";
            CalibrationBar.Value = 0;
            CalibrationEta.Text = App.Current.Monitor.Latest is { } s && s.Power.FullWh is { } full
                ? $"Rough time on this laptop: charging to full ~{Fmt.Duration(TimeSpan.FromHours(full * (1 - ((s.Power.Percent ?? 50) / 100)) / 30 * 1.25))}, then discharging ~{Fmt.Duration(TimeSpan.FromHours(full * 0.92 / 8))} (or ~{Fmt.Duration(TimeSpan.FromHours(full * 0.92 / 20))} with faster discharge)."
                : "";
        }
    }

    private DateTime _infoUpdatedUtc = DateTime.MinValue;

    private async Task LoadHistoryAsync()
    {
        try
        {
            _history = await App.Current.BatteryLog.LoadCapacityHistoryAsync(App.Current.Store.Current.BatteryChangedUtc?.ToLocalTime());
            Chart.SetPoints(_history);
            UpdateCalibrationSuggestion();
        }
        catch (Exception ex)
        {
            Log.Error("Loading capacity history failed", ex);
        }
    }

    /// <summary>Suggests a calibration when the capacity reading fell well below its recent level.</summary>
    private void UpdateCalibrationSuggestion()
    {
        var app = App.Current;
        var settings = app.Store.Current;
        var full = app.Monitor.BatteryInfo is { FullChargedCapacityMWh: > 0 } info ? info.FullChargedCapacityMWh / 1000.0 : (double?)null;
        // Only a run that finished re-measured the gauge; cancelled or failed runs don't count.
        var lastCalibrated = settings.CalibrationHistory
            .Where(r => r.Phase == nameof(CalibrationPhase.Done) && r.FinishedUtc is not null)
            .Select(r => r.FinishedUtc)
            .Max();
        var advice = settings.Calibration is null && app.Hardware.State.Caps?.CanSimulateUnplug == true
            ? CalibrationAdvice.Check(_history, full, lastCalibrated, DateTime.Today)
            : null;
        Simple.SetCalibrationSuggestion(advice);
        CalibrationSuggestion.Visibility = advice is null ? Visibility.Collapsed : Visibility.Visible;
        if (advice is not null)
        {
            CalibrationSuggestionText.Text = $"The battery reports {advice.NowWh:0.0} Wh, down from about {advice.TypicalWh:0} Wh in the last two months. A drop this quick is usually the gauge drifting, not real wear. A calibration re-measures it: a few hours with the charger plugged in.";
        }
    }

    // ---- battery handlers ---------------------------------------------------------------------

    private void OnLimitSliders(object sender, RoutedEventArgs e)
    {
        // Sliders raise ValueChanged while the XAML is still being loaded.
        if (StopSlider is null || StopValue is null || ResumeSlider is null || ResumeValue is null || CustomResume is null || LimitExplain is null)
        {
            return;
        }

        if (!_loading)
        {
            _limitEdited = true; // the user is editing: background refreshes leave these alone
        }

        // The EC's own gap (stop − 10) is the narrowest band; a custom resume point can only be lower.
        var maxResume = ChargePolicy.MaxResumeFor((int)Math.Round(StopSlider.Value));
        ResumeSlider.Maximum = Math.Max(ResumeSlider.Minimum, maxResume);
        ResumeSlider.IsEnabled = CustomResume.IsChecked == true;
        if (CustomResume.IsChecked != true)
        {
            ResumeSlider.Value = Math.Max(ResumeSlider.Minimum, maxResume);
        }

        UpdateLimitText();
    }

    private void UpdateLimitText()
    {
        var stop = (int)Math.Round(StopSlider.Value);
        var resume = (int)Math.Round(ResumeSlider.Value);
        StopValue.Text = $"{stop}%";
        ResumeValue.Text = $"{resume}%";
        LimitExplain.Text = CustomResume.IsChecked == true && resume < ChargePolicy.MaxResumeFor(stop)
            ? $"Charging stops at {stop}% and starts again below {resume}%. The controller's own gap is 10%, so Wattkin widens it to a {resume}–{stop}% band. If Wattkin isn't running, the controller keeps the last value it was given, which is never above {stop}%."
            : $"Charging stops at {stop}% and starts again below {ChargePolicy.MaxResumeFor(stop)}%. The embedded controller does this by itself, even if Wattkin is closed or uninstalled.";
    }

    private async void OnApplyLimit(object sender, RoutedEventArgs e)
    {
        var stop = (int)Math.Round(StopSlider.Value);
        int? resume = CustomResume.IsChecked == true ? (int)Math.Round(ResumeSlider.Value) : null;
        _limitEdited = false;
        await App.Current.Hardware.SetChargeLimitAsync(stop, resume);
    }

    private async void OnFullOnce(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetFullChargeOnceAsync(FullOnce.IsChecked == true);

    private async void OnDrain(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetDrainToLimitAsync(Drain.IsChecked == true);

    private void OnCalibrationStart(object sender, RoutedEventArgs e)
    {
        var fast = CalibrationFast.IsChecked == true;
        if (MessageBox.Show(this,
                "Calibration will:\n\n1. Charge the battery to 100%.\n2. Discharge it to about 8% while the charger stays plugged in.\n3. Hand back to your normal charge limit, which recharges it.\n\nKeep the charger connected the whole time. The laptop won't sleep while this runs. You can keep using it, and you can cancel any time.\n\nStart now?",
                "Battery calibration", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var error = App.Current.Hardware.StartCalibration(fast);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Battery calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshCalibration();
    }

    private void OnCalibrationCancel(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop the calibration? Your normal charge limit takes over again.", "Battery calibration", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            App.Current.Hardware.CancelCalibration();
            RefreshCalibration();
        }
    }

    private void OnCalibrationFast(object sender, RoutedEventArgs e) => App.Current.Hardware.SetCalibrationFast(CalibrationFast.IsChecked == true);

    private void OnOpenBatteryLog(object sender, RoutedEventArgs e) => OpenFolder(App.Current.BatteryLog.Directory);

    private async void OnRefreshHistory(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private void OnAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.AutoSwitchProfiles = AutoSwitch.IsChecked == true;
            s.AcProfile = ProfileOf(AcProfile);
            s.BatteryProfile = ProfileOf(DcProfile);
            s.LowBatteryProfile = ProfileOf(LowProfile);
            s.LowBatteryThreshold = TagOf<int>(LowThreshold);
        });
    }

    private void OnRestoreWindowsPower(object sender, RoutedEventArgs e)
    {
        if (!App.Current.Hardware.ControlsHardware)
        {
            return;
        }

        App.Current.Store.Update(WindowsPower.RestoreOriginals);
        MessageBox.Show(this, "Processor turbo and max-state settings are back to what they were before Wattkin changed them.", "Wattkin");
    }

    // ---- keyboard & devices -------------------------------------------------------------------

    private void OnKeysChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.HandleHotkeys = Hotkeys.IsChecked == true;
            s.ShowOsd = Osd.IsChecked == true;
            s.SyncMuteLeds = MuteLeds.IsChecked == true;
        });
    }

    private async void OnWebcam(object sender, RoutedEventArgs e) => await App.Current.Hardware.SetWebcamAsync(WebcamToggle.IsChecked == true);

    // ---- graphics -----------------------------------------------------------------------------

    private async void OnReEnable(object sender, RoutedEventArgs e)
    {
        ReEnableButton.IsEnabled = false;
        await ReEnableAsync();
    }

    private async Task ReEnableAsync()
    {
        var error = await App.Current.Hardware.ReEnableDgpuAsync();
        if (error is not null)
        {
            MessageBox.Show(this, error, "NVIDIA GPU", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void OnModeHybrid(object sender, RoutedEventArgs e) => await SwitchModeSafe(FirmwareGpuMode.Hybrid);

    private async void OnModeIntegrated(object sender, RoutedEventArgs e) => await SwitchModeSafe(FirmwareGpuMode.Integrated);

    private async Task SwitchModeSafe(FirmwareGpuMode mode)
    {
        ModeHybrid.IsEnabled = ModeIntegrated.IsEnabled = false;
        try
        {
            await SwitchMode(mode);
        }
        catch (Exception ex)
        {
            Log.Error("GPU mode switch failed", ex);
            MessageBox.Show(this, $"The switch failed: {ex.Message}", "GPU mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshFromState();
        }
    }

    private async Task SwitchMode(FirmwareGpuMode mode)
    {
        var description = Describe(mode);
        if (MessageBox.Show(this,
                $"Switch the GPU to {description}?\n\nThis is the same firmware switch MSI Center uses. It takes effect after the laptop fully shuts down and starts again.",
                "GPU mode", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var error = await App.Current.Hardware.RequestGpuModeAsync(mode);
        if (error is not null)
        {
            MessageBox.Show(this, error, "GPU mode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Switch armed. Shut down now to apply it?\n\nSave your work first. MSI's method powers the laptop off and normally starts it again by itself. If it stays off, press the power button.\n\nChoose No to apply it at your next full shutdown.",
                "GPU mode", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            FirmwareGpuSwitch.ShutDownToApply();
        }
    }

    // ---- general ------------------------------------------------------------------------------

    private async void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var wanted = StartWithWindows.IsChecked == true;
        if (!App.Current.Hardware.ControlsHardware)
        {
            await RefreshStartupTaskAsync();
            return;
        }

        if (!Elevation.IsElevated)
        {
            await RefreshStartupTaskAsync();
            if (MessageBox.Show(this, "Setting up start-with-Windows needs administrator rights. Restart Wattkin as administrator now?", "Wattkin", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                App.Current.RestartElevated();
            }

            return;
        }

        StartWithWindows.IsEnabled = false;
        var error = await Task.Run(() => wanted ? StartupTask.Enable() : StartupTask.Disable());
        StartWithWindows.IsEnabled = true;
        if (error is not null)
        {
            MessageBox.Show(this, error, "Start with Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshStartupTaskAsync();
    }

    private void OnGeneralChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Current.Store.Update(s =>
        {
            s.TrayShowsWatts = TrayWatts.IsChecked == true;
            s.BatteryLogging = BatteryLogToggle.IsChecked == true;
        });
    }

    private void OnWidgetToggle(object sender, RoutedEventArgs e) => App.Current.SetWidgetVisible(WidgetToggle.IsChecked == true);

    private void OnElevate(object sender, RoutedEventArgs e) => App.Current.RestartElevated();

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.FilePath))
        {
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        else
        {
            OpenFolder(Log.Directory);
        }
    }

    private async void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"Wattkin-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "Text|*.txt" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DiagnosticsButton.IsEnabled = false;
        DiagnosticsButton.Content = "Reading the hardware…";
        try
        {
            var path = dialog.FileName;
            await Task.Run(() => Diagnostics.Run(path)); // reads hardware for ~15 s: not on the UI thread
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostics failed", ex);
            MessageBox.Show(this, ex.Message, "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            DiagnosticsButton.IsEnabled = true;
            DiagnosticsButton.Content = "Save diagnostics report";
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
