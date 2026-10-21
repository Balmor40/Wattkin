using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Controls;
using Wattkin.Monitoring;
using Wattkin.Native;
using Wattkin.Settings;
using Forms = System.Windows.Forms;

namespace Wattkin.UI;

/// <summary>
/// Tray icon that shows a reading as the icon itself (the laptop's current watts, the CPU temperature
/// or the CPU load; see <see cref="AppSettings.TrayDisplay"/>), so the number is visible at a glance
/// without opening anything. Left click: flyout. Right click: quick menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _staticIcon;
    private IntPtr _currentHandle;
    private string _currentText = "";
    private string _currentTooltip = "";
    private bool _currentLight;
    private bool _taskbarLight = TaskbarIsLight();
    private TrayReading? _shown;
    private ContextMenu? _menu;

    public TrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Wattkin.ico"));
        _staticIcon = resource is null ? SystemIcons.Application : new Icon(resource.Stream, SmallIconSize());
        _icon = new Forms.NotifyIcon { Icon = _staticIcon, Text = "Wattkin", Visible = true };
        _icon.MouseUp += OnMouseUp;
        App.Current.PaletteChanged += () => _taskbarLight = TaskbarIsLight();
    }

    public void Update(Snapshot snapshot)
    {
        var settings = App.Current.Store.Current;
        var hardware = App.Current.Hardware.State;
        var reading = TrayReading.From(settings.TrayDisplay, snapshot)?.Settle(_shown);
        _shown = reading;

        // The tooltip's numbers match the icon's: the settled reading, with its decimal.
        var watts = reading is { Display: TrayDisplay.Watts } shownWatts ? shownWatts.Value : snapshot.Power.WattsNow;
        var tooltip = $"Wattkin · {Fmt.Watts(watts)}";
        if (snapshot.Power.Percent is { } percent)
        {
            tooltip += $" · {percent:0}%";
        }

        tooltip += $"\n{Fmt.BatteryLine(snapshot.Power, hardware.ChargeStatus, coarse: true)}";
        tooltip += reading switch
        {
            { Display: TrayDisplay.CpuTemperature } t => $"\nCPU {Fmt.Temp(t.Value)}",
            { Display: TrayDisplay.CpuLoad } l => $"\nCPU load {l.Value:0}%",
            _ => "",
        };
        tooltip += $"\nProfile: {settings.ActiveProfile}";
        tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (tooltip != _currentTooltip)
        {
            _currentTooltip = tooltip;
            _icon.Text = tooltip;
        }

        SetIcon(reading);
    }

    private void SetIcon(TrayReading? reading)
    {
        var light = _taskbarLight;
        var text = reading is { } r ? r.Number + r.Unit : "";
        if (text == _currentText && light == _currentLight)
        {
            return;
        }

        _currentText = text;
        _currentLight = light;
        if (reading is null)
        {
            _icon.Icon = _staticIcon;
            ReleaseHandle();
            return;
        }

        using var bitmap = Render(reading.Value.Number, reading.Value.Unit, SmallIconSize(), light);
        var handle = bitmap.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        ReleaseHandle();
        _currentHandle = handle;
    }

    /// <summary>
    /// The tray number with its unit, as large as fits the icon: a small "W" or "%" sharing the digits'
    /// baseline ("17W", "45%"), or a degree sign level with their top ("72°"). Built from glyph outlines
    /// so the parts line up exactly at 16–32 px.
    /// </summary>
    internal static Bitmap Render(string number, string unit, System.Drawing.Size size, bool light)
    {
        var bitmap = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var brush = new SolidBrush(light ? Color.FromArgb(20, 20, 20) : Color.White);
        using var family = new FontFamily("Segoe UI");
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        var ascent = family.GetCellAscent(System.Drawing.FontStyle.Bold) / (float)family.GetEmHeight(System.Drawing.FontStyle.Bold);
        var raised = unit == "°";
        var scale = raised ? 0.8f : number.Length >= 3 ? 0.5f : 0.58f; // relative size of the unit
        var gap = size.Width / 32f;

        for (var em = size.Height * 1.25f; em > 5; em -= 0.25f)
        {
            using var digits = new GraphicsPath();
            digits.AddString(number, family, (int)System.Drawing.FontStyle.Bold, em, new PointF(0, 0), format);
            var d = digits.GetBounds();

            using var u = new GraphicsPath();
            var uEm = em * scale;
            // Same baseline: the unit's em box starts lower by the difference in ascent.
            u.AddString(unit, family, (int)System.Drawing.FontStyle.Bold, uEm, new PointF(0, (ascent * em) - (ascent * uEm)), format);
            var ub = u.GetBounds();

            var width = d.Width + gap + ub.Width;
            if (width > size.Width + 0.5f || d.Height > size.Height)
            {
                continue;
            }

            // Centre the pair; vertically centre the digits (they're taller than the unit).
            var dx = ((size.Width - width) / 2f) - d.Left;
            var dy = ((size.Height - d.Height) / 2f) - d.Top;
            using (var move = new Matrix())
            {
                move.Translate(dx, dy);
                digits.Transform(move);
            }

            using (var move = new Matrix())
            {
                move.Translate(dx + d.Right + gap - ub.Left, raised ? dy + d.Top - ub.Top : dy);
                u.Transform(move);
            }

            g.FillPath(brush, digits);
            g.FillPath(brush, u);
            break;
        }

        return bitmap;
    }

    private void ReleaseHandle()
    {
        if (_currentHandle != IntPtr.Zero)
        {
            User32.DestroyIcon(_currentHandle);
            _currentHandle = IntPtr.Zero;
        }
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        try
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                App.Current.ToggleFlyout();
            }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                ShowMenu();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray click failed", ex);
        }
    }

    private void ShowMenu()
    {
        var app = App.Current;
        _menu = new ContextMenu();

        var profiles = new MenuItem { Header = "Profile" };
        foreach (var profile in app.Store.Current.Profiles)
        {
            var item = new MenuItem { Header = profile.Name, IsCheckable = true, IsChecked = profile.Name == app.Store.Current.ActiveProfile };
            var name = profile.Name;
            item.Click += async (_, _) => await app.Hardware.ApplyProfileAsync(name);
            profiles.Items.Add(item);
        }

        var limits = new MenuItem { Header = "Charge limit", IsEnabled = !app.Hardware.IsCalibrating };
        foreach (var limit in new[] { 50, 60, 70, 80, 90, 100 })
        {
            var item = new MenuItem { Header = $"{limit}%", IsCheckable = true, IsChecked = app.Store.Current.ChargeLimit == limit && app.Store.Current.ChargeResumeBelow is null };
            item.Click += async (_, _) => await app.Hardware.SetChargeLimitAsync(limit, null);
            limits.Items.Add(item);
        }

        var full = new MenuItem { Header = "Charge to 100% once", IsCheckable = true, IsChecked = app.Store.Current.FullChargeOnce };
        full.Click += async (_, _) => await app.Hardware.SetFullChargeOnceAsync(full.IsChecked);
        limits.Items.Add(new Separator());
        limits.Items.Add(full);

        var widget = new MenuItem { Header = "Desktop widget", IsCheckable = true, IsChecked = app.IsWidgetVisible };
        widget.Click += (_, _) => app.SetWidgetVisible(widget.IsChecked);

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => app.OpenSettings();

        var exit = new MenuItem { Header = "Exit Wattkin" };
        exit.Click += (_, _) => app.ExitApp();

        _menu.Items.Add(profiles);
        _menu.Items.Add(limits);
        _menu.Items.Add(widget);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(settings);
        if (!Platform.Elevation.IsElevated)
        {
            var elevate = new MenuItem { Header = "Restart as administrator" };
            elevate.Click += (_, _) => app.RestartElevated();
            _menu.Items.Add(elevate);
        }

        _menu.Items.Add(exit);
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
        if (PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource source)
        {
            User32.SetForegroundWindow(source.Handle);
        }
    }

    private static System.Drawing.Size SmallIconSize()
    {
        var dpi = User32.GetDpiForSystem();
        const int SM_CXSMICON = 49;
        const int SM_CYSMICON = 50;
        return new System.Drawing.Size(User32.GetSystemMetricsForDpi(SM_CXSMICON, dpi), User32.GetSystemMetricsForDpi(SM_CYSMICON, dpi));
    }

    private static bool TaskbarIsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        ReleaseHandle();
    }
}
