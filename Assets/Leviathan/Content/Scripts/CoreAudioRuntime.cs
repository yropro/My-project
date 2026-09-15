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

    private static readonly HashSet<GameObject> Voices = new HashSet<GameObject>();

    // Presentation policy is intentionally separate from world-lifetime clip
    // caches. Callers register policy once from their own class/spell tuning and
    // it remains valid across world changes.
    private static readonly Dictionary<string, float> FadeOutStartSeconds =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Configures a default delayed fade-out for calls without a per-call override.
    /// Existing voices capture their policy and never reread this table.
    /// A non-negative value keeps the sound at full volume until this many
    /// playback seconds have elapsed, then fades linearly to zero at clip end.
    /// A negative value removes the policy.
    /// </summary>
    public static void SetFadeOutStartSeconds(string clipName, float seconds)
    {
        if (string.IsNullOrEmpty(clipName) || !IsFinite(seconds))
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
    /// Native Effects-category playback. Duration includes fade; -1 uses the
    /// natural clip length, zero is a successful silent no-op. Null fade uses the
    /// existing clip default; -1 explicitly disables fade for only this call.
    /// Neither setting changes the asset cache or any other playback instance.
    /// Scaled time and native pitch/pause semantics are preserved.
    /// </summary>
    public static bool PlayPositionalOneShot(
        string clipName, Vector2 position, float volumeScale,
        string objectName = "Core Positional Audio", bool warnIfMissing = true,
        float spatialBlend = DefaultSpatialBlend,
        float minDistance = UseNativeDistance, float maxDistance = UseNativeDistance,
        float playbackDurationSeconds = -1f, float? fadeOutStartSeconds = null)
    {
        float fadeStart;
        if (fadeOutStartSeconds.HasValue) fadeStart = fadeOutStartSeconds.Value;
        else if (string.IsNullOrEmpty(clipName) ||
            !FadeOutStartSeconds.TryGetValue(clipName, out fadeStart)) fadeStart = -1f;

        CoreAudioPlaybackTiming timing;
        // Validate even silent calls before consulting/allocating asset state.
        if (!CoreAudioPlaybackTiming.TryResolve(0f, playbackDurationSeconds, fadeStart, out timing) ||
            !IsFinite(volumeScale) || !IsFinite(spatialBlend) ||
            !IsFinite(minDistance) || !IsFinite(maxDistance) ||
            !IsFinite(position.x) || !IsFinite(position.y)) return false;
        if (playbackDurationSeconds == 0f || volumeScale <= 0f) return true;

        AudioClip clip;
        if (!TryGetClip(clipName, out clip))
        {
            if (warnIfMissing && !string.IsNullOrEmpty(clipName) && WarnedMissingClips.Add(clipName))
                Debug.LogWarning("[CoreAudio] Could not find AudioClip '" + clipName +
                    "'. Ensure the clip is included in this mod's AssetBundle.");
            return false;
        }
        if (!CoreAudioPlaybackTiming.TryResolve(clip.length, playbackDurationSeconds, fadeStart, out timing))
            return false;
        if (timing.Silent) return true;

        GameObject audioObject = new GameObject(string.IsNullOrEmpty(objectName)
            ? "Core Positional Audio" : objectName);
        bool retained = false;
        try
        {
            audioObject.transform.position = position;
            SoundEffectPlayer player = audioObject.AddComponent<SoundEffectPlayer>();
            player.Attach(audioObject, SoundEffectPlayer.Category.Effects);
            AudioSource audioSource = audioObject.GetComponent<AudioSource>();
            if (audioSource == null) return false;
            audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);
            if (minDistance >= 0f) audioSource.minDistance = Mathf.Max(0f, minDistance);
            if (maxDistance >= 0f) audioSource.maxDistance = Mathf.Max(audioSource.minDistance, maxDistance);

            SoundEffectPlayer.SoundEffect effect = new SoundEffectPlayer.SoundEffect();
            effect.audioClip = clip;
            effect.volumeScale = Mathf.Max(0f, volumeScale);
            effect.loop = false;
            effect.pitchVariance = 0f;
            effect.volumeVariance = 0f;
            effect.fadePitch = false;
            effect.minPitch = effect.maxPitch = 1f;
            effect.fadeVolume = timing.HasFade;
            effect.minVolume = timing.HasFade ? 0f : 1f;
            effect.maxVolume = 1f;
            effect.fadeSeconds = timing.FadeSeconds;

            // resetFade=false starts a fading voice at full authored volume.
            player.Play(effect, !timing.HasFade, true);
            CoreAudioVoiceLifetime lifetime = audioObject.AddComponent<CoreAudioVoiceLifetime>();
            Voices.Add(audioObject);
            lifetime.Arm(player, audioSource, effect, timing);
            retained = true;
            return true;
        }
        finally
        {
            if (!retained) UnityEngine.Object.Destroy(audioObject);
        }
    }

    internal static void ForgetVoice(GameObject voice) { Voices.Remove(voice); }
    private static bool IsFinite(float value)
    { return !float.IsNaN(value) && !float.IsInfinity(value); }

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
        // Allocation is deliberately at teardown, never in the playback hot path.
        GameObject[] active = new GameObject[Voices.Count];
        Voices.CopyTo(active);
        Voices.Clear();
        for (int i = 0; i < active.Length; i++)
        {
            if (active[i] == null) continue;
            AudioSource source = active[i].GetComponent<AudioSource>();
            if (source != null) source.Stop();
            UnityEngine.Object.Destroy(active[i]);
        }
        Clips.Clear(); MissingClips.Clear(); WarnedMissingClips.Clear();
    }
}

/// <summary>
/// Per-voice native fade and a hard audible deadline. FadeStop's scheduling
/// error cannot extend a voice beyond its requested end. No shared clip is edited.
/// </summary>
public sealed class CoreAudioVoiceLifetime : MonoBehaviour
{
    private SoundEffectPlayer player;
    private AudioSource source;
    private SoundEffectPlayer.SoundEffect effect;
    private CoreAudioPlaybackTiming timing;
    private float elapsed;
    private bool fadeStarted;
    private bool stopped;

    public void Arm(SoundEffectPlayer soundPlayer, AudioSource audioSource,
        SoundEffectPlayer.SoundEffect soundEffect, CoreAudioPlaybackTiming voiceTiming)
    {
        player = soundPlayer; source = audioSource; effect = soundEffect;
        timing = voiceTiming; elapsed = 0f; fadeStarted = stopped = false;
        Tick();
    }

    private void Update() { elapsed += Time.deltaTime; Tick(); }
    private void Tick()
    {
        if (!stopped && (source == null || elapsed >= timing.EndSeconds))
        {
            stopped = true;
            // Stop() alone would begin another native fade; enforce the already
            // budgeted end even if native Update ran before this component.
            if (source != null) source.Stop();
            if (player != null) player.Stop();
        }
        if (!stopped && !fadeStarted && timing.HasFade && elapsed >= timing.FadeStartSeconds)
        {
            fadeStarted = true;
            effect.fadeSeconds = Mathf.Max(0.0001f, timing.EndSeconds - elapsed);
            if (player != null) player.Stop();
        }
        if (elapsed >= Mathf.Max(1f,
            timing.EndSeconds + CoreAudioRuntime.PositionalAudioCleanupPaddingSeconds))
            Destroy(gameObject);
    }
    private void OnDestroy() { CoreAudioRuntime.ForgetVoice(gameObject); }
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
