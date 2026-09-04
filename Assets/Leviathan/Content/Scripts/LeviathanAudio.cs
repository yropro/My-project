using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Small cosmetic audio layer for Leviathan.
///
/// AssetBundle AudioClip naming:
///   predator_01 ... predator_10 (or any number of predator_* clips)
///   boost_01    ... boost_10    (or any number of boost_* clips)
///
/// Predator replaces the hidden source Assault's normal activation sound with
/// one shuffled Predator vocal. Boost has its own shuffled pool, chance and
/// cooldown, and fades out if boosting ends before the vocal finishes.
/// </summary>
public static class LeviathanAudioRuntime
{
    // Intentionally simple first-pass tuning. These are the only boost-vocal
    // behavior values that should normally need playtest adjustment.
    public const float BoostVocalChance = 0.25f;
    public const float BoostVocalCooldown = 8.0f;
    public const float BoostFadeSeconds = 0.25f;

    public const float PredatorVolume = 1.0f;
    public const float BoostVolume = 0.85f;

    private static readonly FieldInfo ParentShipField =
        AccessTools.Field(typeof(Equippable), "parentShip");

    private static readonly List<AudioClip> PredatorClips =
        new List<AudioClip>();

    private static readonly List<AudioClip> BoostClips =
        new List<AudioClip>();

    private static readonly ShufflePool PredatorPool =
        new ShufflePool();

    private static readonly ShufflePool BoostPool =
        new ShufflePool();

    // StartAttack is synchronous, but a set is safer than a single temporary
    // reference if another attack ever gets invoked from inside an attack hook.
    private static readonly HashSet<Assault> PredatorStarts =
        new HashSet<Assault>();

    private static readonly Dictionary<Thruster, BoostVocalState> BoostStates =
        new Dictionary<Thruster, BoostVocalState>();

    private static bool initialized;
    private static bool warnedNoPredatorClips;
    private static bool warnedNoBoostClips;

    private sealed class BoostVocalState
    {
        public GameObject audioObject;
        public SoundEffectPlayer player;
        public float nextAllowedTime;
        public bool stopping;
    }

    private sealed class ShufflePool
    {
        private readonly List<AudioClip> source = new List<AudioClip>();
        private readonly List<AudioClip> bag = new List<AudioClip>();
        private int index;
        private AudioClip lastPlayed;

        public int Count
        {
            get { return source.Count; }
        }

        public void SetSource(List<AudioClip> clips)
        {
            source.Clear();

            if (clips != null)
                source.AddRange(clips);

            bag.Clear();
            index = 0;
            lastPlayed = null;
        }

        public AudioClip Next()
        {
            if (source.Count == 0)
                return null;

            if (source.Count == 1)
            {
                lastPlayed = source[0];
                return source[0];
            }

            if (index >= bag.Count)
                Refill();

            AudioClip clip = bag[index++];
            lastPlayed = clip;
            return clip;
        }

        private void Refill()
        {
            bag.Clear();
            bag.AddRange(source);

            // Fisher-Yates shuffle.
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                AudioClip temp = bag[i];
                bag[i] = bag[j];
                bag[j] = temp;
            }

            // Avoid the last clip of one shuffle immediately repeating as the
            // first clip of the next shuffle.
            if (lastPlayed != null &&
                bag.Count > 1 &&
                bag[0] == lastPlayed)
            {
                int swap = UnityEngine.Random.Range(1, bag.Count);
                AudioClip temp = bag[0];
                bag[0] = bag[swap];
                bag[swap] = temp;
            }

            index = 0;
        }
    }

    public static void Initialize(ModInfo modInfo)
    {
        Shutdown();

        initialized = true;
        LoadAudioClips(modInfo);

        PredatorPool.SetSource(PredatorClips);
        BoostPool.SetSource(BoostClips);

        Debug.Log(
            "[Leviathan] Kaiju audio loaded. Predator clips = " +
            PredatorPool.Count +
            ", Boost clips = " +
            BoostPool.Count +
            "."
        );
    }

    public static void Shutdown()
    {
        foreach (KeyValuePair<Thruster, BoostVocalState> pair in BoostStates)
            DestroyBoostState(pair.Value);

        BoostStates.Clear();
        PredatorStarts.Clear();

        PredatorClips.Clear();
        BoostClips.Clear();
        PredatorPool.SetSource(PredatorClips);
        BoostPool.SetSource(BoostClips);

        initialized = false;
        warnedNoPredatorClips = false;
        warnedNoBoostClips = false;
    }

    private static void LoadAudioClips(ModInfo modInfo)
    {
        PredatorClips.Clear();
        BoostClips.Clear();

        if (modInfo == null || modInfo.assetBundles == null)
            return;

        HashSet<AudioClip> seen = new HashSet<AudioClip>();

        for (int i = 0; i < modInfo.assetBundles.Count; i++)
        {
            AssetBundle bundle = modInfo.assetBundles[i];

            if (bundle == null)
                continue;

            AudioClip[] clips;

            try
            {
                clips = bundle.LoadAllAssets<AudioClip>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[Leviathan] Could not inspect an AssetBundle for kaiju audio: " +
                    ex.Message
                );
                continue;
            }

            if (clips == null)
                continue;

            for (int c = 0; c < clips.Length; c++)
            {
                AudioClip clip = clips[c];

                if (clip == null || seen.Contains(clip))
                    continue;

                string clipName = clip.name ?? string.Empty;

                if (clipName.StartsWith(
                    "predator_",
                    StringComparison.OrdinalIgnoreCase))
                {
                    PredatorClips.Add(clip);
                    seen.Add(clip);
                }
                else if (clipName.StartsWith(
                    "boost_",
                    StringComparison.OrdinalIgnoreCase))
                {
                    BoostClips.Add(clip);
                    seen.Add(clip);
                }
            }
        }

        PredatorClips.Sort(
            (a, b) => string.Compare(
                a == null ? string.Empty : a.name,
                b == null ? string.Empty : b.name,
                StringComparison.OrdinalIgnoreCase
            )
        );

        BoostClips.Sort(
            (a, b) => string.Compare(
                a == null ? string.Empty : a.name,
                b == null ? string.Empty : b.name,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    // -------------------------------------------------------------------------
    // Predator
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called from the Assault.StartAttack prefix. This deliberately has a
    /// broader multiplayer/audio eligibility check than Predator's mechanical
    /// runtime: on another client, a replicated player Assault StartAttack can
    /// still get the custom sound without that client driving the lunge logic.
    /// </summary>
    public static void BeginPredatorStart(Assault assault)
    {
        if (!initialized || assault == null)
            return;

        GameShip ship = GetParentShip(assault);

        if (!HasLeviathanRanks(ship, true))
            return;

        // For our own ship, require the live Leviathan build as well. Remote
        // clients may not own the authoritative LeviathanController state.
        if (IsCurrentPlayer(ship) &&
            (LeviathanMod.Controller == null ||
             LeviathanMod.Controller.GetActiveSectionCount(ship) < 5))
        {
            return;
        }

        PredatorStarts.Add(assault);
    }

    public static void EndPredatorStart(Assault assault)
    {
        if (assault != null)
            PredatorStarts.Remove(assault);
    }

    public static bool TryGetPredatorSound(
        Assault assault,
        out SoundEffectPlayer.SoundEffect soundEffect)
    {
        soundEffect = null;

        if (!initialized ||
            assault == null ||
            !PredatorStarts.Contains(assault))
        {
            return false;
        }

        AudioClip clip = PredatorPool.Next();

        if (clip == null)
        {
            if (!warnedNoPredatorClips)
            {
                warnedNoPredatorClips = true;
                Debug.LogWarning(
                    "[Leviathan] Predator has no predator_* AudioClips in the mod AssetBundle; " +
                    "native Assault audio will be used."
                );
            }

            return false;
        }

        soundEffect = BuildPredatorSound(clip);
        return true;
    }

    private static SoundEffectPlayer.SoundEffect BuildPredatorSound(AudioClip clip)
    {
        SoundEffectPlayer.SoundEffect effect =
            new SoundEffectPlayer.SoundEffect();

        effect.audioClip = clip;
        effect.volumeScale = PredatorVolume;
        effect.loop = false;
        effect.pitchVariance = 0f;
        effect.volumeVariance = 0f;
        effect.fadePitch = false;
        effect.minPitch = 1f;
        effect.maxPitch = 1f;
        effect.fadeVolume = false;
        effect.minVolume = 0f;
        effect.maxVolume = 1f;
        effect.fadeSeconds = 0f;

        return effect;
    }

    // -------------------------------------------------------------------------
    // Boost
    // -------------------------------------------------------------------------

    public static void OnBoostAttemptBegin(Thruster thruster, bool wasBoosting)
    {
        if (!initialized || thruster == null || wasBoosting)
            return;

        // Thruster.Boost can be rejected. The postfix checks IsBoosting() before
        // calling this method, so reaching here means a new boost actually began.
        GameShip ship = GetParentShip(thruster);

        if (!HasLeviathanRanks(ship, false))
            return;

        if (BoostPool.Count == 0)
        {
            if (!warnedNoBoostClips)
            {
                warnedNoBoostClips = true;
                Debug.LogWarning(
                    "[Leviathan] Boost vocals have no boost_* AudioClips in the mod AssetBundle."
                );
            }

            return;
        }

        BoostVocalState state = GetOrCreateBoostState(thruster);

        if (state == null || Time.time < state.nextAllowedTime)
            return;

        if (UnityEngine.Random.value > BoostVocalChance)
            return;

        AudioClip clip = BoostPool.Next();

        if (clip == null)
            return;

        if (state.player.IsPlaying())
            state.player.Stop();

        state.stopping = false;
        state.nextAllowedTime = Time.time + BoostVocalCooldown;

        state.player.Play(
            BuildBoostSound(clip),
            true,
            ship != null && ship.IsPlayer()
        );
    }

    public static void UpdateBoostState(Thruster thruster)
    {
        if (thruster == null)
            return;

        BoostVocalState state;

        if (!BoostStates.TryGetValue(thruster, out state) || state == null)
            return;

        if (state.player == null)
        {
            RemoveBoostState(thruster);
            return;
        }

        if (!thruster.IsBoosting() &&
            state.player.IsPlaying() &&
            !state.stopping)
        {
            // SoundEffectPlayer.Stop respects fadeVolume/fadeSeconds on the
            // currently playing SoundEffect, giving us the requested graceful
            // early-release fade with no custom audio coroutine.
            state.stopping = true;
            state.player.Stop();
        }

        if (state.stopping && !state.player.IsPlaying())
            state.stopping = false;
    }

    public static void RemoveBoostState(Thruster thruster)
    {
        if (thruster == null)
            return;

        BoostVocalState state;

        if (!BoostStates.TryGetValue(thruster, out state))
            return;

        BoostStates.Remove(thruster);
        DestroyBoostState(state);
    }

    private static BoostVocalState GetOrCreateBoostState(Thruster thruster)
    {
        BoostVocalState state;

        if (BoostStates.TryGetValue(thruster, out state) &&
            state != null &&
            state.player != null &&
            state.audioObject != null)
        {
            return state;
        }

        if (state != null)
            DestroyBoostState(state);

        GameObject audioObject = new GameObject("Leviathan Boost Vocal Audio");
        audioObject.transform.SetParent(thruster.transform, false);
        audioObject.transform.localPosition = Vector3.zero;
        audioObject.transform.localRotation = Quaternion.identity;

        SoundEffectPlayer player =
            audioObject.AddComponent<SoundEffectPlayer>();

        player.Attach(
            audioObject,
            SoundEffectPlayer.Category.Effects
        );

        state = new BoostVocalState();
        state.audioObject = audioObject;
        state.player = player;
        state.nextAllowedTime = 0f;
        state.stopping = false;

        BoostStates[thruster] = state;
        return state;
    }

    private static SoundEffectPlayer.SoundEffect BuildBoostSound(AudioClip clip)
    {
        SoundEffectPlayer.SoundEffect effect =
            new SoundEffectPlayer.SoundEffect();

        effect.audioClip = clip;
        effect.volumeScale = BoostVolume;
        effect.loop = false;
        effect.pitchVariance = 0f;
        effect.volumeVariance = 0f;
        effect.fadePitch = false;
        effect.minPitch = 1f;
        effect.maxPitch = 1f;

        // SoundEffectPlayer uses the same fade settings for starting/stopping.
        // A short fade-in is fine; importantly, Stop() now fades a long growl
        // instead of chopping it off when boost is released early.
        effect.fadeVolume = true;
        effect.minVolume = 0f;
        effect.maxVolume = 1f;
        effect.fadeSeconds = BoostFadeSeconds;

        return effect;
    }

    private static void DestroyBoostState(BoostVocalState state)
    {
        if (state == null)
            return;

        if (state.player != null && state.player.IsPlaying())
            state.player.Stop();

        if (state.audioObject != null)
            UnityEngine.Object.Destroy(state.audioObject);

        state.player = null;
        state.audioObject = null;
    }

    // -------------------------------------------------------------------------
    // Eligibility/helpers
    // -------------------------------------------------------------------------

    private static GameShip GetParentShip(Equippable equippable)
    {
        if (equippable == null || ParentShipField == null)
            return null;

        return ParentShipField.GetValue(equippable) as GameShip;
    }

    private static bool HasLeviathanRanks(GameShip ship, bool requirePredator)
    {
        if (ship == null || !ship.IsAnyPlayerShip())
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1)
        {
            return false;
        }

        return !requirePredator ||
            pilot.GetUpgradeLevel(LeviathanMod.PredatorUpgrade) >= 1;
    }

    private static bool IsCurrentPlayer(GameShip ship)
    {
        return ship != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == ship;
    }
}

// -----------------------------------------------------------------------------
// Native audio/boost hooks
// -----------------------------------------------------------------------------

/// <summary>
/// Assault.StartAttack already routes its attack sound through the game's own
/// Equippable/SoundEffectPlayer path. Replacing that SoundEffect here means
/// Predator inherits the same spatialization, mixer/settings and remote attack
/// playback path as the normal Assault sound rather than layering a raw
/// AudioSource on top.
/// </summary>
[HarmonyPatch]
public static class LeviathanPredatorAudioReplacePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Equippable),
            "PlayAudio",
            new Type[]
            {
                typeof(SoundEffectPlayer.SoundEffect),
                typeof(bool)
            }
        );
    }

    public static void Prefix(
        Equippable __instance,
        ref SoundEffectPlayer.SoundEffect __0)
    {
        Assault assault = __instance as Assault;

        if (assault == null)
            return;

        SoundEffectPlayer.SoundEffect replacement;

        if (LeviathanAudioRuntime.TryGetPredatorSound(
            assault,
            out replacement))
        {
            __0 = replacement;
        }
    }
}

/// <summary>
/// Detect only the rising edge of a real Thruster boost. NetSetBoosting(true)
/// calls the same native Boost method for replicated ships, so this also has a
/// natural path to positional boost vocals on other clients without inventing
/// a separate custom network message.
/// </summary>
[HarmonyPatch]
public static class LeviathanBoostVocalStartPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Thruster), "Boost");
    }

    public static void Prefix(Thruster __instance, out bool __state)
    {
        __state = __instance != null && __instance.IsBoosting();
    }

    public static void Postfix(Thruster __instance, bool __state)
    {
        if (__instance == null ||
            __state ||
            !__instance.IsBoosting())
        {
            return;
        }

        LeviathanAudioRuntime.OnBoostAttemptBegin(__instance, __state);
    }
}

/// <summary>
/// Native Thruster.UpdateSounds already runs while boost audio state changes.
/// Piggyback on it to fade a still-playing growl when the boost stops.
/// </summary>
[HarmonyPatch]
public static class LeviathanBoostVocalUpdatePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Thruster), "UpdateSounds");
    }

    public static void Postfix(Thruster __instance)
    {
        LeviathanAudioRuntime.UpdateBoostState(__instance);
    }
}

[HarmonyPatch]
public static class LeviathanBoostVocalUnequipPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Thruster), "Unequip");
    }

    public static void Postfix(Thruster __instance)
    {
        LeviathanAudioRuntime.RemoveBoostState(__instance);
    }
}
