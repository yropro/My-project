using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Behemoth core mechanics and specialization-facing contract.
///
/// Behemoth owns its passive tuning and the resolved configuration consumed by
/// Temporal Dive. The Temporal Dive simulation itself intentionally lives in
/// LeviathanTemporalDive.cs because localized time dilation crosses ships,
/// weapons, beams, projectiles and multiplayer authority boundaries.
/// </summary>
public static class LeviathanBehemoth
{
    public const int MaxRank = 5;

    public static class Tuning
    {
        // Leviathan's baseline redirected-segment damage before Behemoth.
        public const float BaseSegmentDamageMultiplier = 0.40f;

        // Rank 1 -> Rank 5. Preserved from the current live Behemoth source.
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

        internal static readonly float[] StaticHullRegenPerScalingSegmentPerSecondByRank =
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

        // -----------------------------------------------------------------
        // Temporal Dive baseline
        // -----------------------------------------------------------------
        // Spatial values are authored in meters and converted only inside the
        // Temporal Dive runtime. 20 displayed meters == 1 Star Vortex world unit.
        public const float TemporalDiveInnerRadiusMeters = 30f;
        public const float TemporalDiveOuterRadiusMeters = 180f;

        // Time factors, not "percent slow". 0.35 = simulation advances at 35%
        // normal rate at full strength. Allies recover more time than hostiles.
        public const float TemporalDiveEnemyMinimumFactor = 0.35f;
        public const float TemporalDiveAllyMinimumFactor = 0.60f;
        public const float TemporalDiveFalloffExponent = 1.00f;

        public const float TemporalDiveDurationSeconds = 20f;
        public const float TemporalDiveCooldownSeconds = 30f;
    }

    public static class Flags
    {
        public static readonly CoreSpecializationFlag TemporalDive =
            CoreSpecializationFlag.Create(
                "behemoth.temporal_dive",
                "Temporal Dive"
            );
    }

    /// <summary>
    /// One cached answer for all Behemoth consumers. Ordinary "per segment"
    /// scaling uses Growth's canonical ScalingSegmentCount; protection remains
    /// role-based in the damage/status router and is not inferred from this count.
    /// </summary>
    public sealed class ResolvedState
    {
        public bool Active;
        public int Rank;

        public int ScalingSegmentCount;
        public int AnatomyRevision;

        public float SegmentDamageMultiplier;
        public float SegmentDebuffDiscardChance;
        public float FlatHullRegenPerSecond;
        public float MaxHullRegenFractionPerSecond;

        public bool TemporalDive;
        public float TemporalDiveInnerRadiusMeters;
        public float TemporalDiveOuterRadiusMeters;
        public float TemporalDiveEnemyMinimumFactor;
        public float TemporalDiveAllyMinimumFactor;
        public float TemporalDiveFalloffExponent;
        public float TemporalDiveDurationSeconds;
        public float TemporalDiveCooldownSeconds;
    }

    private sealed class ResolvedCacheEntry
    {
        public GameShip Ship;
        public int Rank;
        public bool GrowthActive;
        public int ScalingSegmentCount;
        public int AnatomyRevision;
        public int ConfigurationRevision;
        public bool SpecializationReady;
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
        if (pilot == null || CoreSpecializationRuntime.GetEffectiveClass(pilot) != CoreClassId.Leviathan)
            return inactiveState;

        int rank = Mathf.Clamp(
            pilot.GetUpgradeLevel(LeviathanMod.BehemothUpgrade),
            0,
            MaxRank
        );

        LeviathanGrowth.ResolvedState growth =
            LeviathanGrowth.GetResolvedState(ship);

        bool growthActive = growth != null && growth.Active;

        int scalingSegmentCount = growthActive
            ? Mathf.Max(0, LeviathanGrowth.GetScalingSegmentCount(ship))
            : 0;

        int anatomyRevision = 0;
        if (growthActive)
        {
            var anatomy = LeviathanGrowth.GetAnatomy(ship);

            if (anatomy != null)
                anatomyRevision = anatomy.Revision;
        }

        int configurationRevision =
            CoreSpecializationRuntime.ConfigurationRevision;

        bool specializationReady =
            IsLocalOwner(ship) ||
            !NetSession.InSession ||
            CoreNetwork.HasSynchronizedSpecialization(ship, CoreClassId.Leviathan);

        ResolvedCacheEntry cached;
        if (resolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            object.ReferenceEquals(cached.Ship, ship) &&
            cached.Rank == rank &&
            cached.GrowthActive == growthActive &&
            cached.ScalingSegmentCount == scalingSegmentCount &&
            cached.AnatomyRevision == anatomyRevision &&
            cached.ConfigurationRevision == configurationRevision &&
            cached.SpecializationReady == specializationReady &&
            cached.State != null)
        {
            return cached.State;
        }

        ResolvedState state = BuildResolvedState(
            pilot,
            rank,
            growthActive,
            scalingSegmentCount,
            anatomyRevision,
            specializationReady
        );

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            resolvedByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.Rank = rank;
        cached.GrowthActive = growthActive;
        cached.ScalingSegmentCount = scalingSegmentCount;
        cached.AnatomyRevision = anatomyRevision;
        cached.ConfigurationRevision = configurationRevision;
        cached.SpecializationReady = specializationReady;
        cached.State = state;

        return state;
    }

    /// <summary>
    /// Rank 0 intentionally returns the Leviathan chassis baseline of 0.40x.
    /// The damage router decides whether a particular Body/Tail hit is eligible.
    /// </summary>
    public static float GetSegmentDamageMultiplier(GameShip ship)
    {
        return Mathf.Clamp01(GetResolvedState(ship).SegmentDamageMultiplier);
    }

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

    public static void ForgetShip(GameShip ship)
    {
        if (ship == null)
            return;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot == null)
            return;

        ResolvedCacheEntry cached;
        if (resolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            object.ReferenceEquals(cached.Ship, ship))
        {
            resolvedByPilot.Remove(pilot);
        }
    }

    public static void Reset()
    {
        resolvedByPilot.Clear();
    }

    // ---------------------------------------------------------------------
    // Resolution
    // ---------------------------------------------------------------------

    private static ResolvedState BuildResolvedState(
        Pilot pilot,
        int rank,
        bool growthActive,
        int scalingSegmentCount,
        int anatomyRevision,
        bool specializationReady)
    {
        ResolvedState state = CreateInactiveState();

        rank = Mathf.Clamp(rank, 0, MaxRank);

        // A stale Behemoth native rank without an active Leviathan chassis must
        // not modify a normal ship.
        if (!growthActive || rank < 1)
            return state;

        state.Active = true;
        state.Rank = rank;
        state.ScalingSegmentCount = Mathf.Max(0, scalingSegmentCount);
        state.AnatomyRevision = anatomyRevision;

        state.SegmentDamageMultiplier = Mathf.Clamp01(
            GetRankValue(Tuning.SegmentDamageMultiplierByRank, rank)
        );

        state.SegmentDebuffDiscardChance = Mathf.Clamp01(
            GetRankValue(Tuning.SegmentDebuffDiscardChanceByRank, rank)
        );

        state.FlatHullRegenPerSecond =
            GetRankValue(Tuning.StaticHullRegenPerSecondByRank, rank) +
            state.ScalingSegmentCount *
            GetRankValue(
                Tuning.StaticHullRegenPerScalingSegmentPerSecondByRank,
                rank
            );

        state.MaxHullRegenFractionPerSecond = Mathf.Max(
            0f,
            GetRankValue(Tuning.MaxHullRegenPercentPerSecondByRank, rank) /
            100f
        );

        // Current tree has only the keystone flag; all field-shape values are
        // static tuning. If tree knobs are added later, resolve them here so the
        // Temporal Dive runtime remains node-name agnostic.
        state.TemporalDive =
            specializationReady &&
            pilot != null &&
            CoreSpecializationRuntime.HasFlag(
                pilot,
                Flags.TemporalDive
            );

        state.TemporalDiveInnerRadiusMeters =
            Tuning.TemporalDiveInnerRadiusMeters;
        state.TemporalDiveOuterRadiusMeters =
            Tuning.TemporalDiveOuterRadiusMeters;
        state.TemporalDiveEnemyMinimumFactor =
            Tuning.TemporalDiveEnemyMinimumFactor;
        state.TemporalDiveAllyMinimumFactor =
            Tuning.TemporalDiveAllyMinimumFactor;
        state.TemporalDiveFalloffExponent =
            Tuning.TemporalDiveFalloffExponent;
        state.TemporalDiveDurationSeconds =
            Tuning.TemporalDiveDurationSeconds;
        state.TemporalDiveCooldownSeconds =
            Tuning.TemporalDiveCooldownSeconds;

        return state;
    }

    private static ResolvedState CreateInactiveState()
    {
        ResolvedState state = new ResolvedState();
        state.SegmentDamageMultiplier = Tuning.BaseSegmentDamageMultiplier;
        state.TemporalDiveInnerRadiusMeters = Tuning.TemporalDiveInnerRadiusMeters;
        state.TemporalDiveOuterRadiusMeters = Tuning.TemporalDiveOuterRadiusMeters;
        state.TemporalDiveEnemyMinimumFactor = Tuning.TemporalDiveEnemyMinimumFactor;
        state.TemporalDiveAllyMinimumFactor = Tuning.TemporalDiveAllyMinimumFactor;
        state.TemporalDiveFalloffExponent = Tuning.TemporalDiveFalloffExponent;
        state.TemporalDiveDurationSeconds = Tuning.TemporalDiveDurationSeconds;
        state.TemporalDiveCooldownSeconds = Tuning.TemporalDiveCooldownSeconds;
        return state;
    }

    private static float GetRankValue(float[] values, int rank)
    {
        if (values == null || values.Length == 0 || rank < 1)
            return 0f;

        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }

    internal static bool IsLocalOwner(GameShip ship)
    {
        return ship != null &&
            WorldController.instance != null &&
            object.ReferenceEquals(
                WorldController.instance.GetCurrentPlayerShip(),
                ship
            );
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

        // Native stat modifiers apply only to the locally controlled primary
        // player ship. Attached Leviathan sections are separate GameShip objects
        // and can never pass this ownership gate.
        if (!IsLocalOwner(ship))
            return false;

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
/// regeneration is authored in Hull HP/sec, so convert at this verified native
/// stat boundary and add the separately-authored max-Hull fraction.
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

[HarmonyPatch(typeof(RemoteShipDriver), "DestroyRep")]
public static class LeviathanBehemothRemoteDestroyedPatch
{
    public static void Prefix(GameShip __0)
    {
        LeviathanBehemoth.ForgetShip(__0);
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
