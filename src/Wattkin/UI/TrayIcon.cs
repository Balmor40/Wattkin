using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Controls;
using Wattkin.Monitoring;
using Wattkin.Native;
using Forms = System.Windows.Forms;

namespace Wattkin.UI;

/// <summary>
/// Tray icon that shows the laptop's current watts as the icon itself, so the number is visible at
/// a glance without opening anything. Left click: flyout. Right click: quick menu.
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
    private double? _shownWatts;
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
        var watts = snapshot.Power.WattsNow;
        var hardware = App.Current.Hardware.State;

        // Every change redraws the taskbar (and wakes the display pipeline), so the number only moves
        // when the reading really moved: at least 0.3 W or 3%.
        if (watts is { } w && _shownWatts is { } shown && Math.Abs(w - shown) < Math.Max(0.3, shown * 0.03))
        {
            watts = shown;
        }

        _shownWatts = watts;

        var tooltip = $"Wattkin · {Fmt.Watts(watts)}";
        if (snapshot.Power.Percent is { } percent)
        {
            tooltip += $" · {percent:0}%";
        }

        tooltip += $"\n{Fmt.BatteryLine(snapshot.Power, hardware.ChargeStatus, coarse: true)}\nProfile: {settings.ActiveProfile}";
        tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (tooltip != _currentTooltip)
        {
            _currentTooltip = tooltip;
            _icon.Text = tooltip;
        }

        if (!settings.TrayShowsWatts || watts is null)
        {
            SetIcon(null);
            return;
        }

        // Whole watts in the tray ("17W"); the flyout and tooltip keep the decimal.
        SetIcon(Math.Round(watts.Value, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.CurrentCulture));
    }

    private void SetIcon(string? text)
    {
        var light = _taskbarLight;
        if (text == _currentText && light == _currentLight)
        {
            return;
        }

        _currentText = text ?? "";
        _currentLight = light;
        if (text is null)
        {
            _icon.Icon = _staticIcon;
            ReleaseHandle();
            return;
        }

        using var bitmap = Render(text, SmallIconSize(), light);
        var handle = bitmap.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        ReleaseHandle();
        _currentHandle = handle;
    }

    /// <summary>
    /// The tray number: whole watts with a small "W" sharing its baseline (e.g. "17W"), as large as
    /// fits the icon. Built from glyph outlines so both parts line up exactly at 16–32 px.
    /// </summary>
    internal static Bitmap Render(string number, System.Drawing.Size size, bool light)
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
        var unit = number.Length >= 3 ? 0.5f : 0.58f; // relative size of the "W"
        var gap = size.Width / 32f;

        for (var em = size.Height * 1.25f; em > 5; em -= 0.25f)
        {
            using var digits = new GraphicsPath();
            digits.AddString(number, family, (int)System.Drawing.FontStyle.Bold, em, new PointF(0, 0), format);
            var d = digits.GetBounds();

            using var w = new GraphicsPath();
            var wEm = em * unit;
            // Same baseline: the W's em box starts lower by the difference in ascent.
            w.AddString("W", family, (int)System.Drawing.FontStyle.Bold, wEm, new PointF(0, (ascent * em) - (ascent * wEm)), format);
            var wb = w.GetBounds();

            var width = d.Width + gap + wb.Width;
            if (width > size.Width + 0.5f || d.Height > size.Height)
            {
                continue;
            }

            // Centre the pair; vertically centre the digits (they're taller than the W).
            var dx = ((size.Width - width) / 2f) - d.Left;
            var dy = ((size.Height - d.Height) / 2f) - d.Top;
            using (var move = new Matrix())
            {
                move.Translate(dx, dy);
                digits.Transform(move);
            }

            using (var move = new Matrix())
            {
                move.Translate(dx + d.Right + gap - wb.Left, dy);
                w.Transform(move);
            }

            g.FillPath(brush, digits);
            g.FillPath(brush, w);
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
