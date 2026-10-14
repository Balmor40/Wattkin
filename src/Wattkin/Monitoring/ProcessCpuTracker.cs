using System.Runtime.InteropServices;
using Wattkin.Native;

namespace Wattkin.Monitoring;

/// <summary>CPU share of one app: every process with that image name (browsers and editors run many).</summary>
public readonly record struct AppUsage(string Name, int Processes, double CpuPercent);

/// <summary>
/// Per-app CPU share from one NtQuerySystemInformation call per sample, instead of opening
/// a handle to every process. Only runs while a window that shows it is open.
/// </summary>
internal sealed class ProcessCpuTracker : IDisposable
{
    // SYSTEM_PROCESS_INFORMATION (x64) field offsets.
    private const int OffNext = 0;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageNameLength = 56;
    private const int OffImageNameBuffer = 64;
    private const int OffProcessId = 80;

    private const int OffCreateTime = 32;

    // Keyed by (pid, creation time): a reused PID is a different process.
    private readonly Dictionary<(long Pid, long Created), (long CpuTime, string Name)> _previous = new();
    private readonly Dictionary<(long Pid, long Created), (long CpuTime, string Name)> _current = new();
    private readonly Dictionary<string, (int Processes, double Percent)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _buffer;
    private int _bufferSize;
    private long _previousTimestamp;

    public IReadOnlyList<AppUsage> Sample(int top)
    {
        if (IntPtr.Size != 8 || !Query())
        {
            return [];
        }

        var now = Environment.TickCount64;
        var elapsedMs = now - _previousTimestamp;
        _previousTimestamp = now;

        _current.Clear();
        _apps.Clear();
        var offset = 0;
        while (true)
        {
            var entry = _buffer + offset;
            var pid = Marshal.ReadInt64(entry, OffProcessId);
            var cpu = Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime);
            if (pid != 0)
            {
                var key = (pid, Marshal.ReadInt64(entry, OffCreateTime));
                var name = _previous.TryGetValue(key, out var prior) ? prior.Name : ReadName(entry);
                _current[key] = (cpu, name);
                var percent = 0.0;
                if (prior.Name is not null && elapsedMs > 0)
                {
                    var deltaMs = (cpu - prior.CpuTime) / 10_000.0;
                    percent = Math.Max(0, deltaMs / (elapsedMs * Environment.ProcessorCount) * 100.0);
                }

                var app = _apps.GetValueOrDefault(name);
                _apps[name] = (app.Processes + 1, app.Percent + percent);
            }

            var next = Marshal.ReadInt32(entry, OffNext);
            if (next == 0)
            {
                break;
            }

            offset += next;
        }

        _previous.Clear();
        foreach (var pair in _current)
        {
            _previous[pair.Key] = pair.Value;
        }

        var results = new List<AppUsage>(_apps.Count);
        foreach (var (name, app) in _apps)
        {
            if (app.Percent > 0.05)
            {
                results.Add(new AppUsage(name, app.Processes, app.Percent));
            }
        }

        results.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
        return results.Count > top ? results.GetRange(0, top) : results;
    }

    /// <summary>Forget everything and give the (≥1 MB) snapshot buffer back while nobody is watching.</summary>
    public void Reset()
    {
        if (_previous.Count == 0 && _buffer == IntPtr.Zero)
        {
            return;
        }

        _previous.Clear();
        _previous.TrimExcess();
        _current.Clear();
        _current.TrimExcess();
        _apps.Clear();
        _apps.TrimExcess();
        _previousTimestamp = 0;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    private static string ReadName(IntPtr entry)
    {
        var length = (ushort)Marshal.ReadInt16(entry, OffImageNameLength);
        var buffer = Marshal.ReadIntPtr(entry, OffImageNameBuffer);
        if (length == 0 || buffer == IntPtr.Zero)
        {
            return "System";
        }

        var name = Marshal.PtrToStringUni(buffer, length / 2);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private bool Query()
    {
        if (_buffer == IntPtr.Zero)
        {
            _bufferSize = 1 << 20;
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var status = NtDll.NtQuerySystemInformation(NtDll.SystemProcessInformation, _buffer, _bufferSize, out var needed);
            if (status == 0)
            {
                return true;
            }

            if (status != NtDll.STATUS_INFO_LENGTH_MISMATCH)
            {
                return false;
            }

            Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(needed + (64 * 1024), _bufferSize * 2);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        return false;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
