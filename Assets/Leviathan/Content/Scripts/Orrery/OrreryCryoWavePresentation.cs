using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Presentation-only depth pass for Cone of Cold.
///
/// The mechanical II hit remains the single Orrery cone owned by
/// OrrerySpellRuntime. This layer turns its one native Cryo volley into a short
/// five-rank visual wave without adding damage/status opportunities. Every extra
/// shard is still classified by OrrerySpellPresentationSafety and therefore
/// remains mechanically inert.
/// </summary>
public static class OrreryCryoWavePresentation
{
    public static class Tuning
    {
        // Includes the original native volley. Four additional ranks follow it.
        public const int VisualWaveCount = 5;
        public const float VisualWaveIntervalSeconds = 0.10f;

        // II's mechanical cone remains 60 m. Presentation deliberately carries
        // farther so the cast reads as a travelling cold front rather than a puff.
        public const float VisualRangeMeters = 180f;
        public const float ProjectileScaleMultiplier = 1.50f;

        // Each individual native volley stays at the existing 30-degree spread
        // so presentation-only safety classification remains simple. Offsetting
        // successive ranks by +/-5 degrees gives an overall ~40-degree envelope.
        public const float OuterAimOffsetDegrees = 5f;
        public const float InnerAimOffsetDegrees = 2.5f;
    }

    private sealed class Sequence
    {
        public ChargingLauncher Launcher;
        public Quaternion BaseAim;
        public int ExtraWavesRemaining;
        public int ExtraWaveIndex;
        public float NextWaveTime;
    }

    private static readonly Dictionary<GameShip, Sequence> sequences =
        new Dictionary<GameShip, Sequence>(4);

    private static readonly MethodInfo ActivateNowMethod =
        AccessTools.Method(typeof(Launcher), "ActivateNow");

    // First extra rank goes left, second right, then the two inner offsets. The
    // original volley supplies the centered fifth rank.
    private static readonly float[] ExtraWaveAimOffsets = new float[]
    {
        -Tuning.OuterAimOffsetDegrees,
        Tuning.OuterAimOffsetDegrees,
        -Tuning.InnerAimOffsetDegrees,
        Tuning.InnerAimOffsetDegrees
    };

    public static void ObservePresentationProjectile(
        Launcher launcher,
        Projectile projectile)
    {
        if (launcher == null || projectile == null ||
            !OrrerySpellPresentationSafety.IsPresentationOnly(projectile))
        {
            return;
        }

        ChargingLauncher cryo = launcher as ChargingLauncher;
        GameShip owner = cryo == null ? null : cryo.parentShip;
        if (owner == null || !OrreryRuntime.IsActive(owner) ||
            sequences.ContainsKey(owner))
        {
            return;
        }

        int extraWaveCount = Mathf.Max(0, Tuning.VisualWaveCount - 1);
        if (extraWaveCount <= 0)
            return;

        Sequence sequence = new Sequence();
        sequence.Launcher = cryo;
        sequence.BaseAim = cryo.gameObject == null
            ? Quaternion.identity
            : cryo.gameObject.transform.rotation;
        sequence.ExtraWavesRemaining = extraWaveCount;
        sequence.ExtraWaveIndex = 0;
        sequence.NextWaveTime = Time.fixedTime +
            Mathf.Max(0.01f, Tuning.VisualWaveIntervalSeconds);
        sequences[owner] = sequence;
    }

    public static void Tick(GameShip owner)
    {
        if (owner == null)
            return;

        Sequence sequence;
        if (!sequences.TryGetValue(owner, out sequence) || sequence == null)
            return;

        if (!OrreryRuntime.IsActive(owner) || sequence.Launcher == null ||
            sequence.Launcher.parentShip == null ||
            !ReferenceEquals(sequence.Launcher.parentShip, owner))
        {
            sequences.Remove(owner);
            return;
        }

        if (Time.fixedTime + 0.0001f < sequence.NextWaveTime)
            return;

        if (ActivateNowMethod == null)
        {
            Debug.LogError(
                "[Orrery] Could not resolve Launcher.ActivateNow for Cryo wave presentation.");
            sequences.Remove(owner);
            return;
        }

        float offset = GetAimOffset(sequence.ExtraWaveIndex);
        Quaternion waveAim = sequence.BaseAim * Quaternion.Euler(0f, 0f, offset);

        try
        {
            sequence.Launcher.AimAt(waveAim);
            ActivateNowMethod.Invoke(sequence.Launcher, null);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[Orrery] Cryo visual wave emission failed: " + ex);
            sequences.Remove(owner);
            return;
        }
        finally
        {
            if (sequence.Launcher != null)
                sequence.Launcher.AimAt(sequence.BaseAim);
        }

        sequence.ExtraWavesRemaining--;
        sequence.ExtraWaveIndex++;

        if (sequence.ExtraWavesRemaining <= 0)
        {
            sequences.Remove(owner);
            return;
        }

        sequence.NextWaveTime += Mathf.Max(
            0.01f,
            Tuning.VisualWaveIntervalSeconds);
    }

    public static void ApplyProjectilePresentation(
        Projectile projectile,
        Launcher parentLauncher)
    {
        if (projectile == null || parentLauncher == null ||
            !OrrerySpellPresentationSafety.IsPresentationOnly(projectile))
        {
            return;
        }

        float velocity = Mathf.Max(0.01f, parentLauncher.GetCurrentVelocity());
        float visualRangeWorld = OrreryUnits.MetersToWorld(
            Tuning.VisualRangeMeters);
        float lifetime = visualRangeWorld / velocity;
        projectile.SetDestroyTime(lifetime, lifetime * 0.80f);

        if (projectile.transform != null)
        {
            projectile.transform.localScale *= Mathf.Max(
                0f,
                Tuning.ProjectileScaleMultiplier);
        }
    }

    public static void Cancel(GameShip owner)
    {
        if (owner != null)
            sequences.Remove(owner);
    }

    public static void Reset()
    {
        sequences.Clear();
    }

    private static float GetAimOffset(int index)
    {
        if (index < 0 || index >= ExtraWaveAimOffsets.Length)
            return 0f;
        return ExtraWaveAimOffsets[index];
    }
}

/// <summary>
/// Start the five-rank sequence only after the first native Cryo projectile has
/// been fully registered as presentation-only. Priority.Last makes this run after
/// the existing safety capture postfix.
/// </summary>
[HarmonyPatch(typeof(Launcher), "AddProjectile")]
public static class OrreryCryoWaveCapturePatch
{
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Launcher __instance, Projectile projectile)
    {
        OrreryCryoWavePresentation.ObservePresentationProjectile(
            __instance,
            projectile);
    }
}

/// <summary>
/// Extend and enlarge II shards after the existing pool-safe Orrery sizing pass.
/// The underlying projectile remains mechanically inert.
/// </summary>
[HarmonyPatch(typeof(Projectile), "Init")]
public static class OrreryCryoWaveProjectilePresentationPatch
{
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(
        Projectile __instance,
        Launcher parentLauncher)
    {
        OrreryCryoWavePresentation.ApplyProjectilePresentation(
            __instance,
            parentLauncher);
    }
}

/// <summary>
/// OrrerySpellRuntime already receives one owner fixed tick. Reuse that cadence
/// instead of introducing another MonoBehaviour/update loop.
/// </summary>
[HarmonyPatch(typeof(OrrerySpellRuntime), "FixedTick")]
public static class OrreryCryoWaveTickPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryCryoWavePresentation.Tick(owner);
    }
}

[HarmonyPatch(typeof(OrreryRuntime), "Deactivate", new Type[] { typeof(GameShip) })]
public static class OrreryCryoWaveDeactivatePatch
{
    public static void Prefix(GameShip owner)
    {
        OrreryCryoWavePresentation.Cancel(owner);
    }
}

[HarmonyPatch(typeof(OrreryRuntime), "Reset")]
public static class OrreryCryoWaveResetPatch
{
    public static void Prefix()
    {
        OrreryCryoWavePresentation.Reset();
    }
}
