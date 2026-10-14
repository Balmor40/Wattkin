using System.Runtime.InteropServices;
using Wattkin.Native;
using Microsoft.Win32.SafeHandles;

namespace Wattkin.Monitoring;

[Flags]
public enum BatteryPowerState : uint
{
    None = 0,
    OnLine = 0x1,
    Discharging = 0x2,
    Charging = 0x4,
    Critical = 0x8,
}

public sealed record BatteryInfo(
    uint DesignedCapacityMWh,
    uint FullChargedCapacityMWh,
    uint CycleCount,
    string Chemistry,
    string? Manufacturer,
    string? DeviceName);

public readonly record struct BatteryReading(
    BatteryPowerState State,
    uint? CapacityMWh,
    uint? VoltageMV,
    int? RateMW);

/// <summary>
/// Reads the battery through the Windows battery class driver (IOCTL_BATTERY_*). This is what the
/// Windows battery flyout uses: no admin rights, no WMI provider round trip, cheap enough to poll.
/// </summary>
internal sealed class BatteryDevice : IDisposable
{
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    private const uint BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;
    private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;
    private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);

    private SafeFileHandle? _handle;
    private uint _tag;

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public uint AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved0;
        public byte Reserved1;
        public byte Reserved2;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    /// <summary>
    /// The battery driver issues a new tag around plug events and gauge resets, which makes the old tag
    /// fail. Re-open once and retry so a tag change doesn't show up as a missing reading.
    /// </summary>
    public unsafe BatteryReading? ReadStatus()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!EnsureOpen())
            {
                return null;
            }

            var wait = new WaitStatus { BatteryTag = _tag };
            Status status;
            if (Kernel32.DeviceIoControl(_handle!, IOCTL_BATTERY_QUERY_STATUS, &wait, sizeof(WaitStatus), &status, sizeof(Status), out _, IntPtr.Zero))
            {
                return Convert(status);
            }

            Reset();
        }

        return null;
    }

    private static BatteryReading Convert(Status status)
    {
        return new BatteryReading(
            (BatteryPowerState)status.PowerState,
            status.Capacity == BATTERY_UNKNOWN_CAPACITY ? null : status.Capacity,
            status.Voltage == BATTERY_UNKNOWN_VOLTAGE ? null : status.Voltage,
            status.Rate == BATTERY_UNKNOWN_RATE ? null : status.Rate);
    }

    public unsafe BatteryInfo? ReadInfo()
    {
        if (!EnsureOpen())
        {
            return null;
        }

        var query = new QueryInformation { BatteryTag = _tag, InformationLevel = 0 };
        Information info;
        if (!Kernel32.DeviceIoControl(_handle!, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(QueryInformation), &info, sizeof(Information), out _, IntPtr.Zero))
        {
            Reset();
            return null;
        }

        var chem = info.Chemistry;
        var chemistry = new string([(char)(chem & 0xFF), (char)((chem >> 8) & 0xFF), (char)((chem >> 16) & 0xFF), (char)((chem >> 24) & 0xFF)]).TrimEnd('\0', ' ');
        return new BatteryInfo(info.DesignedCapacity, info.FullChargedCapacity, info.CycleCount, chemistry, QueryString(6), QueryString(4));
    }

    /// <summary>Battery temperature in °C, if the gauge reports it (many don't).</summary>
    public unsafe double? ReadTemperatureC()
    {
        if (!EnsureOpen())
        {
            return null;
        }

        var query = new QueryInformation { BatteryTag = _tag, InformationLevel = 2 };
        uint tenthsKelvin;
        if (!Kernel32.DeviceIoControl(_handle!, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(QueryInformation), &tenthsKelvin, sizeof(uint), out _, IntPtr.Zero) || tenthsKelvin == 0)
        {
            return null;
        }

        return (tenthsKelvin / 10.0) - 273.15;
    }

    private unsafe string? QueryString(int level)
    {
        var query = new QueryInformation { BatteryTag = _tag, InformationLevel = level };
        var buffer = stackalloc char[128];
        if (!Kernel32.DeviceIoControl(_handle!, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(QueryInformation), buffer, 128 * sizeof(char), out var returned, IntPtr.Zero) || returned < 2)
        {
            return null;
        }

        var text = new string(buffer, 0, returned / sizeof(char)).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    private unsafe bool EnsureOpen()
    {
        if (_handle is { IsInvalid: false, IsClosed: false })
        {
            return true;
        }

        var path = FindBatteryPath();
        if (path is null)
        {
            return false;
        }

        var handle = Kernel32.CreateFile(path, Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        uint wait = 0;
        uint tag;
        if (!Kernel32.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, &wait, sizeof(uint), &tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
        {
            handle.Dispose();
            return false;
        }

        _handle = handle;
        _tag = tag;
        return true;
    }

    private static string? FindBatteryPath()
    {
        var set = SetupApi.SetupDiGetClassDevs(SetupApi.GUID_DEVINTERFACE_BATTERY, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var data = new SetupApi.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, SetupApi.GUID_DEVINTERFACE_BATTERY, 0, ref data))
            {
                return null;
            }

            SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required <= 0)
            {
                return null;
            }

            var detail = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (DWORD + one WCHAR, padded).
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                return SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero)
                    ? Marshal.PtrToStringUni(detail + 4)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private void Reset()
    {
        _handle?.Dispose();
        _handle = null;
        _tag = 0;
    }

    public void Dispose() => Reset();
}
