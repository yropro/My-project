using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

// Lightweight Leviathan audio helper. No explicit initialization is required:
// clips are resolved lazily from this mod's loaded AssetBundles the first time
// they are requested.
public static class LeviathanAudioRuntime
{
    // =========================================================================
    // TUNING
    // =========================================================================

    // Unity AudioClip.name normally omits the file extension.
    public const string EventHorizonExplosionClipName = "seismic_charge";

    // Multiplies the clip's one-shot volume. Star Vortex's Effects mixer and
    // positional rolloff are still applied normally.
    public const float EventHorizonExplosionVolume = 1.00f;

    // Extra lifetime after the clip should have completed before the temporary
    // positional audio object is destroyed.
    public const float PositionalAudioCleanupPaddingSeconds = 0.50f;

    // =========================================================================
    // CACHE
    // =========================================================================

    private static AudioClip eventHorizonExplosionClip;
    private static bool searchedEventHorizonExplosionClip;
    private static bool warnedMissingEventHorizonExplosionClip;

    // =========================================================================
    // EVENT HORIZON
    // =========================================================================

    public static void PlayEventHorizonExplosion(Vector2 position)
    {
        AudioClip clip = ResolveEventHorizonExplosionClip();
        if (clip == null)
        {
            if (!warnedMissingEventHorizonExplosionClip)
            {
                warnedMissingEventHorizonExplosionClip = true;
                Debug.LogWarning(
                    "[Leviathan] Could not find AudioClip '" +
                    EventHorizonExplosionClipName +
                    "'. Make sure seismic_charge.wav is assigned to a Leviathan AssetBundle."
                );
            }

            return;
        }

        GameObject audioObject =
            new GameObject("Leviathan Event Horizon Explosion Audio");
        audioObject.transform.position = position;

        SoundEffectPlayer player = audioObject.AddComponent<SoundEffectPlayer>();
        player.Attach(audioObject, SoundEffectPlayer.Category.Effects);

        SoundEffectPlayer.SoundEffect effect =
            new SoundEffectPlayer.SoundEffect();
        effect.audioClip = clip;
        effect.volumeScale = EventHorizonExplosionVolume;
        effect.loop = false;
        effect.pitchVariance = 0f;
        effect.volumeVariance = 0f;
        effect.fadePitch = false;
        effect.minPitch = 1f;
        effect.maxPitch = 1f;
        effect.fadeVolume = false;
        effect.minVolume = 1f;
        effect.maxVolume = 1f;
        effect.fadeSeconds = 0f;

        // resetFade=true applies the explicit pitch/volume values above.
        // priority=true uses Star Vortex's higher-priority SFX setting.
        player.Play(effect, true, true);

        UnityEngine.Object.Destroy(
            audioObject,
            Mathf.Max(
                1f,
                clip.length + PositionalAudioCleanupPaddingSeconds
            )
        );
    }

    // =========================================================================
    // CLIP RESOLUTION
    // =========================================================================

    private static AudioClip ResolveEventHorizonExplosionClip()
    {
        if (eventHorizonExplosionClip != null)
            return eventHorizonExplosionClip;

        if (searchedEventHorizonExplosionClip)
            return null;

        searchedEventHorizonExplosionClip = true;

        // Prefer the ModInfo that actually owns the assembly containing this
        // runtime. This prevents another mod with a same-named clip from winning.
        Assembly thisAssembly = typeof(LeviathanAudioRuntime).Assembly;
        List<ModInfo> loadedMods = ModLoader.GetLoadedMods();

        if (loadedMods != null)
        {
            for (int i = 0; i < loadedMods.Count; i++)
            {
                ModInfo mod = loadedMods[i];
                if (mod == null || mod.status != ModInfo.Status.Loaded ||
                    mod.assemblies == null || !mod.assemblies.Contains(thisAssembly))
                {
                    continue;
                }

                AudioClip clip = FindClipInMod(
                    mod,
                    EventHorizonExplosionClipName);
                if (clip != null)
                {
                    eventHorizonExplosionClip = clip;
                    Debug.Log(
                        "[Leviathan] Loaded Event Horizon sound '" +
                        clip.name + "' from mod '" + mod.name + "'."
                    );
                    return eventHorizonExplosionClip;
                }
            }

            // Fallback for unusual loader/assembly arrangements: search other
            // loaded mod bundles by exact clip basename.
            for (int i = 0; i < loadedMods.Count; i++)
            {
                ModInfo mod = loadedMods[i];
                if (mod == null || mod.status != ModInfo.Status.Loaded)
                    continue;

                AudioClip clip = FindClipInMod(
                    mod,
                    EventHorizonExplosionClipName);
                if (clip != null)
                {
                    eventHorizonExplosionClip = clip;
                    Debug.Log(
                        "[Leviathan] Loaded Event Horizon sound '" +
                        clip.name + "' from bundle fallback."
                    );
                    return eventHorizonExplosionClip;
                }
            }
        }

        // Last-resort fallback for a clip already loaded for some other reason.
        AudioClip[] loadedClips = Resources.FindObjectsOfTypeAll<AudioClip>();
        if (loadedClips != null)
        {
            for (int i = 0; i < loadedClips.Length; i++)
            {
                AudioClip clip = loadedClips[i];
                if (clip != null && string.Equals(
                    clip.name,
                    EventHorizonExplosionClipName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    eventHorizonExplosionClip = clip;
                    return eventHorizonExplosionClip;
                }
            }
        }

        return null;
    }

    private static AudioClip FindClipInMod(ModInfo mod, string clipName)
    {
        if (mod == null || mod.assetBundles == null ||
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
            catch
            {
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
                        "[Leviathan] Failed loading AudioClip '" +
                        assetName + "': " + ex.Message
                    );
                }
            }
        }

        return null;
    }

    // Useful if the mod is hot-reloaded during development.
    public static void Reset()
    {
        eventHorizonExplosionClip = null;
        searchedEventHorizonExplosionClip = false;
        warnedMissingEventHorizonExplosionClip = false;
    }
}
