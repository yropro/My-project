using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Behemoth core mechanics.
///
/// Behemoth is currently one native five-rank skill. It has no specialization
/// mechanics of its own yet; LeviathanBehemothTree only provides the granted
/// tree root so later branches can be added without changing this runtime.
///
/// Current responsibilities:
/// - reduce damage redirected through Leviathan segments,
/// - add segment-originated negative-status discard chance,
/// - add flat Hull regeneration per non-head segment,
/// - add percentage-of-max-Hull regeneration,
/// - resolve all five-rank behavior through one cached ResolvedState.
///
/// Growth rank is deliberately not part of Behemoth resolution. Anatomy comes
/// from LeviathanGrowth's canonical live non-head segment count.
/// </summary>
public static class LeviathanBehemoth
{
    public const int MaxRank = 5;

    public static class Tuning
    {
        // Leviathan's baseline redirected-segment damage before Behemoth.
        public const float BaseSegmentDamageMultiplier = 0.40f;

        // Rank 1 -> Rank 5. These preserve the current tuned values.
        internal static readonly float[] SegmentDamageMultiplierByRank =
        {
            0.35f,
            0.30f,
            0.25f,
            0.23f,
            0.20f
        };

        internal static readonly float[] SegmentDebuffDiscardChanceByRank =
        {
            0.20f,
            0.25f,
            0.30f,
            0.35f,
            0.40f
        };

        internal static readonly float[] StaticHullRegenPerSecondByRank =
        {
            0.00f,
            0.00f,
            0.00f,
            0.00f,
            0.00f
        };

        internal static readonly float[] StaticHullRegenPerNonHeadSegmentPerSecondByRank =
        {
            2.50f,
            3.00f,
            3.50f,
            4.50f,
            6.50f
        };

        // Authored as percent of max Hull regenerated per second.
        internal static readonly float[] MaxHullRegenPercentPerSecondByRank =
        {
            0.30f,
            0.50f,
            0.60f,
            0.80f,
            1.00f
        };
    }

    /// <summary>
    /// Single source of truth for Behemoth's currently resolved native-rank
    /// behavior. No tree node ids or Growth ranks are consulted here.
    /// </summary>
    public sealed class ResolvedState
    {
        public bool Active;
        public int Rank;
        public int NonHeadSegments;

        public float SegmentDamageMultiplier;
        public float SegmentDebuffDiscardChance;

        public float FlatHullRegenPerSecond;
        public float MaxHullRegenFractionPerSecond;
    }

    private sealed class ResolvedCacheEntry
    {
        public GameShip Ship;
        public int Rank;
        public bool GrowthActive;
        public int NonHeadSegments;
        public ResolvedState State;
    }

    private static readonly Dictionary<Pilot, ResolvedCacheEntry> resolvedByPilot =
        new Dictionary<Pilot, ResolvedCacheEntry>();

    private static readonly ResolvedState inactiveState = CreateInactiveState();

    // ---------------------------------------------------------------------
    // Public resolved API
    // ---------------------------------------------------------------------

    public static ResolvedState GetResolvedState(GameShip ship)
    {
        if (ship == null)
            return inactiveState;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot == null)
            return inactiveState;

        int rank = Mathf.Clamp(
            pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade),
            0,
            MaxRank
        );

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(ship);

        bool growthActive = growth != null && growth.Active;
        int nonHeadSegments = growthActive
            ? Mathf.Max(0, growth.NonHeadSegments)
            : 0;

        ResolvedCacheEntry cached;
        if (resolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            object.ReferenceEquals(cached.Ship, ship) &&
            cached.Rank == rank &&
            cached.GrowthActive == growthActive &&
            cached.NonHeadSegments == nonHeadSegments &&
            cached.State != null)
        {
            return cached.State;
        }

        ResolvedState state = BuildResolvedState(
            rank,
            growthActive,
            nonHeadSegments
        );

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            resolvedByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.Rank = rank;
        cached.GrowthActive = growthActive;
        cached.NonHeadSegments = nonHeadSegments;
        cached.State = state;

        return state;
    }

    /// <summary>
    /// Compatibility API used by segment damage transfer.
    /// Rank 0 intentionally returns the Leviathan chassis baseline of 0.40x.
    /// </summary>
    public static float GetSegmentDamageMultiplier(GameShip ship)
    {
        return Mathf.Clamp01(GetResolvedState(ship).SegmentDamageMultiplier);
    }

    /// <summary>
    /// Compatibility API used by segment negative-status transfer.
    /// </summary>
    public static float GetSegmentDebuffDiscardChance(GameShip ship)
    {
        ResolvedState state = GetResolvedState(ship);
        return state.Active
            ? Mathf.Clamp01(state.SegmentDebuffDiscardChance)
            : 0f;
    }

    public static int GetRank(GameShip ship)
    {
        if (ship == null)
            return 0;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        return pilot == null
            ? 0
            : Mathf.Clamp(
                pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade),
                0,
                MaxRank
            );
    }

    public static int GetNonHeadSegmentCount(GameShip ship)
    {
        return GetResolvedState(ship).NonHeadSegments;
    }

    public static void Reset()
    {
        resolvedByPilot.Clear();
    }

    // ---------------------------------------------------------------------
    // Resolution
    // ---------------------------------------------------------------------

    private static ResolvedState BuildResolvedState(
        int rank,
        bool growthActive,
        int nonHeadSegments)
    {
        ResolvedState state = new ResolvedState();
        state.SegmentDamageMultiplier = Tuning.BaseSegmentDamageMultiplier;

        rank = Mathf.Clamp(rank, 0, MaxRank);

        // Behemoth is part of the Leviathan chassis. A stale/native Behemoth
        // rank without an active Leviathan Growth chassis must not modify a
        // normal ship.
        if (!growthActive || rank < 1)
            return state;

        state.Active = true;
        state.Rank = rank;
        state.NonHeadSegments = Mathf.Max(0, nonHeadSegments);

        state.SegmentDamageMultiplier = Mathf.Clamp01(
            GetRankValue(Tuning.SegmentDamageMultiplierByRank, rank)
        );

        state.SegmentDebuffDiscardChance = Mathf.Clamp01(
            GetRankValue(Tuning.SegmentDebuffDiscardChanceByRank, rank)
        );

        state.FlatHullRegenPerSecond =
            GetRankValue(Tuning.StaticHullRegenPerSecondByRank, rank) +
            state.NonHeadSegments *
            GetRankValue(
                Tuning.StaticHullRegenPerNonHeadSegmentPerSecondByRank,
                rank
            );

        state.MaxHullRegenFractionPerSecond = Mathf.Max(
            0f,
            GetRankValue(Tuning.MaxHullRegenPercentPerSecondByRank, rank) /
            100f
        );

        return state;
    }

    private static ResolvedState CreateInactiveState()
    {
        ResolvedState state = new ResolvedState();
        state.SegmentDamageMultiplier = Tuning.BaseSegmentDamageMultiplier;
        return state;
    }

    private static float GetRankValue(float[] values, int rank)
    {
        if (values == null || values.Length == 0 || rank < 1)
            return 0f;

        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }

    // ---------------------------------------------------------------------
    // Native stat boundary
    // ---------------------------------------------------------------------

    internal static bool TryGetLocalActiveState(
        object instance,
        out GameShip ship,
        out ResolvedState state)
    {
        ship = instance as GameShip;
        state = null;

        if (ship == null)
            return false;

        if (LeviathanMod.Controller != null &&
            LeviathanMod.Controller.IsLeviathanSegment(ship))
        {
            return false;
        }

        GameShip localPlayer = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        if (localPlayer == null ||
            !object.ReferenceEquals(localPlayer, ship))
        {
            return false;
        }

        state = GetResolvedState(ship);
        return state != null && state.Active;
    }

    internal static MethodBase FindHealthRegenGetter()
    {
        return AccessTools.Method(typeof(GameShip), "get_HealthRegen") ??
            AccessTools.Method(typeof(Damageable), "get_HealthRegen");
    }
}

/// <summary>
/// Native HealthRegen is a fraction of max Hull per second. Behemoth's flat
/// regeneration is authored in Hull HP/sec, so convert only at this verified
/// native stat boundary and then add the separately-authored max-Hull fraction.
/// </summary>
[HarmonyPatch]
public static class LeviathanBehemothHullRegenPatch
{
    public static MethodBase TargetMethod()
    {
        return LeviathanBehemoth.FindHealthRegenGetter();
    }

    public static void Postfix(object __instance, ref float __result)
    {
        GameShip ship;
        LeviathanBehemoth.ResolvedState state;

        if (!LeviathanBehemoth.TryGetLocalActiveState(
                __instance,
                out ship,
                out state))
        {
            return;
        }

        float maxHull = Mathf.Max(0f, ship.HealthMax);

        if (maxHull > 0f && state.FlatHullRegenPerSecond != 0f)
            __result += state.FlatHullRegenPerSecond / maxHull;

        __result += state.MaxHullRegenFractionPerSecond;
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanBehemothWorldDestroyedPatch
{
    public static void Postfix()
    {
        LeviathanBehemoth.Reset();
    }
}
