using System.Runtime.InteropServices;
using Wattkin.Native;

namespace Wattkin.Monitoring;

/// <summary>Which logical processors are performance vs efficiency cores (Intel hybrid CPUs).</summary>
public sealed class CpuTopology
{
    private readonly Dictionary<string, bool> _isPerformanceByPdhInstance = new(StringComparer.Ordinal);

    public int LogicalCount { get; }
    public int PerformanceThreads { get; }
    public int EfficiencyThreads { get; }
    public string Name { get; }

    public CpuTopology()
    {
        LogicalCount = Environment.ProcessorCount;
        Name = ReadCpuName();

        var classes = ReadEfficiencyClasses();
        if (classes.Count == 0)
        {
            return;
        }

        var maxClass = classes.Values.Max();
        foreach (var ((group, index), efficiencyClass) in classes)
        {
            var isPerformance = efficiencyClass == maxClass;
            _isPerformanceByPdhInstance[$"{group},{index}"] = isPerformance;
            if (isPerformance)
            {
                PerformanceThreads++;
            }
            else
            {
                EfficiencyThreads++;
            }
        }
    }

    /// <summary>True for P-core, false for E-core, null when unknown. Instance names look like "0,7".</summary>
    public bool? IsPerformance(string pdhInstance) =>
        _isPerformanceByPdhInstance.TryGetValue(pdhInstance, out var value) ? value : null;

    private static Dictionary<(int Group, int Index), byte> ReadEfficiencyClasses()
    {
        var result = new Dictionary<(int, int), byte>();
        Kernel32.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Kernel32.GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
            {
                return result;
            }

            var offset = 0;
            while (offset < length)
            {
                var entry = buffer + offset;
                var size = Marshal.ReadInt32(entry);
                var type = Marshal.ReadInt32(entry, 4);
                if (size <= 0)
                {
                    break;
                }

                if (type == 0) // CpuSetInformation
                {
                    var group = (ushort)Marshal.ReadInt16(entry, 12);
                    var logicalIndex = Marshal.ReadByte(entry, 14);
                    var efficiencyClass = Marshal.ReadByte(entry, 18);
                    result[(group, logicalIndex)] = efficiencyClass;
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static string ReadCpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
    }
}
