using System.Runtime.InteropServices;
using Wattkin.Native;

namespace Wattkin.Monitoring;

/// <summary>
/// One PDH query holding several (possibly wildcard) counters, collected together.
/// Language-independent English counter paths; missing counter sets simply yield no counter.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private IntPtr _buffer;
    private uint _bufferSize;

    public PdhQuery()
    {
        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
        }
    }

    public PdhCounter? Add(string englishPath)
    {
        if (_query == IntPtr.Zero)
        {
            return null;
        }

        return Pdh.PdhAddEnglishCounter(_query, englishPath, IntPtr.Zero, out var counter) == 0
            ? new PdhCounter(this, counter)
            : null;
    }

    public bool Collect() => _query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == 0;

    /// <summary>Reads every instance of a counter. Instances without valid data are skipped.</summary>
    internal void ReadInto(IntPtr counter, Dictionary<string, double> target)
    {
        target.Clear();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = _bufferSize;
            var status = Pdh.PdhGetFormattedCounterArray(counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, ref size, out var count, _buffer);
            if (status == Pdh.PDH_MORE_DATA)
            {
                Grow(size);
                continue;
            }

            if (status != 0)
            {
                return;
            }

            var stride = Marshal.SizeOf<Pdh.FormattedItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Pdh.FormattedItem>(_buffer + (i * stride));
                if (item.CStatus is Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA)
                {
                    var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                    target[name] = item.Value;
                }
            }

            return;
        }
    }

    private void Grow(uint size)
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
        }

        _bufferSize = Math.Max(size, _bufferSize * 2);
        _buffer = Marshal.AllocHGlobal((int)_bufferSize);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            Pdh.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}

internal sealed class PdhCounter
{
    private readonly PdhQuery _owner;
    private readonly IntPtr _handle;
    private readonly Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);

    public PdhCounter(PdhQuery owner, IntPtr handle)
    {
        _owner = owner;
        _handle = handle;
    }

    /// <summary>Instance name → value from the most recent collection. Reused between reads.</summary>
    public IReadOnlyDictionary<string, double> Read()
    {
        _owner.ReadInto(_handle, _values);
        return _values;
    }

    public double? ReadInstance(string instance)
    {
        var values = Read();
        return values.TryGetValue(instance, out var value) ? value : null;
    }
}
