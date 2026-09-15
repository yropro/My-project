using System;

/// <summary>
/// Immutable per-voice timing. Durations are scaled playback seconds at native
/// base pitch 1, including fade. No clip, owner, scene or global policy is held.
/// </summary>
public struct CoreAudioPlaybackTiming
{
    public readonly float EndSeconds;
    public readonly float FadeStartSeconds;
    public readonly float FadeSeconds;
    public bool HasFade { get { return FadeSeconds > 0f; } }
    public bool Silent { get { return EndSeconds <= 0f; } }

    private CoreAudioPlaybackTiming(float end, float fadeStart)
    {
        EndSeconds = end;
        FadeStartSeconds = fadeStart;
        FadeSeconds = fadeStart >= 0f ? end - fadeStart : 0f;
    }

    /// <summary>
    /// -1 duration means natural length; zero means intentional silence. A
    /// positive duration caps but never stretches/repeats the source clip.
    /// -1 fade means no fade. Fade at/after end becomes a hard end, not fade-in.
    /// Invalid values fail before allocation or changes to another voice.
    /// </summary>
    public static bool TryResolve(float clipSeconds, float durationSeconds,
        float fadeStartSeconds, out CoreAudioPlaybackTiming timing)
    {
        timing = default(CoreAudioPlaybackTiming);
        if (!Finite(clipSeconds) || clipSeconds < 0f ||
            !Finite(durationSeconds) || durationSeconds < -1f ||
            (durationSeconds < 0f && durationSeconds != -1f) ||
            !Finite(fadeStartSeconds) || fadeStartSeconds < -1f ||
            (fadeStartSeconds < 0f && fadeStartSeconds != -1f)) return false;
        float end = durationSeconds < 0f ? clipSeconds : Math.Min(clipSeconds, durationSeconds);
        float fade = fadeStartSeconds >= 0f && fadeStartSeconds < end
            ? fadeStartSeconds : -1f;
        timing = new CoreAudioPlaybackTiming(end, fade);
        return true;
    }

    private static bool Finite(float value)
    { return !float.IsNaN(value) && !float.IsInfinity(value); }
}
