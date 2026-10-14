using System.Management;

namespace Wattkin.Hardware;

/// <summary>
/// Transport for the BIOS-provided ACPI-WMI interface root\WMI:MSI_ACPI
/// (GUID ABBC0F6E-8EA1-11D1-00A0-C90629100000). This is the same interface MSI Center uses. It is
/// part of the firmware (served by Windows' built-in ACPI-WMI driver), not of MSI's software, so it
/// keeps working after MSI Center is uninstalled.
///
/// Wire format (verified against MSI's MSIWMIACPI2.dll and its call log on this laptop):
///   input  = Package_32 { Bytes[32] }, [0] = subfeature or EC address, [1..] = data
///   output = Package_32 { Bytes[32] }, [0] = status (1 = success), [1..] = data
/// Get_WMI / Get_EC take no input. The firmware handler isn't re-entrant, so every call is serialized:
/// across all instances in this process, and across processes (e.g. a --diagnose run) by a named mutex.
/// Needs administrator rights.
/// </summary>
public sealed class MsiAcpi : IDisposable
{
    private const string InstancePath = @"MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'";
    private const int PackageSize = 32;
    private static readonly object ProcessGate = new();
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly Mutex _machineGate = new(false, @"Local\Wattkin.EcCall");
    private ManagementScope _scope;
    private ManagementClass _packageClass;
    private ManagementObject _instance;
    private bool _broken;
    private bool _disposed;
    private DateTime _nextReconnectUtc = DateTime.MinValue;
    private int _failuresLogged;

    private MsiAcpi(ManagementScope scope, ManagementObject instance, ManagementClass packageClass)
    {
        _scope = scope;
        _instance = instance;
        _packageClass = packageClass;
    }

    public int WmiMajor { get; private set; }
    public int WmiMinor { get; private set; }

    /// <summary>EC firmware string, e.g. "1594EMS1.1131007202413:20:06" (version + build date/time).</summary>
    public string EcFirmware { get; private set; } = string.Empty;

    /// <summary>EC firmware version only, e.g. "1594EMS1.113".</summary>
    public string EcVersion => EcFirmware.Length >= 12 ? EcFirmware[..12] : EcFirmware;

    /// <summary>
    /// Set when another Wattkin owns the hardware (screenshots, measurements): every Set_* call is
    /// refused here, whatever the caller, so this process can only read.
    /// </summary>
    public bool ReadOnly { get; private init; }

    public static MsiAcpi? TryOpen(out string? error, bool readOnly = false)
    {
        try
        {
            var (scope, instance, packageClass) = Bind();
            var acpi = new MsiAcpi(scope, instance, packageClass) { ReadOnly = readOnly };

            var wmi = acpi.Call("Get_WMI", null);
            if (wmi is not { Length: PackageSize } || wmi[0] != 1)
            {
                error = "The MSI WMI interface did not answer.";
                acpi.Dispose();
                return null;
            }

            acpi.WmiMajor = wmi[1];
            acpi.WmiMinor = wmi[2];
            var ec = acpi.Call("Get_EC", null);
            if (ec is { Length: PackageSize } && ec[0] == 1)
            {
                acpi.EcFirmware = System.Text.Encoding.ASCII.GetString(ec, 2, 28).TrimEnd('\0', ' ');
            }

            error = null;
            Log.Info($"MSI_ACPI opened: WMI {acpi.WmiMajor}.{acpi.WmiMinor}, EC '{acpi.EcFirmware}'");
            return acpi;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            error = "Administrator rights are needed to talk to the embedded controller.";
        }
        catch (ManagementException ex)
        {
            error = $"MSI WMI interface not available ({ex.ErrorCode}).";
        }
        catch (UnauthorizedAccessException)
        {
            error = "Administrator rights are needed to talk to the embedded controller.";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            error = $"WMI error 0x{ex.HResult:X8}.";
        }

        return null;
    }

    /// <summary>A fresh connection: scope, instance and the Package_32 class all belong to one WMI session.</summary>
    private static (ManagementScope, ManagementObject, ManagementClass) Bind()
    {
        var scope = new ManagementScope(@"root\WMI");
        scope.Connect();
        var instance = new ManagementObject(scope, new ManagementPath(InstancePath), null);
        instance.Get();
        return (scope, instance, new ManagementClass(scope, new ManagementPath("Package_32"), null));
    }

    /// <summary>Calls a Get_* method with one selector byte. Returns the 31 data bytes, or null on failure.</summary>
    public byte[]? Get(string method, byte selector)
    {
        var input = new byte[PackageSize];
        input[0] = selector;
        var output = Call(method, input);
        return output is { Length: PackageSize } && output[0] == 1 ? output[1..] : null;
    }

    /// <summary>Calls a Set_* method. Returns true when the firmware reports success.</summary>
    public bool Set(string method, byte selector, ReadOnlySpan<byte> data)
    {
        if (data.Length > PackageSize - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        if (ReadOnly)
        {
            Log.Warn($"Refused {method}(0x{selector:X2}): this copy of Wattkin is read-only.");
            return false;
        }

        var input = new byte[PackageSize];
        input[0] = selector;
        data.CopyTo(input.AsSpan(1));
        var output = Call(method, input);
        return output is { Length: PackageSize } && output[0] == 1;
    }

    public byte? ReadEc(byte address) => Get("Get_Data", address)?[0];

    public bool WriteEc(byte address, byte value)
    {
        var ok = Set("Set_Data", address, [value]);
        if (ok || _failuresLogged < 20)
        {
            Log.Info($"EC write 0x{address:X2} = 0x{value:X2} -> {(ok ? "ok" : "FAILED")}");
        }

        return ok;
    }

    /// <summary>
    /// Read-modify-write of selected bits; other bits in the register are preserved. With
    /// <paramref name="force"/> the byte is written even when unchanged (a heartbeat the EC may expect).
    /// </summary>
    public bool WriteEcBits(byte address, byte mask, byte value, bool force = false)
    {
        lock (ProcessGate)
        {
            var current = ReadEc(address);
            if (current is null)
            {
                return false;
            }

            var next = (byte)((current.Value & ~mask) | (value & mask));
            return (next == current.Value && !force) || WriteEc(address, next);
        }
    }

    /// <summary>
    /// One serialized firmware call. Any failure returns null. After a COM-level failure (e.g. the WMI
    /// service restarted) the whole binding is rebuilt, with backoff, before the next call.
    /// </summary>
    private byte[]? Call(string method, byte[]? input)
    {
        lock (ProcessGate)
        {
            if (_disposed)
            {
                return null;
            }

            var owned = false;
            try
            {
                try
                {
                    owned = _machineGate.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (_broken && !Reconnect())
                {
                    return null;
                }

                if (input is null)
                {
                    using var plain = _instance.InvokeMethod(method, null, null);
                    return Extract(plain);
                }

                using var parameters = _instance.GetMethodParameters(method);
                using var package = _packageClass.CreateInstance();
                package["Bytes"] = input;
                parameters["Data"] = package;
                using var result = _instance.InvokeMethod(method, parameters, null);
                return Extract(result);
            }
            catch (ManagementException ex)
            {
                LogFailure($"MSI_ACPI.{method}({(input is null ? "" : $"0x{input[0]:X2}")}) failed", ex);
                return null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                LogFailure($"MSI_ACPI.{method} failed; will reconnect", ex);
                _broken = true;
                return null;
            }
            finally
            {
                if (owned)
                {
                    _machineGate.ReleaseMutex();
                }
            }
        }
    }

    private bool Reconnect()
    {
        if (DateTime.UtcNow < _nextReconnectUtc)
        {
            return false;
        }

        _nextReconnectUtc = DateTime.UtcNow + ReconnectBackoff;
        try
        {
            var (scope, instance, packageClass) = Bind();
            _instance.Dispose();
            _packageClass.Dispose();
            (_scope, _instance, _packageClass) = (scope, instance, packageClass);
            _broken = false;
            _failuresLogged = 0;
            Log.Info("MSI_ACPI reconnected.");
            return true;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            LogFailure("MSI_ACPI reconnect failed", ex);
            return false;
        }
    }

    /// <summary>Failures are logged, but not endlessly, so the log keeps its history of real writes.</summary>
    private void LogFailure(string message, Exception ex)
    {
        if (_failuresLogged++ < 20)
        {
            Log.Error(message, ex);
        }
    }

    private static byte[]? Extract(ManagementBaseObject? result)
    {
        if (result?["Data"] is not ManagementBaseObject data)
        {
            return null;
        }

        using (data)
        {
            return data["Bytes"] as byte[];
        }
    }

    public void Dispose()
    {
        lock (ProcessGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _instance.Dispose();
            _packageClass.Dispose();
            _machineGate.Dispose();
        }
    }
}
