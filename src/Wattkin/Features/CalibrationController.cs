using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.Features;

public enum CalibrationPhase
{
    ChargingToFull,
    /// <summary>The EC won't top up while the battery is at 90% or more, so it drains below 85% first (MSI's tool does the same).</summary>
    DrainingForTopUp,
    Discharging,
    Done,
    Failed,
    Cancelled,
}

public sealed record CalibrationProgress(
    CalibrationPhase Phase,
    string Message,
    double? Percent,
    double TargetPercent,
    TimeSpan Elapsed,
    TimeSpan? PhaseEta,
    TimeSpan? TotalEta);

/// <summary>
/// Battery-gauge calibration: charge to 100%, discharge to near empty while still plugged in
/// (EC "simulate unplug", 0xBF bit0), then hand back to the normal charge limit so it recharges.
/// A full discharge is what makes the fuel gauge re-measure capacity. This is MSIBatteryCalibration.exe's
/// procedure, plus ETAs, a capped and debounced top-up retry, resume after restart/sleep and a
/// before/after record. Like <see cref="ChargeController"/>, it only says what the EC should be set to;
/// HardwareService reconciles that against the EC's actual registers. All entry points are called
/// under HardwareService's lock.
/// </summary>
internal sealed class CalibrationController : IDisposable
{
    private const double TopUpThreshold = 85;
    private const int MaxTopUpAttempts = 2;
    private const int NotChargingConfirm = 3;
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan TopUpGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan UnknownPercentLimit = TimeSpan.FromSeconds(10);

    private readonly SettingsStore _store;
    private readonly Func<BatteryInfo?> _batteryInfo;
    private readonly StayAwake _awake = new("Battery calibration");
    private readonly LoadGenerator _load = new();

    private DateTime _phaseStartedUtc = DateTime.UtcNow;
    private DateTime _lastPercentChangeUtc = DateTime.UtcNow;
    private DateTime _acMissingSinceUtc = DateTime.MaxValue;
    private DateTime _percentUnknownSinceUtc = DateTime.MaxValue;
    private double? _lastPercent;
    private double? _dischargeW;
    private double? _chargeW;
    private int _topUpAttempts;
    private int _notChargingTicks;

    public CalibrationController(SettingsStore store, Func<BatteryInfo?> batteryInfo)
    {
        _store = store;
        _batteryInfo = batteryInfo;
    }

    public bool IsActive => _store.Current.Calibration is { FinishedUtc: null };
    public CalibrationProgress? Progress { get; private set; }

    public event Action<CalibrationProgress>? Finished;

    public string? Start(Snapshot latest, bool fastDischarge)
    {
        if (IsActive)
        {
            return Strings.Calib_AlreadyRunning;
        }

        if (!latest.Power.OnAc)
        {
            return Strings.Calib_PlugIn;
        }

        if (latest.Power.Percent is null)
        {
            return Strings.Calib_NoLevel;
        }

        // Reset all run state before the record is published, so the next tick can't see a previous run.
        ResetTimers();
        _topUpAttempts = 0;
        _dischargeW = _chargeW = null;
        Progress = null;

        var limitBefore = _store.Current.ChargeLimit;
        _store.Update(s =>
        {
            s.FullChargeOnce = false;
            s.DrainToLimit = false;
            s.Calibration = new CalibrationRecord
            {
                StartedUtc = DateTime.UtcNow,
                Phase = nameof(CalibrationPhase.ChargingToFull),
                FullChargeBeforeMWh = _batteryInfo()?.FullChargedCapacityMWh ?? 0,
                LimitBefore = limitBefore,
                FastDischarge = fastDischarge,
            };
        });
        Log.Info($"Calibration started at {latest.Power.Percent:0}% (fast discharge: {fastDischarge}).");
        return null;
    }

    public void Cancel() => Finish(CalibrationPhase.Cancelled, Strings.Calib_Cancelled);

    public void SetFastDischarge(bool fast) => _store.Update(s =>
    {
        if (s.Calibration is not null)
        {
            s.Calibration.FastDischarge = fast;
        }
    });

    /// <summary>After sleep the wall-clock gaps say nothing about the battery: start the timers again.</summary>
    public void OnResume() => ResetTimers();

    /// <summary>Returns what the EC should be set to while calibrating, or null when not calibrating.</summary>
    /// <param name="simulatingNow">EC 0xBF bit0 as read just now.</param>
    public ChargeTarget? Tick(Snapshot snapshot, bool simulatingNow)
    {
        var record = _store.Current.Calibration;
        if (record is null || record.FinishedUtc is not null)
        {
            return null;
        }

        var now = snapshot.TimestampUtc;
        if (now - record.StartedUtc > MaxDuration)
        {
            return Finish(CalibrationPhase.Failed, Strings.Calib_TooLong);
        }

        if (snapshot.Power.Percent is not { } percent)
        {
            // Never keep discharging blind. Pause the discharge; give up if the level stays unknown.
            if (_percentUnknownSinceUtc == DateTime.MaxValue)
            {
                _percentUnknownSinceUtc = now;
            }
            else if (now - _percentUnknownSinceUtc > TimeSpan.FromMinutes(5))
            {
                return Finish(CalibrationPhase.Failed, Strings.Calib_LevelUnknown);
            }

            return new ChargeTarget(100, simulatingNow && now - _percentUnknownSinceUtc < UnknownPercentLimit);
        }

        _percentUnknownSinceUtc = DateTime.MaxValue;
        var phase = Enum.TryParse<CalibrationPhase>(record.Phase, out var p) ? p : CalibrationPhase.ChargingToFull;
        var target = Math.Max(8, WindowsPower.CriticalBatteryPercent() + 3);

        if (_lastPercent is null || Math.Abs(percent - _lastPercent.Value) >= 0.5)
        {
            _lastPercent = percent;
            _lastPercentChangeUtc = now;
        }

        TrackRates(snapshot.Power.BatteryRateWatts);
        _notChargingTicks = snapshot.Power.OnAc && !snapshot.Power.Charging ? _notChargingTicks + 1 : 0;

        ChargeTarget wanted;
        switch (phase)
        {
            case CalibrationPhase.ChargingToFull:
                Activity(false);
                wanted = new ChargeTarget(100, false);
                if (!snapshot.Power.OnAc && !simulatingNow)
                {
                    if (_acMissingSinceUtc == DateTime.MaxValue)
                    {
                        _acMissingSinceUtc = now;
                    }
                    else if (now - _acMissingSinceUtc > TimeSpan.FromSeconds(30))
                    {
                        return Finish(CalibrationPhase.Failed, Strings.Calib_Unplugged);
                    }

                    break;
                }

                _acMissingSinceUtc = DateTime.MaxValue;
                if (_notChargingTicks >= NotChargingConfirm && percent >= ChargePolicy.FullPercent)
                {
                    EnterPhase(CalibrationPhase.Discharging);
                    wanted = new ChargeTarget(100, true);
                }
                else if (_notChargingTicks >= NotChargingConfirm && now - _phaseStartedUtc > TopUpGrace)
                {
                    if (percent >= TopUpThreshold)
                    {
                        if (++_topUpAttempts > MaxTopUpAttempts)
                        {
                            return Finish(CalibrationPhase.Failed, string.Format(Strings.Calib_WontCharge, percent));
                        }

                        EnterPhase(CalibrationPhase.DrainingForTopUp);
                        wanted = new ChargeTarget(100, true);
                    }
                    else if (now - _phaseStartedUtc > TimeSpan.FromMinutes(5))
                    {
                        return Finish(CalibrationPhase.Failed, string.Format(Strings.Calib_NotCharging, percent));
                    }
                }

                break;

            case CalibrationPhase.DrainingForTopUp:
                Activity(false);
                if (percent < TopUpThreshold - 0.5)
                {
                    EnterPhase(CalibrationPhase.ChargingToFull);
                    wanted = new ChargeTarget(100, false);
                }
                else if (now - _lastPercentChangeUtc > StallLimit)
                {
                    return Finish(CalibrationPhase.Failed, Strings.Calib_Stalled);
                }
                else
                {
                    wanted = new ChargeTarget(100, true);
                }

                break;

            default:
                Activity(record.FastDischarge);
                if (percent <= target)
                {
                    var after = _batteryInfo()?.FullChargedCapacityMWh;
                    _store.Update(s =>
                    {
                        if (s.Calibration is not null)
                        {
                            s.Calibration.FullChargeAfterMWh = after;
                        }
                    });
                    return Finish(CalibrationPhase.Done, string.Format(Strings.Calib_Done, record.LimitBefore));
                }

                if (now - _lastPercentChangeUtc > StallLimit)
                {
                    return Finish(CalibrationPhase.Failed, Strings.Calib_Stalled);
                }

                wanted = new ChargeTarget(100, true);
                break;
        }

        Progress = BuildProgress(phase, record, snapshot, percent, target, now);
        return wanted;
    }

    /// <summary>Averaged rates for ETAs (instant readings swing a lot, especially near full).</summary>
    private void TrackRates(double? rate)
    {
        const double alpha = 0.1;
        if (rate is < -0.5)
        {
            _dischargeW = _dischargeW is { } d ? d + (alpha * (-rate.Value - d)) : -rate.Value;
        }
        else if (rate is > 0.5)
        {
            _chargeW = _chargeW is { } c ? c + (alpha * (rate.Value - c)) : rate.Value;
        }
    }

    private CalibrationProgress BuildProgress(CalibrationPhase phase, CalibrationRecord record, Snapshot snapshot, double percent, double target, DateTime now)
    {
        var fullWh = snapshot.Power.FullWh ?? 0;
        var remainingWh = snapshot.Power.RemainingWh ?? 0;
        var drainW = _dischargeW ?? 8;
        var mainDischargeW = record.FastDischarge ? Math.Max(drainW, 20) : drainW;
        var chargeW = Math.Max(_chargeW ?? 30, 10);
        var fullDischarge = Hours(fullWh * (1 - (target / 100)) / mainDischargeW) ?? TimeSpan.Zero;

        TimeSpan? phaseEta;
        var future = TimeSpan.Zero;
        string message;
        switch (phase)
        {
            case CalibrationPhase.ChargingToFull:
                message = string.Format(snapshot.Power.Charging ? Strings.Calib_Step1Charging : Strings.Calib_Step1Waiting, percent);
                phaseEta = Hours((fullWh - remainingWh) / chargeW * 1.25);
                future = fullDischarge;
                break;
            case CalibrationPhase.DrainingForTopUp:
                message = string.Format(Strings.Calib_Step1TooFull, percent);
                phaseEta = Hours((remainingWh - (fullWh * 0.84)) / drainW);
                future = (Hours(fullWh * 0.16 / chargeW * 1.25) ?? TimeSpan.Zero) + fullDischarge;
                break;
            default:
                message = string.Format(record.FastDischarge ? Strings.Calib_Step2Fast : Strings.Calib_Step2, target, percent);
                phaseEta = Hours((remainingWh - (fullWh * target / 100)) / mainDischargeW);
                break;
        }

        return new CalibrationProgress(phase, message, percent, target, now - record.StartedUtc, phaseEta, phaseEta is { } pe ? pe + future : null);
    }

    private void EnterPhase(CalibrationPhase phase)
    {
        ResetTimers();
        _store.Update(s =>
        {
            if (s.Calibration is not null)
            {
                s.Calibration.Phase = phase.ToString();
            }
        });
        Log.Info($"Calibration phase: {phase}");
    }

    private void ResetTimers()
    {
        _phaseStartedUtc = DateTime.UtcNow;
        _lastPercentChangeUtc = DateTime.UtcNow;
        _acMissingSinceUtc = DateTime.MaxValue;
        _percentUnknownSinceUtc = DateTime.MaxValue;
        _notChargingTicks = 0;
        _lastPercent = null;
    }

    /// <summary>Ends the run. Returns the EC state to use straight away: the user's limit, no discharge.</summary>
    private ChargeTarget? Finish(CalibrationPhase phase, string message)
    {
        Activity(false);
        _awake.Set(false, false);

        var record = _store.Current.Calibration;
        if (record is null || record.FinishedUtc is not null)
        {
            return null;
        }

        _store.Update(s =>
        {
            record.Phase = phase.ToString();
            record.FinishedUtc = DateTime.UtcNow;
            record.Outcome = message;
            s.CalibrationHistory = [record, .. s.CalibrationHistory.Take(19)];
            s.Calibration = null;
        });

        Progress = new CalibrationProgress(phase, message, _lastPercent, 0, DateTime.UtcNow - record.StartedUtc, null, null);
        Log.Info($"Calibration finished: {phase}: {message}");
        Finished?.Invoke(Progress);
        return new ChargeTarget(_store.Current.ChargeLimit, false);
    }

    private void Activity(bool fast)
    {
        _awake.Set(system: true, display: fast);
        if (fast)
        {
            _load.Start(0.25);
        }
        else
        {
            _load.Stop();
        }
    }

    /// <summary>Stops load and lets the PC sleep again (exit, shutdown, suspend). The run resumes later.</summary>
    public void Pause()
    {
        _load.Stop();
        _awake.Set(false, false);
    }

    private static TimeSpan? Hours(double hours) => double.IsFinite(hours) && hours > 0 ? TimeSpan.FromHours(hours) : null;

    public void Dispose()
    {
        Pause();
        _load.Dispose();
        _awake.Dispose();
    }
}

/// <summary>Light, lowest-priority CPU load to speed up a discharge (MSI's tool uses 25% per thread).</summary>
internal sealed class LoadGenerator : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Thread> _threads = [];
    private volatile bool _running;
    private double _duty;

    public void Start(double duty)
    {
        lock (_gate)
        {
            _duty = Math.Clamp(duty, 0.05, 0.9);
            if (_running)
            {
                return;
            }

            _running = true;
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                var thread = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "calibration-load" };
                _threads.Add(thread);
                thread.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            foreach (var thread in _threads)
            {
                thread.Join(200);
            }

            _threads.Clear();
        }
    }

    private void Work()
    {
        var sw = new System.Diagnostics.Stopwatch();
        while (_running)
        {
            sw.Restart();
            var busy = (long)(100 * _duty);
            while (sw.ElapsedMilliseconds < busy)
            {
                Thread.SpinWait(200);
            }

            Thread.Sleep((int)(100 - busy));
        }
    }

    public void Dispose() => Stop();
}
