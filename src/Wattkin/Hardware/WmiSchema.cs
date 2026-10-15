using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace Wattkin.Hardware;

/// <summary>
/// Wattkin's own description of MSI's ACPI-WMI interface, so it works on a Windows install that never
/// had MSI software.
///
/// The interface is in the BIOS (GUID ABBC0F6E-…, served by Windows' own wmiacpi.sys), but the BIOS
/// doesn't describe it: on MSI laptops that description (MSI_ACPI, MSI_Event, …) comes from MSI's
/// msiapcfg.dll, which MSI's service copies to SysWOW64 and registers as WmiAcpi's MofImagePath. A
/// clean install has neither, so WMI answers "InvalidClass". Wattkin registers the same GUIDs and
/// method numbers under its own class names, compiled from memory into root\WMI, so it never depends
/// on, or clashes with, MSI's files. Only the calls Wattkin makes are declared.
/// </summary>
public static class WmiSchema
{
    public const string AcpiClass = "Wattkin_MsiAcpi";
    public const string PackageClass = "Wattkin_MsiPackage32";
    public const string EventClass = "Wattkin_MsiEvent";

    /// <summary>Changes whenever the text below does, so an older registration is replaced.</summary>
    private const string Version = "Wattkin MSI interface v1";

    private const string Mof = $$"""
        #pragma namespace("\\\\.\\root\\WMI")

        [WMI, Locale("MS\\0x409"), Description("{{Version}}: 32-byte package"), guid("{ABBC0F63-8EA1-11d1-00A0-C90629100000}")]
        class {{PackageClass}}
        {
            [WmiDataId(1), read, write, MAX(32)] uint8 Bytes[];
        };

        [WMI, dynamic: ToInstance, provider("WmiProv"), Locale("MS\\0x409"), Description("{{Version}}"), guid("{ABBC0F6E-8EA1-11d1-00A0-C90629100000}")]
        class {{AcpiClass}}
        {
            [key, read] string InstanceName;
            [read] boolean Active;
            [WmiMethodId(3), Implemented, read, write] void Get_EC([out] {{PackageClass}} Data);
            [WmiMethodId(13), Implemented, read, write] void Get_Temperature([in, out] {{PackageClass}} Data);
            [WmiMethodId(17), Implemented, read, write] void Get_Fan([in, out] {{PackageClass}} Data);
            [WmiMethodId(18), Implemented, read, write] void Set_Fan([in, out] {{PackageClass}} Data);
            [WmiMethodId(19), Implemented, read, write] void Get_Device([in, out] {{PackageClass}} Data);
            [WmiMethodId(25), Implemented, read, write] void Get_AP([in, out] {{PackageClass}} Data);
            [WmiMethodId(27), Implemented, read, write] void Get_Data([in, out] {{PackageClass}} Data);
            [WmiMethodId(28), Implemented, read, write] void Set_Data([in, out] {{PackageClass}} Data);
            [WmiMethodId(29), Implemented, read, write] void Get_WMI([out] {{PackageClass}} Data);
        };

        [WMI, dynamic: ToInstance, provider("WmiProv"), Locale("MS\\0x409"), Description("{{Version}}: hotkey event"), guid("{5B3CC38A-40D9-7245-8AE6-1145B751BE3F}")]
        class {{EventClass}} : WMIEvent
        {
            [key, read] string InstanceName;
            [read] boolean Active;
            [WmiDataId(1), read] uint32 MSIEvt;
        };
        """;

    /// <summary>True when this version of Wattkin's classes is in the WMI repository.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var acpi = new ManagementClass(@"root\WMI", AcpiClass, null);
            acpi.Get();
            return acpi.Qualifiers["Description"].Value as string == Version;
        }
        catch (ManagementException)
        {
            return false;
        }
    }

    /// <summary>Registers the classes when they're missing or out of date. Needs administrator rights. Returns an error, or null.</summary>
    public static string? EnsureRegistered()
    {
        if (IsRegistered())
        {
            return null;
        }

        var error = Compile(Mof);
        if (error is not null)
        {
            // An older version whose shape changed can refuse an in-place update: replace it.
            Remove();
            error = Compile(Mof);
        }

        if (error is null)
        {
            Log.Info($"Registered Wattkin's description of MSI's firmware interface ({Version}).");
        }
        else
        {
            Log.Error($"Couldn't register Wattkin's description of MSI's firmware interface: {error}");
        }

        return error;
    }

    /// <summary>Removes Wattkin's classes (uninstall). MSI's own classes, if any, are left alone.</summary>
    public static void Remove()
    {
        foreach (var name in new[] { AcpiClass, EventClass, PackageClass })
        {
            try
            {
                using var cls = new ManagementClass(@"root\WMI", name, null);
                cls.Delete();
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
            {
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                Log.Error($"Removing WMI class {name} failed", ex);
            }
        }
    }

    // ---- The WMI MOF compiler (IMofCompiler), fed from memory -----------------------------------

    private static readonly Guid MofCompilerClsid = new("6daf9757-2e37-11d2-aec9-00c04fb68820");

    [StructLayout(LayoutKind.Sequential)]
    private struct CompileStatus
    {
        public int PhaseError;
        public int HResult;
        public int ObjectNumber;
        public int FirstLine;
        public int LastLine;
        public uint OutFlags;
    }

    [ComImport, Guid("6daf974e-2e37-11d2-aec9-00c04fb68820"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMofCompiler
    {
        [PreserveSig]
        int CompileFile(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.LPWStr)] string? serverAndNamespace,
            [MarshalAs(UnmanagedType.LPWStr)] string? user, [MarshalAs(UnmanagedType.LPWStr)] string? authority, [MarshalAs(UnmanagedType.LPWStr)] string? password,
            int optionFlags, int classFlags, int instanceFlags, ref CompileStatus info);

        [PreserveSig]
        int CompileBuffer(
            int bufferSize, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[] buffer, [MarshalAs(UnmanagedType.LPWStr)] string? serverAndNamespace,
            [MarshalAs(UnmanagedType.LPWStr)] string? user, [MarshalAs(UnmanagedType.LPWStr)] string? authority, [MarshalAs(UnmanagedType.LPWStr)] string? password,
            int optionFlags, int classFlags, int instanceFlags, ref CompileStatus info);
    }

    /// <summary>
    /// Compiles MOF text straight from memory: no file an unprivileged process could swap out between
    /// writing and compiling. Returns an error, or null.
    /// </summary>
    private static string? Compile(string mof)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(MofCompilerClsid, throwOnError: true)!);
            if (instance is not IMofCompiler compiler)
            {
                return "The WMI MOF compiler isn't available.";
            }

            var bytes = Encoding.ASCII.GetBytes(mof);
            var status = default(CompileStatus);
            var hr = compiler.CompileBuffer(bytes.Length, bytes, null, null, null, null, 0, 0, 0, ref status);
            return hr == 0 ? null : $"the MOF compiler returned 0x{hr:X8} (phase {status.PhaseError}, 0x{status.HResult:X8}, lines {status.FirstLine}–{status.LastLine})";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }
}
