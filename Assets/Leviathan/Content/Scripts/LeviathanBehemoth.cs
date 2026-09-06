using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class LeviathanBehemoth
{
    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5.

    // Segment damage transferred to the player.
    // No Behemoth rank uses BaseSegmentDamageMultiplier below.
    public const float BaseSegmentDamageMultiplier = 0.50f;

    private static readonly float[] SegmentDamageMultiplierByRank =
    {
        0.47f, // Rank 1
        0.38f, // Rank 2
        0.33f, // Rank 3
        0.30f, // Rank 4
        0.25f  // Rank 5
    };

    // Additional chance to discard a status-effect attempt transferred from a segment.
    private static readonly float[] SegmentDebuffDiscardChanceByRank =
    {
        0.27f, // Rank 1
        0.30f, // Rank 2
        0.35f, // Rank 3
        0.37f, // Rank 4
        0.40f  // Rank 5
    };

    // Multiplies all six final hull resistance values.
    // 1.00 = unchanged.
    private static readonly float[] ResistanceMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Star Vortex's armor layer is DamageReduction.
    // 1.00 = unchanged.
    private static readonly float[] ArmorMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplies max hull after Growth's hull-per-segment bonus is added.
    // 1.00 = unchanged.
    private static readonly float[] MaxHullMultiplierByRank =
    {
        1.05f, // Rank 1
        1.10f, // Rank 2
        1.20f, // Rank 3
        1.25f, // Rank 4
        1.30f  // Rank 5
    };

    // Flat maximum hull added after the multiplier.
    private static readonly float[] MaxHullFlatBonusByRank =
    {
        50.00f, // Rank 1
        55.00f, // Rank 2
        60.00f, // Rank 3
        65.00f, // Rank 4
        70.00f  // Rank 5
    };

    // Flat maximum hull PER attached Leviathan segment (body + tail).
    // Example: 10.0f with 9 bodies + tail = 10 segments = +100 max hull.
    private static readonly float[] MaxHullFlatBonusPerSegmentByRank =
    {
        50.00f, // Rank 1
        55.00f, // Rank 2
        60.00f, // Rank 3
        65.00f, // Rank 4
        70.00f  // Rank 5
    };

    // Flat hull regenerated per second.
    private static readonly float[] StaticHullRegenPerSecondByRank =
    {
        5.00f, // Rank 1
        5.00f, // Rank 2
        10.00f, // Rank 3
        15.00f, // Rank 4
        50.00f  // Rank 5
    };

    // Flat hull regenerated per second PER attached Leviathan segment
    // (body + tail). Example: 0.5f with 9 bodies + tail = +5 hull/sec.
    private static readonly float[] StaticHullRegenPerSegmentPerSecondByRank =
    {
        2.00f, // Rank 1
        4.75f, // Rank 2
        5.50f, // Rank 3
        6.50f, // Rank 4
        8.50f  // Rank 5
    };

    // Additional hull regenerated per second as a percentage of adjusted max hull.
    // Enter percentage points directly: 1.00f = +1% max hull per second.
    private static readonly float[] MaxHullRegenPercentPerSecondByRank =
    {
        0.20f, // Rank 1
        0.40f, // Rank 2
        0.65f, // Rank 3
        0.80f, // Rank 4
        1.00f  // Rank 5
    };

    public const int MaxRank = 5;

    // =========================================================================
    // RUNTIME
    // =========================================================================

    private static readonly MethodInfo HealthMaxGetter =
        FindStatGetter("get_HealthMax");

    public static float GetSegmentDamageMultiplier(GameShip player)
    {
        int rank = Mathf.Clamp(GetRank(player), 0, MaxRank);

        if (rank < 1)
            return BaseSegmentDamageMultiplier;

        return Mathf.Clamp01(
            GetRankValue(SegmentDamageMultiplierByRank, rank)
        );
    }

    public static int GetRank(GameShip player)
    {
        if (player == null)
            return 0;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        return pilot == null
            ? 0
            : pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade);
    }

    public static float GetSegmentDebuffDiscardChance(GameShip player)
    {
        int rank = Mathf.Clamp(GetRank(player), 0, MaxRank);

        if (rank < 1)
            return 0f;

        return GetRankValue(SegmentDebuffDiscardChanceByRank, rank);
    }

    internal static bool TryGetActiveRank(
        object instance,
        out GameShip player,
        out int rank)
    {
        player = instance as GameShip;
        rank = 0;

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

        if (LeviathanGrowth.GetRank(player) < 1)
            return false;

        rank = Mathf.Clamp(GetRank(player), 0, MaxRank);
        return rank >= 1;
    }

    internal static float GetResistanceMultiplier(int rank)
    {
        return GetRankValue(ResistanceMultiplierByRank, rank);
    }

    internal static float GetArmorMultiplier(int rank)
    {
        return GetRankValue(ArmorMultiplierByRank, rank);
    }

    internal static float GetMaxHullMultiplier(int rank)
    {
        return GetRankValue(MaxHullMultiplierByRank, rank);
    }

    internal static float GetMaxHullFlatBonus(int rank)
    {
        return GetRankValue(MaxHullFlatBonusByRank, rank);
    }

    internal static float GetMaxHullFlatBonusPerSegment(int rank)
    {
        return GetRankValue(MaxHullFlatBonusPerSegmentByRank, rank);
    }

    internal static float GetStaticHullRegen(int rank)
    {
        return GetRankValue(StaticHullRegenPerSecondByRank, rank);
    }

    internal static float GetStaticHullRegenPerSegment(int rank)
    {
        return GetRankValue(
            StaticHullRegenPerSegmentPerSecondByRank,
            rank
        );
    }

    internal static float GetMaxHullRegenPercent(int rank)
    {
        return GetRankValue(MaxHullRegenPercentPerSecondByRank, rank);
    }

    internal static float GetAdjustedMaxHull(GameShip player)
    {
        if (player == null || HealthMaxGetter == null)
            return 0f;

        object value = HealthMaxGetter.Invoke(player, null);

        // Native GameShip.HealthMax is int. Accept float too so this helper stays
        // tolerant if another supported target ever exposes a floating-point stat.
        if (value is int)
            return (float)(int)value;

        if (value is float)
            return (float)value;

        return 0f;
    }

    internal static MethodInfo FindStatGetter(string name)
    {
        return AccessTools.Method(typeof(GameShip), name) ??
            AccessTools.Method(typeof(Damageable), name);
    }

    private static float GetRankValue(float[] values, int rank)
    {
        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }
}

[HarmonyPatch]
public static class LeviathanBehemothResistancePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        string[] getters =
        {
            "get_ResistanceKinetic",
            "get_ResistanceElectric",
            "get_ResistanceCold",
            "get_ResistanceCorrosive",
            "get_ResistanceThermal",
            "get_ResistanceRadiation"
        };

        for (int i = 0; i < getters.Length; i++)
        {
            MethodBase method = LeviathanBehemoth.FindStatGetter(getters[i]);

            if (method != null)
                yield return method;
        }
    }

    public static void Postfix(
        object __instance,
        ref float __result)
    {
        GameShip player;
        int rank;

        if (!LeviathanBehemoth.TryGetActiveRank(
                __instance,
                out player,
                out rank))
        {
            return;
        }

        __result *= LeviathanBehemoth.GetResistanceMultiplier(rank);
    }
}

[HarmonyPatch]
public static class LeviathanBehemothArmorPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanBehemoth.FindStatGetter("get_DamageReduction");
    }

    public static void Postfix(
        object __instance,
        ref float __result)
    {
        GameShip player;
        int rank;

        if (!LeviathanBehemoth.TryGetActiveRank(
                __instance,
                out player,
                out rank))
        {
            return;
        }

        __result *= LeviathanBehemoth.GetArmorMultiplier(rank);
    }
}

[HarmonyPatch]
public static class LeviathanBehemothHullRegenPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanBehemoth.FindStatGetter("get_HealthRegen");
    }

    public static void Postfix(
        object __instance,
        ref float __result)
    {
        GameShip player;
        int rank;

        if (!LeviathanBehemoth.TryGetActiveRank(
                __instance,
                out player,
                out rank))
        {
            return;
        }

        int segmentCount =
            LeviathanGrowth.GetSegmentCount(player);

        float maxHull = LeviathanBehemoth.GetAdjustedMaxHull(player);

        // GameShip.HealthRegen is normalized against max hull rather than stored
        // directly as HP/sec. Convert flat HP/sec bonuses into that normalized value.
        float flatRegenPerSecond =
            LeviathanBehemoth.GetStaticHullRegen(rank) +
            segmentCount *
            LeviathanBehemoth.GetStaticHullRegenPerSegment(rank);

        if (maxHull > 0f)
            __result += flatRegenPerSecond / maxHull;

        // Percentage points convert directly to the normalized regen value.
        __result +=
            LeviathanBehemoth.GetMaxHullRegenPercent(rank) / 100f;
    }
}
