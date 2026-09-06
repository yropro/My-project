using HarmonyLib;
using StarVortex;
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

// Starfire-owned values read their named knobs directly inside
// LeviathanStarfireRuntime. Native Activatable properties still need Harmony
// hooks because those values live in Star Vortex rather than Starfire code.
[HarmonyPatch(typeof(Activatable), "get_Cooldown")]
public static class LeviathanStarfireSpecializationCooldownPatch
{
    public static void Postfix(Activatable __instance, ref float __result)
    {
        Torch torch = __instance as Torch;
        if (!IsCurrentStarfireSource(torch))
            return;

        __result *= GetRecoveryMultiplier(
            LeviathanStarfireKnobs.Cooldown
        );
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

    internal static float GetRecoveryMultiplier(
        LeviathanSpecializationKnob specificKnob)
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (pilot == null)
            return 1f;

        float common = LeviathanSpecializationRuntime.GetKnobMultiplier(
            pilot,
            LeviathanStarfireKnobs.RechargeTime
        );
        float specific = LeviathanSpecializationRuntime.GetKnobMultiplier(
            pilot,
            specificKnob
        );

        return Mathf.Max(0f, common * specific);
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

        __result *= LeviathanStarfireSpecializationCooldownPatch.GetRecoveryMultiplier(
            LeviathanStarfireKnobs.RechargeSeconds
        );
    }
}
