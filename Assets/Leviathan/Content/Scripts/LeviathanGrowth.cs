using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Growth mechanics/runtime boundary.
///
/// Current responsibilities:
/// - expose stable Growth knobs/flags for the tree,
/// - resolve Growth once per Pilot/configuration,
/// - provide one canonical non-head segment budget,
/// - capture fully resolved vanilla Heat Capacity + Heat Loss Time,
/// - apply the 90% Leviathan thermal baseline before Growth modifiers,
/// - bridge straightforward chassis stats through native getter boundaries,
/// - provide linear per-segment mass/high-speed air resistance,
/// - provide flat Hull regen and movement-driven Cooling Fins,
/// - apply Gigantism through native ship-builder class-override semantics.
///
/// Deliberately deferred to behavior-specific passes:
/// forked Bifurcation attachment topology, Specialized Predator slot removal and
/// purchase validation, Ancient Wyrm global crit/status hooks, Cold Blooded
/// dynamic movement, Star Dragon/Supernova, and Coronal Mass Ejection.
/// </summary>
public static class LeviathanGrowth
{
    public static class Tuning
    {
        // Vanilla modifiers are already inside the captured values. These
        // multipliers are the Leviathan baseline applied AFTER vanilla resolves.
        public const float BaselineMaxHeatMultiplier = 0.90f;
        public const float BaselineHeatDissipationMultiplier = 0.90f;

        // Growth's granted root anatomy. "Segment" everywhere else means every
        // Leviathan piece except the head, so the root starts at 2 segments.
        public const int RootBodySegments = 1;
        public const int RootTails = 1;

        // Segment-originated negative status protection. The chassis starts at
        // 20%, then gains +1 percentage point for every 2 Evolution Points spent
        // specifically in the Growth tree. Point-cost scaling intentionally means
        // expensive Growth nodes advance this physiology faster than cheap ones.
        public const float BaselineSegmentDebuffDiscardChance = 0.20f;
        public const int SegmentDebuffDiscardPointsPerStep = 2;
        public const float SegmentDebuffDiscardPerStep = 0.01f;

        // New Growth chassis scaling is authored per non-head segment. These
        // values are rederived from the old full-Growth reference point:
        // 16 non-head pieces (15 bodies + tail) = 3.0x player mass and
        // 0.65 MaxSpeed/sec high-speed resistance at the top of the curve.
        public const float InherentMassPercentPerSegment = 0.125f;
        public const float AirResistanceStrengthPerSegment = 0.65f / 16f;
        public const float AirResistanceStartFraction = 0.40f;

        // Cooling Fins directly vents heat while moving. At 180 m/s this is
        // 18% of FinalHeatDissipationPerSecond each second.
        public const float CoolingFinsDissipationFractionPerMeterPerSecond = 0.001f;
        public const float WorldUnitsPerMeter = 1f / 20f;

        // Cold Blooded is resolved dynamically in the later thermal-behavior
        // pass. 100% effective Heat = +50%; 200% = +100%.
        public const float ColdBloodedMovementBonusPerHeatFraction = 0.50f;

        public const float MinimumHeatCapacity = 1f;
        public const float MinimumHeatDissipationPerSecond = 0.01f;

        // LeviathanTest is an authored seed pool, not a progression table.
        // The runtime may clone additional body-slot definitions when the
        // specialization grants more than these authored slots.
        public const int AuthoredSeedBodySlots = 15;
    }

    public static class Knobs
    {
        // Anatomy.
        public static readonly LeviathanSpecializationKnob AdditionalBodySegments =
            LeviathanSpecializationKnob.Flat(
                "growth.additional_body_segments",
                "Body Segments"
            );

        public static readonly LeviathanSpecializationKnob AdditionalTails =
            LeviathanSpecializationKnob.Flat(
                "growth.additional_tails",
                "Tails"
            );

        // Thermal physiology.
        public static readonly LeviathanSpecializationKnob MaxHeatPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.max_heat_percent",
                "Maximum Heat Capacity"
            );

        public static readonly LeviathanSpecializationKnob HeatDissipationPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.heat_dissipation_percent",
                "Heat Dissipation"
            );

        public static readonly LeviathanSpecializationKnob HeatDissipationPerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.heat_dissipation_per_segment_percent",
                "Heat Dissipation per Segment"
            );

        // Straightforward chassis stats. These are resolved now and intentionally
        // hooked into native gameplay in later verified passes.
        public static readonly LeviathanSpecializationKnob HullFlat =
            LeviathanSpecializationKnob.Flat(
                "growth.hull_flat",
                "Hull"
            );

        public static readonly LeviathanSpecializationKnob HealthRegenFlat =
            LeviathanSpecializationKnob.Flat(
                "growth.health_regen_flat",
                "Health Regeneration",
                "/s"
            );

        public static readonly LeviathanSpecializationKnob AccelerationPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.acceleration_percent",
                "Acceleration"
            );

        public static readonly LeviathanSpecializationKnob MobilityPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.mobility_percent",
                "Maneuverability"
            );

        public static readonly LeviathanSpecializationKnob TurnSpeedPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.turn_speed_percent",
                "Turn Speed"
            );

        public static readonly LeviathanSpecializationKnob TurnSpeedPerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.turn_speed_per_segment_percent",
                "Turn Speed per Segment"
            );

        public static readonly LeviathanSpecializationKnob TopSpeedPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.top_speed_percent",
                "Top Speed"
            );

        public static readonly LeviathanSpecializationKnob TopSpeedPerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.top_speed_per_segment_percent",
                "Top Speed per Segment"
            );

        public static readonly LeviathanSpecializationKnob BoostPerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.boost_per_segment_percent",
                "Boost per Segment"
            );

        public static readonly LeviathanSpecializationKnob ArmorPoints =
            LeviathanSpecializationKnob.PercentagePoints(
                "growth.armor_points",
                "Armor"
            );

        public static readonly LeviathanSpecializationKnob ArmorPerSegmentPoints =
            LeviathanSpecializationKnob.PercentagePoints(
                "growth.armor_per_segment_points",
                "Armor per Segment"
            );

        public static readonly LeviathanSpecializationKnob AllResistancePoints =
            LeviathanSpecializationKnob.PercentagePoints(
                "growth.all_resistance_points",
                "All Resistances"
            );

        public static readonly LeviathanSpecializationKnob ShieldPerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.shield_per_segment_percent",
                "Shield per Segment"
            );

        public static readonly LeviathanSpecializationKnob AirResistancePercent =
            LeviathanSpecializationKnob.Percent(
                "growth.air_resistance_percent",
                "Leviathan Air Resistance"
            );

        public static readonly LeviathanSpecializationKnob MassPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.mass_percent",
                "Mass"
            );

        public static readonly LeviathanSpecializationKnob MassPerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.mass_per_segment_percent",
                "Mass per Segment"
            );

        public static readonly LeviathanSpecializationKnob CritDamagePerSegmentPercent =
            LeviathanSpecializationKnob.Percent(
                "growth.crit_damage_per_segment_percent",
                "Critical Damage per Segment"
            );

        public static readonly LeviathanSpecializationKnob StatusChancePerSegmentPoints =
            LeviathanSpecializationKnob.PercentagePoints(
                "growth.status_chance_per_segment_points",
                "Status Chance per Segment"
            );

        public static readonly LeviathanSpecializationKnob GlobalDamagePercent =
            LeviathanSpecializationKnob.Percent(
                "growth.global_damage_percent",
                "Global Damage"
            );

        public static readonly LeviathanSpecializationKnob WeaponSlotsFlat =
            LeviathanSpecializationKnob.Flat(
                "growth.weapon_slots_flat",
                "Weapon Slots"
            );

        public static readonly LeviathanSpecializationKnob ShipSizeCategoryIncrease =
            LeviathanSpecializationKnob.Flat(
                "growth.ship_size_category_increase",
                "Ship Size Category"
            );

        // Supernova/CME configuration. The behavior layer consumes these later.
        public static readonly LeviathanSpecializationKnob SupernovaCollapseHeatPercent =
            LeviathanSpecializationKnob.Flat(
                "growth.supernova_collapse_heat_percent",
                "Supernova Collapse Heat",
                "%"
            );

        public static readonly LeviathanSpecializationKnob SupernovaDisableSeconds =
            LeviathanSpecializationKnob.Flat(
                "growth.supernova_disable_seconds",
                "Supernova Disable",
                "s"
            );
    }

    public static class Flags
    {
        public static readonly LeviathanSpecializationFlag ColdBlooded =
            LeviathanSpecializationFlag.Create(
                "growth.cold_blooded",
                "Cold Blooded"
            );

        public static readonly LeviathanSpecializationFlag StarDragon =
            LeviathanSpecializationFlag.Create(
                "growth.star_dragon",
                "Star Dragon / Supernova"
            );

        public static readonly LeviathanSpecializationFlag CoronalMassEjection =
            LeviathanSpecializationFlag.Create(
                "growth.coronal_mass_ejection",
                "Coronal Mass Ejection"
            );

        public static readonly LeviathanSpecializationFlag Bifurcation =
            LeviathanSpecializationFlag.Create(
                "growth.bifurcation",
                "Bifurcation"
            );

        public static readonly LeviathanSpecializationFlag Gigantism =
            LeviathanSpecializationFlag.Create(
                "growth.gigantism",
                "Gigantism"
            );

        public static readonly LeviathanSpecializationFlag SpecializedPredator =
            LeviathanSpecializationFlag.Create(
                "growth.specialized_predator",
                "Specialized Predator"
            );

        public static readonly LeviathanSpecializationFlag LaminarScutes =
            LeviathanSpecializationFlag.Create(
                "growth.laminar_scutes",
                "Laminar Scutes"
            );

        public static readonly LeviathanSpecializationFlag CoolingFins =
            LeviathanSpecializationFlag.Create(
                "growth.cooling_fins",
                "Cooling Fins"
            );
    }

    public sealed class ResolvedState
    {
        public bool Active;
        public bool TreeActive;

        public int BodySegments;
        public int TailSegments;
        public int NonHeadSegments;
        public int GrowthTreePointsSpent;
        public float SegmentDebuffDiscardChance;

        public float VanillaMaxHeat;
        public float VanillaHeatLossSeconds;
        public float VanillaHeatDissipationPerSecond;

        public float BaseMaxHeat;
        public float BaseHeatDissipationPerSecond;
        public float FinalMaxHeat;
        public float FinalHeatDissipationPerSecond;
        public float EffectiveHeatLossSeconds;

        public float HullFlat;
        public float HealthRegenFlat;
        public float AccelerationMultiplier;
        public float MobilityMultiplier;
        public float TurnSpeedMultiplier;
        public float TopSpeedMultiplier;
        public float BoostMultiplier;
        public float ArmorBonus;
        public float AllResistanceBonus;
        public float ShieldMultiplier;
        public float MassMultiplier;
        public float AirResistanceStrength;
        public float CritDamageBonus;
        public float StatusChanceBonus;
        public float GlobalDamageMultiplier;
        public int WeaponSlotDelta;
        public int ShipSizeCategoryIncrease;

        public bool ColdBlooded;
        public bool StarDragon;
        public bool CoronalMassEjection;
        public bool Bifurcation;
        public bool Gigantism;
        public bool SpecializedPredator;
        public bool LaminarScutes;
        public bool CoolingFins;

        public float SupernovaCollapseHeatPercent;
        public float SupernovaDisableSeconds;
    }

    private sealed class VanillaHeatBaseline
    {
        public float MaxHeat;
        public float HeatLossSeconds;
        public float DissipationPerSecond;
        public int Revision;
    }

    private sealed class ResolvedCacheEntry
    {
        public GameShip Ship;
        public int ConfigurationRevision;
        public int BaselineRevision;
        public int EvolutionRank;
        public ResolvedState State;
    }

    private static readonly Dictionary<GameShip, VanillaHeatBaseline> heatBaselines =
        new Dictionary<GameShip, VanillaHeatBaseline>();

    private static readonly Dictionary<Pilot, ResolvedCacheEntry> resolvedByPilot =
        new Dictionary<Pilot, ResolvedCacheEntry>();

    private static int nextBaselineRevision;

    // Shared immutable fail-closed result for null/unsynchronized remote queries.
    // Avoid allocating one ResolvedState per presentation frame while a remote
    // Pilot is waiting for its specialization spec block.
    private static readonly ResolvedState inactiveState = CreateInactiveState();

    [ThreadStatic]
    private static GameShip suppressedHeatOverrideShip;

    [ThreadStatic]
    private static bool handlingSpecializationInvalidation;

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

        bool localOwner = IsLocalOwner(ship);
        if (!localOwner && !LeviathanNetwork.HasSynchronizedSpecialization(ship))
            return inactiveState;

        VanillaHeatBaseline baseline;
        heatBaselines.TryGetValue(ship, out baseline);

        int baselineRevision = baseline == null ? 0 : baseline.Revision;
        int configurationRevision = LeviathanSpecializationRuntime.ConfigurationRevision;
        int evolutionRank = pilot.GetUpgradeLevel(
            LeviathanSpecializationCurrency.UpgradeKey
        );

        ResolvedCacheEntry cached;
        if (resolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            cached.Ship == ship &&
            cached.ConfigurationRevision == configurationRevision &&
            cached.BaselineRevision == baselineRevision &&
            cached.EvolutionRank == evolutionRank &&
            cached.State != null)
        {
            return cached.State;
        }

        ResolvedState state = BuildResolvedState(pilot, baseline);

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            resolvedByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.ConfigurationRevision = configurationRevision;
        cached.BaselineRevision = baselineRevision;
        cached.EvolutionRank = evolutionRank;
        cached.State = state;
        return state;
    }

    public static int GetBodySegmentCount(GameShip ship)
    {
        return GetResolvedState(ship).BodySegments;
    }

    // Narrow compile bridge for the current pre-rework Constrictor, which asks
    // only for the old "rank 1" minimum-body baseline. There is no rank scaling
    // behind this method anymore: positive input means the one-rank chassis
    // baseline, zero means inactive. Remove this bridge with the Constrictor
    // rework and use the live controller/resolved-state APIs instead.
    public static int GetBodySegmentCountForRank(int rank)
    {
        return rank > 0 ? Tuning.RootBodySegments : 0;
    }

    public static int GetTailCount(GameShip ship)
    {
        return GetResolvedState(ship).TailSegments;
    }

    public static int GetNonHeadSegmentCount(GameShip ship)
    {
        return GetResolvedState(ship).NonHeadSegments;
    }

    public static bool IsGrowthActive(GameShip ship)
    {
        return GetResolvedState(ship).Active;
    }

    public static bool IsGrowthTreeActive(GameShip ship)
    {
        return GetResolvedState(ship).TreeActive;
    }

    public static float GetSegmentDebuffDiscardChance(GameShip ship)
    {
        ResolvedState state = GetResolvedState(ship);
        return state != null && state.Active
            ? Mathf.Clamp01(state.SegmentDebuffDiscardChance)
            : 0f;
    }


    public static bool TryGetLocalState(
        GameShip ship,
        out ResolvedState state)
    {
        state = null;
        if (!IsLocalOwner(ship))
            return false;

        state = GetResolvedState(ship);
        return state != null && state.Active;
    }

    public static float GetCoolingFinsHeatRemovalPerSecond(GameShip ship)
    {
        ResolvedState state;
        if (!TryGetLocalState(ship, out state) ||
            !state.TreeActive ||
            !state.CoolingFins ||
            state.FinalHeatDissipationPerSecond <= 0f)
        {
            return 0f;
        }

        Rigidbody2D body = ship.GetRigidBody();
        if (body == null)
            return 0f;

        float metersPerSecond =
            body.velocity.magnitude / Tuning.WorldUnitsPerMeter;

        return state.FinalHeatDissipationPerSecond *
            Mathf.Max(0f, metersPerSecond) *
            Tuning.CoolingFinsDissipationFractionPerMeterPerSecond;
    }

    public static void TickOwnerResources(GameShip ship)
    {
        ResolvedState state;
        if (!TryGetLocalState(ship, out state) || !state.TreeActive)
            return;

        float dt = Time.fixedDeltaTime;
        if (dt <= 0f)
            return;

        // Redundant Systems is intentionally flat Hull HP/sec. Native
        // Damageable.HealthRegen is a fraction of max Hull/sec, so using that
        // modifier would give completely different semantics.
        if (state.HealthRegenFlat > 0f &&
            ship.health > 0f &&
            ship.health < ship.HealthMax)
        {
            ship.health = Mathf.Min(
                ship.HealthMax,
                ship.health + state.HealthRegenFlat * dt
            );
        }

        // Cooling Fins directly remove heat and therefore continue working
        // while weapons/boost are generating heat. Supernova overflow will
        // be inserted ahead of ordinary heat in the later thermal pass.
        float finsPerSecond =
            GetCoolingFinsHeatRemovalPerSecond(ship);

        if (finsPerSecond > 0f && ship.heat > 0f)
        {
            ship.heat = Mathf.Max(
                0f,
                ship.heat - finsPerSecond * dt
            );
        }
    }

    private static void ClampLocalDefensiveResources(GameShip ship)
    {
        if (!IsLocalOwner(ship))
            return;

        int maxHull = ship.HealthMax;
        if (ship.health > maxHull)
            ship.health = maxHull;

        if (ship.shield != null)
        {
            int maxShield = ship.shield.ShieldMax;
            if (ship.shield.shield > maxShield)
                ship.shield.shield = maxShield;
        }
    }

    // ---------------------------------------------------------------------
    // Vanilla heat baseline capture
    // ---------------------------------------------------------------------

    public static void RefreshVanillaHeatBaseline(GameShip ship)
    {
        if (ship == null)
            return;

        GameShip previous = suppressedHeatOverrideShip;
        suppressedHeatOverrideShip = ship;

        try
        {
            CaptureVanillaHeatBaselineWhileSuppressed(ship);
        }
        finally
        {
            suppressedHeatOverrideShip = previous;
        }

        ClampLocalHeatToResolvedMaximum(ship);
    }

    internal static GameShip BeginNativeModifierRegeneration(GameShip ship)
    {
        if (!IsLocalOwner(ship))
            return null;

        GameShip previous = suppressedHeatOverrideShip;
        suppressedHeatOverrideShip = ship;
        return previous;
    }

    internal static void CompleteNativeModifierRegeneration(
        GameShip ship,
        GameShip previousSuppressedShip)
    {
        if (ship == null || suppressedHeatOverrideShip != ship)
            return;

        try
        {
            CaptureVanillaHeatBaselineWhileSuppressed(ship);
        }
        finally
        {
            suppressedHeatOverrideShip = previousSuppressedShip;
        }

        ClampLocalHeatToResolvedMaximum(ship);
    }

    internal static void AbortNativeModifierRegeneration(
        GameShip ship,
        GameShip previousSuppressedShip)
    {
        if (suppressedHeatOverrideShip == ship)
            suppressedHeatOverrideShip = previousSuppressedShip;
    }

    private static void CaptureVanillaHeatBaselineWhileSuppressed(GameShip ship)
    {
        if (ship == null)
            return;

        float vanillaMaxHeat = Mathf.Max(
            Tuning.MinimumHeatCapacity,
            ship.HeatCapacity
        );

        float vanillaLossSeconds = Mathf.Max(
            0.0001f,
            ship.HeatCooldownSeconds
        );

        VanillaHeatBaseline baseline;
        if (!heatBaselines.TryGetValue(ship, out baseline) || baseline == null)
        {
            baseline = new VanillaHeatBaseline();
            heatBaselines[ship] = baseline;
        }

        baseline.MaxHeat = vanillaMaxHeat;
        baseline.HeatLossSeconds = vanillaLossSeconds;
        baseline.DissipationPerSecond = vanillaMaxHeat / vanillaLossSeconds;

        unchecked
        {
            nextBaselineRevision++;
            if (nextBaselineRevision == 0)
                nextBaselineRevision = 1;
        }

        baseline.Revision = nextBaselineRevision;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot != null)
            resolvedByPilot.Remove(pilot);
    }

    private static bool TryGetOwnerHeatState(
        GameShip ship,
        out ResolvedState state)
    {
        state = null;

        if (ship == null || suppressedHeatOverrideShip == ship || !IsLocalOwner(ship))
            return false;

        VanillaHeatBaseline baseline;
        if (!heatBaselines.TryGetValue(ship, out baseline) || baseline == null)
            return false;

        state = GetResolvedState(ship);
        return state != null && state.Active;
    }

    private static void ClampLocalHeatToResolvedMaximum(GameShip ship)
    {
        if (!IsLocalOwner(ship))
            return;

        ResolvedState state;
        if (!TryGetOwnerHeatState(ship, out state))
            return;

        float maxHeat = Mathf.Max(Tuning.MinimumHeatCapacity, state.FinalMaxHeat);
        if (ship.heat > maxHeat)
            ship.heat = maxHeat;
    }

    // ---------------------------------------------------------------------
    // Resolution
    // ---------------------------------------------------------------------

    private static ResolvedState BuildResolvedState(
        Pilot pilot,
        VanillaHeatBaseline baseline)
    {
        ResolvedState state = CreateInactiveState();

        if (pilot == null)
            return state;

        // Evolution is the single native Leviathan gateway. Growth is automatically
        // unlocked at Evolution rank 1 and its root is derived/auto-granted.
        if (pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey) < 1)
            return state;

        state.Active = true;
        state.TreeActive = LeviathanSpecializationRuntime.IsTreeUnlocked(
            pilot,
            LeviathanGrowthTree.TreeId
        );

        state.GrowthTreePointsSpent = state.TreeActive
            ? LeviathanSpecializationRuntime.GetTreeSpentPoints(
                pilot,
                LeviathanGrowthTree.TreeId
            )
            : 0;

        int discardSteps = state.GrowthTreePointsSpent /
            Tuning.SegmentDebuffDiscardPointsPerStep;

        state.SegmentDebuffDiscardChance = Mathf.Clamp01(
            Tuning.BaselineSegmentDebuffDiscardChance +
            discardSteps * Tuning.SegmentDebuffDiscardPerStep
        );

        int additionalBodies = state.TreeActive
            ? Mathf.Max(
                0,
                Mathf.RoundToInt(
                    LeviathanSpecializationRuntime.GetKnobFlat(
                        pilot,
                        Knobs.AdditionalBodySegments)
                )
            )
            : 0;

        // The Evolution-awakened Leviathan chassis always starts as one body segment + one tail.
        // The specialization only adds to that baseline. Bifurcation redistributes
        // the same total budget into two branches; it never grants a free segment.
        state.NonHeadSegments =
            Tuning.RootBodySegments +
            Tuning.RootTails +
            additionalBodies;

        state.Bifurcation = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.Bifurcation);

        state.TailSegments = state.Bifurcation ? 2 : 1;
        state.BodySegments = Mathf.Max(
            0,
            state.NonHeadSegments - state.TailSegments
        );

        if (baseline != null)
        {
            state.VanillaMaxHeat = baseline.MaxHeat;
            state.VanillaHeatLossSeconds = baseline.HeatLossSeconds;
            state.VanillaHeatDissipationPerSecond = baseline.DissipationPerSecond;

            state.BaseMaxHeat =
                baseline.MaxHeat * Tuning.BaselineMaxHeatMultiplier;

            state.BaseHeatDissipationPerSecond =
                baseline.DissipationPerSecond *
                Tuning.BaselineHeatDissipationMultiplier;

            float maxHeatMultiplier = state.TreeActive
                ? LeviathanSpecializationRuntime.GetKnobMultiplier(
                    pilot,
                    Knobs.MaxHeatPercent)
                : 1f;

            float dissipationPercent = state.TreeActive
                ? PercentContribution(
                    pilot,
                    Knobs.HeatDissipationPercent)
                : 0f;

            float dissipationPerSegmentPercent = state.TreeActive
                ? PercentContribution(
                    pilot,
                    Knobs.HeatDissipationPerSegmentPercent)
                : 0f;

            float finalDissipationMultiplier = Mathf.Max(
                0f,
                1f +
                dissipationPercent +
                dissipationPerSegmentPercent * state.NonHeadSegments
            );

            state.FinalMaxHeat = Mathf.Max(
                Tuning.MinimumHeatCapacity,
                state.BaseMaxHeat * maxHeatMultiplier
            );

            state.FinalHeatDissipationPerSecond = Mathf.Max(
                Tuning.MinimumHeatDissipationPerSecond,
                state.BaseHeatDissipationPerSecond *
                    finalDissipationMultiplier
            );

            state.EffectiveHeatLossSeconds =
                state.FinalMaxHeat /
                state.FinalHeatDissipationPerSecond;
        }

        state.HullFlat = state.TreeActive
            ? LeviathanSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.HullFlat)
            : 0f;

        state.HealthRegenFlat = state.TreeActive
            ? LeviathanSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.HealthRegenFlat)
            : 0f;

        state.AccelerationMultiplier = state.TreeActive
            ? LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.AccelerationPercent)
            : 1f;

        state.MobilityMultiplier = state.TreeActive
            ? LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.MobilityPercent)
            : 1f;

        state.TurnSpeedMultiplier = Mathf.Max(
            0f,
            1f +
            GrowthPercentContribution(pilot, state.TreeActive, Knobs.TurnSpeedPercent) +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.TurnSpeedPerSegmentPercent) *
                state.NonHeadSegments
        );

        state.TopSpeedMultiplier = Mathf.Max(
            0f,
            1f +
            GrowthPercentContribution(pilot, state.TreeActive, Knobs.TopSpeedPercent) +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.TopSpeedPerSegmentPercent) *
                state.NonHeadSegments
        );

        state.BoostMultiplier = Mathf.Max(
            0f,
            1f +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.BoostPerSegmentPercent) *
                state.NonHeadSegments
        );

        state.ArmorBonus =
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.ArmorPoints) +
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.ArmorPerSegmentPoints) *
                state.NonHeadSegments;

        state.AllResistanceBonus =
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.AllResistancePoints);

        state.ShieldMultiplier = Mathf.Max(
            0f,
            1f +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.ShieldPerSegmentPercent) *
                state.NonHeadSegments
        );

        float massPercent =
            Tuning.InherentMassPercentPerSegment *
                state.NonHeadSegments +
            GrowthPercentContribution(pilot, state.TreeActive, Knobs.MassPercent) +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.MassPerSegmentPercent) *
                state.NonHeadSegments;

        state.MassMultiplier =
            Mathf.Max(0.01f, 1f + massPercent);

        float airResistanceMultiplier =
            Mathf.Max(
                0f,
                GrowthMultiplier(
                    pilot,
                    state.TreeActive,
                    Knobs.AirResistancePercent)
            );

        state.AirResistanceStrength =
            Tuning.AirResistanceStrengthPerSegment *
            state.NonHeadSegments *
            airResistanceMultiplier;

        state.CritDamageBonus =
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.CritDamagePerSegmentPercent) *
            state.NonHeadSegments;

        state.StatusChanceBonus =
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.StatusChancePerSegmentPoints) *
            state.NonHeadSegments;

        state.GlobalDamageMultiplier =
            GrowthMultiplier(
                pilot,
                state.TreeActive,
                Knobs.GlobalDamagePercent);

        state.WeaponSlotDelta = Mathf.RoundToInt(
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.WeaponSlotsFlat)
        );

        state.ShipSizeCategoryIncrease = Mathf.Max(
            0,
            Mathf.RoundToInt(
                GrowthFlat(
                    pilot,
                    state.TreeActive,
                    Knobs.ShipSizeCategoryIncrease)
            )
        );

        state.ColdBlooded = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.ColdBlooded);

        state.StarDragon = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.StarDragon);

        state.CoronalMassEjection = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.CoronalMassEjection);

        state.Gigantism = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.Gigantism);

        state.SpecializedPredator = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.SpecializedPredator);

        state.LaminarScutes = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.LaminarScutes);

        state.CoolingFins = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.CoolingFins);

        state.SupernovaCollapseHeatPercent =
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.SupernovaCollapseHeatPercent);

        state.SupernovaDisableSeconds =
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.SupernovaDisableSeconds);

        return state;
    }

    private static float PercentContribution(
        Pilot pilot,
        LeviathanSpecializationKnob knob)
    {
        return Mathf.Max(
            -1f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(pilot, knob) - 1f
        );
    }

    private static float GrowthPercentContribution(
        Pilot pilot,
        bool treeActive,
        LeviathanSpecializationKnob knob)
    {
        return treeActive ? PercentContribution(pilot, knob) : 0f;
    }

    private static float GrowthFlat(
        Pilot pilot,
        bool treeActive,
        LeviathanSpecializationKnob knob)
    {
        return treeActive
            ? LeviathanSpecializationRuntime.GetKnobFlat(pilot, knob)
            : 0f;
    }

    private static float GrowthMultiplier(
        Pilot pilot,
        bool treeActive,
        LeviathanSpecializationKnob knob)
    {
        return treeActive
            ? LeviathanSpecializationRuntime.GetKnobMultiplier(pilot, knob)
            : 1f;
    }

    private static ResolvedState CreateInactiveState()
    {
        ResolvedState state = new ResolvedState();
        state.AccelerationMultiplier = 1f;
        state.MobilityMultiplier = 1f;
        state.TurnSpeedMultiplier = 1f;
        state.TopSpeedMultiplier = 1f;
        state.BoostMultiplier = 1f;
        state.ShieldMultiplier = 1f;
        state.MassMultiplier = 1f;
        state.GlobalDamageMultiplier = 1f;
        return state;
    }

    private static bool IsLocalOwner(GameShip ship)
    {
        return ship != null &&
            WorldController.instance != null &&
            object.ReferenceEquals(
                WorldController.instance.GetCurrentPlayerShip(),
                ship
            );
    }

    // ---------------------------------------------------------------------
    // Lifecycle / invalidation
    // ---------------------------------------------------------------------

    public static void OnSpecializationConfigurationChanged()
    {
        // Auto-granted-node synchronization may itself invalidate the framework
        // once while a newly unlocked tree is being materialized. Avoid recursively
        // re-entering Growth's clamp path during that derived-state update.
        if (handlingSpecializationInvalidation)
            return;

        handlingSpecializationInvalidation = true;
        try
        {
            GameShip ship = WorldController.instance == null
                ? null
                : WorldController.instance.GetCurrentPlayerShip();

            if (ship == null)
                return;

            if (!heatBaselines.ContainsKey(ship))
                RefreshVanillaHeatBaseline(ship);
            else
                ClampLocalHeatToResolvedMaximum(ship);

            ClampLocalDefensiveResources(ship);

            if (!LeviathanSpecializationCurrency.IsMigratingLegacyGrowth &&
                LeviathanMod.Controller != null)
            {
                LeviathanMod.Controller.RequestGrowthRefreshIfNeeded(ship);
            }
        }
        finally
        {
            handlingSpecializationInvalidation = false;
        }
    }

    public static void ForgetShip(GameShip ship)
    {
        if (ship == null)
            return;

        heatBaselines.Remove(ship);

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot != null)
        {
            ResolvedCacheEntry cached;
            if (resolvedByPilot.TryGetValue(pilot, out cached) &&
                cached != null && cached.Ship == ship)
            {
                resolvedByPilot.Remove(pilot);
            }
        }

        if (suppressedHeatOverrideShip == ship)
            suppressedHeatOverrideShip = null;
    }

    public static void ResetRuntime()
    {
        heatBaselines.Clear();
        resolvedByPilot.Clear();
        suppressedHeatOverrideShip = null;
        handlingSpecializationInvalidation = false;
        nextBaselineRevision = 0;
    }

    // ---------------------------------------------------------------------
    // Native property patch helpers
    // ---------------------------------------------------------------------

    internal static void ApplyHeatCapacityOverride(
        GameShip ship,
        ref int result)
    {
        ResolvedState state;
        if (!TryGetOwnerHeatState(ship, out state))
            return;

        result = Mathf.Max(
            1,
            Mathf.RoundToInt(state.FinalMaxHeat)
        );
    }

    internal static void ApplyHeatLossTimeOverride(
        GameShip ship,
        ref float result)
    {
        ResolvedState state;
        if (!TryGetOwnerHeatState(ship, out state))
            return;

        result = Mathf.Max(
            0.0001f,
            state.EffectiveHeatLossSeconds
        );
    }
}


// =============================================================================
// STRAIGHTFORWARD GROWTH STAT BRIDGES
// =============================================================================
//
// These patches consume the cached Growth ResolvedState. They intentionally sit
// after vanilla has already resolved item/local/global modifiers, so Growth
// remains a distinct additive specialization layer.

[HarmonyPatch(typeof(GameShip), "get_MaxSpeed")]
public static class LeviathanGrowthMaxSpeedPatch
{
    public static void Postfix(GameShip __instance, ref float __result)
    {
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(__instance, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result *= state.TopSpeedMultiplier;
    }
}

[HarmonyPatch(typeof(GameShip), "get_TurnSpeed")]
public static class LeviathanGrowthTurnSpeedPatch
{
    public static void Postfix(GameShip __instance, ref float __result)
    {
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(__instance, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result *= state.TurnSpeedMultiplier;
    }
}

[HarmonyPatch(typeof(Thruster), "get_AccelerationFactor")]
public static class LeviathanGrowthAccelerationPatch
{
    public static void Postfix(Thruster __instance, ref float __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(ship, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result *= state.AccelerationMultiplier;
    }
}

[HarmonyPatch(typeof(Thruster), "get_BoostFactor")]
public static class LeviathanGrowthBoostPatch
{
    public static void Postfix(Thruster __instance, ref float __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(ship, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result *= state.BoostMultiplier;
    }
}

[HarmonyPatch(typeof(Thruster), "get_ControlMultiplier")]
public static class LeviathanGrowthManeuverabilityPatch
{
    public static void Postfix(Thruster __instance, ref float __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(ship, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result *= state.MobilityMultiplier;
    }
}

[HarmonyPatch(typeof(GameShip), "get_HealthMax")]
public static class LeviathanGrowthHullPatch
{
    public static void Postfix(GameShip __instance, ref int __result)
    {
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(__instance, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result = Mathf.Max(
            1,
            __result + Mathf.RoundToInt(state.HullFlat)
        );
    }
}

[HarmonyPatch(typeof(Shield), "get_ShieldMax")]
public static class LeviathanGrowthShieldPatch
{
    public static void Postfix(Shield __instance, ref int __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(ship, out state) ||
            !state.TreeActive)
        {
            return;
        }

        __result = Mathf.Max(
            0,
            Mathf.RoundToInt(__result * state.ShieldMultiplier)
        );
    }
}

// Armor and elemental resistances are native percentage-point stats. Add the
// Growth contribution at the same percentage boundary vanilla uses, before the
// native getter applies its normal cap/status-effect rules.
[HarmonyPatch]
public static class LeviathanGrowthDefensivePercentagePatch
{
    public static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "ApplyModifierToPercentage",
            new Type[]
            {
                typeof(Modifier.Type),
                typeof(Item.Category),
                typeof(float)
            }
        );
    }

    public static void Postfix(
        GameShip __instance,
        Modifier.Type __0,
        ref float __result)
    {
        LeviathanGrowth.ResolvedState state;
        if (!LeviathanGrowth.TryGetLocalState(__instance, out state) ||
            !state.TreeActive)
        {
            return;
        }

        if (__0 == Modifier.Type.DamageReduction)
        {
            __result += state.ArmorBonus;
            return;
        }

        if (__0 == Modifier.Type.ResistanceKinetic ||
            __0 == Modifier.Type.ResistanceCold ||
            __0 == Modifier.Type.ResistanceCorrosive ||
            __0 == Modifier.Type.ResistanceElectric ||
            __0 == Modifier.Type.ResistanceThermal ||
            __0 == Modifier.Type.ResistanceRadiation)
        {
            __result += state.AllResistanceBonus;
        }
    }
}


// Gigantism deliberately changes only the ship-builder class-override boolean.
// It does not mutate the already-built ship's native class or class-derived
// combat stats.
[HarmonyPatch]
public static class LeviathanGrowthGigantismShipBuilderPatch
{
    public static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(ShipBuilder),
            "Init",
            new Type[]
            {
                typeof(string),
                typeof(Item.Serializable[]),
                typeof(int),
                typeof(int),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(string[]),
                typeof(string[]),
                typeof(Action<string>)
            }
        );
    }

    public static void Prefix(ref bool __4)
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();

        if (pilot == null ||
            !LeviathanSpecializationRuntime.IsTreeUnlocked(
                pilot,
                LeviathanGrowthTree.TreeId) ||
            !LeviathanSpecializationRuntime.HasFlag(
                pilot,
                LeviathanGrowth.Flags.Gigantism))
        {
            return;
        }

        __4 = true;
    }
}

// =============================================================================
// HEAT FOUNDATION
// =============================================================================

// RegenerateModifiers is Star Vortex's native resolved-stat invalidation
// boundary. Keep Leviathan heat suppressed while vanilla rebuilds and clamps its
// own values; after the rebuild, snapshot fully-resolved vanilla capacity/loss
// time once, then resume the Leviathan overlay.
[HarmonyPatch(typeof(GameShip), "RegenerateModifiers")]
public static class LeviathanGrowthRegenerateModifiersPatch
{
    public static void Prefix(GameShip __instance, out GameShip __state)
    {
        __state = LeviathanGrowth.BeginNativeModifierRegeneration(__instance);
    }

    public static void Postfix(GameShip __instance, GameShip __state)
    {
        LeviathanGrowth.CompleteNativeModifierRegeneration(
            __instance,
            __state
        );
    }

    public static Exception Finalizer(
        Exception __exception,
        GameShip __instance,
        GameShip __state)
    {
        LeviathanGrowth.AbortNativeModifierRegeneration(
            __instance,
            __state
        );
        return __exception;
    }
}

[HarmonyPatch(typeof(GameShip), "get_HeatCapacity")]
public static class LeviathanGrowthHeatCapacityPatch
{
    public static void Postfix(GameShip __instance, ref int __result)
    {
        LeviathanGrowth.ApplyHeatCapacityOverride(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(GameShip), "get_HeatCooldownSeconds")]
public static class LeviathanGrowthHeatLossTimePatch
{
    public static void Postfix(GameShip __instance, ref float __result)
    {
        LeviathanGrowth.ApplyHeatLossTimeOverride(
            __instance,
            ref __result
        );
    }
}

// Initial ship construction normally regenerates modifiers before
// WorldController marks the ship as the local player. Snapshot immediately after
// that ownership boundary so the first HeatCapacity query already has a baseline.
[HarmonyPatch(typeof(WorldController), "SetCurrentPlayerShip")]
public static class LeviathanGrowthCurrentPlayerShipPatch
{
    public static void Postfix(GameShip __0)
    {
        if (__0 != null)
            LeviathanGrowth.RefreshVanillaHeatBaseline(__0);
    }
}

// Specialization changes do not imply vanilla modifiers changed. Keep the
// captured native baseline and only invalidate/re-clamp the derived Leviathan
// state. This is intentionally separate from RegenerateModifiers.
[HarmonyPatch(typeof(LeviathanSpecializationRuntime), "InvalidateConfiguration")]
public static class LeviathanGrowthSpecializationInvalidationPatch
{
    public static void Postfix()
    {
        LeviathanGrowth.OnSpecializationConfigurationChanged();
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class LeviathanGrowthShipDestroyPatch
{
    public static void Prefix(GameShip __instance)
    {
        LeviathanGrowth.ForgetShip(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanGrowthWorldDestroyPatch
{
    public static void Prefix()
    {
        LeviathanGrowth.ResetRuntime();
    }
}
