using System.Management;

namespace Wattkin.Hardware;

/// <summary>
/// Hotkey notifications from the BIOS (root\WMI:MSI_Event, code = MSIEvt &amp; 0xFF, extra data in
/// the higher bits). Codes from MSI's OmApSvcBroker.exe and API_NB_Base Module.dll. The EC already
/// handles keyboard-backlight, webcam power and Fn-lock itself; these events are the parts MSI did in
/// software, which Wattkin takes over.
/// </summary>
public enum MsiHotkey
{
    CameraApp = 3,
    CoolerBoost = 4,
    FnKey = 11,
    WebcamLock = 15,
    MicMute = 25,
    PerformanceMode = 29,
    TouchpadOff = 67,
    TouchpadOn = 68,
    LaunchControlCenter = 70,
    MonitorOff = 75,
    Bluetooth = 86,
    Webcam = 87,
    FnLock = 92,
    WiFi = 95,
    LaunchControlCenter2 = 111,
    BluetoothSettings = 120,
}

public readonly record struct HotkeyEvent(int Code, uint Extra)
{
    public MsiHotkey? Known => Enum.IsDefined(typeof(MsiHotkey), Code) ? (MsiHotkey)Code : null;
}

internal sealed class HotkeyEvents : IDisposable
{
    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private Timer? _retry;
    private bool _disposed;

    public event Action<HotkeyEvent>? Pressed;

    /// <summary>Starts listening; if WMI drops the subscription (e.g. service restart) it re-subscribes with backoff.</summary>
    public string? Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            StopWatcher();
            try
            {
                _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new WqlEventQuery("SELECT * FROM MSI_Event"));
                _watcher.EventArrived += OnEvent;
                _watcher.Stopped += OnStopped;
                _watcher.Start();
                return null;
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                StopWatcher();
                ScheduleRetry();
                return ex is UnauthorizedAccessException ? "Hotkey events need administrator rights." : $"Hotkey events unavailable ({ex.Message}); retrying.";
            }
        }
    }

    private void OnStopped(object sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                Log.Warn("Hotkey subscription stopped; re-subscribing.");
                ScheduleRetry();
            }
        }
    }

    private void ScheduleRetry()
    {
        _retry?.Dispose();
        _retry = new Timer(_ =>
        {
            if (Start() is { } error)
            {
                Log.Warn(error);
            }
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void StopWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EventArrived -= OnEvent;
        _watcher.Stopped -= OnStopped;
        try
        {
            _watcher.Stop();
        }
        catch (Exception)
        {
            // Stopping a dead subscription can throw COM or WMI errors; nothing to do.
        }

        _watcher.Dispose();
        _watcher = null;
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var raw = Convert.ToUInt32(e.NewEvent["MSIEvt"]);
            var evt = new HotkeyEvent((int)(raw & 0xFF), raw >> 8);
            Log.Info($"Hotkey event {evt.Code} ({evt.Known?.ToString() ?? "unknown"}), extra 0x{evt.Extra:X}");
            Pressed?.Invoke(evt);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ManagementException)
        {
            Log.Error("Unreadable hotkey event", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _retry?.Dispose();
            StopWatcher();
        }
    }
}
