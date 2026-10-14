using Wattkin.Hardware;

namespace Wattkin.Monitoring;

public sealed record DiskStat(string Name, double ReadBytesPerSec, double WriteBytesPerSec, double ActivePercent);

public sealed record CpuStat(
    double UsagePercent,
    double AverageMHz,
    double MaxCoreMHz,
    double? PerformanceCoreMHz,
    double? EfficiencyCoreMHz,
    double? CoresWatts,
    double? GraphicsWatts);

public sealed record MemoryStat(ulong TotalBytes, ulong UsedBytes, uint LoadPercent, ulong CommitUsedBytes, ulong CommitLimitBytes);

/// <summary>
/// How busy one GPU is: its busiest engine (3D, copy, video…), like Task Manager. Watts: the Intel GPU's
/// from the CPU's energy meter, the RTX's from its own sensor, and only while it's busy.
/// </summary>
public sealed record GpuLoad(GpuAdapter Adapter, double Percent, double? Watts = null);

/// <summary>Sensors that need the EC (administrator). Null fields mean not available.</summary>
public sealed record EcSensors(int? CpuTempC, int? GpuTempC, int? Fan1Rpm, int? Fan2Rpm);

public sealed record Snapshot(
    DateTime TimestampUtc,
    PowerState Power,
    CpuStat Cpu,
    MemoryStat Memory,
    IReadOnlyList<DiskStat> Disks,
    EcSensors? Sensors,
    double? ThermalZoneC,
    double? BatteryTempC,
    DgpuStatus Dgpu,
    IReadOnlyList<AppUsage> TopProcesses,
    IReadOnlyList<GpuLoad> Gpus);
