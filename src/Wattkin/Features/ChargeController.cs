using Wattkin.Monitoring;
using Wattkin.Settings;

namespace Wattkin.Features;

/// <summary>
/// Turns the user's charge settings into the EC state they need. It doesn't touch the EC itself:
/// <see cref="HardwareService"/> compares the result with what the EC actually holds and writes only the difference.
/// </summary>
internal sealed class ChargeController
{
    private const int ConfirmPasses = 2;

    private readonly SettingsStore _store;
    private int _fullSeen;
    private int _drainedSeen;

    public ChargeController(SettingsStore store) => _store = store;

    public string Status { get; private set; } = "Waiting for battery data";

    /// <param name="simulatingNow">EC 0xBF bit0 as read just now. While it's set, Windows reports battery power even though the charger is connected.</param>
    public ChargeTarget? Decide(Snapshot snapshot, bool simulatingNow)
    {
        if (snapshot.Power.Percent is not { } percent)
        {
            return null;
        }

        var settings = _store.Current;
        var decision = ChargePolicy.Decide(
            settings.ChargeLimit,
            settings.ChargeResumeBelow,
            settings.FullChargeOnce,
            settings.DrainToLimit,
            settings.ChargeBandHolding,
            new ChargeInputs(percent, snapshot.Power.OnAc || simulatingNow, snapshot.Power.Charging));

        // One-off completions must hold for two passes in a row: a single odd reading mustn't end them.
        _fullSeen = decision.FullChargeDone ? _fullSeen + 1 : 0;
        _drainedSeen = decision.DrainDone ? _drainedSeen + 1 : 0;
        var fullDone = _fullSeen >= ConfirmPasses;
        var drainDone = _drainedSeen >= ConfirmPasses;

        if (decision.FullChargeDone && !fullDone)
        {
            Status = "Checking the battery is full…";
            return new ChargeTarget(ChargePolicy.Max, false);
        }

        if (fullDone || drainDone || decision.BandHolding != settings.ChargeBandHolding)
        {
            _store.Update(s =>
            {
                if (fullDone)
                {
                    s.FullChargeOnce = false;
                    Log.Info("Charge to 100% once: done.");
                }

                if (drainDone)
                {
                    s.DrainToLimit = false;
                    Log.Info($"Drain to {s.ChargeLimit}%: done.");
                }

                s.ChargeBandHolding = decision.BandHolding;
            });
            if (fullDone || drainDone)
            {
                _fullSeen = _drainedSeen = 0;
            }
        }

        Status = decision.Status;
        return new ChargeTarget(decision.EcLimit, decision.SimulateUnplug);
    }
}
