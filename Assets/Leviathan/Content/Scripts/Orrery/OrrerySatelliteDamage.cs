using HarmonyLib;
using StarVortex;
using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Applies Orrery satellite incoming-damage tuning at the native GameShip damage
/// boundary. GameShip.Damage is already owner-authoritative in multiplayer and
/// already mutates DamageData in place, so this adds no parallel damage route or
/// per-hit allocation.
/// </summary>
[HarmonyPatch]
public static class OrrerySatelliteDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "Damage",
            new Type[]
            {
                typeof(Damageable.DamageType),
                typeof(Damageable.DamageData[]),
                typeof(float),
                typeof(int),
                typeof(Vector2),
                typeof(GameShip),
                typeof(bool)
            });
    }

    public static void Prefix(
        GameShip __instance,
        Damageable.DamageData[] damageData)
    {
        if (__instance == null || damageData == null || damageData.Length == 0)
            return;

        GameShip owner;
        OrrerySatellites.SatelliteContext context;
        if (!OrrerySatellites.TryGetSatelliteContext(
                __instance,
                out owner,
                out context) ||
            owner == null ||
            context == null)
        {
            return;
        }

        OrreryRuntime.ResolvedState resolved =
            OrreryRuntime.GetResolvedState(owner);
        if (resolved == null || !resolved.Active)
            return;

        float multiplier = resolved.SatelliteIncomingDamageMultiplier;
        if (Mathf.Approximately(multiplier, 1f))
            return;

        for (int i = 0; i < damageData.Length; i++)
        {
            Damageable.DamageData current = damageData[i];
            damageData[i] = new Damageable.DamageData(
                current.modifierType,
                current.damage * multiplier,
                current.dps * multiplier);
        }
    }
}

/// <summary>
/// Canonical local repair state for destroyed Orrery satellites. A lethal hit
/// disables the semantic satellite without destroying its native GameShip, makes
/// it immune/non-colliding, suppresses native regen, and restores hull and shield
/// linearly to full over the configured duration. Storage is fixed by the maximum
/// formula-satellite count and the fixed-step path allocates nothing.
/// </summary>
public static class OrrerySatelliteRepair
{
    public static class Tuning
    {
        public const float RepairDurationSeconds = 5f;
    }

    private struct RepairState
    {
        public bool Active;
        public GameShip Ship;
        public float ElapsedSeconds;
    }

    private static readonly RepairState[] states =
        new RepairState[OrreryOrbit.MaxSatellites];

    private static GameShip repairOwner;

    public static bool TryBegin(
        GameShip satellite,
        GameShip owner,
        OrrerySatellites.SatelliteContext context)
    {
        if (satellite == null || owner == null || context == null ||
            !context.IsValid ||
            !object.ReferenceEquals(context.Ship, satellite) ||
            !object.ReferenceEquals(context.Owner, owner) ||
            context.SatelliteId == 0 ||
            context.SatelliteId > states.Length)
        {
            return false;
        }

        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        if (resolved == null || !resolved.Active)
            return false;

        if (repairOwner != null && !object.ReferenceEquals(repairOwner, owner))
            Reset();
        repairOwner = owner;

        int index = context.SatelliteId - 1;
        RepairState existing = states[index];
        if (existing.Active)
        {
            if (object.ReferenceEquals(existing.Ship, satellite))
            {
                if (satellite.health <= 0f)
                    satellite.health = 1f;
                return true;
            }

            ReleaseState(index, false);
        }

        if (!OrrerySatellites.SetDisabled(satellite, true))
            return false;

        // A destroyed satellite begins its repair from an empty defensive state.
        // Native lethal damage normally already emptied the shield; the explicit
        // drain also makes non-standard Destroyed(false) paths deterministic.
        if (satellite.shield != null && satellite.shield.shield > 0f)
            satellite.shield.Absorb(satellite.shield.shield, null);

        // Damageable.Heal refuses to operate at zero hull, so retain the native
        // object at one hull while the authored repair curve begins from zero.
        satellite.health = 1f;
        satellite.ToggleImmune(true);
        satellite.ToggleCollisions(false);
        satellite.ToggleRegen(false);

        states[index] = new RepairState
        {
            Active = true,
            Ship = satellite,
            ElapsedSeconds = 0f
        };
        return true;
    }

    public static void FixedTick(GameShip owner, float deltaTime)
    {
        if (repairOwner == null)
            return;

        if (owner == null ||
            !object.ReferenceEquals(owner, repairOwner) ||
            !OrreryRuntime.IsActive(owner))
        {
            Reset();
            return;
        }

        float duration = Mathf.Max(0.01f, Tuning.RepairDurationSeconds);
        float step = Mathf.Max(0f, deltaTime);

        for (int i = 0; i < states.Length; i++)
        {
            RepairState state = states[i];
            if (!state.Active)
                continue;

            OrrerySatellites.SatelliteContext context;
            if (state.Ship == null ||
                !OrrerySatellites.TryGetSatellite(
                    owner,
                    (byte)(i + 1),
                    out context) ||
                context == null ||
                !object.ReferenceEquals(context.Ship, state.Ship) ||
                !context.Disabled)
            {
                ReleaseState(i, false);
                continue;
            }

            GameShip ship = state.Ship;
            state.ElapsedSeconds += step;
            float t = Mathf.Clamp01(state.ElapsedSeconds / duration);

            float hullMax = Mathf.Max(1f, (float)ship.HealthMax);
            float hullTarget = Mathf.Max(1f, hullMax * t);
            if (ship.health <= 0f)
                ship.health = 1f;
            if (ship.health < hullTarget)
            {
                ship.Heal(
                    hullTarget - ship.health,
                    false,
                    Vector2.zero,
                    null,
                    false,
                    true);
            }

            if (ship.shield != null)
            {
                float shieldMax = Mathf.Max(0f, (float)ship.shield.ShieldMax);
                float shieldTarget = shieldMax * t;
                if (ship.shield.shield < shieldTarget)
                {
                    ship.shield.Heal(
                        shieldTarget - ship.shield.shield,
                        Vector2.zero);
                }
            }

            states[i] = state;

            if (t >= 1f)
                CompleteState(i);
        }

        if (!HasActiveState())
            repairOwner = null;
    }

    public static void Reset()
    {
        for (int i = 0; i < states.Length; i++)
            ReleaseState(i, false);
        repairOwner = null;
    }

    private static void CompleteState(int index)
    {
        RepairState state = states[index];
        GameShip ship = state.Ship;
        if (ship != null)
        {
            ship.health = Mathf.Max(1f, (float)ship.HealthMax);
            if (ship.shield != null)
            {
                float shieldMax = Mathf.Max(0f, (float)ship.shield.ShieldMax);
                if (ship.shield.shield < shieldMax)
                    ship.shield.Heal(shieldMax - ship.shield.shield, Vector2.zero);
                ship.shield.shield = shieldMax;
            }
        }

        ReleaseState(index, true);
    }

    private static void ReleaseState(int index, bool reenableSatellite)
    {
        if (index < 0 || index >= states.Length)
            return;

        RepairState state = states[index];
        if (!state.Active)
            return;

        states[index] = default(RepairState);

        GameShip ship = state.Ship;
        if (ship == null)
            return;

        // These native APIs are counted toggles, so releasing exactly the one
        // token acquired at repair start does not stomp unrelated systems.
        ship.ToggleRegen(true);
        ship.ToggleCollisions(true);
        ship.ToggleImmune(false);

        if (reenableSatellite)
            OrrerySatellites.SetDisabled(ship, false);
    }

    private static bool HasActiveState()
    {
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].Active)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Normal lethal damage reaches GameShip.TryPreventDeath before native teardown.
/// Orrery satellites consume that death and enter their repair state instead.
/// </summary>
[HarmonyPatch]
public static class OrrerySatellitePreventDeathPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(GameShip), "TryPreventDeath");
    }

    public static bool Prefix(GameShip __instance, ref bool __result)
    {
        GameShip owner;
        OrrerySatellites.SatelliteContext context;
        if (__instance == null ||
            !OrrerySatellites.TryGetSatelliteContext(
                __instance,
                out owner,
                out context) ||
            !OrrerySatelliteRepair.TryBegin(__instance, owner, context))
        {
            return true;
        }

        __result = true;
        return false;
    }
}

/// <summary>
/// Narrow fallback for native instant-kill/direct-destruction paths that bypass
/// TryPreventDeath. Voluntary teardown is never intercepted.
/// </summary>
[HarmonyPatch(typeof(GameShip), "Destroyed")]
public static class OrrerySatelliteDestroyedPatch
{
    public static bool Prefix(GameShip __instance, bool voluntary)
    {
        if (__instance == null || voluntary)
            return true;

        GameShip owner;
        OrrerySatellites.SatelliteContext context;
        if (!OrrerySatellites.TryGetSatelliteContext(
                __instance,
                out owner,
                out context))
        {
            return true;
        }

        return !OrrerySatelliteRepair.TryBegin(__instance, owner, context);
    }
}

/// <summary>
/// Satellite repair advances on the Orrery physical controller's fixed-step
/// lifetime instead of on every GameShip in the world.
/// </summary>
[HarmonyPatch(typeof(OrreryController), "FixedUpdate")]
public static class OrrerySatelliteRepairTickPatch
{
    public static void Postfix()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        OrrerySatelliteRepair.FixedTick(owner, Time.fixedDeltaTime);
    }
}

/// <summary>
/// Release counted native toggles before the controller invalidates or destroys
/// its satellite objects during owner changes, rebuilds, or world teardown.
/// </summary>
[HarmonyPatch(typeof(OrreryController), "TearDownCurrentBuild")]
public static class OrrerySatelliteRepairTeardownPatch
{
    public static void Prefix()
    {
        OrrerySatelliteRepair.Reset();
    }
}
