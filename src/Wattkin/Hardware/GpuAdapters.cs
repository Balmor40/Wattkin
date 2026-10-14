using System.Runtime.InteropServices;
using Wattkin.Native;

namespace Wattkin.Hardware;

/// <summary>A display adapter and the LUID Windows' GPU counters name it by.</summary>
public sealed record GpuAdapter(string Name, string ShortName, long Luid, bool Discrete);

/// <summary>
/// Lists the GPUs from Windows' device records (SetupAPI), including each one's adapter LUID
/// (DEVPKEY_Gpu_Luid). Like <see cref="DiscreteGpu.Query"/> this never talks to a GPU, so it can't
/// wake a sleeping one.
/// </summary>
public static class GpuAdapters
{
    private static readonly SetupApi.DEVPROPKEY LuidKey = new() { FormatId = new Guid("60b193cb-5276-4d0f-96fc-f173abad3ec6"), PropertyId = 2 };

    public static IReadOnlyList<GpuAdapter> List()
    {
        var result = new List<GpuAdapter>();
        var set = SetupApi.SetupDiGetClassDevs(SetupApi.GUID_DEVCLASS_DISPLAY, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT);
        if (set == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var data = new SetupApi.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
            for (var i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (Luid(set, ref data) is not { } luid || Text(set, ref data, 0x0) is not { } name)
                {
                    continue; // not a GPU Windows schedules work on (e.g. a disabled one)
                }

                var hardware = Text(set, ref data, 0x1) ?? ""; // SPDRP_HARDWAREID
                var discrete = hardware.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)
                    || (hardware.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) && !name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase));
                result.Add(new GpuAdapter(name, Short(name, discrete), luid, discrete));
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    /// <summary>"NVIDIA GeForce RTX 4050 Laptop GPU" → "RTX 4050"; integrated GPUs by vendor.</summary>
    private static string Short(string name, bool discrete)
    {
        if (!discrete)
        {
            return name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "Radeon" : "iGPU";
        }

        var s = name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Replace("AMD Radeon ", "Radeon ").Replace(" Laptop GPU", "");
        return s.Length <= 12 ? s : "dGPU";
    }

    private static unsafe long? Luid(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        long value = 0;
        return SetupApi.SetupDiGetDeviceProperty(set, ref data, LuidKey, out _, &value, sizeof(long), out var required, 0) && required >= 4 ? value : null;
    }

    private static unsafe string? Text(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data, int property)
    {
        var buffer = stackalloc char[512];
        return SetupApi.SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, 512 * sizeof(char), out _) ? new string(buffer) : null;
    }
}
