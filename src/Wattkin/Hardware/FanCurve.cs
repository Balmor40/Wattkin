using System.Globalization;

namespace Wattkin.Hardware;

/// <summary>
/// One fan's table in the embedded controller, used only in the "custom curve" fan mode (0xD4 bit 7):
/// below Temps[0] the fan runs at Speeds[0], from Temps[i] at Speeds[i + 1]. Speeds are MSI's
/// percentages (above 100 is allowed; full speed is Cooler Boost).
///
/// The firmware hands it out as Get_Temperature(n) and Get_Fan(n), n = 1 for the CPU fan, 2 for the GPU
/// fan, the calls MSI Center's fan page uses. On the Prestige 16 Studio the bytes match the EC's classic
/// layout: temperatures 0x6A–0x6F (GPU 0x82–0x87) arrive as Get_Temperature data [3..7] then [1], and
/// speeds 0x72–0x78 (GPU 0x8A–0x90) as Get_Fan data [1..7].
///
/// Verified there on 2026-10-14: in curve mode the controller's CPU fan target (0x71, Get_Fan(1)
/// data [0]) follows this table (a test table of 100% below 60°C took the target to 100 and the fan
/// from 0 to ~4,700 rpm; the original put it back to 0 and the fan stopped). It steps down slowly:
/// minutes after a change, at ~100 rpm/s.
/// </summary>
public sealed record FanCurve(IReadOnlyList<int> Temps, IReadOnlyList<int> Speeds)
{
    public static FanCurve? Parse(byte[]? temperatures, byte[]? speeds)
    {
        if (temperatures is not { Length: >= 8 } || speeds is not { Length: >= 8 })
        {
            return null;
        }

        return new FanCurve(
            [temperatures[3], temperatures[4], temperatures[5], temperatures[6], temperatures[7], temperatures[1]],
            [.. speeds[1..8].Select(b => (int)b)]);
    }

    /// <summary>
    /// Safe to hand the fans to: temperatures rising through a plausible range, speeds never falling, and
    /// the top step at least 100% so the fan runs flat out when it's hot.
    /// </summary>
    public bool Sane =>
        Temps.Count == 6 && Speeds.Count == 7
        && Temps.All(t => t is >= 20 and <= 110) && Temps.Zip(Temps.Skip(1)).All(p => p.Second > p.First)
        && Speeds.All(s => s is >= 0 and <= 150) && Speeds.Zip(Speeds.Skip(1)).All(p => p.Second >= p.First)
        && Speeds[^1] >= 100;

    /// <summary>"off below 60°C · 50% from 60°C · 55% from 70°C …" (repeated speeds folded into the first step).</summary>
    public string Describe()
    {
        var parts = new List<string> { $"{Speed(Speeds[0])} below {Temps[0]}°C" };
        for (var i = 0; i < Temps.Count; i++)
        {
            if (Speeds[i + 1] != Speeds[i])
            {
                parts.Add($"{Speed(Speeds[i + 1])} from {Temps[i]}°C");
            }
        }

        return string.Join(" · ", parts);
    }

    private static string Speed(int percent) => percent == 0 ? "off" : percent.ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>Both fans' tables.</summary>
public sealed record FanCurves(FanCurve Cpu, FanCurve Gpu)
{
    public bool Sane => Cpu.Sane && Gpu.Sane;
}
