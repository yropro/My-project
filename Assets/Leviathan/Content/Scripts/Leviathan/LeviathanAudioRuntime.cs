using UnityEngine;

/// <summary>
/// Leviathan-owned sound selection for Stellar Converter presentation.
/// Generic asset discovery, caching and playback live in CoreAudioRuntime.
/// </summary>
public static class LeviathanAudioRuntime
{
    // Unity AudioClip.name normally omits the file extension.
    public const string DyingStarExplosionClipName = "seismic_charge";
    public const float DyingStarExplosionVolume = 1.00f;

    /// <summary>
    /// Plays the Dying Star detonation cue. The historical method name is kept
    /// here only because the existing Stellar Converter call sites use it; the
    /// sound choice itself is now explicitly Dying Star-owned.
    /// </summary>
    public static void PlayEventHorizonExplosion(Vector2 position)
    {
        CoreAudioRuntime.PlayPositionalOneShot(
            DyingStarExplosionClipName,
            position,
            DyingStarExplosionVolume,
            "Leviathan Dying Star Explosion Audio");
    }
}
