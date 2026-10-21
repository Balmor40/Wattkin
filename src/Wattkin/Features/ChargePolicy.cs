namespace Wattkin.Features;

public readonly record struct ChargeInputs(double Percent, bool OnAc, bool Charging);

/// <summary>What the EC should be set to: charge stop % (register 0xD7) and "run from battery while plugged in" (0xBF bit0).</summary>
public readonly record struct ChargeTarget(int Limit, bool SimulateUnplug);

public sealed record ChargeDecision(
    int EcLimit,
    bool SimulateUnplug,
    bool BandHolding,
    bool FullChargeDone,
    bool DrainDone,
    string Status);

/// <summary>
/// Decides what the EC should be set to so the battery stays where the user asked.
///
/// The EC only knows a stop percentage S (register 0xD7) and resumes charging below S − 10.
///  - Default (resume = S − 10): just write S. The EC does everything, even with the app closed.
///  - Wider band (e.g. 70–90): charge with S = 90. Once full, write S = 70 + 10 = 80, so the EC stays
///    idle until the battery is below 70. When it starts charging again, write S = 90.
///    If the app isn't running, the last value stays in effect, which is never above the band's top.
/// Narrower bands than the EC's own 10% gap aren't offered: they'd need the app to keep nudging the EC.
/// </summary>
public static class ChargePolicy
{
    public const int Hysteresis = 10;
    public const int Min = 10;
    public const int Max = 100;
    public const int DrainFloor = 15;
    public const double FullPercent = 97;

    /// <summary>
    /// Windows' percentage (remaining / full capacity) can sit a little below the EC's integer stop
    /// point, so "reached the top" allows 1.5 points of slack.
    /// </summary>
    public const double TopSlack = 1.5;

    /// <summary>Lowest "start charging below" allowed for a stop point (the EC's gap is the narrowest band).</summary>
    public static int MaxResumeFor(int limit) => Math.Max(0, limit - Hysteresis);

    public static ChargeDecision Decide(int limit, int? resumeBelow, bool fullChargeOnce, bool drainToLimit, bool bandHolding, ChargeInputs input)
    {
        limit = Math.Clamp(limit, Min, Max);

        if (fullChargeOnce)
        {
            // Gauges often settle at 97–99% when the EC reports the battery full. With the limit at 100
            // the EC never stops below 90%, so 97 can't be confused with "won't top up".
            var full = input.OnAc && !input.Charging && input.Percent >= FullPercent;
            if (!full)
            {
                var status = input.Charging ? Strings.Charge_ToFullOnce
                    : input.OnAc && input.Percent >= 89 ? string.Format(Strings.Charge_TopsUpBelow90, input.Percent)
                    : Strings.Charge_WillChargeToFull;
                return new ChargeDecision(Max, false, false, false, false, status);
            }

            var after = Decide(limit, resumeBelow, false, false, true, input);
            return after with { FullChargeDone = true, Status = string.Format(Strings.Charge_FullBackToLimit, limit) };
        }

        var simulate = false;
        var drainDone = false;
        if (drainToLimit && input.OnAc)
        {
            if (input.Percent > limit + 0.5 && input.Percent > DrainFloor + 0.5)
            {
                simulate = true;
            }
            else
            {
                drainDone = true;
            }
        }

        var defaultResume = MaxResumeFor(limit);
        var resume = resumeBelow is { } r ? Math.Clamp(r, 0, defaultResume) : defaultResume;
        int ecLimit;
        var holding = false;
        if (resume >= defaultResume)
        {
            ecLimit = limit;
        }
        else
        {
            holding = bandHolding;
            if (input.Charging)
            {
                holding = false;
            }
            else if (input.OnAc && input.Percent >= limit - TopSlack)
            {
                holding = true;
            }

            ecLimit = holding ? Math.Clamp(resume + Hysteresis, Min, limit) : limit;
        }

        var text = simulate ? string.Format(Strings.Charge_Draining, limit)
            : drainDone ? string.Format(Strings.Charge_Drained, limit)
            : Describe(input, limit, resume);
        return new ChargeDecision(ecLimit, simulate, holding, false, drainDone, text);
    }

    private static string Describe(ChargeInputs input, int limit, int resume)
    {
        if (!input.OnAc)
        {
            return string.Format(Strings.Charge_OnBattery, limit, resume);
        }

        if (input.Charging)
        {
            return string.Format(Strings.Charge_ChargingTo, limit);
        }

        return string.Format(input.Percent >= limit - 1 ? Strings.Charge_Holding : Strings.Charge_NotCharging, limit, resume);
    }
}
