using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Native;

namespace Wattkin.UI;

/// <summary>
/// Desktop widget. Not "always on top" by default: it sits behind other windows like a desktop
/// gadget. Drag to move; right-click for options. Asks the monitor for 1 s updates while visible.
/// </summary>
public partial class WidgetWindow : Window
{
    public WidgetWindow()
    {
        InitializeComponent();
        var settings = App.Current.Store.Current;
        Opacity = Math.Clamp(settings.WidgetOpacity, 0.3, 1);
        Topmost = settings.WidgetTopmost;
        Details.Visibility = settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactItem.IsChecked = settings.WidgetCompact;
        TopmostItem.IsChecked = settings.WidgetTopmost;

        SourceInitialized += (_, _) => WindowEffects.Apply(new WindowInteropHelper(this).Handle, toolWindow: true);
        Loaded += (_, _) => PlaceInitially();
        MouseLeftButtonDown += OnDrag;
        Activated += (_, _) => SendToBackIfPinned();
        // A desktop widget doesn't need 1 s updates: every 2 s (compact needs only watts; it still gets 2 s).
        IsVisibleChanged += (_, _) => App.Current.Monitor.Demand(this, fast: IsVisible, interval: TimeSpan.FromSeconds(2));
        Closed += (_, _) => App.Current.Monitor.Demand(this, fast: false);
    }

    public void Update(Snapshot s)
    {
        var p = s.Power;
        Watts.Text = Fmt.WattsNumber(p.WattsNow);
        Battery.Text = p.Percent is { } percent ? $"{percent:0}%{(p.Charging ? " ⚡" : "")}" : "";
        Runtime.Text = p.Source switch
        {
            PowerSource.BatteryMeasured => p.TimeToEmpty is { } t ? $"{Fmt.Duration(Fmt.Steady(t))} left" : "estimating…",
            PowerSource.AcEstimated => p.Charging && p.TimeToTarget is { } c ? $"charging · {Fmt.Duration(c)} to limit" : $"plugged in · ≈{Fmt.Duration(Fmt.Steady(p.UnpluggedRuntimeEstimate))} on battery",
            PowerSource.AcCpuOnly => "plugged in · CPU only",
            _ => "",
        };

        if (Details.Visibility != Visibility.Visible)
        {
            return;
        }

        var temp = s.Sensors?.CpuTempC is { } ct ? $" · {ct}°C" : s.ThermalZoneC is { } tz ? $" · {tz:0}°C" : "";
        Cpu.Text = $"CPU {s.Cpu.UsagePercent:0}% · {Fmt.GHz(s.Cpu.AverageMHz)}{temp}";
        Ram.Text = $"RAM {s.Memory.LoadPercent}% · {Fmt.Bytes(s.Memory.UsedBytes)}";
        Disk.Text = $"Disk ↓{Fmt.Rate(s.Disks.Sum(d => d.ReadBytesPerSec))} ↑{Fmt.Rate(s.Disks.Sum(d => d.WriteBytesPerSec))}";
        Gpu.Text = s.Dgpu.State is DgpuState.None ? "" : $"dGPU {Fmt.Dgpu(s.Dgpu).ToLowerInvariant()}";
    }

    private void PlaceInitially()
    {
        var settings = App.Current.Store.Current;
        var area = SystemParameters.WorkArea;
        if (settings.WidgetLeft is { } left && settings.WidgetTop is { } top
            && left >= SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && top >= SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = area.Right - ActualWidth - 24;
            Top = area.Top + 24;
        }

        SendToBackIfPinned();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        App.Current.Store.Update(s =>
        {
            s.WidgetLeft = Left;
            s.WidgetTop = Top;
        });
        SendToBackIfPinned();
    }

    private void SendToBackIfPinned()
    {
        if (!Topmost)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            User32.SetWindowPos(hwnd, User32.HWND_BOTTOM, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    private void OnCompact(object sender, RoutedEventArgs e)
    {
        var compact = CompactItem.IsChecked;
        Details.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        App.Current.Store.Update(s => s.WidgetCompact = compact);
    }

    private void OnTopmost(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostItem.IsChecked;
        App.Current.Store.Update(s => s.WidgetTopmost = Topmost);
        SendToBackIfPinned();
    }

    private void OnOpacity(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            Opacity = value;
            App.Current.Store.Update(s => s.WidgetOpacity = value);
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => App.Current.ToggleFlyout();

    private void OnHide(object sender, RoutedEventArgs e) => App.Current.SetWidgetVisible(false);
}
