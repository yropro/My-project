using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Shared, bounded contact-motion effects for ships and projectiles.
///
/// Spells grant short renewable leases keyed by source identity. Consumers never
/// overwrite a native speed field and never cache an "original" speed. Ship
/// movement composes at native resolved-stat boundaries, while projectile speed
/// is restored before the projectile's complete native FixedUpdate chain and the
/// resolved multiplier is re-applied only after that chain finishes. This keeps
/// seeking, missile acceleration, black-hole steering and other native direction
/// logic authoritative while allowing multiple future slow spells to coexist.
///
/// Default composition is strongest-slow-wins (minimum multiplier), deliberately
/// avoiding accidental multiplicative stacking between unrelated fields.
/// </summary>
public static class CoreMotionEffects
{
    public const int MaxContributorsPerTarget = 8;
    public const float MinimumMultiplier = 0.02f;

    public struct SourceKey : IEquatable<SourceKey>
    {
        public ushort EffectId;
        public int SourceInstanceId;
        public uint Generation;

        public SourceKey(ushort effectId, int sourceInstanceId, uint generation)
        {
            EffectId = effectId;
            SourceInstanceId = sourceInstanceId;
            Generation = generation;
        }

        public bool IsValid { get { return EffectId != 0; } }

        public bool Equals(SourceKey other)
        {
            return EffectId == other.EffectId &&
                SourceInstanceId == other.SourceInstanceId &&
                Generation == other.Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is SourceKey && Equals((SourceKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = EffectId;
                hash = hash * 397 ^ SourceInstanceId;
                hash = hash * 397 ^ (int)Generation;
                return hash;
            }
        }
    }

    private struct Contribution
    {
        public bool Active;
        public SourceKey Source;
        public float Multiplier;
        public float ExpiresAt;
    }

    private sealed class TargetState
    {
        public readonly Contribution[] Entries =
            new Contribution[MaxContributorsPerTarget];
        public float LastProjectileMultiplier = 1f;
    }

    private static readonly Dictionary<GameShip, TargetState> shipStates =
        new Dictionary<GameShip, TargetState>(32);
    private static readonly Dictionary<Projectile, TargetState> projectileStates =
        new Dictionary<Projectile, TargetState>(128);

    public static bool GrantShipSlow(
        GameShip target,
        SourceKey source,
        float multiplier,
        float leaseSeconds)
    {
        if (target == null)
            return false;
        return Grant(
            shipStates,
            target,
            source,
            multiplier,
            leaseSeconds);
    }

    public static bool GrantProjectileSlow(
        Projectile target,
        SourceKey source,
        float multiplier,
        float leaseSeconds)
    {
        if (target == null || target.netRendered)
            return false;
        return Grant(
            projectileStates,
            target,
            source,
            multiplier,
            leaseSeconds);
    }

    public static float GetShipMultiplier(GameShip target)
    {
        if (target == null)
            return 1f;

        TargetState state;
        return shipStates.TryGetValue(target, out state) && state != null
            ? Resolve(state, Time.time)
            : 1f;
    }

    public static float GetProjectileMultiplier(Projectile target)
    {
        if (target == null || target.netRendered)
            return 1f;

        TargetState state;
        return projectileStates.TryGetValue(target, out state) && state != null
            ? Resolve(state, Time.time)
            : 1f;
    }

    public static void RemoveShipSource(GameShip target, SourceKey source)
    {
        RemoveSource(shipStates, target, source);
    }

    public static void RemoveProjectileSource(Projectile target, SourceKey source)
    {
        RemoveSource(projectileStates, target, source);
    }

    public static void ForgetShip(GameShip target)
    {
        if (target != null)
            shipStates.Remove(target);
    }

    public static void ForgetProjectile(Projectile target)
    {
        ForgetProjectile(target, true);
    }

    public static void Reset()
    {
        // Restore any projectile velocity that still has our multiplier applied
        // before dropping bookkeeping. This matters for pooled projectiles when a
        // world is torn down between fixed steps.
        foreach (KeyValuePair<Projectile, TargetState> pair in projectileStates)
            RestoreProjectileVelocity(pair.Key, pair.Value);
        projectileStates.Clear();
        shipStates.Clear();
    }

    private static bool Grant<T>(
        Dictionary<T, TargetState> states,
        T target,
        SourceKey source,
        float multiplier,
        float leaseSeconds)
        where T : class
    {
        if (target == null || !source.IsValid || leaseSeconds <= 0f ||
            float.IsNaN(multiplier) || float.IsInfinity(multiplier))
        {
            return false;
        }

        multiplier = Mathf.Clamp(multiplier, MinimumMultiplier, 1f);
        float expiresAt = Time.time + leaseSeconds;

        TargetState state;
        if (!states.TryGetValue(target, out state) || state == null)
        {
            state = new TargetState();
            states[target] = state;
        }

        int free = -1;
        int replace = -1;
        float replaceMultiplier = -1f;
        float replaceExpiry = float.PositiveInfinity;
        float now = Time.time;

        for (int i = 0; i < state.Entries.Length; i++)
        {
            Contribution entry = state.Entries[i];
            if (entry.Active && entry.ExpiresAt <= now)
            {
                state.Entries[i] = default(Contribution);
                entry = default(Contribution);
            }

            if (entry.Active && entry.Source.Equals(source))
            {
                entry.Multiplier = multiplier;
                entry.ExpiresAt = expiresAt;
                state.Entries[i] = entry;
                return true;
            }

            if (!entry.Active)
            {
                if (free < 0)
                    free = i;
                continue;
            }

            // If the bounded table is full, preserve the strongest contributions.
            // The weakest slow (largest multiplier) is the best eviction target.
            if (entry.Multiplier > replaceMultiplier ||
                (Mathf.Approximately(entry.Multiplier, replaceMultiplier) &&
                 entry.ExpiresAt < replaceExpiry))
            {
                replace = i;
                replaceMultiplier = entry.Multiplier;
                replaceExpiry = entry.ExpiresAt;
            }
        }

        int index = free >= 0 ? free : replace;
        if (index < 0)
            return false;

        if (free < 0 && multiplier >= replaceMultiplier)
            return false;

        Contribution added = new Contribution();
        added.Active = true;
        added.Source = source;
        added.Multiplier = multiplier;
        added.ExpiresAt = expiresAt;
        state.Entries[index] = added;
        return true;
    }

    private static float Resolve(TargetState state, float now)
    {
        if (state == null)
            return 1f;

        float resolved = 1f;
        for (int i = 0; i < state.Entries.Length; i++)
        {
            Contribution entry = state.Entries[i];
            if (!entry.Active)
                continue;

            if (entry.ExpiresAt <= now)
            {
                state.Entries[i] = default(Contribution);
                continue;
            }

            if (entry.Multiplier < resolved)
                resolved = entry.Multiplier;
        }
        return Mathf.Clamp(resolved, MinimumMultiplier, 1f);
    }

    private static void RemoveSource<T>(
        Dictionary<T, TargetState> states,
        T target,
        SourceKey source)
        where T : class
    {
        if (target == null || !source.IsValid)
            return;

        TargetState state;
        if (!states.TryGetValue(target, out state) || state == null)
            return;

        for (int i = 0; i < state.Entries.Length; i++)
        {
            if (state.Entries[i].Active && state.Entries[i].Source.Equals(source))
                state.Entries[i] = default(Contribution);
        }
    }

    internal static void BeginProjectileFixedStep(Projectile projectile)
    {
        if (projectile == null || projectile.netRendered)
            return;

        TargetState state;
        if (!projectileStates.TryGetValue(projectile, out state) || state == null)
            return;

        RestoreProjectileVelocity(projectile, state);
    }

    internal static void EndProjectileFixedStep(Projectile projectile)
    {
        if (projectile == null || projectile.netRendered || projectile.rigidBody == null)
            return;

        TargetState state;
        if (!projectileStates.TryGetValue(projectile, out state) || state == null)
            return;

        float multiplier = Resolve(state, Time.time);
        if (!Mathf.Approximately(multiplier, 1f))
            projectile.rigidBody.velocity *= multiplier;
        state.LastProjectileMultiplier = multiplier;

        if (Mathf.Approximately(multiplier, 1f) && !HasActiveEntries(state))
            projectileStates.Remove(projectile);
    }

    private static void ForgetProjectile(Projectile target, bool restoreVelocity)
    {
        if (target == null)
            return;

        TargetState state;
        if (!projectileStates.TryGetValue(target, out state) || state == null)
            return;

        if (restoreVelocity)
            RestoreProjectileVelocity(target, state);
        projectileStates.Remove(target);
    }

    private static void RestoreProjectileVelocity(
        Projectile projectile,
        TargetState state)
    {
        if (projectile == null || state == null || projectile.rigidBody == null)
            return;

        float prior = state.LastProjectileMultiplier;
        if (prior >= MinimumMultiplier && !Mathf.Approximately(prior, 1f))
            projectile.rigidBody.velocity /= prior;
        state.LastProjectileMultiplier = 1f;
    }

    private static bool HasActiveEntries(TargetState state)
    {
        float now = Time.time;
        bool active = false;
        for (int i = 0; i < state.Entries.Length; i++)
        {
            Contribution entry = state.Entries[i];
            if (!entry.Active)
                continue;
            if (entry.ExpiresAt <= now)
                state.Entries[i] = default(Contribution);
            else
                active = true;
        }
        return active;
    }
}

[HarmonyPatch]
public static class CoreMotionEffectsProjectileFixedPatch
{
    [ThreadStatic]
    private static int depth;

    public static IEnumerable<MethodBase> TargetMethods()
    {
        Type projectileType = typeof(Projectile);
        Type[] types = projectileType.Assembly.GetTypes();
        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            if (type == null || type.IsAbstract ||
                !projectileType.IsAssignableFrom(type))
            {
                continue;
            }

            MethodInfo method = AccessTools.DeclaredMethod(
                type,
                nameof(Projectile.FixedUpdate),
                Type.EmptyTypes);
            if (method != null)
                yield return method;
        }
    }

    public static void Prefix(Projectile __instance)
    {
        if (depth++ == 0)
            CoreMotionEffects.BeginProjectileFixedStep(__instance);
    }

    public static void Postfix(Projectile __instance)
    {
        depth--;
        if (depth <= 0)
        {
            depth = 0;
            CoreMotionEffects.EndProjectileFixedStep(__instance);
        }
    }
}

[HarmonyPatch(typeof(GameShip), nameof(GameShip.ApplyModifier),
    new Type[] { typeof(Modifier.Type), typeof(Item.Category), typeof(float), typeof(bool) })]
public static class CoreMotionEffectsGameShipModifierPatch
{
    public static void Postfix(
        GameShip __instance,
        Modifier.Type __0,
        ref float __result)
    {
        if (__instance == null)
            return;

        if (__0 == Modifier.Type.MaxSpeed || __0 == Modifier.Type.TurnSpeed)
            __result *= CoreMotionEffects.GetShipMultiplier(__instance);
    }
}

[HarmonyPatch]
public static class CoreMotionEffectsThrusterPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.AccelerationFactor));
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.DodgeFactor));
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.BoostFactor));
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.ControlMultiplier));
    }

    public static void Postfix(Thruster __instance, ref float __result)
    {
        if (__instance != null && __instance.parentShip != null)
            __result *= CoreMotionEffects.GetShipMultiplier(__instance.parentShip);
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.PoolDestroy))]
public static class CoreMotionEffectsProjectilePoolPatch
{
    public static void Prefix(Projectile __instance)
    {
        CoreMotionEffects.ForgetProjectile(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.ResetObject))]
public static class CoreMotionEffectsProjectileResetPatch
{
    public static void Prefix(Projectile __instance)
    {
        CoreMotionEffects.ForgetProjectile(__instance);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class CoreMotionEffectsShipDestroyPatch
{
    public static void Prefix(GameShip __instance)
    {
        CoreMotionEffects.ForgetShip(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreMotionEffectsWorldDestroyPatch
{
    public static void Prefix()
    {
        CoreMotionEffects.Reset();
    }
}
