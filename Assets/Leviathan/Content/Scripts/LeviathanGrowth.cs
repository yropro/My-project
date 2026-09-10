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
/// - own canonical intended and live Leviathan anatomy,
/// - capture fully resolved vanilla Heat Capacity + Heat Loss Time,
/// - apply the 90% Leviathan thermal baseline before Growth modifiers,
/// - bridge straightforward chassis stats through native getter boundaries,
/// - provide linear per-segment mass/high-speed air resistance,
/// - provide flat Hull regen and movement-driven Cooling Fins,
/// - apply Gigantism through native ship-builder class-override semantics.
///
/// Deliberately deferred to behavior-specific passes:
/// multi-head construction and deeper branched-body topology, Specialized Predator
/// slot removal and purchase validation, Ancient Wyrm global crit/status hooks, Cold Blooded
/// dynamic movement, Star Dragon/Supernova, and Coronal Mass Ejection.
/// </summary>
public static class LeviathanGrowth
{
    private const int AnatomyProbeRetryFrames = 10;

    public static class Tuning
    {
        // Vanilla modifiers are already inside the captured values. These
        // multipliers are the Leviathan baseline applied AFTER vanilla resolves.
        public const float BaselineMaxHeatMultiplier = 0.90f;
        public const float BaselineHeatDissipationMultiplier = 0.90f;

        // Root anatomy. A "segment" is a Body or Tail. Heads are not literal
        // segments, although every Head after the primary Head contributes one
        // ScalingSegment for ordinary "per segment" stat bonuses.
        public const int RootHeads = 1;
        public const int RootBodySegments = 1;
        public const int RootTails = 1;
        public const int RootSegmentCount = RootBodySegments + RootTails;
        public const int RootTotalSectionCount =
            RootHeads + RootBodySegments + RootTails;
        public const int RootScalingSegmentCount =
            RootTotalSectionCount - 1;

        // Segment-originated negative status protection. The chassis starts at
        // 20%, then gains +1 percentage point for every 2 Evolution Points spent
        // specifically in the Growth tree. Point-cost scaling intentionally means
        // expensive Growth nodes advance this physiology faster than cheap ones.
        public const float BaselineSegmentDebuffDiscardChance = 0.20f;
        public const int SegmentDebuffDiscardPointsPerStep = 2;
        public const float SegmentDebuffDiscardPerStep = 0.01f;

        // Growth chassis scaling is authored per ScalingSegment. With one Head,
        // this is identical to the historical non-head count. Future additional
        // Heads count for scaling but remain ineligible for segment protections.
        // These values are rederived from the old full-Growth reference point:
        // 16 scaling segments (15 bodies + tail) = 3.0x player mass and
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
        // Adds physical section budget without prescribing a terminal role.
        // Current growth nodes describe these as body segments because Body is
        // the default residual role. Morphology nodes may reclassify that same
        // budget into Heads/Tails without changing how many sections exist.
        // Future terminal-granting nodes combine this with AdditionalHeads or
        // AdditionalTails when they also add a new physical section.
        public static readonly LeviathanSpecializationKnob AdditionalSections =
            LeviathanSpecializationKnob.Flat(
                "growth.additional_sections",
                "Sections"
            );

        public static readonly LeviathanSpecializationKnob AdditionalHeads =
            LeviathanSpecializationKnob.Flat(
                "growth.additional_heads",
                "Heads"
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

    public enum AnatomyRole : byte
    {
        Unknown = 0,
        Head = 1,
        Body = 2,
        Tail = 3
    }

    /// <summary>
    /// Progression-derived anatomy requested from the builder. This is build
    /// intent, not a claim that every requested section currently exists.
    /// </summary>
    public struct AnatomyIntent
    {
        public bool Active;
        public bool TreeActive;
        public bool TopologyFitsBudget;
        public int HeadCount;
        public int BodySegmentCount;
        public int TailCount;
        public int SegmentCount;
        public int ScalingSegmentCount;
        public int TotalSectionCount;
    }

    /// <summary>
    /// Immutable live anatomy snapshot. Counts describe instantiated sections.
    /// The primary Head is the player ship and is the only section excluded from
    /// ScalingSegmentCount. Body and Tail are literal segments; every additional
    /// Head contributes to scaling but never becomes segment-protection eligible.
    /// </summary>
    public sealed class AnatomySnapshot
    {
        public GameShip PrimaryHead { get; private set; }
        public int Revision { get; private set; }
        public bool PublishedByBuilder { get; private set; }
        public int HeadCount { get; private set; }
        public int BodySegmentCount { get; private set; }
        public int TailCount { get; private set; }
        public int SegmentCount { get; private set; }
        public int ScalingSegmentCount { get; private set; }
        public int TotalSectionCount { get; private set; }

        private AnatomySnapshot()
        {
        }

        internal static AnatomySnapshot Create(
            GameShip primaryHead,
            int revision,
            bool publishedByBuilder,
            int headCount,
            int bodySegmentCount,
            int tailCount)
        {
            AnatomySnapshot snapshot = new AnatomySnapshot();
            snapshot.PrimaryHead = primaryHead;
            snapshot.Revision = revision;
            snapshot.PublishedByBuilder = publishedByBuilder;
            snapshot.HeadCount = Mathf.Max(0, headCount);
            snapshot.BodySegmentCount = Mathf.Max(0, bodySegmentCount);
            snapshot.TailCount = Mathf.Max(0, tailCount);
            snapshot.SegmentCount =
                snapshot.BodySegmentCount + snapshot.TailCount;
            snapshot.TotalSectionCount =
                snapshot.HeadCount + snapshot.SegmentCount;
            snapshot.ScalingSegmentCount = Mathf.Max(
                0,
                snapshot.TotalSectionCount -
                    (snapshot.PrimaryHead == null ? 0 : 1));
            return snapshot;
        }
    }

    public sealed class ResolvedState
    {
        public bool Active;
        public bool TreeActive;

        public AnatomyIntent IntendedAnatomy;
        public AnatomySnapshot Anatomy;
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

    private sealed class AnatomyIntentCacheEntry
    {
        public GameShip Ship;
        public int ConfigurationRevision;
        public int EvolutionRank;
        public AnatomyIntent Intent;
    }

    private sealed class AnatomyCacheEntry
    {
        public GameShip PrimaryHead;
        public AnatomySnapshot Snapshot;
        public bool PublishedByBuilder;
        public int ConfigurationRevision;
        public Squadron Squadron;
        public int SquadronSlotCount;
        public int NextProbeFrame;
        public readonly List<GameShip> Heads = new List<GameShip>();
        public readonly List<GameShip> Bodies = new List<GameShip>();
        public readonly List<GameShip> Tails = new List<GameShip>();
        public readonly Dictionary<GameShip, AnatomyRole> Roles =
            new Dictionary<GameShip, AnatomyRole>();
    }

    private sealed class ResolvedCacheEntry
    {
        public GameShip Ship;
        public int ConfigurationRevision;
        public int BaselineRevision;
        public int EvolutionRank;
        public int AnatomyRevision;
        public ResolvedState State;
    }

    private static readonly Dictionary<GameShip, VanillaHeatBaseline> heatBaselines =
        new Dictionary<GameShip, VanillaHeatBaseline>();

    private static readonly Dictionary<Pilot, AnatomyIntentCacheEntry>
        anatomyIntentByPilot =
            new Dictionary<Pilot, AnatomyIntentCacheEntry>();

    private static readonly Dictionary<GameShip, AnatomyCacheEntry> anatomyByShip =
        new Dictionary<GameShip, AnatomyCacheEntry>();

    // Reverse membership makes section destruction/invalidation O(1). It also
    // gives protection/damage systems one canonical owner lookup if needed.
    private static readonly Dictionary<GameShip, GameShip> anatomyOwnerBySection =
        new Dictionary<GameShip, GameShip>();

    private static readonly Dictionary<Pilot, ResolvedCacheEntry> resolvedByPilot =
        new Dictionary<Pilot, ResolvedCacheEntry>();

    private static int nextBaselineRevision;
    private static int nextAnatomyRevision;

    private static readonly AnatomySnapshot emptyAnatomy = CreateEmptyAnatomy();

    // Shared immutable fail-closed result for null/unsynchronized remote queries.
    // Avoid allocating one ResolvedState per presentation frame while a remote
    // Pilot is waiting for its specialization spec block.
    private static readonly ResolvedState inactiveState = CreateInactiveState();

    [ThreadStatic]
    private static GameShip suppressedHeatOverrideShip;

    [ThreadStatic]
    private static bool handlingSpecializationInvalidation;

    // ---------------------------------------------------------------------
    // Anatomy contract
    // ---------------------------------------------------------------------

    public static AnatomyIntent GetAnatomyIntent(GameShip ship)
    {
        Pilot pilot;
        if (!TryGetSynchronizedPilot(ship, out pilot))
            return new AnatomyIntent();

        return GetAnatomyIntent(ship, pilot);
    }

    public static AnatomySnapshot GetAnatomy(GameShip ship)
    {
        Pilot pilot;
        if (!TryGetSynchronizedPilot(ship, out pilot))
            return emptyAnatomy;

        AnatomyIntent intent = GetAnatomyIntent(ship, pilot);
        if (!intent.Active)
            return emptyAnatomy;

        return GetOrBuildLiveAnatomy(ship, intent);
    }

    public static int GetHeadCount(GameShip ship)
    {
        return GetAnatomy(ship).HeadCount;
    }

    public static int GetBodySegmentCount(GameShip ship)
    {
        return GetAnatomy(ship).BodySegmentCount;
    }

    public static int GetTailCount(GameShip ship)
    {
        return GetAnatomy(ship).TailCount;
    }

    /// <summary>
    /// Literal anatomical segments: Body + Tail. Heads are never segments for
    /// hit/protection semantics.
    /// </summary>
    public static int GetSegmentCount(GameShip ship)
    {
        return GetAnatomy(ship).SegmentCount;
    }

    /// <summary>
    /// Count used by ordinary "gain X per segment" stat scaling: every live
    /// Leviathan section except the primary Head. Additional Heads therefore
    /// contribute to scaling without becoming literal/protected segments.
    /// </summary>
    public static int GetScalingSegmentCount(GameShip ship)
    {
        return GetAnatomy(ship).ScalingSegmentCount;
    }

    public static int GetTotalSectionCount(GameShip ship)
    {
        return GetAnatomy(ship).TotalSectionCount;
    }

    public static AnatomyRole GetSectionRole(
        GameShip owner,
        GameShip section)
    {
        if (owner == null || section == null)
            return AnatomyRole.Unknown;

        AnatomyCacheEntry entry;
        if (!TryGetLiveAnatomyEntry(owner, out entry))
            return AnatomyRole.Unknown;

        AnatomyRole role;
        return entry.Roles.TryGetValue(section, out role)
            ? role
            : AnatomyRole.Unknown;
    }

    public static bool IsSegment(GameShip owner, GameShip section)
    {
        AnatomyRole role = GetSectionRole(owner, section);
        return role == AnatomyRole.Body || role == AnatomyRole.Tail;
    }

    public static bool IsSegmentProtectionEligible(
        GameShip owner,
        GameShip section)
    {
        // Protection semantics are deliberately narrower than stat scaling.
        // Additional Heads count toward ScalingSegmentCount but remain Heads.
        return IsSegment(owner, section);
    }

    public static bool TryGetAnatomyOwner(
        GameShip section,
        out GameShip owner)
    {
        owner = null;
        if (section == null)
            return false;

        return anatomyOwnerBySection.TryGetValue(section, out owner) &&
            owner != null;
    }

    /// <summary>
    /// Resolve a live section directly to its owning Leviathan and anatomical
    /// role. Hit/protection systems should use this instead of reconstructing
    /// ownership or inferring role from squadron order.
    /// </summary>
    public static bool TryGetSectionContext(
        GameShip section,
        out GameShip owner,
        out AnatomyRole role)
    {
        owner = null;
        role = AnatomyRole.Unknown;

        if (!TryGetAnatomyOwner(section, out owner))
            return false;

        AnatomyCacheEntry entry;
        if (!anatomyByShip.TryGetValue(owner, out entry) ||
            entry == null ||
            entry.Snapshot == null)
        {
            owner = null;
            return false;
        }

        if (!entry.Roles.TryGetValue(section, out role) ||
            role == AnatomyRole.Unknown)
        {
            owner = null;
            role = AnatomyRole.Unknown;
            return false;
        }

        return true;
    }

    public static AnatomyRole GetSectionRole(GameShip section)
    {
        GameShip owner;
        AnatomyRole role;
        return TryGetSectionContext(section, out owner, out role)
            ? role
            : AnatomyRole.Unknown;
    }

    public static bool IsHead(GameShip section)
    {
        return GetSectionRole(section) == AnatomyRole.Head;
    }

    public static bool IsBodySegment(GameShip section)
    {
        return GetSectionRole(section) == AnatomyRole.Body;
    }

    public static bool IsTail(GameShip section)
    {
        return GetSectionRole(section) == AnatomyRole.Tail;
    }

    public static bool IsPrimaryHead(GameShip section)
    {
        GameShip owner;
        AnatomyRole role;
        return TryGetSectionContext(section, out owner, out role) &&
            role == AnatomyRole.Head &&
            ReferenceEquals(section, owner);
    }

    public static bool IsAdditionalHead(GameShip section)
    {
        GameShip owner;
        AnatomyRole role;
        return TryGetSectionContext(section, out owner, out role) &&
            role == AnatomyRole.Head &&
            !ReferenceEquals(section, owner);
    }

    public static bool IsSegment(GameShip section)
    {
        AnatomyRole role = GetSectionRole(section);
        return role == AnatomyRole.Body || role == AnatomyRole.Tail;
    }

    public static bool IsSegmentProtectionEligible(GameShip section)
    {
        return IsSegment(section);
    }

    public static void CollectHeadShips(
        GameShip owner,
        List<GameShip> output)
    {
        CollectRoleShips(owner, AnatomyRole.Head, output);
    }

    public static void CollectBodyShips(
        GameShip owner,
        List<GameShip> output)
    {
        CollectRoleShips(owner, AnatomyRole.Body, output);
    }

    public static void CollectTailShips(
        GameShip owner,
        List<GameShip> output)
    {
        CollectRoleShips(owner, AnatomyRole.Tail, output);
    }

    public static void CollectSegmentShips(
        GameShip owner,
        List<GameShip> output)
    {
        if (output == null)
            return;

        output.Clear();

        AnatomyCacheEntry entry;
        if (!TryGetLiveAnatomyEntry(owner, out entry))
            return;

        AppendShips(entry.Bodies, output);
        AppendShips(entry.Tails, output);
    }

    public static void CollectScalingShips(
        GameShip owner,
        List<GameShip> output)
    {
        if (output == null)
            return;

        output.Clear();

        AnatomyCacheEntry entry;
        if (!TryGetLiveAnatomyEntry(owner, out entry))
            return;

        GameShip primaryHead = entry.Snapshot.PrimaryHead;
        for (int i = 0; i < entry.Heads.Count; i++)
        {
            GameShip head = entry.Heads[i];
            if (head != null && !ReferenceEquals(head, primaryHead))
                output.Add(head);
        }

        AppendShips(entry.Bodies, output);
        AppendShips(entry.Tails, output);
    }

    /// <summary>
    /// Builder-owned atomic publication of live roles. The player ship is the
    /// primary Head and is added automatically. Call this after a successful
    /// build/rebuild, and call InvalidateLiveAnatomy before tearing topology down.
    /// </summary>
    public static bool PublishLiveAnatomy(
        GameShip primaryHead,
        IList<GameShip> additionalHeads,
        IList<GameShip> bodySegments,
        IList<GameShip> tails)
    {
        if (primaryHead == null)
            return false;

        AnatomyCacheEntry candidate = new AnatomyCacheEntry();
        candidate.PrimaryHead = primaryHead;

        if (!TryAddSection(candidate, primaryHead, AnatomyRole.Head) ||
            !TryAddSections(candidate, additionalHeads, AnatomyRole.Head) ||
            !TryAddSections(candidate, bodySegments, AnatomyRole.Body) ||
            !TryAddSections(candidate, tails, AnatomyRole.Tail))
        {
            Debug.LogError(
                "[Leviathan] Rejected live anatomy publication because a " +
                "section was null, duplicated, or assigned multiple roles."
            );
            return false;
        }

        CommitLiveAnatomy(primaryHead, candidate, true);
        return true;
    }

    public static void InvalidateLiveAnatomy(GameShip owner)
    {
        if (owner == null)
            return;

        RemoveAnatomyRecord(owner);
        InvalidateResolvedState(owner);
    }

    private static bool TryGetLiveAnatomyEntry(
        GameShip owner,
        out AnatomyCacheEntry entry)
    {
        entry = null;
        if (owner == null)
            return false;

        // Ensure the lazy native-replica path has had a chance to materialize.
        GetAnatomy(owner);

        return anatomyByShip.TryGetValue(owner, out entry) &&
            entry != null &&
            entry.Snapshot != null;
    }

    private static void CollectRoleShips(
        GameShip owner,
        AnatomyRole role,
        List<GameShip> output)
    {
        if (output == null)
            return;

        output.Clear();

        AnatomyCacheEntry entry;
        if (!TryGetLiveAnatomyEntry(owner, out entry))
            return;

        if (role == AnatomyRole.Head)
            AppendShips(entry.Heads, output);
        else if (role == AnatomyRole.Body)
            AppendShips(entry.Bodies, output);
        else if (role == AnatomyRole.Tail)
            AppendShips(entry.Tails, output);
    }

    private static void AppendShips(
        List<GameShip> source,
        List<GameShip> output)
    {
        if (source == null || output == null)
            return;

        for (int i = 0; i < source.Count; i++)
        {
            GameShip ship = source[i];
            if (ship != null)
                output.Add(ship);
        }
    }

    private static bool TryAddSections(
        AnatomyCacheEntry entry,
        IList<GameShip> sections,
        AnatomyRole role)
    {
        if (sections == null)
            return true;

        for (int i = 0; i < sections.Count; i++)
        {
            if (!TryAddSection(entry, sections[i], role))
                return false;
        }

        return true;
    }

    private static bool TryAddSection(
        AnatomyCacheEntry entry,
        GameShip section,
        AnatomyRole role)
    {
        if (entry == null || section == null || role == AnatomyRole.Unknown)
            return false;

        if (entry.Roles.ContainsKey(section))
            return false;

        entry.Roles.Add(section, role);

        if (role == AnatomyRole.Head)
            entry.Heads.Add(section);
        else if (role == AnatomyRole.Body)
            entry.Bodies.Add(section);
        else if (role == AnatomyRole.Tail)
            entry.Tails.Add(section);

        return true;
    }

    private static AnatomyIntent GetAnatomyIntent(
        GameShip ship,
        Pilot pilot)
    {
        int configurationRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;
        int evolutionRank = pilot.GetUpgradeLevel(
            LeviathanSpecializationCurrency.UpgradeKey
        );

        AnatomyIntentCacheEntry cached;
        if (anatomyIntentByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            cached.Ship == ship &&
            cached.ConfigurationRevision == configurationRevision &&
            cached.EvolutionRank == evolutionRank)
        {
            return cached.Intent;
        }

        AnatomyIntent intent = BuildAnatomyIntent(pilot, evolutionRank);

        if (cached == null)
        {
            cached = new AnatomyIntentCacheEntry();
            anatomyIntentByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.ConfigurationRevision = configurationRevision;
        cached.EvolutionRank = evolutionRank;
        cached.Intent = intent;
        return intent;
    }

    private static AnatomyIntent BuildAnatomyIntent(
        Pilot pilot,
        int evolutionRank)
    {
        AnatomyIntent intent = new AnatomyIntent();
        if (pilot == null || evolutionRank < 1)
            return intent;

        intent.Active = true;
        intent.TreeActive = LeviathanSpecializationRuntime.IsTreeUnlocked(
            pilot,
            LeviathanGrowthTree.TreeId
        );

        int additionalSections = intent.TreeActive
            ? Mathf.Max(0, Mathf.RoundToInt(
                LeviathanSpecializationRuntime.GetKnobFlat(
                    pilot,
                    Knobs.AdditionalSections)))
            : 0;

        int additionalHeads = intent.TreeActive
            ? Mathf.Max(0, Mathf.RoundToInt(
                LeviathanSpecializationRuntime.GetKnobFlat(
                    pilot,
                    Knobs.AdditionalHeads)))
            : 0;

        int additionalTails = intent.TreeActive
            ? Mathf.Max(0, Mathf.RoundToInt(
                LeviathanSpecializationRuntime.GetKnobFlat(
                    pilot,
                    Knobs.AdditionalTails)))
            : 0;

        int totalBudget =
            Tuning.RootTotalSectionCount +
            additionalSections;

        int requestedHeads = Tuning.RootHeads + additionalHeads;
        int requestedTails = Tuning.RootTails + additionalTails;

        intent.TopologyFitsBudget =
            requestedHeads >= 1 &&
            requestedTails >= 0 &&
            requestedHeads + requestedTails <= totalBudget;

        if (!intent.TopologyFitsBudget)
        {
            Debug.LogError(
                "[Leviathan] Growth anatomy intent exceeds its physical " +
                "section budget. Requested heads=" + requestedHeads +
                ", tails=" + requestedTails +
                ", total sections=" + totalBudget + "."
            );
        }

        // Stay fail-safe even if a future tree definition is authored
        // incorrectly. The validation flag and error expose the content bug;
        // clamping prevents negative body counts or invalid runtime geometry.
        intent.HeadCount = Mathf.Clamp(
            requestedHeads,
            1,
            Mathf.Max(1, totalBudget));

        intent.TailCount = Mathf.Clamp(
            requestedTails,
            0,
            Mathf.Max(0, totalBudget - intent.HeadCount));

        intent.BodySegmentCount = Mathf.Max(
            0,
            totalBudget - intent.HeadCount - intent.TailCount);

        intent.SegmentCount =
            intent.BodySegmentCount + intent.TailCount;
        intent.TotalSectionCount =
            intent.HeadCount + intent.SegmentCount;
        intent.ScalingSegmentCount =
            Mathf.Max(0, intent.TotalSectionCount - 1);

        return intent;
    }

    private static AnatomySnapshot GetOrBuildLiveAnatomy(
        GameShip ship,
        AnatomyIntent intent)
    {
        if (ship == null || !intent.Active)
            return emptyAnatomy;

        AnatomyCacheEntry cached;
        if (!anatomyByShip.TryGetValue(ship, out cached) || cached == null)
        {
            cached = new AnatomyCacheEntry();
            anatomyByShip[ship] = cached;
        }

        // Builder publication is the authoritative live topology. It remains
        // valid until the builder explicitly invalidates/replaces it; a tree
        // change describes new intent, not an already-completed rebuild.
        if (cached.PublishedByBuilder && cached.Snapshot != null)
            return cached.Snapshot;

        Squadron squadron = ship.squadron;
        List<Squadron.SquadronShip> slots =
            squadron == null ? null : squadron.ships;
        int slotCount = slots == null ? 0 : slots.Count;
        int configurationRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;

        if (cached.Snapshot != null &&
            cached.ConfigurationRevision == configurationRevision &&
            ReferenceEquals(cached.Squadron, squadron) &&
            cached.SquadronSlotCount == slotCount)
        {
            return cached.Snapshot;
        }

        // A configuration change invalidates a previous probe throttle. The
        // cached live snapshot remains usable until a replacement can be proven.
        if (cached.ConfigurationRevision != configurationRevision)
            cached.NextProbeFrame = 0;

        if (Time.frameCount < cached.NextProbeFrame)
            return cached.Snapshot ?? emptyAnatomy;

        // The current builder publishes a deterministic single-Head slot role
        // layout: [primary Head, Body..., Tail...]. Remote replicas receive the
        // same physical slot order plus synchronized anatomy intent, so Growth
        // can classify live roles without replicating redundant counts or relying
        // on the owner's private AttachedAIShip parent pointers. Multi-Head
        // topology remains explicit-only until a verified forward-branch builder
        // and replica representation exist.
        if (intent.HeadCount != 1 ||
            slots == null ||
            slots.Count != intent.TotalSectionCount)
        {
            cached.ConfigurationRevision = configurationRevision;
            cached.Squadron = squadron;
            cached.SquadronSlotCount = slotCount;
            cached.NextProbeFrame = Time.frameCount + AnatomyProbeRetryFrames;
            return cached.Snapshot ?? emptyAnatomy;
        }

        // Before the first successful snapshot, reuse the cache entry itself as
        // reconstruction scratch so a temporarily incomplete native replica does
        // not allocate a new AnatomyCacheEntry every retry. Once a valid snapshot
        // exists, rebuild into a separate candidate so failure cannot destroy the
        // last proven live anatomy.
        AnatomyCacheEntry candidate = cached.Snapshot == null
            ? cached
            : new AnatomyCacheEntry();

        if (!TryBuildNativeReplicaAnatomy(ship, intent, candidate))
        {
            // TryBuildNativeReplicaAnatomy clears candidate state before probing.
            // When candidate == cached there is no proven snapshot to preserve;
            // retain the scratch buffers and only update the retry metadata.
            cached.ConfigurationRevision = configurationRevision;
            cached.Squadron = squadron;
            cached.SquadronSlotCount = slotCount;
            cached.NextProbeFrame = Time.frameCount + AnatomyProbeRetryFrames;
            return cached.Snapshot ?? emptyAnatomy;
        }

        CommitLiveAnatomy(ship, candidate, false);
        return candidate.Snapshot;
    }

    private static bool TryBuildNativeReplicaAnatomy(
        GameShip primaryHead,
        AnatomyIntent intent,
        AnatomyCacheEntry entry)
    {
        if (entry == null)
            return false;

        ClearAnatomyEntry(entry);

        if (primaryHead == null ||
            intent.HeadCount != 1 ||
            primaryHead.squadron == null ||
            primaryHead.squadron.ships == null)
        {
            return false;
        }

        List<Squadron.SquadronShip> slots =
            primaryHead.squadron.ships;

        if (slots.Count != intent.TotalSectionCount ||
            slots.Count < 2 ||
            slots[0] == null ||
            !ReferenceEquals(slots[0].ship, primaryHead))
        {
            return false;
        }

        entry.PrimaryHead = primaryHead;

        if (!TryAddSection(entry, primaryHead, AnatomyRole.Head))
            return false;

        int bodyEndExclusive =
            1 + intent.BodySegmentCount;

        for (int i = 1; i < slots.Count; i++)
        {
            Squadron.SquadronShip slot = slots[i];
            GameShip section = slot == null ? null : slot.ship;

            if (section == null || ReferenceEquals(section, primaryHead))
                return false;

            AnatomyRole role = i < bodyEndExclusive
                ? AnatomyRole.Body
                : AnatomyRole.Tail;

            if (!TryAddSection(entry, section, role))
                return false;
        }

        return entry.Bodies.Count == intent.BodySegmentCount &&
            entry.Tails.Count == intent.TailCount;
    }

    private static void ClearAnatomyEntry(AnatomyCacheEntry entry)
    {
        if (entry == null)
            return;

        entry.PrimaryHead = null;
        entry.Snapshot = null;
        entry.PublishedByBuilder = false;
        entry.Heads.Clear();
        entry.Bodies.Clear();
        entry.Tails.Clear();
        entry.Roles.Clear();
    }

    private static void CommitLiveAnatomy(
        GameShip owner,
        AnatomyCacheEntry entry,
        bool publishedByBuilder)
    {
        if (owner == null || entry == null || entry.PrimaryHead == null)
            return;

        RemoveAnatomyRecord(owner);

        unchecked
        {
            nextAnatomyRevision++;
            if (nextAnatomyRevision == 0)
                nextAnatomyRevision = 1;
        }

        AnatomySnapshot snapshot = AnatomySnapshot.Create(
            entry.PrimaryHead,
            nextAnatomyRevision,
            publishedByBuilder,
            entry.Heads.Count,
            entry.Bodies.Count,
            entry.Tails.Count
        );
        entry.Snapshot = snapshot;

        entry.PublishedByBuilder = publishedByBuilder;
        entry.ConfigurationRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;
        entry.Squadron = owner.squadron;
        entry.SquadronSlotCount =
            entry.Squadron == null || entry.Squadron.ships == null
                ? 0
                : entry.Squadron.ships.Count;
        entry.NextProbeFrame = 0;
        anatomyByShip[owner] = entry;

        foreach (KeyValuePair<GameShip, AnatomyRole> pair in entry.Roles)
        {
            if (pair.Key != null)
                anatomyOwnerBySection[pair.Key] = owner;
        }

        InvalidateResolvedState(owner);
    }

    private static void RemoveReverseMappings(
        GameShip owner,
        AnatomyCacheEntry entry)
    {
        if (owner == null || entry == null)
            return;

        foreach (KeyValuePair<GameShip, AnatomyRole> pair in entry.Roles)
        {
            GameShip section = pair.Key;
            if (ReferenceEquals(section, null))
                continue;

            GameShip mappedOwner;
            if (anatomyOwnerBySection.TryGetValue(section, out mappedOwner) &&
                ReferenceEquals(mappedOwner, owner))
            {
                anatomyOwnerBySection.Remove(section);
            }
        }
    }

    private static void RemoveAnatomyRecord(GameShip owner)
    {
        if (owner == null)
            return;

        AnatomyCacheEntry entry;
        if (anatomyByShip.TryGetValue(owner, out entry) && entry != null)
            RemoveReverseMappings(owner, entry);

        anatomyByShip.Remove(owner);
    }

    private static void InvalidateResolvedState(GameShip ship)
    {
        Pilot pilot = ship == null ? null : GameShip.GetPlayerSourcePilot(ship);
        if (pilot != null)
            resolvedByPilot.Remove(pilot);
    }

    private static bool TryGetSynchronizedPilot(
        GameShip ship,
        out Pilot pilot)
    {
        pilot = null;
        if (ship == null)
            return false;

        pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot == null)
            return false;

        return IsLocalOwner(ship) ||
            LeviathanNetwork.HasSynchronizedSpecialization(ship);
    }

    // ---------------------------------------------------------------------
    // Public resolved API
    // ---------------------------------------------------------------------

    public static ResolvedState GetResolvedState(GameShip ship)
    {
        Pilot pilot;
        if (!TryGetSynchronizedPilot(ship, out pilot))
            return inactiveState;

        AnatomyIntent intent = GetAnatomyIntent(ship, pilot);
        if (!intent.Active)
            return inactiveState;

        AnatomySnapshot anatomy = GetOrBuildLiveAnatomy(ship, intent);

        VanillaHeatBaseline baseline;
        heatBaselines.TryGetValue(ship, out baseline);

        int baselineRevision = baseline == null ? 0 : baseline.Revision;
        int configurationRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;
        int evolutionRank = pilot.GetUpgradeLevel(
            LeviathanSpecializationCurrency.UpgradeKey
        );
        int anatomyRevision = anatomy == null ? 0 : anatomy.Revision;

        ResolvedCacheEntry cached;
        if (resolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            cached.Ship == ship &&
            cached.ConfigurationRevision == configurationRevision &&
            cached.BaselineRevision == baselineRevision &&
            cached.EvolutionRank == evolutionRank &&
            cached.AnatomyRevision == anatomyRevision &&
            cached.State != null)
        {
            return cached.State;
        }

        ResolvedState state = BuildResolvedState(
            pilot,
            baseline,
            intent,
            anatomy);

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            resolvedByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.ConfigurationRevision = configurationRevision;
        cached.BaselineRevision = baselineRevision;
        cached.EvolutionRank = evolutionRank;
        cached.AnatomyRevision = anatomyRevision;
        cached.State = state;
        return state;
    }

    public static bool IsGrowthActive(GameShip ship)
    {
        return GetAnatomyIntent(ship).Active;
    }

    public static bool IsGrowthTreeActive(GameShip ship)
    {
        return GetAnatomyIntent(ship).TreeActive;
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
        VanillaHeatBaseline baseline,
        AnatomyIntent intent,
        AnatomySnapshot anatomy)
    {
        ResolvedState state = CreateInactiveState();

        if (pilot == null || !intent.Active)
            return state;

        state.Active = true;
        state.TreeActive = intent.TreeActive;
        state.IntendedAnatomy = intent;
        state.Anatomy = anatomy ?? emptyAnatomy;

        int scalingSegmentCount = state.Anatomy.ScalingSegmentCount;

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
                dissipationPerSegmentPercent * scalingSegmentCount
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
                scalingSegmentCount
        );

        state.TopSpeedMultiplier = Mathf.Max(
            0f,
            1f +
            GrowthPercentContribution(pilot, state.TreeActive, Knobs.TopSpeedPercent) +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.TopSpeedPerSegmentPercent) *
                scalingSegmentCount
        );

        state.BoostMultiplier = Mathf.Max(
            0f,
            1f +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.BoostPerSegmentPercent) *
                scalingSegmentCount
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
                scalingSegmentCount;

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
                scalingSegmentCount
        );

        float massPercent =
            Tuning.InherentMassPercentPerSegment *
                scalingSegmentCount +
            GrowthPercentContribution(pilot, state.TreeActive, Knobs.MassPercent) +
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.MassPerSegmentPercent) *
                scalingSegmentCount;

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
            scalingSegmentCount *
            airResistanceMultiplier;

        state.CritDamageBonus =
            GrowthPercentContribution(
                pilot,
                state.TreeActive,
                Knobs.CritDamagePerSegmentPercent) *
            scalingSegmentCount;

        state.StatusChanceBonus =
            GrowthFlat(
                pilot,
                state.TreeActive,
                Knobs.StatusChancePerSegmentPoints) *
            scalingSegmentCount;

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

        state.Bifurcation = state.TreeActive &&
            LeviathanSpecializationRuntime.HasFlag(
                pilot,
                Flags.Bifurcation);

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

    private static AnatomySnapshot CreateEmptyAnatomy()
    {
        return AnatomySnapshot.Create(
            null,
            0,
            false,
            0,
            0,
            0
        );
    }

    private static ResolvedState CreateInactiveState()
    {
        ResolvedState state = new ResolvedState();
        state.Anatomy = emptyAnatomy;
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

            if (LeviathanMod.Controller != null)
                LeviathanMod.Controller.RequestGrowthRefreshIfNeeded(ship);
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

        GameShip owner;
        if (anatomyOwnerBySection.TryGetValue(ship, out owner) && owner != null)
        {
            RemoveAnatomyRecord(owner);
            InvalidateResolvedState(owner);
        }
        else
        {
            RemoveAnatomyRecord(ship);
        }

        heatBaselines.Remove(ship);

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot != null)
        {
            anatomyIntentByPilot.Remove(pilot);
            resolvedByPilot.Remove(pilot);
        }

        if (suppressedHeatOverrideShip == ship)
            suppressedHeatOverrideShip = null;
    }

    public static void ResetRuntime()
    {
        heatBaselines.Clear();
        anatomyIntentByPilot.Clear();
        anatomyByShip.Clear();
        anatomyOwnerBySection.Clear();
        resolvedByPilot.Clear();
        suppressedHeatOverrideShip = null;
        handlingSpecializationInvalidation = false;
        nextBaselineRevision = 0;
        nextAnatomyRevision = 0;
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
