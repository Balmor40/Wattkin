using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Wattkin.Native;

namespace Wattkin.Hardware;

/// <summary>
/// The PawnIO driver (pawnio.eu, installed separately by the owner): a signed kernel driver that only
/// runs modules signed by its author, each of which decides what it lets user mode do. Wattkin is
/// otherwise driver-free; this is the one way it reaches the processor's power-limit register, and
/// only with the official IntelMSR / IntelMCHBAR modules embedded in the exe (Hardware\PawnIO).
///
/// Protocol (PawnIOLib.cpp, LibreHardwareMonitor's PawnIo.cs): open \Device\PawnIO, load a module with
/// IOCTL_PIO_LOAD_BINARY (one module per handle), then call its functions with IOCTL_PIO_EXECUTE_FN:
/// a 32-byte zero-padded ASCII name followed by 64-bit inputs, 64-bit outputs back. Talking to it
/// only through device I/O control is what PawnIO's license exception allows for non-GPL-2 programs.
/// Needs administrator rights.
/// </summary>
internal sealed unsafe class PawnIo : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
    private const uint IoctlExecute = DeviceType | (0x841 << 2);
    private const int FunctionNameLength = 32;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();

    private PawnIo(SafeFileHandle handle) => _handle = handle;

    /// <summary>The installed PawnIO's version (its Settings › Apps entry), or null when it isn't installed.</summary>
    public static Version? InstalledVersion { get; } = ReadInstalledVersion();

    /// <summary>Opens the driver and loads one embedded module ("IntelMSR", "IntelMCHBAR"). Null with a reason when it can't.</summary>
    public static PawnIo? Open(string module, out string? error)
    {
        if (InstalledVersion is null)
        {
            error = "PawnIO isn't installed (winget install namazso.PawnIO, then restart Wattkin).";
            return null;
        }

        using var stream = typeof(PawnIo).Assembly.GetManifestResourceStream($"Wattkin.PawnIO.{module}.bin");
        if (stream is null)
        {
            error = $"The {module} module is missing from this build.";
            return null;
        }

        var blob = new byte[stream.Length];
        stream.ReadExactly(blob);

        var handle = Kernel32.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", GenericReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = $"The PawnIO driver couldn't be opened (error {Marshal.GetLastWin32Error()}); it needs administrator rights.";
            handle.Dispose();
            return null;
        }

        fixed (byte* p = blob)
        {
            if (!Kernel32.DeviceIoControl(handle, IoctlLoadBinary, p, blob.Length, null, 0, out _, IntPtr.Zero))
            {
                error = $"PawnIO refused the {module} module (error {Marshal.GetLastWin32Error()}): not this kind of processor, or an older PawnIO.";
                handle.Dispose();
                return null;
            }
        }

        error = null;
        return new PawnIo(handle);
    }

    /// <summary>Calls a module function. Returns its outputs, or null when the call failed (the module refused it, or the driver went away).</summary>
    public ulong[]? Execute(string function, ReadOnlySpan<ulong> input, int outputs)
    {
        var request = new byte[FunctionNameLength + (input.Length * sizeof(ulong))];
        Encoding.ASCII.GetBytes(function.AsSpan(0, Math.Min(function.Length, FunctionNameLength - 1)), request);
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FunctionNameLength));
        var output = new ulong[outputs];
        lock (_gate)
        {
            if (_handle.IsClosed)
            {
                return null;
            }

            fixed (byte* pIn = request)
            fixed (ulong* pOut = output)
            {
                if (!Kernel32.DeviceIoControl(_handle, IoctlExecute, pIn, request.Length, outputs == 0 ? null : pOut, outputs * sizeof(ulong), out var returned, IntPtr.Zero)
                    || returned < outputs * sizeof(ulong))
                {
                    return null;
                }
            }
        }

        return output;
    }

    private static Version? ReadInstalledVersion()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handle.Dispose();
        }
    }
}
