using HarmonyLib;
using StarVortex;
using System;
using System.Reflection;
using UnityEngine;

// Starfire is owned by its Evolution-tree unlock. The legacy native Starfire
// skill remains a fallback only when the specialization tree is not unlocked.
[HarmonyPatch(typeof(LeviathanStarfireRuntime), "TryGetStarfireRank")]
public static class LeviathanStarfireSpecializationRootActivationPatch
{
    public static void Postfix(GameShip player, ref int rank, ref bool __result)
    {
        if (player == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != player ||
            LeviathanMod.Controller == null)
        {
            return;
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null ||
            !LeviathanSpecializationRuntime.IsTreeActive(
                pilot,
                LeviathanStarfireTree.TreeId) ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1 ||
            LeviathanMod.Controller.GetActiveSectionCount(player) <
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2)
        {
            return;
        }

        // Tree unlock grants the skill's rank-1 baseline. Specialization nodes
        // modify exposed knobs instead of pretending to be native skill ranks.
        rank = 1;
        __result = true;
    }
}

// Starfire's existing rank arrays remain the baseline implementation knobs.
// Specialization nodes point at named knob objects; this bridge is the only
// place that translates those knob modifiers into Starfire's runtime values.
[HarmonyPatch]
public static class LeviathanStarfireSpecializationRankValuePatch
{
    private static readonly FieldInfo LengthValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "LengthMultiplierByRank");
    private static readonly FieldInfo WidthValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "WidthMultiplierByRank");
    private static readonly FieldInfo DamageValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "DamageMultiplierByRank");
    private static readonly FieldInfo DebuffValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "DebuffChanceMultiplierByRank");
    private static readonly FieldInfo ChargeRampValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "ChargeRampSpeedMultiplierByRank");

    private static object lengthArray;
    private static object widthArray;
    private static object damageArray;
    private static object debuffArray;
    private static object chargeRampArray;

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(LeviathanStarfireRuntime),
            "GetRankValue",
            new Type[] { typeof(float[]), typeof(int) }
        );
    }

    public static void Postfix(float[] __0, ref float __result)
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (pilot == null || __0 == null)
            return;

        ResolveArrays();

        if (ReferenceEquals(__0, lengthArray))
        {
            __result *= LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                LeviathanStarfireKnobs.Length
            );
            return;
        }

        if (ReferenceEquals(__0, widthArray))
        {
            __result *= LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                LeviathanStarfireKnobs.Width
            );
            return;
        }

        if (ReferenceEquals(__0, damageArray))
        {
            __result *= LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                LeviathanStarfireKnobs.Damage
            );
            return;
        }

        if (ReferenceEquals(__0, debuffArray))
        {
            __result *= LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                LeviathanStarfireKnobs.DebuffChance
            );
            return;
        }

        if (ReferenceEquals(__0, chargeRampArray))
        {
            // More Duration slows Starfire's contraction clock; negative
            // Duration nodes speed the contraction up using the same knob.
            float duration = Mathf.Max(
                0.05f,
                LeviathanSpecializationRuntime.GetKnobMultiplier(
                    pilot,
                    LeviathanStarfireKnobs.Duration
                )
            );
            __result /= duration;
        }
    }

    private static void ResolveArrays()
    {
        if (lengthArray == null && LengthValues != null)
            lengthArray = LengthValues.GetValue(null);
        if (widthArray == null && WidthValues != null)
            widthArray = WidthValues.GetValue(null);
        if (damageArray == null && DamageValues != null)
            damageArray = DamageValues.GetValue(null);
        if (debuffArray == null && DebuffValues != null)
            debuffArray = DebuffValues.GetValue(null);
        if (chargeRampArray == null && ChargeRampValues != null)
            chargeRampArray = ChargeRampValues.GetValue(null);
    }
}

[HarmonyPatch(typeof(Activatable), "get_Cooldown")]
public static class LeviathanStarfireSpecializationCooldownPatch
{
    public static void Postfix(Activatable __instance, ref float __result)
    {
        Torch torch = __instance as Torch;
        if (!IsCurrentStarfireSource(torch))
            return;

        __result *= GetRechargeMultiplier();
    }

    internal static bool IsCurrentStarfireSource(Torch torch)
    {
        if (torch == null || WorldController.instance == null)
            return false;

        GameShip player = WorldController.instance.GetCurrentPlayerShip();
        if (player == null)
            return false;

        int rank;
        if (!LeviathanStarfireRuntime.TryGetStarfireRank(player, out rank))
            return false;

        if (player.slots == null)
            return false;

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];
            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            Torch candidate = slot.equippable as Torch;
            if (candidate != null && candidate.type == Item.Type.PrimaryWeapon)
                return ReferenceEquals(candidate, torch);
        }

        return false;
    }

    internal static float GetRechargeMultiplier()
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        return pilot == null
            ? 1f
            : LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                LeviathanStarfireKnobs.RechargeTime
            );
    }
}

[HarmonyPatch(typeof(Activatable), "get_RechargeSeconds")]
public static class LeviathanStarfireSpecializationRechargePatch
{
    public static void Postfix(Activatable __instance, ref float __result)
    {
        Torch torch = __instance as Torch;
        if (!LeviathanStarfireSpecializationCooldownPatch.IsCurrentStarfireSource(torch))
            return;

        __result *=
            LeviathanStarfireSpecializationCooldownPatch.GetRechargeMultiplier();
    }
}
