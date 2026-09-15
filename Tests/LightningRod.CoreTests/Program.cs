using System;
using Rod = OrreryLightningRodState<object>;

// These tests link the actual production C# classes; they are not a rewritten
// Python reference model. They do NOT exercise Unity, native damage, the audio
// device, target acquisition or network transport. No NuGet packages are used.
internal static class Program
{
    private static int assertions;

    private static int Main()
    {
        Action[] groups = {
            AudioNaturalAndSilent, AudioDurationAndFade, AudioInvalid,
            AudioIndependentValues, RodBaselineAndIdentity, RodExpiryBoundary,
            RodFirstDelayAndCount, RodTargetAndRangeCancellation,
            RodRangeGrace, RodClockAndRepeatedTick, RodHitch, RodInvalidConfiguration
        };
        try
        {
            for (int i = 0; i < groups.Length; i++) groups[i]();
            Console.WriteLine("PASS: " + groups.Length + " regression groups (" + assertions + " assertions).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + assertions + " assertions: " + error.Message);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(name);
    }
    private static void Equal<T>(T expected, T actual, string name)
    {
        Check(object.Equals(expected, actual), name + ": expected " + expected + ", got " + actual);
    }
    private static void Near(double expected, double actual, string name)
    {
        Check(Math.Abs(expected - actual) < 0.00001, name);
    }
    private static CoreAudioPlaybackTiming Audio(float length, float duration, float fade)
    {
        CoreAudioPlaybackTiming value;
        Check(CoreAudioPlaybackTiming.TryResolve(length, duration, fade, out value), "valid audio timing");
        return value;
    }
    private static Rod Start(double duration = 6, double delay = 0, double interval = 2,
        int count = 3, double grace = 0, double now = 0)
    {
        Rod value;
        Check(Rod.TryStart(new object(), now, new Rod.Settings(duration, delay, interval, count, grace), out value),
            "valid rod settings");
        return value;
    }

    private static void AudioNaturalAndSilent()
    {
        CoreAudioPlaybackTiming natural = Audio(4, -1, -1);
        Near(4, natural.EndSeconds, "natural length");
        Check(!natural.HasFade && !natural.Silent, "natural envelope");
        CoreAudioPlaybackTiming silent = Audio(4, 0, 0);
        Check(silent.Silent && !silent.HasFade, "zero duration is silent");
        Check(Audio(0, -1, -1).Silent, "empty clip is silent");
    }
    private static void AudioDurationAndFade()
    {
        CoreAudioPlaybackTiming shortVoice = Audio(4, 0.75f, 0.5f);
        Near(0.75, shortVoice.EndSeconds, "duration includes fade");
        Near(0.5, shortVoice.FadeStartSeconds, "per-call fade start");
        Near(0.25, shortVoice.FadeSeconds, "fade length");
        Check(shortVoice.HasFade, "fade enabled");
        Near(4, Audio(4, 40, -1).EndSeconds, "duration never stretches clip");
        Check(!Audio(4, 0.5f, 0.5f).HasFade, "fade at end is hard stop");
        Check(!Audio(4, 0.5f, 2).HasFade, "fade past end is hard stop");
        Near(0.5, Audio(4, 0.5f, 0).FadeSeconds, "fade from time zero");
        Near(1.765, Audio(4, -1, 2.235f).FadeSeconds, "existing Plasma fade timing");
    }
    private static void AudioInvalid()
    {
        float[] invalid = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -2f, -0.5f };
        CoreAudioPlaybackTiming value;
        for (int i = 0; i < invalid.Length; i++)
        {
            Check(!CoreAudioPlaybackTiming.TryResolve(4, invalid[i], -1, out value), "reject invalid duration");
            Check(!CoreAudioPlaybackTiming.TryResolve(4, -1, invalid[i], out value), "reject invalid fade");
        }
        Check(!CoreAudioPlaybackTiming.TryResolve(-1, -1, -1, out value), "reject negative clip length");
        Check(!CoreAudioPlaybackTiming.TryResolve(float.NaN, -1, -1, out value), "reject NaN clip length");
        Check(!CoreAudioPlaybackTiming.TryResolve(float.PositiveInfinity, -1, -1, out value), "reject infinite clip length");
    }
    private static void AudioIndependentValues()
    {
        CoreAudioPlaybackTiming a = Audio(4, 0.75f, 0.5f);
        CoreAudioPlaybackTiming b = Audio(4, -1, 2.235f);
        CoreAudioPlaybackTiming c = Audio(4, 1, -1);
        Near(0.75, a.EndSeconds, "first voice unaffected by later resolutions");
        Near(4, b.EndSeconds, "natural voice independent");
        Near(1, c.EndSeconds, "third voice independent");
        Check(a.HasFade && b.HasFade && !c.HasFade, "independent fade policy");
    }
    private static void RodBaselineAndIdentity()
    {
        object target = new object();
        Rod rod;
        Check(Rod.TryStart(target, 10, new Rod.Settings(6, 0, 2, 3, 0), out rod), "start baseline");
        Check(ReferenceEquals(target, rod.Target), "exact target retained");
        Check(!typeof(Rod).GetProperty("Target").CanWrite, "target cannot be reassigned");
        Equal(Rod.Step.Strike, rod.Tick(10, true, true), "immediate strike");
        Equal(Rod.Step.None, rod.Tick(11.99, true, true), "wait for second");
        Equal(Rod.Step.Strike, rod.Tick(12, true, true), "second strike");
        Equal(Rod.Step.Strike, rod.Tick(14, true, true), "third strike");
        Check(rod.IsActive, "last hit does not end six-second lifetime");
        Equal(Rod.Step.None, rod.Tick(15.99, true, true), "wait until end");
        Equal(Rod.Step.Completed, rod.Tick(16, true, true), "complete at six seconds");
        Equal(3, rod.StrikesPerformed, "exactly three baseline strikes");
        Equal(Rod.EndReason.DurationElapsed, rod.Reason, "completion reason");
        Equal(Rod.Step.None, rod.Tick(18, true, true), "completion is terminal");
    }
    private static void RodExpiryBoundary()
    {
        Rod rod = Start(count: 64);
        Equal(Rod.Step.Strike, rod.Tick(0, true, true), "first");
        Equal(Rod.Step.Strike, rod.Tick(2, true, true), "second");
        Equal(Rod.Step.Strike, rod.Tick(4, true, true), "third");
        Equal(Rod.Step.Completed, rod.Tick(6, true, true), "no boundary fourth hit");
        Equal(3, rod.StrikesPerformed, "exclusive lifetime wins over maximum count");
    }
    private static void RodFirstDelayAndCount()
    {
        Rod delayed = Start(delay: 1);
        Equal(Rod.Step.None, delayed.Tick(0, true, true), "first delay");
        Equal(Rod.Step.Strike, delayed.Tick(1, true, true), "delayed first");
        Equal(Rod.Step.Strike, delayed.Tick(3, true, true), "delayed second");
        Equal(Rod.Step.Strike, delayed.Tick(5, true, true), "delayed third");
        Equal(Rod.Step.Completed, delayed.Tick(6, true, true), "delay does not extend lifetime");
        Rod limited = Start(duration: 10, count: 1);
        Equal(Rod.Step.Strike, limited.Tick(0, true, true), "single allowed hit");
        Equal(Rod.Step.None, limited.Tick(8, true, true), "count cap independent of duration");
        Check(limited.IsActive, "count cap is not completion");
        Equal(Rod.Step.Completed, limited.Tick(10, true, true), "count-limited lifetime");
    }
    private static void RodTargetAndRangeCancellation()
    {
        Rod invalid = Start();
        Equal(Rod.Step.Cancelled, invalid.Tick(0, false, true), "invalid before first hit");
        Equal(0, invalid.StrikesPerformed, "no invalid-target hit");
        Equal(Rod.EndReason.TargetInvalid, invalid.Reason, "target cancellation reason");
        Equal(Rod.Step.None, invalid.Tick(2, true, true), "validity return cannot restart");
        Rod broken = Start();
        broken.Tick(0, true, true);
        Equal(Rod.Step.Cancelled, broken.Tick(1, true, false), "immediate range break");
        Equal(Rod.EndReason.RangeBroken, broken.Reason, "range cancellation reason");
        Equal(Rod.Step.None, broken.Tick(2, true, true), "range return cannot resume");
        Rod cancelled = Start();
        Equal(Rod.Step.Cancelled, cancelled.Cancel(), "owner cancellation");
        Equal(Rod.Step.None, cancelled.Cancel(), "cancel idempotence");
        Equal(Rod.Step.None, cancelled.Tick(0, true, true), "no hit after explicit cancel");
    }
    private static void RodRangeGrace()
    {
        Rod returned = Start(grace: 0.25);
        returned.Tick(0, true, true);
        Equal(Rod.Step.None, returned.Tick(2, true, false), "no damage outside during grace");
        Equal(1, returned.StrikesPerformed, "outside does not consume a strike");
        Equal(Rod.Step.Strike, returned.Tick(2.125, true, true), "return inside grace");
        Rod expired = Start(grace: 0.25);
        expired.Tick(0, true, true);
        Equal(Rod.Step.None, expired.Tick(1, true, false), "begin grace");
        Equal(Rod.Step.Cancelled, expired.Tick(1.25, true, false), "grace boundary cancels");
        Equal(Rod.Step.None, expired.Tick(2, true, true), "expired grace cannot resume");
    }
    private static void RodClockAndRepeatedTick()
    {
        Rod rod = Start();
        Equal(Rod.Step.Strike, rod.Tick(0, true, true), "first at timestamp");
        Equal(Rod.Step.None, rod.Tick(0, true, true), "no duplicate timestamp strike");
        Equal(Rod.Step.Cancelled, rod.Tick(0, false, true), "same-timestamp invalidation still terminates");
        double[] clocks = { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        for (int i = 0; i < clocks.Length; i++)
        {
            Rod invalid = Start();
            Equal(Rod.Step.Cancelled, invalid.Tick(clocks[i], true, true), "clock fail closed");
            Equal(Rod.EndReason.InvalidClock, invalid.Reason, "invalid-clock reason");
        }
        Rod rewind = Start();
        rewind.Tick(1, true, true);
        Equal(Rod.Step.Cancelled, rewind.Tick(0.5, true, true), "backward clock terminates");
    }
    private static void RodHitch()
    {
        Rod rod = Start();
        rod.Tick(0, true, true);
        Equal(Rod.Step.Strike, rod.Tick(3, true, true), "late second strike");
        Equal(Rod.Step.None, rod.Tick(3, true, true), "no same-tick catch-up");
        Equal(Rod.Step.None, rod.Tick(3.1, true, true), "no next-tick catch-up");
        Equal(Rod.Step.None, rod.Tick(4, true, true), "no shortened interval after hitch");
        Equal(Rod.Step.Strike, rod.Tick(5, true, true), "next hit preserves full interval");
        Equal(Rod.Step.Completed, rod.Tick(6, true, true), "hitch never extends cast");
        Rod expired = Start();
        expired.Tick(0, true, true);
        Equal(Rod.Step.Completed, expired.Tick(20, true, true), "long hitch expires without replay");
        Equal(1, expired.StrikesPerformed, "missed hits not backfilled after expiry");
    }
    private static void RodInvalidConfiguration()
    {
        Rod rod;
        Rod.Settings valid = new Rod.Settings(6, 0, 2, 3, 0);
        Check(!Rod.TryStart(null, 0, valid, out rod), "null target rejected");
        Check(!Rod.TryStart(new object(), -1, valid, out rod), "negative start rejected");
        Check(!Rod.TryStart(new object(), double.NaN, valid, out rod), "NaN start rejected");
        Check(!Rod.TryStart(new object(), double.MaxValue, valid, out rod), "unrepresentable deadline rejected");
        Rod.Settings[] invalid = {
            default(Rod.Settings), new Rod.Settings(0, 0, 2, 3, 0),
            new Rod.Settings(121, 0, 2, 3, 0), new Rod.Settings(6, -1, 2, 3, 0),
            new Rod.Settings(6, 6, 2, 3, 0), new Rod.Settings(6, 0, 0, 3, 0),
            new Rod.Settings(6, 0, 0.01, 3, 0), new Rod.Settings(6, 0, 2, 0, 0),
            new Rod.Settings(6, 0, 2, 65, 0), new Rod.Settings(6, 0, 2, 3, -1),
            new Rod.Settings(6, 0, 2, 3, 7), new Rod.Settings(double.NaN, 0, 2, 3, 0),
            new Rod.Settings(6, 0, double.PositiveInfinity, 3, 0)
        };
        for (int i = 0; i < invalid.Length; i++)
            Check(!Rod.TryStart(new object(), 0, invalid[i], out rod), "invalid settings rejected");
    }
}
