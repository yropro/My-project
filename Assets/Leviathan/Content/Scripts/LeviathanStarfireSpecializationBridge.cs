using HarmonyLib;
using StarVortex;
using System;
using System.Reflection;
using UnityEngine;
using static StarVortex.Damageable;

// A purchased specialization root owns Starfire. While it is active, the old
// native Starfire rank is ignored and Starfire runs from its rank-1 baseline;
// all additional scaling comes from specialization nodes. If the root is absent,
// the old native Starfire skill remains a legacy fallback.
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
            !LeviathanSpecializationRuntime.HasNode(
                pilot,
                LeviathanStarfireSpecialization.TreeId,
                LeviathanStarfireSpecialization.RootNodeId) ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1 ||
            LeviathanMod.Controller.GetActiveSectionCount(player) <
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2)
        {
            return;
        }

        rank = 1;
        __result = true;
    }
}

// Applies generic Starfire specialization stats at the same rank-value boundary
// already used by LeviathanStarfireRuntime. This keeps the tree data-driven:
// adding another +X% Width node does not require another gameplay patch.
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
    private static readonly FieldInfo StartupValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "StartupDelaySecondsByRank");
    private static readonly FieldInfo ChargeRampValues =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "ChargeRampSpeedMultiplierByRank");

    private static object lengthArray;
    private static object widthArray;
    private static object damageArray;
    private static object debuffArray;
    private static object startupArray;
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
            __result *= Get(
                pilot,
                LeviathanStarfireSpecialization.Stats.Length
            );
            return;
        }

        if (ReferenceEquals(__0, widthArray))
        {
            __result *= Get(
                pilot,
                LeviathanStarfireSpecialization.Stats.Width
            );
            return;
        }

        if (ReferenceEquals(__0, damageArray))
        {
            __result *= Get(
                pilot,
                LeviathanStarfireSpecialization.Stats.Damage
            );
            return;
        }

        if (ReferenceEquals(__0, debuffArray))
        {
            __result *= Get(
                pilot,
                LeviathanStarfireSpecialization.Stats.DebuffChance
            );
            return;
        }

        if (ReferenceEquals(__0, chargeRampArray))
        {
            // Native Starfire's charge ramp is also its longitudinal contraction
            // clock. More Duration means a slower ramp; less Duration collapses it
            // faster. This makes the generic Duration stat immediately tangible.
            float duration = Mathf.Max(
                0.05f,
                Get(pilot, LeviathanStarfireSpecialization.Stats.Duration)
            );
            __result /= duration;
            return;
        }

        if (ReferenceEquals(__0, startupArray) &&
            Has(pilot, LeviathanStarfireSpecialization.Flags.DeepBreath))
        {
            __result += 0.75f;
        }
    }

    private static float Get(Pilot pilot, string stat)
    {
        return LeviathanSpecializationRuntime.GetMultiplier(
            pilot,
            LeviathanStarfireSpecialization.TreeId,
            stat
        );
    }

    private static bool Has(Pilot pilot, string flag)
    {
        return LeviathanSpecializationRuntime.HasFlag(
            pilot,
            LeviathanStarfireSpecialization.TreeId,
            flag
        );
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
        if (startupArray == null && StartupValues != null)
            startupArray = StartupValues.GetValue(null);
        if (chargeRampArray == null && ChargeRampValues != null)
            chargeRampArray = ChargeRampValues.GetValue(null);
    }
}

// Recharge is a generic specialization stat too. Apply it to both native
// cooldown and charge-recharge timing for Starfire's selected source Torch.
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
            : LeviathanSpecializationRuntime.GetMultiplier(
                pilot,
                LeviathanStarfireSpecialization.TreeId,
                LeviathanStarfireSpecialization.Stats.RechargeTime
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

// Forceful Exhalation gets a genuinely bursty opening window instead of only a
// generic damage bonus. The normal specialization Damage multiplier has already
// been applied when this Postfix runs.
[HarmonyPatch(typeof(LeviathanStarfireRuntime), "ScaleNativeTorchDamage")]
public static class LeviathanForcefulExhalationBurstPatch
{
    private static readonly FieldInfo CurrentDamageTorchField =
        AccessTools.Field(typeof(LeviathanStarfireRuntime), "currentDamageTorch");

    private static readonly FieldInfo ChargeField =
        AccessTools.Field(typeof(Torch), "charge");

    private const float OpeningChargeThreshold = 0.22f;
    private const float OpeningBurstMultiplier = 1.60f;

    public static void Postfix(object[] args)
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (pilot == null ||
            !LeviathanSpecializationRuntime.HasFlag(
                pilot,
                LeviathanStarfireSpecialization.TreeId,
                LeviathanStarfireSpecialization.Flags.ForcefulExhalation) ||
            args == null ||
            args.Length < 3)
        {
            return;
        }

        Torch torch = CurrentDamageTorchField == null
            ? null
            : CurrentDamageTorchField.GetValue(null) as Torch;

        if (torch == null || ChargeField == null)
            return;

        object rawCharge = ChargeField.GetValue(torch);
        if (!(rawCharge is float) || (float)rawCharge > OpeningChargeThreshold)
            return;

        DamageData[] packet = args[2] as DamageData[];
        if (packet == null || packet.Length == 0)
            return;

        DamageData[] burst = new DamageData[packet.Length];
        for (int i = 0; i < packet.Length; i++)
        {
            DamageData datum = packet[i];
            datum.damage *= OpeningBurstMultiplier;
            datum.dps *= OpeningBurstMultiplier;
            burst[i] = datum;
        }

        args[2] = burst;
    }
}
