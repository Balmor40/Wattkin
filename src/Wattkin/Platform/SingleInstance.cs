using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wattkin.Platform;

/// <summary>
/// Named events for "one copy only": Instance (show the flyout) and Exit (quit cleanly, used by the
/// installer). They're created with a security descriptor that lets a normal-rights launch (e.g. the
/// Start-menu shortcut) signal the copy that runs as administrator, and nothing more: the interactive
/// user gets only SYNCHRONIZE | EVENT_MODIFY_STATE.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Sddl = "D:(A;;0x00100002;;;IU)(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";
    private const int ERROR_ALREADY_EXISTS = 183;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const int ASFW_ANY = -1;

    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _exit;

    private SingleInstance(EventWaitHandle show, EventWaitHandle exit)
    {
        _show = show;
        _exit = exit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Becomes the running instance, or signals the existing one and returns null.
    /// With <paramref name="waitForPrevious"/>, waits for a copy that is about to exit (restart as admin).
    /// </summary>
    public static SingleInstance? Acquire(string name, bool waitForPrevious)
    {
        var deadline = DateTime.UtcNow + (waitForPrevious ? TimeSpan.FromSeconds(8) : TimeSpan.Zero);
        while (true)
        {
            var instance = TryCreate(name, out var unavailable);
            if (instance is not null)
            {
                return instance;
            }

            if (unavailable)
            {
                // Couldn't create or open the event at all (name squatted by another object type,
                // or a foreign security descriptor). Run anyway rather than vanish silently.
                Log.Warn($"Single-instance event {name} unavailable; continuing without it.");
                return new SingleInstance(new EventWaitHandle(false, EventResetMode.AutoReset), new EventWaitHandle(false, EventResetMode.AutoReset));
            }

            if (DateTime.UtcNow >= deadline)
            {
                // Let the running copy bring its flyout to the front.
                AllowSetForegroundWindow(ASFW_ANY);
                Signal(name + ".Show");
                return null;
            }

            Thread.Sleep(250);
        }
    }


    private static SingleInstance? TryCreate(string name, out bool unavailable)
    {
        var handle = Create(name + ".Show", out var alreadyExists);
        unavailable = handle is null;
        if (handle is not null && !alreadyExists)
        {
            var exit = Create(name + ".Exit", out _);
            return new SingleInstance(
                new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle },
                new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = exit ?? new SafeWaitHandle(IntPtr.Zero, false) });
        }

        handle?.Dispose();
        return null;
    }

    /// <summary>Asks a running copy to show its flyout. Returns false if none is running.</summary>
    public static bool RequestShow(string name)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        return Signal(name + ".Show");
    }

    /// <summary>Asks a running copy to exit cleanly (it releases the hardware first). Returns false if none answered.</summary>
    public static bool RequestExit(string name) => Signal(name + ".Exit");

    /// <summary>Callbacks run on background threads.</summary>
    public void Listen(Action onShow, Action onExit)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var handles = new WaitHandle[] { _show, _exit };
                while (true)
                {
                    if (WaitHandle.WaitAny(handles) == 0)
                    {
                        onShow();
                    }
                    else
                    {
                        onExit();
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ArgumentException)
            {
            }
        })
        { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }

    /// <returns>Null if the event can't be created or opened.</returns>
    private static SafeWaitHandle? Create(string name, out bool alreadyExists)
    {
        alreadyExists = false;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out var descriptor, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
            var handle = CreateEventW(ref attributes, false, false, name);
            var error = Marshal.GetLastWin32Error();
            if (handle.IsInvalid)
            {
                handle.Dispose();
                // Access denied means a copy exists whose event we may not open fully; probe whether we can signal it.
                using var probe = OpenEventW(EVENT_MODIFY_STATE, false, name);
                if (probe.IsInvalid)
                {
                    return null;
                }

                alreadyExists = true;
                return new SafeWaitHandle(IntPtr.Zero, false);
            }

            alreadyExists = error == ERROR_ALREADY_EXISTS;
            return handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static bool Signal(string name)
    {
        using var handle = OpenEventW(EVENT_MODIFY_STATE, false, name);
        return !handle.IsInvalid && SetEvent(handle);
    }

    public void Dispose()
    {
        _show.Dispose();
        _exit.Dispose();
    }
}

/// <summary>Returns freed managed memory to Windows after a window closes (the tray app idles most of the time).</summary>
internal static class MemoryTrim
{
    private static int _pending;

    public static void Soon()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _pending, 0);
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, TaskScheduler.Default);
    }
}
