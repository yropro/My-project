using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Small shared boundary for temporary player-ship stat contributions.
///
/// Effects contribute resolved multipliers instead of overwriting GameShip's
/// mutable percentage fields. Native effects such as FrozenStatusEffect own
/// those fields directly, so last-writer-wins mutation would be fragile. Core
/// composes at the native stat/query boundaries instead, which naturally layers
/// with Freeze, equipment and future timed effects.
/// </summary>
public static class CoreTimedShipEffects
{
    public const int MaxEffectsPerShip = 8;

    public struct Profile
    {
        public float MovementMultiplier;
        public float HeatGenerationMultiplier;
        public float WeaponCadenceMultiplier;
        public float ShieldGrantPerSecond;

        public static Profile Identity
        {
            get
            {
                Profile value = new Profile();
                value.MovementMultiplier = 1f;
                value.HeatGenerationMultiplier = 1f;
                value.WeaponCadenceMultiplier = 1f;
                value.ShieldGrantPerSecond = 0f;
                return value;
            }
        }

        public Profile Sanitized()
        {
            Profile value = this;
            value.MovementMultiplier = Mathf.Max(0f, value.MovementMultiplier);
            value.HeatGenerationMultiplier = Mathf.Max(0f, value.HeatGenerationMultiplier);
            value.WeaponCadenceMultiplier = Mathf.Max(0.01f, value.WeaponCadenceMultiplier);
            value.ShieldGrantPerSecond = Mathf.Max(0f, value.ShieldGrantPerSecond);
            return value;
        }
    }

    private struct Entry
    {
        public bool Active;
        public ushort EffectId;
        public float AppliedAt;
        public float ExpiresAt;
        public Profile Profile;
    }

    private sealed class ShipState
    {
        public readonly Entry[] Entries = new Entry[MaxEffectsPerShip];
        public int ActiveCount;
        public float MovementMultiplier = 1f;
        public float HeatGenerationMultiplier = 1f;
        public float WeaponCadenceMultiplier = 1f;
    }

    private static readonly Dictionary<GameShip, ShipState> states =
        new Dictionary<GameShip, ShipState>(8);

    [ThreadStatic]
    private static GameShip heatUpdateShip;

    public static bool ApplyOrRefresh(
        GameShip target,
        ushort effectId,
        float durationSeconds,
        Profile profile)
    {
        if (target == null || effectId == 0 || durationSeconds <= 0f ||
            !target.IsPlayer())
        {
            return false;
        }

        ShipState state;
        if (!states.TryGetValue(target, out state) || state == null)
        {
            state = new ShipState();
            states[target] = state;
        }

        if (PruneExpired(state, Time.time) && state.ActiveCount <= 0)
        {
            state = new ShipState();
            states[target] = state;
        }

        int freeIndex = -1;
        for (int i = 0; i < state.Entries.Length; i++)
        {
            Entry entry = state.Entries[i];
            if (entry.Active && entry.EffectId == effectId)
            {
                entry.AppliedAt = Time.time;
                entry.ExpiresAt = Time.time + durationSeconds;
                entry.Profile = profile.Sanitized();
                state.Entries[i] = entry;
                RebuildAggregate(state);
                return true;
            }

            if (!entry.Active && freeIndex < 0)
                freeIndex = i;
        }

        if (freeIndex < 0)
        {
            Debug.LogWarning(
                "[CoreTimedShipEffects] Timed-effect capacity reached for " +
                target.name + "; refusing effect " + effectId + ".");
            return false;
        }

        Entry added = new Entry();
        added.Active = true;
        added.EffectId = effectId;
        added.AppliedAt = Time.time;
        added.ExpiresAt = Time.time + durationSeconds;
        added.Profile = profile.Sanitized();
        state.Entries[freeIndex] = added;
        state.ActiveCount++;
        RebuildAggregate(state);
        return true;
    }

    public static bool HasEffect(GameShip target, ushort effectId)
    {
        if (target == null || effectId == 0)
            return false;

        ShipState state;
        if (!states.TryGetValue(target, out state) || state == null)
            return false;

        float now = Time.time;
        for (int i = 0; i < state.Entries.Length; i++)
        {
            Entry entry = state.Entries[i];
            if (entry.Active && entry.EffectId == effectId && entry.ExpiresAt > now)
                return true;
        }
        return false;
    }

    public static void Remove(GameShip target, ushort effectId)
    {
        ShipState state;
        if (target == null || effectId == 0 ||
            !states.TryGetValue(target, out state) || state == null)
        {
            return;
        }

        bool changed = false;
        for (int i = 0; i < state.Entries.Length; i++)
        {
            if (!state.Entries[i].Active || state.Entries[i].EffectId != effectId)
                continue;

            state.Entries[i] = default(Entry);
            state.ActiveCount--;
            changed = true;
        }

        if (!changed)
            return;

        if (state.ActiveCount <= 0)
            states.Remove(target);
        else
            RebuildAggregate(state);
    }

    public static void Tick(GameShip target, float deltaTime)
    {
        ShipState state;
        if (target == null || !states.TryGetValue(target, out state) || state == null)
            return;

        float now = Time.time;
        float stepStart = now - Mathf.Max(0f, deltaTime);
        float shieldGrant = 0f;
        bool changed = false;

        for (int i = 0; i < state.Entries.Length; i++)
        {
            Entry entry = state.Entries[i];
            if (!entry.Active)
                continue;

            float activeStart = Mathf.Max(stepStart, entry.AppliedAt);
            float activeEnd = Mathf.Min(now, entry.ExpiresAt);
            if (activeEnd > activeStart && entry.Profile.ShieldGrantPerSecond > 0f)
            {
                shieldGrant += entry.Profile.ShieldGrantPerSecond *
                    (activeEnd - activeStart);
            }

            if (entry.ExpiresAt <= now)
            {
                state.Entries[i] = default(Entry);
                state.ActiveCount--;
                changed = true;
            }
        }

        if (shieldGrant > 0f)
        {
            CoreShieldGrant.Grant(
                target,
                shieldGrant,
                target.transform.position);
        }

        if (state.ActiveCount <= 0)
        {
            states.Remove(target);
            return;
        }

        if (changed)
            RebuildAggregate(state);
    }

    public static void Forget(GameShip target)
    {
        if (target != null)
            states.Remove(target);
    }

    public static void Reset()
    {
        states.Clear();
        heatUpdateShip = null;
    }

    public static float GetMovementMultiplier(GameShip target)
    {
        ShipState state;
        return target != null && states.TryGetValue(target, out state) && state != null
            ? state.MovementMultiplier
            : 1f;
    }

    public static float GetHeatGenerationMultiplier(GameShip target)
    {
        ShipState state;
        return target != null && states.TryGetValue(target, out state) && state != null
            ? state.HeatGenerationMultiplier
            : 1f;
    }

    public static float GetWeaponCadenceMultiplier(GameShip target)
    {
        ShipState state;
        return target != null && states.TryGetValue(target, out state) && state != null
            ? state.WeaponCadenceMultiplier
            : 1f;
    }

    internal static void EnterHeatUpdate(GameShip ship)
    {
        heatUpdateShip = ship;
    }

    internal static void ExitHeatUpdate(GameShip ship)
    {
        if (object.ReferenceEquals(heatUpdateShip, ship))
            heatUpdateShip = null;
    }

    internal static float ApplyScopedBoostHeatMultiplier(float value)
    {
        GameShip ship = heatUpdateShip;
        if (ship == null)
            return value;

        return value * GetHeatGenerationMultiplier(ship);
    }

    private static bool PruneExpired(ShipState state, float now)
    {
        bool changed = false;
        for (int i = 0; i < state.Entries.Length; i++)
        {
            Entry entry = state.Entries[i];
            if (!entry.Active || entry.ExpiresAt > now)
                continue;

            state.Entries[i] = default(Entry);
            state.ActiveCount--;
            changed = true;
        }
        return changed;
    }

    private static void RebuildAggregate(ShipState state)
    {
        float movement = 1f;
        float heat = 1f;
        float cadence = 1f;

        for (int i = 0; i < state.Entries.Length; i++)
        {
            Entry entry = state.Entries[i];
            if (!entry.Active)
                continue;

            Profile profile = entry.Profile;
            movement *= profile.MovementMultiplier;
            heat *= profile.HeatGenerationMultiplier;
            cadence *= profile.WeaponCadenceMultiplier;
        }

        state.MovementMultiplier = Mathf.Max(0f, movement);
        state.HeatGenerationMultiplier = Mathf.Max(0f, heat);
        state.WeaponCadenceMultiplier = Mathf.Max(0.01f, cadence);
    }
}

/// <summary>
/// Ship-level resolved movement and beam-tick modifiers. Max speed / turn speed
/// already flow through GameShip.ApplyModifier; BeamTickRate is a native interval
/// in seconds, so higher cadence divides the resolved interval.
/// </summary>
[HarmonyPatch]
public static class CoreTimedShipEffectsGameShipModifierPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            nameof(GameShip.ApplyModifier),
            new Type[]
            {
                typeof(Modifier.Type),
                typeof(Item.Category),
                typeof(float),
                typeof(bool)
            });
    }

    public static void Postfix(
        GameShip __instance,
        Modifier.Type __0,
        ref float __result)
    {
        if (__instance == null)
            return;

        switch (__0)
        {
            case Modifier.Type.MaxSpeed:
            case Modifier.Type.TurnSpeed:
                __result *= CoreTimedShipEffects.GetMovementMultiplier(__instance);
                break;

            case Modifier.Type.BeamTickRate:
                float cadence =
                    CoreTimedShipEffects.GetWeaponCadenceMultiplier(__instance);
                if (cadence > 0f && !Mathf.Approximately(cadence, 1f))
                    __result /= cadence;
                break;
        }
    }
}

/// <summary>
/// Thruster acceleration/dodge/boost live on the equipped Thruster rather than
/// GameShip.ApplyModifier. Patch the three resolved global getters so temporary
/// movement effects compose after native item rolls and before GameShip's own
/// FrozenStatusEffect percentage fields are applied.
/// </summary>
[HarmonyPatch]
public static class CoreTimedShipEffectsThrusterPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.AccelerationFactor));
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.DodgeFactor));
        yield return AccessTools.PropertyGetter(
            typeof(Thruster), nameof(Thruster.BoostFactor));
    }

    public static void Postfix(Thruster __instance, ref float __result)
    {
        if (__instance == null || __instance.parentShip == null)
            return;

        __result *= CoreTimedShipEffects.GetMovementMultiplier(
            __instance.parentShip);
    }
}

/// <summary>
/// Native launcher ROF is its resolved reload interval. Apply temporary cadence
/// after native ReloadTime rolls; GameShip.weaponSpeedPerc (Freeze, etc.) still
/// composes independently at the launch site.
/// </summary>
[HarmonyPatch(typeof(Launcher), nameof(Launcher.ReloadTime), MethodType.Getter)]
public static class CoreTimedShipEffectsLauncherCadencePatch
{
    public static void Postfix(Launcher __instance, ref float __result)
    {
        if (__instance == null || __instance.parentShip == null)
            return;

        float cadence = CoreTimedShipEffects.GetWeaponCadenceMultiplier(
            __instance.parentShip);
        if (cadence > 0f && !Mathf.Approximately(cadence, 1f))
            __result /= cadence;
    }
}

/// <summary>
/// Weapon heat is accumulated in GameShip.heatPerSecond, while boost/displace
/// heat is queried through static Ship methods from inside UpdateHeat. Scope the
/// active ship during that native update so both generated-heat paths receive the
/// same temporary multiplier without changing cooling or status-effect heat.
/// </summary>
[HarmonyPatch(typeof(GameShip), "UpdateHeat")]
public static class CoreTimedShipEffectsHeatPatch
{
    public static void Prefix(
        GameShip __instance,
        ref float ___heatPerSecond,
        out float __state)
    {
        __state = ___heatPerSecond;
        float multiplier =
            CoreTimedShipEffects.GetHeatGenerationMultiplier(__instance);
        ___heatPerSecond *= multiplier;
        CoreTimedShipEffects.EnterHeatUpdate(__instance);
    }

    public static Exception Finalizer(
        GameShip __instance,
        ref float ___heatPerSecond,
        float __state,
        Exception __exception)
    {
        // Finalizer runs on both normal and exceptional exits, so the temporary
        // native-field substitution and thread-static scope can never leak into
        // a later ship update.
        ___heatPerSecond = __state;
        CoreTimedShipEffects.ExitHeatUpdate(__instance);
        return __exception;
    }
}

[HarmonyPatch(typeof(Ship), nameof(Ship.GetClassBoostHeat))]
public static class CoreTimedShipEffectsBoostHeatPatch
{
    public static void Postfix(ref float __result)
    {
        __result = CoreTimedShipEffects.ApplyScopedBoostHeatMultiplier(__result);
    }
}

[HarmonyPatch(typeof(Ship), nameof(Ship.GetClassDisplaceHeat))]
public static class CoreTimedShipEffectsDisplaceHeatPatch
{
    public static void Postfix(ref float __result)
    {
        __result = CoreTimedShipEffects.ApplyScopedBoostHeatMultiplier(__result);
    }
}

[HarmonyPatch(typeof(GameShip), nameof(GameShip.FixedUpdate))]
public static class CoreTimedShipEffectsFixedTickPatch
{
    public static void Postfix(GameShip __instance)
    {
        if (__instance != null && __instance.IsPlayer())
            CoreTimedShipEffects.Tick(__instance, Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class CoreTimedShipEffectsShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        CoreTimedShipEffects.Forget(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreTimedShipEffectsWorldDestroyedPatch
{
    public static void Prefix()
    {
        CoreTimedShipEffects.Reset();
    }
}
