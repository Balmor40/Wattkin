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
    /// The lowest top speed of a real fan table. Laptops differ: the Prestige 16 Studio's hottest step is
    /// 130%, the Katana GF66 12UG's 75% (CPU) and 60% (GPU).
    /// </summary>
    public const int MinTopSpeed = 50;

    /// <summary>
    /// Safe to hand the fans to: temperatures rising through a plausible range, speeds never falling, and
    /// the top step fast enough to be a real fan table's (<see cref="MinTopSpeed"/>).
    /// </summary>
    public bool Sane =>
        Temps.Count == 6 && Speeds.Count == 7
        && Temps.All(t => t is >= 20 and <= 110) && Temps.Zip(Temps.Skip(1)).All(p => p.Second > p.First)
        && Speeds.All(s => s is >= 0 and <= 150) && Speeds.Zip(Speeds.Skip(1)).All(p => p.Second >= p.First)
        && Speeds[^1] >= MinTopSpeed;

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

/// <summary>
/// The part of both fan tables a user can set: the speed below the first temperature step and from
/// each of the next five, as MSI Center's own fan page does. The seventh speed, at the hottest step,
/// always stays the firmware's (130% on the Prestige 16 Studio, 75% and 60% on the Katana GF66 12UG).
/// </summary>
public sealed class FanSpeeds
{
    public const int Steps = 6;
    public const int Max = 150;

    /// <summary>The hottest editable step (0-based): the fans must be near the laptop's top speed there, unless the user accepted the experimental mode.</summary>
    public const int HottestStep = 5;

    public int[] Cpu { get; set; } = [];
    public int[] Gpu { get; set; } = [];

    public FanSpeeds Clone() => new() { Cpu = [.. Cpu], Gpu = [.. Gpu] };

    public static FanSpeeds From(FanCurves curves) => new() { Cpu = [.. curves.Cpu.Speeds.Take(Steps)], Gpu = [.. curves.Gpu.Speeds.Take(Steps)] };

    /// <summary>
    /// The slowest a fan may run at the hottest editable step: four fifths of the laptop's fixed top
    /// speed, at most 100%. That's 100% on the Prestige 16 Studio (top 130%), and 60% and 48% on the
    /// Katana GF66 12UG (top 75% and 60%), whose own curve runs 60% and 50% there.
    /// </summary>
    public static int Floor(int topSpeed) => Math.Min(100, topSpeed * 4 / 5);

    /// <summary>
    /// What's wrong with these speeds, or null. Always: six speeds per fan, 0–150%. Unless
    /// <paramref name="unrestricted"/>: speeds never fall as it gets hotter, and at the hottest editable
    /// step the fans run at least the <see cref="Floor"/> of <paramref name="laptop"/>'s top speed (100%
    /// without it), so the laptop can't be left to overheat.
    /// </summary>
    public string? Problem(bool unrestricted, FanCurves? laptop = null)
    {
        foreach (var (name, speeds, table) in new[] { ("CPU", Cpu, laptop?.Cpu), ("GPU", Gpu, laptop?.Gpu) })
        {
            if (speeds is not { Length: Steps } || speeds.Any(s => s is < 0 or > Max))
            {
                return string.Format(Strings.Fans_CurveSpeeds, name, Steps, Max);
            }

            if (unrestricted)
            {
                continue;
            }

            if (speeds.Zip(speeds.Skip(1)).Any(p => p.Second < p.First))
            {
                return string.Format(Strings.Fans_SpeedDrops, name);
            }

            var floor = table is { } t ? Floor(t.Speeds[^1]) : 100;
            if (speeds[HottestStep] < floor)
            {
                return string.Format(Strings.Fans_CurveTooSlow, name, floor);
            }
        }

        return null;
    }
}
