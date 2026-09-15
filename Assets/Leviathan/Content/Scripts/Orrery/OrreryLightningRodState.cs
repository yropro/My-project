using System;

/// <summary>
/// Owner-side Lightning Rod target lock and schedule. TTarget is GameShip at the
/// integration boundary; keeping this state independent of Unity makes the real
/// scheduling implementation testable without replacing it with a test model.
///
/// This is NOT a remote presentation timer. Only an authoritative owner may turn
/// a Strike result into gameplay and then publish an accepted strike event.
/// The integration layer owns target acquisition, native validity/range checks,
/// CoreAbilityExecution, damage, Compendium resolution and replica lifetimes.
/// </summary>
public sealed class OrreryLightningRodState<TTarget> where TTarget : class
{
    // Guardrails, not balance defaults. Reject invalid tuning rather than silently
    // changing the authored duration/cadence. No backlog loop exists in Tick.
    public const double MaximumDurationSeconds = 120.0;
    public const double MinimumStrikeIntervalSeconds = 0.05;
    public const int MaximumStrikeCount = 64;

    public struct Settings
    {
        public readonly double DurationSeconds;
        public readonly double FirstStrikeDelaySeconds;
        public readonly double StrikeIntervalSeconds;
        public readonly int MaxStrikes;
        public readonly double RangeBreakGraceSeconds;

        public Settings(double durationSeconds, double firstStrikeDelaySeconds,
            double strikeIntervalSeconds, int maxStrikes, double rangeBreakGraceSeconds)
        {
            DurationSeconds = durationSeconds;
            FirstStrikeDelaySeconds = firstStrikeDelaySeconds;
            StrikeIntervalSeconds = strikeIntervalSeconds;
            MaxStrikes = maxStrikes;
            RangeBreakGraceSeconds = rangeBreakGraceSeconds;
        }

        public bool IsValid
        {
            get
            {
                return Finite(DurationSeconds) && DurationSeconds > 0.0 &&
                    DurationSeconds <= MaximumDurationSeconds &&
                    Finite(FirstStrikeDelaySeconds) && FirstStrikeDelaySeconds >= 0.0 &&
                    FirstStrikeDelaySeconds < DurationSeconds &&
                    Finite(StrikeIntervalSeconds) &&
                    StrikeIntervalSeconds >= MinimumStrikeIntervalSeconds &&
                    StrikeIntervalSeconds <= MaximumDurationSeconds &&
                    MaxStrikes >= 1 && MaxStrikes <= MaximumStrikeCount &&
                    Finite(RangeBreakGraceSeconds) && RangeBreakGraceSeconds >= 0.0 &&
                    RangeBreakGraceSeconds <= DurationSeconds;
            }
        }
    }

    public enum Step : byte { None, Strike, Completed, Cancelled }
    public enum EndReason : byte { None, DurationElapsed, TargetInvalid, RangeBroken, OwnerCancelled, InvalidClock }

    private readonly TTarget target;
    private readonly Settings settings;
    private readonly double endsAt;
    private double nextStrikeAt;
    private double lastTickAt;
    private double outOfRangeSince;
    private bool hasTicked;
    private bool outOfRange;
    private int strikesPerformed;
    private EndReason endReason;

    public TTarget Target { get { return target; } }
    public bool IsActive { get { return endReason == EndReason.None; } }
    public int StrikesPerformed { get { return strikesPerformed; } }
    public EndReason Reason { get { return endReason; } }
    public double EndsAt { get { return endsAt; } }

    private OrreryLightningRodState(TTarget lockedTarget, double now, Settings configuration)
    {
        target = lockedTarget;
        settings = configuration;
        lastTickAt = now;
        endsAt = now + settings.DurationSeconds;
        nextStrikeAt = now + settings.FirstStrikeDelaySeconds;
    }

    public static bool TryStart(TTarget lockedTarget, double nowScaledSeconds,
        Settings configuration, out OrreryLightningRodState<TTarget> state)
    {
        state = null;
        if (ReferenceEquals(lockedTarget, null) || !configuration.IsValid ||
            !Finite(nowScaledSeconds) || nowScaledSeconds < 0.0 ||
            !Finite(nowScaledSeconds + configuration.DurationSeconds) ||
            nowScaledSeconds + configuration.DurationSeconds <= nowScaledSeconds)
            return false;
        state = new OrreryLightningRodState<TTarget>(lockedTarget, nowScaledSeconds, configuration);
        return true;
    }

    /// <summary>
    /// One authoritative simulation step. Validity and range always refer to the
    /// immutable Target, never a fresh cursor query. Out-of-range grace allows a
    /// return but never deals damage while outside range. Expiry is exclusive:
    /// a strike due exactly at EndsAt is not emitted.
    ///
    /// At most one strike is emitted per distinct timestamp. After a late hit,
    /// the next interval starts at that accepted hit: no catch-up burst, no
    /// shortened gap, and no replay after expiry. The integration must invoke
    /// Tick once per owner simulation step.
    /// </summary>
    public Step Tick(double nowScaledSeconds, bool targetValid, bool withinTetherRange)
    {
        if (!IsActive) return Step.None;
        if (!Finite(nowScaledSeconds) || nowScaledSeconds < lastTickAt)
            return End(EndReason.InvalidClock);
        if (!targetValid) return End(EndReason.TargetInvalid);
        if (nowScaledSeconds >= endsAt) return End(EndReason.DurationElapsed);

        bool repeatedTimestamp = hasTicked && nowScaledSeconds == lastTickAt;
        lastTickAt = nowScaledSeconds;
        hasTicked = true;

        if (!withinTetherRange)
        {
            if (!outOfRange)
            {
                outOfRange = true;
                outOfRangeSince = nowScaledSeconds;
            }
            if (nowScaledSeconds - outOfRangeSince >= settings.RangeBreakGraceSeconds)
                return End(EndReason.RangeBroken);
            return Step.None;
        }

        outOfRange = false;
        if (repeatedTimestamp || strikesPerformed >= settings.MaxStrikes ||
            nowScaledSeconds < nextStrikeAt) return Step.None;

        // Consume before returning: nested callbacks cannot consume this hit twice.
        strikesPerformed++;
        nextStrikeAt = nowScaledSeconds + settings.StrikeIntervalSeconds;
        return Step.Strike;
    }

    public Step Cancel()
    {
        return IsActive ? End(EndReason.OwnerCancelled) : Step.None;
    }

    private Step End(EndReason reason)
    {
        endReason = reason;
        return reason == EndReason.DurationElapsed ? Step.Completed : Step.Cancelled;
    }

    private static bool Finite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
