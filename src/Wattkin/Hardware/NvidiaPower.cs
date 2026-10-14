using System.Runtime.InteropServices;

namespace Wattkin.Hardware;

/// <summary>
/// The RTX's own power sensor, through NVIDIA's management library (nvml.dll, installed with the driver).
///
/// Any NVML call powers a sleeping GPU up, so <see cref="Read"/> is only for when Windows' scheduler
/// counters already show the GPU doing work, and the library is unloaded again once it's idle.
/// Measured on the Prestige 16 Studio (RTX 4050): one query keeps the RTX awake about a second, and the
/// first reading after it powers up can be garbage (590 W), so implausible values are dropped.
/// </summary>
internal sealed unsafe class NvidiaPower : IDisposable
{
    private const int Success = 0;
    private const int NotSupported = 3;
    private const double MaxPlausibleWatts = 200;
    private static readonly TimeSpan UnloadAfterIdle = TimeSpan.FromSeconds(3);

    private nint _library;
    private nint _device;
    private delegate* unmanaged<int> _shutdown;
    private delegate* unmanaged<nint, uint*, int> _powerUsage;
    private DateTime _lastUseUtc;

    /// <summary>No NVML, or the GPU has no power sensor: stop trying.</summary>
    private bool _unavailable;

    /// <summary>Watts the RTX draws now; call only while it's awake and busy.</summary>
    public double? Read(DateTime nowUtc)
    {
        if (_unavailable || (_library == 0 && !Load()))
        {
            return null;
        }

        _lastUseUtc = nowUtc;
        uint milliwatts;
        var status = _powerUsage(_device, &milliwatts);
        if (status == NotSupported)
        {
            Log.Info("NVML: this GPU doesn't report its power draw.");
            _unavailable = true;
            Unload();
            return null;
        }

        var watts = milliwatts / 1000.0;
        return status == Success && watts is > 0 and < MaxPlausibleWatts ? watts : null;
    }

    /// <summary>Unloads NVML a few seconds after the last read, so nothing holds the GPU.</summary>
    public void ReleaseIfIdle(DateTime nowUtc)
    {
        if (_library != 0 && nowUtc - _lastUseUtc > UnloadAfterIdle)
        {
            Unload();
        }
    }

    private bool Load()
    {
        // Full path: only the driver's copy in System32, never one found elsewhere on the search path.
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var library))
        {
            _unavailable = true;
            return false;
        }

        if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init)
            || !NativeLibrary.TryGetExport(library, "nvmlShutdown", out var shutdown)
            || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out var byIndex)
            || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetPowerUsage", out var powerUsage))
        {
            NativeLibrary.Free(library);
            _unavailable = true;
            return false;
        }

        if (((delegate* unmanaged<int>)init)() != Success)
        {
            NativeLibrary.Free(library);
            return false; // the driver may be restarting; try again next time
        }

        nint device;
        if (((delegate* unmanaged<uint, nint*, int>)byIndex)(0, &device) != Success)
        {
            ((delegate* unmanaged<int>)shutdown)();
            NativeLibrary.Free(library);
            return false;
        }

        _library = library;
        _device = device;
        _shutdown = (delegate* unmanaged<int>)shutdown;
        _powerUsage = (delegate* unmanaged<nint, uint*, int>)powerUsage;
        return true;
    }

    private void Unload()
    {
        if (_library == 0)
        {
            return;
        }

        _shutdown();
        NativeLibrary.Free(_library);
        _library = 0;
        _device = 0;
    }

    public void Dispose() => Unload();
}
