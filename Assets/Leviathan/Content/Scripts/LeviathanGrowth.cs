using HarmonyLib;
using StarVortex;
using System.Reflection;
using UnityEngine;

public static class LeviathanGrowth
{
    // =========================================================================
    // BALANCE TUNING
    // =========================================================================

    // New BODY segments gained per Growth rank.
    // The authored LeviathanTest prefab contains 15 body slots total.
    public const int BodySegmentsPerRank = 3;

    // Flat maximum hull granted by each attached Leviathan segment.
    // "Segment" means body + tail; the head is not counted.
    public const float HullPerSegment = 100.0f;

    // Effective mass of the player/head per Growth rank.
    public const float MassBonusPerRank = 0.25f;

    // Extra Leviathan cruising resistance starts above this fraction of MaxSpeed.
    public const float ResistanceStartFraction = 0.40f;

    // At theoretical MaxSpeed, remove this fraction of MaxSpeed per second.
    public const float ResistanceStrength = 0.55f;

    // Chance to discard a status-effect attempt transferred from a segment.
    private static readonly float[] SegmentDebuffDiscardChanceByRank =
    {
        0.20f, // Rank 1
        0.25f, // Rank 2
        0.30f, // Rank 3
        0.35f, // Rank 4
        0.40f  // Rank 5
    };

    // =========================================================================
    // NATIVE / MECHANICAL CONSTANTS
    // =========================================================================

    public const int MaxRank = 5;

    // Physical pool authored in LeviathanTest: head + 15 body + tail.
    public const int MaxBodySegmentsInPrefab = 15;

    // =========================================================================
    // RUNTIME HELPERS
    // =========================================================================

    public static int GetRank(GameShip player)
    {
        if (player == null)
            return 0;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        return pilot == null
            ? 0
            : Mathf.Clamp(
                pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade),
                0,
                MaxRank
            );
    }

    public static int GetBodySegmentCountForRank(int rank)
    {
        int effectiveRank = Mathf.Clamp(rank, 0, MaxRank);

        return Mathf.Min(
            effectiveRank * BodySegmentsPerRank,
            MaxBodySegmentsInPrefab
        );
    }

    public static int GetSegmentCountForRank(int rank)
    {
        int bodyCount = GetBodySegmentCountForRank(rank);

        // Growth-active Leviathan always has one tail in addition to its bodies.
        return bodyCount > 0
            ? bodyCount + 1
            : 0;
    }

    public static int GetSegmentCount(GameShip player)
    {
        if (player == null)
            return 0;

        // Prefer the actual live chain when available.
        if (LeviathanMod.Controller != null)
        {
            int active =
                LeviathanMod.Controller.GetActiveLeviathanSegmentCount(player);

            if (active > 0)
                return active;
        }

        return GetSegmentCountForRank(GetRank(player));
    }

    public static int GetBodySegmentCount(GameShip player)
    {
        int segmentCount = GetSegmentCount(player);

        return segmentCount > 0
            ? segmentCount - 1
            : 0;
    }

    public static float GetHullBonus(GameShip player)
    {
        return GetSegmentCount(player) * HullPerSegment;
    }

    public static float GetMassMultiplier(int rank)
    {
        int effectiveRank = Mathf.Clamp(rank, 0, MaxRank);
        return 1f + effectiveRank * MassBonusPerRank;
    }

    public static float GetSegmentDebuffDiscardChance(GameShip player)
    {
        int rank = GetRank(player);

        if (rank < 1)
            return 0f;

        return SegmentDebuffDiscardChanceByRank[
            Mathf.Clamp(rank, 1, MaxRank) - 1
        ];
    }

    internal static bool TryGetLeviathanPlayer(
        object instance,
        out GameShip player)
    {
        player = instance as GameShip;

        if (player == null)
            return false;

        if (LeviathanMod.Controller != null &&
            LeviathanMod.Controller.IsLeviathanSegment(player))
        {
            return false;
        }

        GameShip currentPlayer =
            WorldController.instance == null
                ? null
                : WorldController.instance.GetCurrentPlayerShip();

        if (currentPlayer != null && currentPlayer != player)
            return false;

        return GetRank(player) >= 1;
    }

    internal static MethodInfo FindStatGetter(string name)
    {
        return AccessTools.Method(typeof(GameShip), name) ??
            AccessTools.Method(typeof(Damageable), name);
    }
}

// Growth owns the final max-hull composition so Growth and Behemoth stack in a
// deterministic order:
//
// native max hull
// -> + Growth hull/segment (body + tail)
// -> x Behemoth max-hull multiplier
// -> + Behemoth flat hull
// -> + Behemoth flat hull/segment
[HarmonyPatch]
public static class LeviathanGrowthMaxHullPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanGrowth.FindStatGetter("get_HealthMax");
    }

    public static void Postfix(
        object __instance,
        ref int __result)
    {
        GameShip player;

        if (!LeviathanGrowth.TryGetLeviathanPlayer(
                __instance,
                out player))
        {
            return;
        }

        int segmentCount =
            LeviathanGrowth.GetSegmentCount(player);

        float adjustedHull =
            __result +
            segmentCount * LeviathanGrowth.HullPerSegment;

        int behemothRank = Mathf.Clamp(
            LeviathanBehemoth.GetRank(player),
            0,
            LeviathanBehemoth.MaxRank
        );

        if (behemothRank >= 1)
        {
            adjustedHull =
                adjustedHull *
                    LeviathanBehemoth.GetMaxHullMultiplier(behemothRank) +
                LeviathanBehemoth.GetMaxHullFlatBonus(behemothRank) +
                segmentCount *
                    LeviathanBehemoth.GetMaxHullFlatBonusPerSegment(
                        behemothRank
                    );
        }

        // Native GameShip.HealthMax is an int. Keep tuning math in float so
        // fractional multipliers/bonuses remain useful, then round once at the end.
        __result = Mathf.RoundToInt(adjustedHull);
    }
}
