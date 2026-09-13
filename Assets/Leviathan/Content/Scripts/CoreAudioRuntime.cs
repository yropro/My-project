using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Shared custom-audio asset lookup and positional one-shot playback.
///
/// Core owns only the reusable mechanism: locating AudioClips in this mod's
/// AssetBundles, caching exact-name lookups, and routing playback through Star
/// Vortex's native SoundEffectPlayer. Class/spell sound choices remain with the
/// caller.
/// </summary>
public static class CoreAudioRuntime
{
    // A partially spatial source keeps useful left/right positioning without the
    // hard 100%-left/100%-right headphone effect of a fully 3D point source.
    // Individual sounds can override this per call when a different presentation
    // is appropriate.
    public const float DefaultSpatialBlend = 0.75f;

    // Negative distance overrides preserve SoundEffectPlayer.Attach()'s native
    // min/max distance configuration. Positive values opt a sound into custom
    // attenuation without changing the shared default.
    public const float UseNativeDistance = -1f;

    // Extra lifetime after a one-shot should have completed before its temporary
    // positional audio object is destroyed.
    public const float PositionalAudioCleanupPaddingSeconds = 0.50f;

    private static readonly Dictionary<string, AudioClip> Clips =
        new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MissingClips =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> WarnedMissingClips =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Presentation policy is intentionally separate from world-lifetime clip
    // caches. Callers register policy once from their own class/spell tuning and
    // it remains valid across world changes.
    private static readonly Dictionary<string, float> FadeOutStartSeconds =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Configures an optional delayed fade-out for all uses of one custom clip.
    /// A non-negative value keeps the sound at full volume until this many
    /// playback seconds have elapsed, then fades linearly to zero at clip end.
    /// A negative value removes the policy.
    /// </summary>
    public static void SetFadeOutStartSeconds(string clipName, float seconds)
    {
        if (string.IsNullOrEmpty(clipName))
            return;

        if (seconds < 0f)
        {
            FadeOutStartSeconds.Remove(clipName);
            return;
        }

        FadeOutStartSeconds[clipName] = Mathf.Max(0f, seconds);
    }

    /// <summary>
    /// Resolves an AudioClip by exact Unity asset basename. The first lookup is
    /// lazy; successful and unsuccessful results are cached until Reset().
    /// </summary>
    public static bool TryGetClip(string clipName, out AudioClip clip)
    {
        clip = null;
        if (string.IsNullOrEmpty(clipName))
            return false;

        if (Clips.TryGetValue(clipName, out clip) && clip != null)
            return true;

        if (MissingClips.Contains(clipName))
            return false;

        clip = ResolveClip(clipName);
        if (clip == null)
        {
            MissingClips.Add(clipName);
            return false;
        }

        Clips[clipName] = clip;
        return true;
    }

    /// <summary>
    /// Plays one spatial Effects-category sound through Star Vortex's native
    /// SoundEffectPlayer. Returns false when the requested clip is unavailable.
    /// Missing assets are safe and warn at most once per clip name.
    ///
    /// spatialBlend is clamped to Unity's 0..1 range. Negative min/max distance
    /// values leave the native SoundEffectPlayer attenuation distances untouched.
    /// Registered delayed fade-outs use the native SoundEffectPlayer fade rather
    /// than directly manipulating AudioSource volume.
    /// </summary>
    public static bool PlayPositionalOneShot(
        string clipName,
        Vector2 position,
        float volumeScale,
        string objectName = "Core Positional Audio",
        bool warnIfMissing = true,
        float spatialBlend = DefaultSpatialBlend,
        float minDistance = UseNativeDistance,
        float maxDistance = UseNativeDistance)
    {
        AudioClip clip;
        if (!TryGetClip(clipName, out clip))
        {
            if (warnIfMissing &&
                !string.IsNullOrEmpty(clipName) &&
                WarnedMissingClips.Add(clipName))
            {
                Debug.LogWarning(
                    "[CoreAudio] Could not find AudioClip '" + clipName +
                    "'. Ensure the clip is included in this mod's AssetBundle.");
            }

            return false;
        }

        GameObject audioObject = new GameObject(
            string.IsNullOrEmpty(objectName)
                ? "Core Positional Audio"
                : objectName);
        audioObject.transform.position = position;

        SoundEffectPlayer player = audioObject.AddComponent<SoundEffectPlayer>();
        player.Attach(audioObject, SoundEffectPlayer.Category.Effects);

        // Attach() creates/configures the native AudioSource. Apply only the
        // presentation overrides Core explicitly owns; native mixer routing,
        // rolloff mode and all other SoundEffectPlayer behavior remain intact.
        AudioSource audioSource = audioObject.GetComponent<AudioSource>();
        if (audioSource != null)
        {
            audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);

            if (minDistance >= 0f)
                audioSource.minDistance = Mathf.Max(0f, minDistance);

            if (maxDistance >= 0f)
                audioSource.maxDistance = Mathf.Max(audioSource.minDistance, maxDistance);
        }

        float fadeOutStartSeconds;
        bool fadeOut =
            FadeOutStartSeconds.TryGetValue(clipName, out fadeOutStartSeconds) &&
            fadeOutStartSeconds >= 0f &&
            clip.length > 0f &&
            fadeOutStartSeconds < clip.length;
        float fadeSeconds = fadeOut
            ? Mathf.Max(0f, clip.length - fadeOutStartSeconds)
            : 0f;

        SoundEffectPlayer.SoundEffect effect =
            new SoundEffectPlayer.SoundEffect();
        effect.audioClip = clip;
        effect.volumeScale = Mathf.Max(0f, volumeScale);
        effect.loop = false;
        effect.pitchVariance = 0f;
        effect.volumeVariance = 0f;
        effect.fadePitch = false;
        effect.minPitch = 1f;
        effect.maxPitch = 1f;
        effect.fadeVolume = fadeOut;
        effect.minVolume = fadeOut ? 0f : 1f;
        effect.maxVolume = 1f;
        effect.fadeSeconds = fadeSeconds;

        if (fadeOut)
        {
            // resetFade=false makes the native player begin immediately at its
            // configured maximum instead of interpreting fadeVolume as a fade-in.
            // Stop() later consumes the same native fade settings in reverse.
            player.Play(effect, false, true);
            CoreAudioDelayedFadeStop delayedFade =
                audioObject.AddComponent<CoreAudioDelayedFadeStop>();
            delayedFade.Arm(player, fadeOutStartSeconds);
        }
        else
        {
            // resetFade=true preserves the previous explicit pitch/volume setup.
            // priority=true preserves the previous custom-audio behavior.
            player.Play(effect, true, true);
        }

        UnityEngine.Object.Destroy(
            audioObject,
            Mathf.Max(
                1f,
                clip.length + PositionalAudioCleanupPaddingSeconds));

        return true;
    }

    private static AudioClip ResolveClip(string clipName)
    {
        // Prefer the ModInfo that actually owns the assembly containing Core.
        // This prevents another loaded mod with a same-named clip from winning.
        Assembly thisAssembly = typeof(CoreAudioRuntime).Assembly;
        List<ModInfo> loadedMods = ModLoader.GetLoadedMods();

        if (loadedMods != null)
        {
            for (int i = 0; i < loadedMods.Count; i++)
            {
                ModInfo mod = loadedMods[i];
                if (mod == null ||
                    mod.status != ModInfo.Status.Loaded ||
                    mod.assemblies == null ||
                    !mod.assemblies.Contains(thisAssembly))
                {
                    continue;
                }

                AudioClip ownedClip = FindClipInMod(mod, clipName);
                if (ownedClip != null)
                    return ownedClip;
            }

            // Defensive fallback for unusual loader/assembly arrangements.
            for (int i = 0; i < loadedMods.Count; i++)
            {
                ModInfo mod = loadedMods[i];
                if (mod == null || mod.status != ModInfo.Status.Loaded)
                    continue;

                AudioClip fallbackClip = FindClipInMod(mod, clipName);
                if (fallbackClip != null)
                    return fallbackClip;
            }
        }

        // Last resort for a clip already loaded for some other reason.
        AudioClip[] loadedClips = Resources.FindObjectsOfTypeAll<AudioClip>();
        if (loadedClips != null)
        {
            for (int i = 0; i < loadedClips.Length; i++)
            {
                AudioClip loadedClip = loadedClips[i];
                if (loadedClip != null &&
                    string.Equals(
                        loadedClip.name,
                        clipName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return loadedClip;
                }
            }
        }

        return null;
    }

    private static AudioClip FindClipInMod(ModInfo mod, string clipName)
    {
        if (mod == null ||
            mod.assetBundles == null ||
            string.IsNullOrEmpty(clipName))
        {
            return null;
        }

        for (int bundleIndex = 0;
            bundleIndex < mod.assetBundles.Count;
            bundleIndex++)
        {
            AssetBundle bundle = mod.assetBundles[bundleIndex];
            if (bundle == null)
                continue;

            string[] assetNames;
            try
            {
                assetNames = bundle.GetAllAssetNames();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[CoreAudio] Could not inspect AssetBundle for '" +
                    clipName + "': " + ex.Message);
                continue;
            }

            if (assetNames == null)
                continue;

            for (int assetIndex = 0;
                assetIndex < assetNames.Length;
                assetIndex++)
            {
                string assetName = assetNames[assetIndex];
                if (string.IsNullOrEmpty(assetName) ||
                    !string.Equals(
                        Path.GetFileNameWithoutExtension(assetName),
                        clipName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    AudioClip clip = bundle.LoadAsset<AudioClip>(assetName);
                    if (clip != null)
                        return clip;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        "[CoreAudio] Failed loading AudioClip '" +
                        assetName + "': " + ex.Message);
                }
            }
        }

        return null;
    }

    public static void Reset()
    {
        Clips.Clear();
        MissingClips.Clear();
        WarnedMissingClips.Clear();
    }
}

/// <summary>
/// Per-playback timer that asks the native SoundEffectPlayer to begin its authored
/// fade-out after a caller-selected amount of playback time. Time.deltaTime keeps
/// this aligned with the game's own fade/pause semantics.
/// </summary>
public sealed class CoreAudioDelayedFadeStop : MonoBehaviour
{
    private SoundEffectPlayer player;
    private float triggerSeconds;
    private float elapsedSeconds;
    private bool triggered;

    public void Arm(SoundEffectPlayer soundPlayer, float seconds)
    {
        player = soundPlayer;
        triggerSeconds = Mathf.Max(0f, seconds);
        elapsedSeconds = 0f;
        triggered = false;

        if (triggerSeconds <= 0f)
            Trigger();
    }

    private void Update()
    {
        if (triggered || player == null)
            return;

        elapsedSeconds += Time.deltaTime;
        if (elapsedSeconds >= triggerSeconds)
            Trigger();
    }

    private void Trigger()
    {
        if (triggered)
            return;

        triggered = true;
        if (player != null)
            player.Stop();
        Destroy(this);
    }
}

/// <summary>
/// Explicit world-lifetime reset for static audio caches. Harmony registration
/// is already assembly-wide through the mod's existing PatchAll().
/// </summary>
[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreAudioRuntimeWorldDestroyPatch
{
    public static void Prefix()
    {
        CoreAudioRuntime.Reset();
    }
}
