using System.Runtime.InteropServices;
using Wattkin.Native;

namespace Wattkin.Hardware;

public enum DgpuState
{
    /// <summary>No discrete GPU known to Windows.</summary>
    None,
    /// <summary>Known to Windows but not on the PCIe bus: firmware has it switched off (MSI "iGPU only").</summary>
    RemovedByFirmware,
    /// <summary>Present but disabled in Windows (Device Manager, or an older Wattkin's "quick off").</summary>
    DisabledInWindows,
    /// <summary>Present, powered down by Optimus (D3).</summary>
    Sleeping,
    /// <summary>Present and powered (D0) — something is using it or it is waking.</summary>
    Active,
    /// <summary>Present, state could not be read.</summary>
    Unknown,
}

public sealed record DgpuStatus(DgpuState State, string? Name, IReadOnlyList<string> InstanceIds);

/// <summary>
/// Observes the NVIDIA GPU through the PnP manager, and turns it back on when it was left disabled
/// in Windows (Wattkin never disables it: a disabled RTX can't power itself off). Reading the cached device
/// power state never touches the GPU, so polling this does not wake it (unlike NVML/nvidia-smi).
/// </summary>
internal static class DiscreteGpu
{
    private const string NvidiaPrefix = @"PCI\VEN_10DE&";
    private const uint DN_STARTED = 0x00000008;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint CM_PROB_DISABLED = 22;

    public static DgpuStatus Query()
    {
        var present = Enumerate(SetupApi.GUID_DEVCLASS_DISPLAY, presentOnly: true);
        var gpu = present.FirstOrDefault(d => d.InstanceId.StartsWith(NvidiaPrefix, StringComparison.OrdinalIgnoreCase));
        if (gpu is null)
        {
            var known = Enumerate(SetupApi.GUID_DEVCLASS_DISPLAY, presentOnly: false)
                .FirstOrDefault(d => d.InstanceId.StartsWith(NvidiaPrefix, StringComparison.OrdinalIgnoreCase));
            return known is null
                ? new DgpuStatus(DgpuState.None, null, [])
                : new DgpuStatus(DgpuState.RemovedByFirmware, known.Name, [known.InstanceId]);
        }

        var state = gpu.Problem == CM_PROB_DISABLED ? DgpuState.DisabledInWindows
            : (gpu.Status & DN_STARTED) == 0 ? DgpuState.Unknown
            : gpu.PowerState switch
            {
                1 => DgpuState.Active,
                >= 2 and <= 4 => DgpuState.Sleeping,
                _ => DgpuState.Unknown,
            };
        return new DgpuStatus(state, gpu.Name, [gpu.InstanceId]);
    }

    /// <summary>
    /// Enables every present NVIDIA PCI function (GPU and its HD-audio function).
    /// Needs administrator rights. Returns an error message, or null on success.
    /// </summary>
    public static string? Enable()
    {
        var set = SetupApi.SetupDiGetClassDevs(Guid.Empty, "PCI", IntPtr.Zero, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1))
        {
            return "Could not enumerate PCI devices.";
        }

        var changed = 0;
        var needsRestart = false;
        try
        {
            var data = new SetupApi.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
            for (var i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var id = InstanceId(set, ref data);
                if (id is null || !id.StartsWith(NvidiaPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parameters = new SetupApi.SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = new SetupApi.SP_CLASSINSTALL_HEADER
                    {
                        cbSize = Marshal.SizeOf<SetupApi.SP_CLASSINSTALL_HEADER>(),
                        InstallFunction = SetupApi.DIF_PROPERTYCHANGE,
                    },
                    StateChange = SetupApi.DICS_ENABLE,
                    Scope = SetupApi.DICS_FLAG_GLOBAL,
                    HwProfile = 0,
                };

                if (!SetupApi.SetupDiSetClassInstallParams(set, ref data, ref parameters, Marshal.SizeOf<SetupApi.SP_PROPCHANGE_PARAMS>())
                    || !SetupApi.SetupDiCallClassInstaller(SetupApi.DIF_PROPERTYCHANGE, set, ref data))
                {
                    return $"Windows refused to enable {id} (error {Marshal.GetLastWin32Error()}).";
                }

                // A driver that won't stop right now gets the change queued for the next restart instead.
                var install = new SetupApi.SP_DEVINSTALL_PARAMS { cbSize = Marshal.SizeOf<SetupApi.SP_DEVINSTALL_PARAMS>() };
                if (SetupApi.SetupDiGetDeviceInstallParams(set, ref data, ref install) && (install.Flags & (SetupApi.DI_NEEDRESTART | SetupApi.DI_NEEDREBOOT)) != 0)
                {
                    needsRestart = true;
                }

                changed++;
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        if (changed == 0)
        {
            return "No NVIDIA device is present (is the GPU switched off in firmware?).";
        }

        return needsRestart
            ? "Windows will finish turning the GPU back on at the next restart."
            : null;
    }

    private sealed record Device(string InstanceId, string? Name, uint Status, uint Problem, int PowerState);

    private static List<Device> Enumerate(Guid classGuid, bool presentOnly)
    {
        var result = new List<Device>();
        var set = SetupApi.SetupDiGetClassDevs(classGuid, null, IntPtr.Zero, presentOnly ? SetupApi.DIGCF_PRESENT : 0);
        if (set == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var data = new SetupApi.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
            for (var i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var id = InstanceId(set, ref data);
                if (id is null)
                {
                    continue;
                }

                uint status = 0, problem = 0;
                if (presentOnly)
                {
                    SetupApi.CM_Get_DevNode_Status(out status, out problem, data.DevInst, 0);
                    if ((status & DN_HAS_PROBLEM) == 0)
                    {
                        problem = 0;
                    }
                }

                result.Add(new Device(id, FriendlyName(set, ref data), status, problem, presentOnly ? PowerState(set, ref data) : 0));
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static unsafe string? InstanceId(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        var buffer = stackalloc char[512];
        return SetupApi.SetupDiGetDeviceInstanceId(set, ref data, buffer, 512, out _) ? new string(buffer) : null;
    }

    private static unsafe string? FriendlyName(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        const int SPDRP_DEVICEDESC = 0x0;
        var buffer = stackalloc char[256];
        return SetupApi.SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_DEVICEDESC, out _, buffer, 256 * sizeof(char), out _)
            ? new string(buffer)
            : null;
    }

    private static unsafe int PowerState(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        var buffer = stackalloc byte[128];
        if (!SetupApi.SetupDiGetDeviceRegistryProperty(set, ref data, SetupApi.SPDRP_DEVICE_POWER_DATA, out _, buffer, 128, out var required) || required < sizeof(SetupApi.CM_POWER_DATA))
        {
            return 0;
        }

        return ((SetupApi.CM_POWER_DATA*)buffer)->PD_MostRecentPowerState;
    }
}
