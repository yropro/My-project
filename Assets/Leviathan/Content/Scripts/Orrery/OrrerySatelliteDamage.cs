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
///
/// Disabled/death interception deliberately does not live here yet. That policy
/// must be settled together with disabled collision behavior so a disabled
/// satellite cannot accidentally become an immortal projectile shield.
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
                typeof(bool),
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
