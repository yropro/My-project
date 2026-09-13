using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

/// <summary>
/// Owner-side Predator lunge, Prey, Hunt Streak and Thrill runtime.
/// Later archetypes retain neutral authoring surfaces; declaring a knob does
/// not activate an unimplemented mechanic.
/// </summary>
public static class LeviathanPredatorRuntime
{
    // =========================================================================
    // AGREED BASELINE
    // =========================================================================

    public static class Tuning
    {
        public const float BaselineDamageMultiplier = 2.00f;
        public const float BaselineLungeDistanceMultiplier = 0.66f;
        public const float BaselineLungeSpeedMultiplier = 0.50f;
        public const float BaselineCooldownMultiplier = 3.00f;
        public const float BaselineCritChanceBonus = 0.00f;
        public const float BaselineStatusChanceBonus = 0.00f;

        public const float BaselinePreyDurationSeconds = 5.00f;
        public const float BaselineHuntStreakDurationSeconds = 4.00f;
        public const int BaselineHuntStacksPerPreyKill = 1;
        public const int BaselineHuntMaxStacks = 0; // 0 = uncapped.

        // Thrill is dormant until enabled. 50% HP is agreed; its radius is left
        // neutral so the eventual node explicitly authors the playable range.
        public const float BaselineThrillEnemyHealthThreshold = 0.50f;
        public const float BaselineThrillRadiusMeters = 0.00f;
        public const int BaselineThrillRequiredEnemies = 1;
        public const int BaselineThrillMaxStacks = 1;
        public const float BaselineThrillAdditionalEnemyEffectScale = 0.00f;

        public const float BaselineVenomTickSeconds = 0.25f;
        public const int BaselineVenomMaxStacks = 1;
        public const int BaselineMaxHeldTargets = 1;
        public const int BaselineMaxSwallowedTargets = 1;
        public const int BaselineAutoChainMaxFollowups = 1;

        public const float WorldUnitsPerMeter = 1f / 20f;
        public const float ThrillQueryIntervalSeconds = 0.10f;
    }

    // Small constructors keep a large knob surface readable and consistent.
    private static CoreSpecializationKnob P(string id, string name)
    {
        return CoreSpecializationKnob.Percent("predator." + id, name);
    }

    private static CoreSpecializationKnob PP(string id, string name)
    {
        return CoreSpecializationKnob.PercentagePoints("predator." + id, name);
    }

    private static CoreSpecializationKnob F(
        string id,
        string name,
        string unit = "")
    {
        return CoreSpecializationKnob.Flat("predator." + id, name, unit);
    }

    private static CoreSpecializationFlag Flag(string id, string name)
    {
        return CoreSpecializationFlag.Create("predator." + id, name);
    }

    // =========================================================================
    // SHARED CONDITIONAL STAT CHANNELS
    // =========================================================================

    /// <summary>
    /// Named conditions such as Thrill, Prey, or Redshift contribute to the same
    /// final Predator stats rather than owning parallel final-stat systems.
    /// </summary>
    public sealed class ModifierKnobSet
    {
        public readonly CoreSpecializationKnob Damage;
        public readonly CoreSpecializationKnob LungeDistance;
        public readonly CoreSpecializationKnob LungeSpeed;
        public readonly CoreSpecializationKnob Cooldown;
        public readonly CoreSpecializationKnob CooldownRecovery;
        public readonly CoreSpecializationKnob CritChance;
        public readonly CoreSpecializationKnob StatusChance;
        public readonly CoreSpecializationKnob MoveSpeed;
        public readonly CoreSpecializationKnob Acceleration;
        public readonly CoreSpecializationKnob TurnSpeed;

        internal ModifierKnobSet(string id, string name)
        {
            string prefix = "conditional." + id + ".";
            Damage = P(prefix + "damage", name + " Damage");
            LungeDistance = P(prefix + "lunge_distance", name + " Lunge Distance");
            LungeSpeed = P(prefix + "lunge_speed", name + " Lunge Speed");
            Cooldown = P(prefix + "cooldown", name + " Cooldown");
            CooldownRecovery = P(prefix + "cooldown_recovery", name + " Cooldown Recovery");
            CritChance = PP(prefix + "crit_chance", name + " Critical Chance");
            StatusChance = PP(prefix + "status_chance", name + " Status Chance");
            MoveSpeed = P(prefix + "move_speed", name + " Move Speed");
            Acceleration = P(prefix + "acceleration", name + " Acceleration");
            TurnSpeed = P(prefix + "turn_speed", name + " Turn Speed");
        }
    }

    public struct ModifierValues
    {
        public float DamagePercent;
        public float LungeDistancePercent;
        public float LungeSpeedPercent;
        public float CooldownPercent;
        public float CooldownRecoveryPercent;
        public float CritChanceBonus;
        public float StatusChanceBonus;
        public float MoveSpeedPercent;
        public float AccelerationPercent;
        public float TurnSpeedPercent;

        public void AddScaled(ModifierValues other, float scale)
        {
            DamagePercent += other.DamagePercent * scale;
            LungeDistancePercent += other.LungeDistancePercent * scale;
            LungeSpeedPercent += other.LungeSpeedPercent * scale;
            CooldownPercent += other.CooldownPercent * scale;
            CooldownRecoveryPercent += other.CooldownRecoveryPercent * scale;
            CritChanceBonus += other.CritChanceBonus * scale;
            StatusChanceBonus += other.StatusChanceBonus * scale;
            MoveSpeedPercent += other.MoveSpeedPercent * scale;
            AccelerationPercent += other.AccelerationPercent * scale;
            TurnSpeedPercent += other.TurnSpeedPercent * scale;
        }
    }

    // =========================================================================
    // KNOBS / FLAGS
    // =========================================================================

    public static class Knobs
    {
        // Canonical Predator stats.
        public static readonly CoreSpecializationKnob Damage = P("damage", "Damage");
        public static readonly CoreSpecializationKnob LungeDistance = P("lunge_distance", "Lunge Distance");
        public static readonly CoreSpecializationKnob LungeSpeed = P("lunge_speed", "Lunge Speed");
        public static readonly CoreSpecializationKnob DamagePerScalingSegment = P("per_scaling_segment.damage", "Damage Per Scaling Segment");
        public static readonly CoreSpecializationKnob LungeSpeedPerScalingSegment = P("per_scaling_segment.lunge_speed", "Lunge Speed Per Scaling Segment");
        public static readonly CoreSpecializationKnob LungeDistancePerScalingSegment = P("per_scaling_segment.lunge_distance", "Lunge Distance Per Scaling Segment");
        public static readonly CoreSpecializationKnob DamagePerHead = P("per_head.damage", "Damage Per Head");
        public static readonly CoreSpecializationKnob LungeSpeedPerHead = P("per_head.lunge_speed", "Lunge Speed Per Head");
        public static readonly CoreSpecializationKnob LungeDistancePerHead = P("per_head.lunge_distance", "Lunge Distance Per Head");
        public static readonly CoreSpecializationKnob Cooldown = P("cooldown", "Cooldown");
        public static readonly CoreSpecializationKnob CooldownRecovery = P("cooldown_recovery", "Cooldown Recovery");
        public static readonly CoreSpecializationKnob CritChance = PP("crit_chance", "Critical Chance");
        public static readonly CoreSpecializationKnob StatusChance = PP("status_chance", "Status Chance");
        public static readonly CoreSpecializationKnob MoveSpeed = P("move_speed", "Move Speed");
        public static readonly CoreSpecializationKnob Acceleration = P("acceleration", "Acceleration");
        public static readonly CoreSpecializationKnob TurnSpeed = P("turn_speed", "Turn Speed");

        // Prey / Hunt Streak.
        public static readonly CoreSpecializationKnob PreyDuration = F("prey.duration", "Prey Duration", "s");
        public static readonly CoreSpecializationKnob HuntStreakDuration = F("hunt.duration", "Hunt Streak Duration", "s");
        public static readonly CoreSpecializationKnob HuntStacksPerPreyKill = F("hunt.stacks_per_prey_kill", "Hunt Stacks Per Prey Kill", " stacks");
        public static readonly CoreSpecializationKnob HuntMaxStacks = F("hunt.max_stacks", "Hunt Streak Maximum Stacks", " stacks");
        public static readonly CoreSpecializationKnob HealMaxHpOnPreyKill = PP("prey_kill.heal_max_hp", "Maximum HP Healed On Prey Kill");
        public static readonly CoreSpecializationKnob HealMaxHpOnLungeKill = PP("lunge_kill.heal_max_hp", "Maximum HP Healed On Lunge Kill");
        public static readonly CoreSpecializationKnob CooldownReductionOnPreyKill = PP("prey_kill.cooldown_reduction", "Remaining Cooldown Reduced On Prey Kill");
        public static readonly CoreSpecializationKnob CooldownReductionOnLungeKill = PP("lunge_kill.cooldown_reduction", "Remaining Cooldown Reduced On Lunge Kill");
        public static readonly CoreSpecializationKnob FullResetChanceOnPreyKill = PP("prey_kill.reset_chance", "Full Cooldown Reset Chance On Prey Kill");
        public static readonly CoreSpecializationKnob FullResetChanceOnLungeKill = PP("lunge_kill.reset_chance", "Full Cooldown Reset Chance On Lunge Kill");

        // Thrill of the Hunt. The modifier bundle below decides what Thrill buffs.
        public static readonly CoreSpecializationKnob ThrillHealthThreshold = PP("thrill.health_threshold", "Thrill Enemy Health Threshold");
        public static readonly CoreSpecializationKnob ThrillRadius = F("thrill.radius", "Thrill Detection Radius", "m");
        public static readonly CoreSpecializationKnob ThrillRequiredEnemies = F("thrill.required_enemies", "Thrill Required Enemies", " enemies");
        public static readonly CoreSpecializationKnob ThrillMaxStacks = F("thrill.max_stacks", "Thrill Maximum Enemy Stacks", " stacks");
        public static readonly CoreSpecializationKnob ThrillAdditionalEnemyEffectScale = F("thrill.additional_enemy_scale", "Thrill Additional Enemy Effect Scale", "x");

        // Generic execute: either threshold can independently qualify a target.
        public static readonly CoreSpecializationKnob ExecuteTargetOwnHealthThreshold = PP("execute.target_health", "Execute Target Health Threshold");
        public static readonly CoreSpecializationKnob ExecuteVsLeviathanMaxHealthThreshold = PP("execute.vs_leviathan_hp", "Execute Threshold Vs Leviathan Max HP");

        // Mass Extinction: deliberately linear. +0.5 means +0.5% damage per mass unit.
        public static readonly CoreSpecializationKnob MassDamagePerMassUnit = PP("mass.damage_per_unit", "Damage Per Mass Unit");
        public static readonly CoreSpecializationKnob MassDamageCap = PP("mass.damage_cap", "Mass Damage Bonus Cap");

        // Reusable forced movement / carrying.
        public static readonly CoreSpecializationKnob MaxHeldTargets = F("hold.max_targets", "Maximum Held Targets", " targets");
        public static readonly CoreSpecializationKnob MaximumHeldTargetMassRatio = F("hold.max_mass_ratio", "Maximum Held Target Mass Ratio", "x");
        public static readonly CoreSpecializationKnob HoldOffset = F("hold.offset", "Held Prey Offset", "m");
        public static readonly CoreSpecializationKnob HoldBreakDistance = F("hold.break_distance", "Hold Break Distance", "m");
        public static readonly CoreSpecializationKnob PullSpeed = F("pull.speed", "Pull Speed", "m/s");
        public static readonly CoreSpecializationKnob PullAcceleration = F("pull.acceleration", "Pull Acceleration", "m/s²");
        public static readonly CoreSpecializationKnob ReturnSpeed = F("return.speed", "Return Speed", "m/s");
        public static readonly CoreSpecializationKnob ReturnAcceleration = F("return.acceleration", "Return Acceleration", "m/s²");
        public static readonly CoreSpecializationKnob ReturnDelay = F("return.delay", "Return Delay", "s");

        // Swallow / digestion / spit.
        public static readonly CoreSpecializationKnob SwallowTargetOwnHealthThreshold = PP("swallow.target_health", "Swallow Target Health Threshold");
        public static readonly CoreSpecializationKnob SwallowVsLeviathanMaxHealthThreshold = PP("swallow.vs_leviathan_hp", "Swallow Threshold Vs Leviathan Max HP");
        public static readonly CoreSpecializationKnob SwallowSizeAdvantageThresholdGain = PP("swallow.size_advantage_gain", "Swallow Threshold Gain Per Size Advantage");
        public static readonly CoreSpecializationKnob SwallowMaximumHealthThreshold = PP("swallow.max_health_threshold", "Swallow Maximum Health Threshold");
        public static readonly CoreSpecializationKnob MaxSwallowedTargets = F("swallow.max_targets", "Maximum Swallowed Targets", " targets");
        public static readonly CoreSpecializationKnob SwallowDuration = F("swallow.duration", "Swallow Duration", "s");
        public static readonly CoreSpecializationKnob DigestionTargetMaxHpPerSecond = PP("swallow.target_hp_per_second", "Digestion Target Max HP Per Second");
        public static readonly CoreSpecializationKnob DigestionPredatorHitFractionPerSecond = F("swallow.predator_hits_per_second", "Digestion Predator Hits Per Second", "x");
        public static readonly CoreSpecializationKnob SpitSpeed = F("spit.speed", "Spit Speed", "m/s");
        public static readonly CoreSpecializationKnob SpitCollisionDamage = P("spit.collision_damage", "Spit Collision Damage");
        public static readonly CoreSpecializationKnob SpitThrownPreyDamage = P("spit.thrown_prey_damage", "Spit Damage To Thrown Prey");

        // Automatic chained lunges.
        public static readonly CoreSpecializationKnob AutoChainMaxFollowups = F("chain.max_followups", "Maximum Automatic Follow-up Lunges", " lunges");
        public static readonly CoreSpecializationKnob AutoChainAcquireRadius = F("chain.acquire_radius", "Automatic Lunge Acquire Radius", "m");
        public static readonly CoreSpecializationKnob AutoChainDelay = F("chain.delay", "Automatic Lunge Delay", "s");

        // Heavy DoT / venom transformation.
        public static readonly CoreSpecializationKnob VenomImmediateDamage = P("venom.immediate_damage", "Venom Immediate Damage");
        public static readonly CoreSpecializationKnob VenomDotDamageFraction = PP("venom.dot_damage_fraction", "Venom DoT Damage Fraction");
        public static readonly CoreSpecializationKnob VenomDuration = F("venom.duration", "Venom Duration", "s");
        public static readonly CoreSpecializationKnob VenomTickSeconds = F("venom.tick_seconds", "Venom Tick Interval", "s");
        public static readonly CoreSpecializationKnob VenomMaxStacks = F("venom.max_stacks", "Venom Maximum Stacks", " stacks");
        public static readonly CoreSpecializationKnob VenomCritChance = PP("venom.crit_chance", "Venom Critical Chance");
        public static readonly CoreSpecializationKnob VenomStatusChance = PP("venom.status_chance", "Venom Status Chance");

        // Predator-owned cross-tree outputs. Foreign skills can query semantic
        // helpers below; they do not need to know which Predator node granted them.
        public static readonly CoreSpecializationKnob ConstrictorDamageVsPrey = P("cross.constrictor_damage_vs_prey", "Constrictor Damage Vs Prey");
        public static readonly CoreSpecializationKnob DroneDamageVsPrey = P("cross.drone_damage_vs_prey", "Drone Damage Vs Prey");
        public static readonly CoreSpecializationKnob DroneTargetPriorityVsPrey = F("cross.drone_priority_vs_prey", "Drone Target Priority Vs Prey");
        public static readonly CoreSpecializationKnob StarfireDamageVsPrey = P("cross.starfire_damage_vs_prey", "Starfire Damage Vs Prey");
        public static readonly CoreSpecializationKnob StarfireStatusChanceVsPrey = PP("cross.starfire_status_vs_prey", "Starfire Status Chance Vs Prey");
        public static readonly CoreSpecializationKnob StarfireResourceOnPreyKill = PP("cross.starfire_resource_on_prey_kill", "Starfire Resource Restored On Prey Kill");

        // Conditional contributors to the SAME canonical stats above.
        public static readonly ModifierKnobSet VsPrey = new ModifierKnobSet("prey", "Vs Prey");
        public static readonly ModifierKnobSet PerHuntStreakStack = new ModifierKnobSet("hunt_stack", "Per Hunt Streak Stack");
        public static readonly ModifierKnobSet Thrill = new ModifierKnobSet("thrill", "Thrill");
        public static readonly ModifierKnobSet VsConstricted = new ModifierKnobSet("constricted", "Vs Constricted Target");
        public static readonly ModifierKnobSet VsGravityAffected = new ModifierKnobSet("gravity", "Vs Gravity-Affected Target");
        public static readonly ModifierKnobSet VsStarfireAffected = new ModifierKnobSet("starfire", "Vs Starfire-Affected Target");
        public static readonly ModifierKnobSet AutoChain = new ModifierKnobSet("auto_chain", "Automatic Lunge");
        public static readonly ModifierKnobSet PerAutoChainLink = new ModifierKnobSet("auto_chain_link", "Per Automatic Lunge Chain Link");

        // Redshift's velocity-specific component sits on top of the generic gravity bundle.
        public static readonly CoreSpecializationKnob RedshiftDamagePerIncomingVelocity = PP("redshift.damage_per_incoming_mps", "Redshift Damage Per Incoming m/s");
        public static readonly CoreSpecializationKnob RedshiftVelocityDamageCap = PP("redshift.damage_cap", "Redshift Velocity Damage Cap");
    }

    public static class Flags
    {
        public static readonly CoreSpecializationFlag ResetCooldownOnPreyDamage = Flag("prey_damage.reset_cooldown", "Reset Cooldown On Prey Damage");
        public static readonly CoreSpecializationFlag ThrillOfTheHunt = Flag("thrill.enabled", "Thrill of the Hunt");
        public static readonly CoreSpecializationFlag MassExtinction = Flag("mass.enabled", "Mass Extinction");
        public static readonly CoreSpecializationFlag HoldPrey = Flag("hold.enabled", "Hold Prey");
        public static readonly CoreSpecializationFlag ReturnAfterImpact = Flag("return.enabled", "Return After Impact");
        public static readonly CoreSpecializationFlag SwallowWhole = Flag("swallow.enabled", "Swallow Whole");
        public static readonly CoreSpecializationFlag SpitPrey = Flag("spit.enabled", "Spit Prey");
        public static readonly CoreSpecializationFlag VenomousBite = Flag("venom.enabled", "Venomous Bite");
        public static readonly CoreSpecializationFlag VenomRefreshes = Flag("venom.refreshes", "Venom Refreshes On Re-bite");
        public static readonly CoreSpecializationFlag AutoChainOnPreyKill = Flag("chain.on_prey_kill", "Automatic Lunge On Prey Kill");
        public static readonly CoreSpecializationFlag AutoChainOnLungeKill = Flag("chain.on_lunge_kill", "Automatic Lunge On Lunge Kill");
        public static readonly CoreSpecializationFlag UnlimitedAutoChain = Flag("chain.unlimited", "Unlimited Automatic Lunge Chain");
        public static readonly CoreSpecializationFlag StopChainWhenTargetSurvives = Flag("chain.stop_on_survivor", "Stop Automatic Chain When Target Survives");
        public static readonly CoreSpecializationFlag Redshift = Flag("redshift.enabled", "Redshift");
    }

    // =========================================================================
    // RESOLVED STATE
    // =========================================================================

    public sealed class ResolvedState
    {
        public bool Active;
        public ModifierValues BaseModifiers;
        public ModifierValues VsPrey;
        public ModifierValues PerHuntStreakStack;
        public ModifierValues ThrillModifiers;
        public ModifierValues VsConstricted;
        public ModifierValues VsGravityAffected;
        public ModifierValues VsStarfireAffected;
        public ModifierValues AutoChainModifiers;
        public ModifierValues PerAutoChainLink;

        public float PreyDurationSeconds;
        public float HuntStreakDurationSeconds;
        public int HuntStacksPerPreyKill;
        public int HuntMaxStacks;
        public bool ResetCooldownOnPreyDamage;

        public float HealMaxHpOnPreyKill;
        public float HealMaxHpOnLungeKill;
        public float CooldownReductionOnPreyKill;
        public float CooldownReductionOnLungeKill;
        public float FullResetChanceOnPreyKill;
        public float FullResetChanceOnLungeKill;

        public bool ThrillEnabled;
        public float ThrillEnemyHealthThreshold;
        public float ThrillRadiusMeters;
        public int ThrillRequiredEnemies;
        public int ThrillMaxStacks;
        public float ThrillAdditionalEnemyEffectScale;

        public float ExecuteTargetOwnHealthThreshold;
        public float ExecuteVsLeviathanMaxHealthThreshold;

        public bool MassExtinctionEnabled;
        public float MassDamagePerMassUnit;
        public float MassDamageCap;

        public bool HoldEnabled;
        public bool ReturnAfterImpactEnabled;
        public int MaxHeldTargets;
        public float MaximumHeldTargetMassRatio;
        public float HoldOffsetMeters;
        public float HoldBreakDistanceMeters;
        public float PullSpeedMetersPerSecond;
        public float PullAccelerationMetersPerSecondSquared;
        public float ReturnSpeedMetersPerSecond;
        public float ReturnAccelerationMetersPerSecondSquared;
        public float ReturnDelaySeconds;

        public bool SwallowEnabled;
        public bool SpitEnabled;
        public float SwallowTargetOwnHealthThreshold;
        public float SwallowVsLeviathanMaxHealthThreshold;
        public float SwallowSizeAdvantageThresholdGain;
        public float SwallowMaximumHealthThreshold;
        public int MaxSwallowedTargets;
        public float SwallowDurationSeconds;
        public float DigestionTargetMaxHpPerSecond;
        public float DigestionPredatorHitFractionPerSecond;
        public float SpitSpeedMetersPerSecond;
        public float SpitCollisionDamagePercent;
        public float SpitThrownPreyDamagePercent;

        public bool AutoChainOnPreyKill;
        public bool AutoChainOnLungeKill;
        public bool UnlimitedAutoChain;
        public bool StopChainWhenTargetSurvives;
        public int AutoChainMaxFollowups;
        public float AutoChainAcquireRadiusMeters;
        public float AutoChainDelaySeconds;

        public bool VenomEnabled;
        public bool VenomRefreshesOnRebite;
        public float VenomImmediateDamagePercent;
        public float VenomDotDamageFractionOfPredatorHit;
        public float VenomDurationSeconds;
        public float VenomTickSeconds;
        public int VenomMaxStacks;
        public float VenomCritChanceBonus;
        public float VenomStatusChanceBonus;

        public float ConstrictorDamageVsPreyPercent;
        public float DroneDamageVsPreyPercent;
        public float DroneTargetPriorityVsPrey;
        public float StarfireDamageVsPreyPercent;
        public float StarfireStatusChanceVsPrey;
        public float StarfireResourceOnPreyKill;

        public bool RedshiftEnabled;
        public float RedshiftDamagePerIncomingVelocity;
        public float RedshiftVelocityDamageCap;
    }

    /// <summary>
    /// Dynamic facts are supplied by gameplay and never invalidate ResolvedState.
    /// </summary>
    public struct CombatContext
    {
        public bool TargetIsPrey;
        public bool TargetIsConstricted;
        public bool TargetIsGravityAffected;
        public bool TargetIsStarfireAffected;
        public int HuntStreakStacks;
        public int ThrillStacks;
        public bool IsAutomaticChainLunge;
        public int AutomaticChainLinkIndex;
        public float IncomingGravityVelocityMetersPerSecond;
        public float LeviathanMass;
    }

    public struct EffectiveStats
    {
        public float DamageMultiplier;

        // Lunge distance and speed are the authored/tunable concepts.
        // Duration is derived so changing distance never changes requested speed:
        //
        //   durationMultiplier = distanceMultiplier / speedMultiplier
        //
        // Example:
        //   2.0x distance at 1.0x speed -> 2.0x duration
        //   0.5x distance at 1.0x speed -> 0.5x duration
        //   0.66x distance at 0.5x speed -> 1.32x duration
        public float LungeDistanceMultiplier;
        public float LungeSpeedMultiplier;
        public float LungeDurationMultiplier;

        public float CooldownMultiplier;
        public float CooldownRecoveryMultiplier;
        public float CritChanceBonus;
        public float StatusChanceBonus;
        public float MoveSpeedMultiplier;
        public float AccelerationMultiplier;
        public float TurnSpeedMultiplier;
    }

    private sealed class CacheEntry
    {
        public int ConfigurationRevision;
        public int RegistryRevision;
        public int AnatomyRevision;
        public GameShip Owner;
        public ResolvedState State;
    }

    private static readonly Dictionary<Pilot, CacheEntry> ResolvedStateCache =
        new Dictionary<Pilot, CacheEntry>();

    public static ResolvedState GetResolvedState(GameShip owner)
    {
        if (owner == null)
            return null;

        Pilot pilot = GameShip.GetPlayerSourcePilot(owner);
        if (pilot == null || !IsSpecializationProfile(owner, pilot))
            return null;

        int configRevision = CoreSpecializationRuntime.ConfigurationRevision;
        int registryRevision = CoreSpecializationRegistry.Revision;
        LeviathanGrowth.AnatomySnapshot anatomy = LeviathanGrowth.GetAnatomy(owner);

        CacheEntry entry;
        if (ResolvedStateCache.TryGetValue(pilot, out entry) &&
            entry != null && entry.State != null &&
            entry.ConfigurationRevision == configRevision &&
            entry.RegistryRevision == registryRevision && entry.Owner == owner &&
            entry.AnatomyRevision == anatomy.Revision)
        {
            return entry.State;
        }

        if (entry == null)
        {
            entry = new CacheEntry();
            ResolvedStateCache[pilot] = entry;
        }

        entry.ConfigurationRevision = configRevision;
        entry.RegistryRevision = registryRevision;
        entry.Owner = owner;
        entry.AnatomyRevision = anatomy.Revision;
        entry.State = BuildResolvedState(pilot);
        // Growth's live counts include additional Heads in scaling segments.
        // HeadCount also includes the primary Head. Contributions are additive.
        entry.State.BaseModifiers.DamagePercent +=
            Percent(pilot, Knobs.DamagePerScalingSegment) * anatomy.ScalingSegmentCount +
            Percent(pilot, Knobs.DamagePerHead) * anatomy.HeadCount;
        entry.State.BaseModifiers.LungeSpeedPercent +=
            Percent(pilot, Knobs.LungeSpeedPerScalingSegment) * anatomy.ScalingSegmentCount +
            Percent(pilot, Knobs.LungeSpeedPerHead) * anatomy.HeadCount;
        entry.State.BaseModifiers.LungeDistancePercent +=
            Percent(pilot, Knobs.LungeDistancePerScalingSegment) * anatomy.ScalingSegmentCount +
            Percent(pilot, Knobs.LungeDistancePerHead) * anatomy.HeadCount;
        return entry.State;
    }

    private static bool IsSpecializationProfile(GameShip owner, Pilot pilot)
    {
        bool local = WorldController.instance != null &&
            ReferenceEquals(WorldController.instance.GetCurrentPlayerShip(), owner);
        bool remote = owner.IsRemotePlayer() &&
            CoreNetwork.HasSynchronizedSpecialization(owner, CoreClassId.Leviathan);

        return (local || remote) &&
            CoreSpecializationRuntime.IsTreeActive(
                pilot,
                LeviathanPredatorTree.TreeId);
    }

    private static ResolvedState BuildResolvedState(Pilot pilot)
    {
        ResolvedState s = new ResolvedState();
        s.Active = true;
        s.BaseModifiers = ResolveBaseModifiers(pilot);
        s.VsPrey = ResolveModifierSet(pilot, Knobs.VsPrey);
        s.PerHuntStreakStack = ResolveModifierSet(pilot, Knobs.PerHuntStreakStack);
        s.ThrillModifiers = ResolveModifierSet(pilot, Knobs.Thrill);
        s.VsConstricted = ResolveModifierSet(pilot, Knobs.VsConstricted);
        s.VsGravityAffected = ResolveModifierSet(pilot, Knobs.VsGravityAffected);
        s.VsStarfireAffected = ResolveModifierSet(pilot, Knobs.VsStarfireAffected);
        s.AutoChainModifiers = ResolveModifierSet(pilot, Knobs.AutoChain);
        s.PerAutoChainLink = ResolveModifierSet(pilot, Knobs.PerAutoChainLink);

        s.PreyDurationSeconds = Pos(Apply(pilot, Knobs.PreyDuration, Tuning.BaselinePreyDurationSeconds));
        s.HuntStreakDurationSeconds = Pos(Apply(pilot, Knobs.HuntStreakDuration, Tuning.BaselineHuntStreakDurationSeconds));
        s.HuntStacksPerPreyKill = NonNegativeInt(Apply(pilot, Knobs.HuntStacksPerPreyKill, Tuning.BaselineHuntStacksPerPreyKill));
        s.HuntMaxStacks = NonNegativeInt(Apply(pilot, Knobs.HuntMaxStacks, Tuning.BaselineHuntMaxStacks));
        s.ResetCooldownOnPreyDamage = Has(pilot, Flags.ResetCooldownOnPreyDamage);
        s.HealMaxHpOnPreyKill = Pos(Flat(pilot, Knobs.HealMaxHpOnPreyKill));
        s.HealMaxHpOnLungeKill = Pos(Flat(pilot, Knobs.HealMaxHpOnLungeKill));
        s.CooldownReductionOnPreyKill = Mathf.Clamp01(Flat(pilot, Knobs.CooldownReductionOnPreyKill));
        s.CooldownReductionOnLungeKill = Mathf.Clamp01(Flat(pilot, Knobs.CooldownReductionOnLungeKill));
        s.FullResetChanceOnPreyKill = Mathf.Clamp01(Flat(pilot, Knobs.FullResetChanceOnPreyKill));
        s.FullResetChanceOnLungeKill = Mathf.Clamp01(Flat(pilot, Knobs.FullResetChanceOnLungeKill));

        s.ThrillEnabled = Has(pilot, Flags.ThrillOfTheHunt);
        s.ThrillEnemyHealthThreshold = Mathf.Clamp01(
            Tuning.BaselineThrillEnemyHealthThreshold +
            Flat(pilot, Knobs.ThrillHealthThreshold));
        s.ThrillRadiusMeters = Pos(Apply(pilot, Knobs.ThrillRadius, Tuning.BaselineThrillRadiusMeters));
        s.ThrillRequiredEnemies = Mathf.Max(1, Mathf.RoundToInt(Apply(pilot, Knobs.ThrillRequiredEnemies, Tuning.BaselineThrillRequiredEnemies)));
        s.ThrillMaxStacks = Mathf.Max(1, Mathf.RoundToInt(Apply(pilot, Knobs.ThrillMaxStacks, Tuning.BaselineThrillMaxStacks)));
        s.ThrillAdditionalEnemyEffectScale = Pos(Apply(pilot, Knobs.ThrillAdditionalEnemyEffectScale, Tuning.BaselineThrillAdditionalEnemyEffectScale));

        s.ExecuteTargetOwnHealthThreshold = Mathf.Clamp01(Flat(pilot, Knobs.ExecuteTargetOwnHealthThreshold));
        s.ExecuteVsLeviathanMaxHealthThreshold = Mathf.Clamp01(Flat(pilot, Knobs.ExecuteVsLeviathanMaxHealthThreshold));

        s.MassExtinctionEnabled = Has(pilot, Flags.MassExtinction);
        s.MassDamagePerMassUnit = Pos(Flat(pilot, Knobs.MassDamagePerMassUnit));
        s.MassDamageCap = Pos(Flat(pilot, Knobs.MassDamageCap));

        s.HoldEnabled = Has(pilot, Flags.HoldPrey);
        s.ReturnAfterImpactEnabled = Has(pilot, Flags.ReturnAfterImpact);
        s.MaxHeldTargets = Mathf.Max(1, Mathf.RoundToInt(Apply(pilot, Knobs.MaxHeldTargets, Tuning.BaselineMaxHeldTargets)));
        s.MaximumHeldTargetMassRatio = Pos(Flat(pilot, Knobs.MaximumHeldTargetMassRatio));
        s.HoldOffsetMeters = Pos(Flat(pilot, Knobs.HoldOffset));
        s.HoldBreakDistanceMeters = Pos(Flat(pilot, Knobs.HoldBreakDistance));
        s.PullSpeedMetersPerSecond = Pos(Flat(pilot, Knobs.PullSpeed));
        s.PullAccelerationMetersPerSecondSquared = Pos(Flat(pilot, Knobs.PullAcceleration));
        s.ReturnSpeedMetersPerSecond = Pos(Flat(pilot, Knobs.ReturnSpeed));
        s.ReturnAccelerationMetersPerSecondSquared = Pos(Flat(pilot, Knobs.ReturnAcceleration));
        s.ReturnDelaySeconds = Pos(Flat(pilot, Knobs.ReturnDelay));

        s.SwallowEnabled = Has(pilot, Flags.SwallowWhole);
        s.SpitEnabled = Has(pilot, Flags.SpitPrey);
        s.SwallowTargetOwnHealthThreshold = Mathf.Clamp01(Flat(pilot, Knobs.SwallowTargetOwnHealthThreshold));
        s.SwallowVsLeviathanMaxHealthThreshold = Mathf.Clamp01(Flat(pilot, Knobs.SwallowVsLeviathanMaxHealthThreshold));
        s.SwallowSizeAdvantageThresholdGain = Pos(Flat(pilot, Knobs.SwallowSizeAdvantageThresholdGain));
        s.SwallowMaximumHealthThreshold = Mathf.Clamp01(Flat(pilot, Knobs.SwallowMaximumHealthThreshold));
        if (s.SwallowMaximumHealthThreshold <= 0f) s.SwallowMaximumHealthThreshold = 1f;
        s.MaxSwallowedTargets = Mathf.Max(1, Mathf.RoundToInt(Apply(pilot, Knobs.MaxSwallowedTargets, Tuning.BaselineMaxSwallowedTargets)));
        s.SwallowDurationSeconds = Pos(Flat(pilot, Knobs.SwallowDuration));
        s.DigestionTargetMaxHpPerSecond = Pos(Flat(pilot, Knobs.DigestionTargetMaxHpPerSecond));
        s.DigestionPredatorHitFractionPerSecond = Pos(Flat(pilot, Knobs.DigestionPredatorHitFractionPerSecond));
        s.SpitSpeedMetersPerSecond = Pos(Flat(pilot, Knobs.SpitSpeed));
        s.SpitCollisionDamagePercent = Percent(pilot, Knobs.SpitCollisionDamage);
        s.SpitThrownPreyDamagePercent = Percent(pilot, Knobs.SpitThrownPreyDamage);

        s.AutoChainOnPreyKill = Has(pilot, Flags.AutoChainOnPreyKill);
        s.AutoChainOnLungeKill = Has(pilot, Flags.AutoChainOnLungeKill);
        s.UnlimitedAutoChain = Has(pilot, Flags.UnlimitedAutoChain);
        s.StopChainWhenTargetSurvives = Has(pilot, Flags.StopChainWhenTargetSurvives);
        s.AutoChainMaxFollowups = NonNegativeInt(Apply(pilot, Knobs.AutoChainMaxFollowups, Tuning.BaselineAutoChainMaxFollowups));
        s.AutoChainAcquireRadiusMeters = Pos(Flat(pilot, Knobs.AutoChainAcquireRadius));
        s.AutoChainDelaySeconds = Pos(Flat(pilot, Knobs.AutoChainDelay));

        s.VenomEnabled = Has(pilot, Flags.VenomousBite);
        s.VenomRefreshesOnRebite = Has(pilot, Flags.VenomRefreshes);
        s.VenomImmediateDamagePercent = Percent(pilot, Knobs.VenomImmediateDamage);
        s.VenomDotDamageFractionOfPredatorHit = Pos(Flat(pilot, Knobs.VenomDotDamageFraction));
        s.VenomDurationSeconds = Pos(Flat(pilot, Knobs.VenomDuration));
        s.VenomTickSeconds = Mathf.Max(0.02f, Apply(pilot, Knobs.VenomTickSeconds, Tuning.BaselineVenomTickSeconds));
        s.VenomMaxStacks = Mathf.Max(1, Mathf.RoundToInt(Apply(pilot, Knobs.VenomMaxStacks, Tuning.BaselineVenomMaxStacks)));
        s.VenomCritChanceBonus = Flat(pilot, Knobs.VenomCritChance);
        s.VenomStatusChanceBonus = Flat(pilot, Knobs.VenomStatusChance);

        s.ConstrictorDamageVsPreyPercent = Percent(pilot, Knobs.ConstrictorDamageVsPrey);
        s.DroneDamageVsPreyPercent = Percent(pilot, Knobs.DroneDamageVsPrey);
        s.DroneTargetPriorityVsPrey = Flat(pilot, Knobs.DroneTargetPriorityVsPrey);
        s.StarfireDamageVsPreyPercent = Percent(pilot, Knobs.StarfireDamageVsPrey);
        s.StarfireStatusChanceVsPrey = Flat(pilot, Knobs.StarfireStatusChanceVsPrey);
        s.StarfireResourceOnPreyKill = Flat(pilot, Knobs.StarfireResourceOnPreyKill);

        s.RedshiftEnabled = Has(pilot, Flags.Redshift);
        s.RedshiftDamagePerIncomingVelocity = Pos(Flat(pilot, Knobs.RedshiftDamagePerIncomingVelocity));
        s.RedshiftVelocityDamageCap = Pos(Flat(pilot, Knobs.RedshiftVelocityDamageCap));
        return s;
    }

    private static ModifierValues ResolveBaseModifiers(Pilot pilot)
    {
        ModifierValues v = new ModifierValues();
        v.DamagePercent = Percent(pilot, Knobs.Damage);
        v.LungeDistancePercent = Percent(pilot, Knobs.LungeDistance);
        v.LungeSpeedPercent = Percent(pilot, Knobs.LungeSpeed);
        v.CooldownPercent = Percent(pilot, Knobs.Cooldown);
        v.CooldownRecoveryPercent = Percent(pilot, Knobs.CooldownRecovery);
        v.CritChanceBonus = Flat(pilot, Knobs.CritChance);
        v.StatusChanceBonus = Flat(pilot, Knobs.StatusChance);
        v.MoveSpeedPercent = Percent(pilot, Knobs.MoveSpeed);
        v.AccelerationPercent = Percent(pilot, Knobs.Acceleration);
        v.TurnSpeedPercent = Percent(pilot, Knobs.TurnSpeed);
        return v;
    }

    private static ModifierValues ResolveModifierSet(Pilot pilot, ModifierKnobSet k)
    {
        ModifierValues v = new ModifierValues();
        v.DamagePercent = Percent(pilot, k.Damage);
        v.LungeDistancePercent = Percent(pilot, k.LungeDistance);
        v.LungeSpeedPercent = Percent(pilot, k.LungeSpeed);
        v.CooldownPercent = Percent(pilot, k.Cooldown);
        v.CooldownRecoveryPercent = Percent(pilot, k.CooldownRecovery);
        v.CritChanceBonus = Flat(pilot, k.CritChance);
        v.StatusChanceBonus = Flat(pilot, k.StatusChance);
        v.MoveSpeedPercent = Percent(pilot, k.MoveSpeed);
        v.AccelerationPercent = Percent(pilot, k.Acceleration);
        v.TurnSpeedPercent = Percent(pilot, k.TurnSpeed);
        return v;
    }

    // =========================================================================
    // EFFECTIVE COMBAT STATS
    // =========================================================================

    /// <summary>
    /// Combines static build configuration with dynamic combat conditions.
    /// Percentages remain additive against the Predator baseline.
    /// </summary>
    public static EffectiveStats ResolveEffectiveStats(
        ResolvedState s,
        CombatContext c)
    {
        EffectiveStats r = new EffectiveStats
        {
            DamageMultiplier = 1f, LungeDistanceMultiplier = 1f,
            LungeSpeedMultiplier = 1f, LungeDurationMultiplier = 1f,
            CooldownMultiplier = 1f, CooldownRecoveryMultiplier = 1f,
            MoveSpeedMultiplier = 1f, AccelerationMultiplier = 1f,
            TurnSpeedMultiplier = 1f
        };
        if (s == null || !s.Active)
            return r;

        ModifierValues v = s.BaseModifiers;
        if (c.TargetIsPrey) v.AddScaled(s.VsPrey, 1f);

        int hunt = Mathf.Max(0, c.HuntStreakStacks);
        if (s.HuntMaxStacks > 0) hunt = Mathf.Min(hunt, s.HuntMaxStacks);
        v.AddScaled(s.PerHuntStreakStack, hunt);

        if (s.ThrillEnabled && c.ThrillStacks > 0)
        {
            int stacks = Mathf.Min(c.ThrillStacks, s.ThrillMaxStacks);
            float scale = 1f + Mathf.Max(0, stacks - 1) * s.ThrillAdditionalEnemyEffectScale;
            v.AddScaled(s.ThrillModifiers, scale);
        }

        if (c.TargetIsConstricted) v.AddScaled(s.VsConstricted, 1f);
        if (c.TargetIsGravityAffected) v.AddScaled(s.VsGravityAffected, 1f);
        if (c.TargetIsStarfireAffected) v.AddScaled(s.VsStarfireAffected, 1f);

        if (c.IsAutomaticChainLunge)
        {
            v.AddScaled(s.AutoChainModifiers, 1f);
            v.AddScaled(s.PerAutoChainLink, Mathf.Max(0, c.AutomaticChainLinkIndex));
        }

        if (s.MassExtinctionEnabled && c.LeviathanMass > 0f)
        {
            float bonus = c.LeviathanMass * s.MassDamagePerMassUnit;
            if (s.MassDamageCap > 0f) bonus = Mathf.Min(bonus, s.MassDamageCap);
            v.DamagePercent += Mathf.Max(0f, bonus);
        }

        if (s.RedshiftEnabled && c.TargetIsGravityAffected &&
            c.IncomingGravityVelocityMetersPerSecond > 0f)
        {
            float bonus = c.IncomingGravityVelocityMetersPerSecond *
                s.RedshiftDamagePerIncomingVelocity;
            if (s.RedshiftVelocityDamageCap > 0f)
                bonus = Mathf.Min(bonus, s.RedshiftVelocityDamageCap);
            v.DamagePercent += Mathf.Max(0f, bonus);
        }

        r.DamageMultiplier = Mathf.Max(0f, Tuning.BaselineDamageMultiplier * (1f + v.DamagePercent));
        r.LungeDistanceMultiplier = Mathf.Max(0f, Tuning.BaselineLungeDistanceMultiplier * (1f + v.LungeDistancePercent));

        // Speed is authored independently from distance. Keep it above zero so
        // duration math cannot divide by zero; a true "disable lunge" mechanic
        // should be represented by a flag/state rather than zero movement speed.
        r.LungeSpeedMultiplier = Mathf.Max(
            0.01f,
            Tuning.BaselineLungeSpeedMultiplier * (1f + v.LungeSpeedPercent)
        );

        r.LungeDurationMultiplier =
            r.LungeDistanceMultiplier / r.LungeSpeedMultiplier;

        r.CooldownMultiplier = Mathf.Max(0.01f, Tuning.BaselineCooldownMultiplier * (1f + v.CooldownPercent));
        r.CooldownRecoveryMultiplier = Mathf.Max(0f, 1f + v.CooldownRecoveryPercent);
        r.CritChanceBonus = Tuning.BaselineCritChanceBonus + v.CritChanceBonus;
        r.StatusChanceBonus = Tuning.BaselineStatusChanceBonus + v.StatusChanceBonus;
        r.MoveSpeedMultiplier = Mathf.Max(0f, 1f + v.MoveSpeedPercent);
        r.AccelerationMultiplier = Mathf.Max(0f, 1f + v.AccelerationPercent);
        r.TurnSpeedMultiplier = Mathf.Max(0f, 1f + v.TurnSpeedPercent);
        return r;
    }

    public static EffectiveStats ResolveEffectiveStats(GameShip owner, CombatContext context)
    {
        return ResolveEffectiveStats(GetResolvedState(owner), context);
    }

    /// <summary>
    /// Applies Predator's authored distance/speed multipliers to a native lunge.
    ///
    /// Star Vortex gives the lunge as distance + duration. Predator instead
    /// authors distance + speed and derives the matching duration:
    ///
    ///   desiredDistance = nativeDistance * distanceMultiplier
    ///   desiredSpeed    = nativeSpeed    * speedMultiplier
    ///   desiredDuration = nativeDuration * distanceMultiplier / speedMultiplier
    ///
    /// This keeps a 1.0x speed lunge at vanilla speed regardless of whether a
    /// node halves or doubles its distance.
    /// </summary>
    public static void ApplyLungeKinematics(
        ref float distance,
        ref float duration,
        EffectiveStats stats)
    {
        distance *= Mathf.Max(0f, stats.LungeDistanceMultiplier);
        duration *= Mathf.Max(0f, stats.LungeDurationMultiplier);
    }

    // =========================================================================
    // EXECUTE / SWALLOW ELIGIBILITY
    // =========================================================================

    public static bool IsExecuteEligible(
        ResolvedState s,
        float targetCurrentHp,
        float targetMaxHp,
        float leviathanMaxHp)
    {
        return s != null && PassesEitherThreshold(
            targetCurrentHp,
            targetMaxHp,
            leviathanMaxHp,
            s.ExecuteTargetOwnHealthThreshold,
            s.ExecuteVsLeviathanMaxHealthThreshold);
    }

    /// <summary>
    /// Swallow's size bonus is intentionally linear: each +1.0 of
    /// LeviathanMaxHP / TargetMaxHP above parity adds the authored threshold gain.
    /// </summary>
    public static float GetSwallowTargetOwnHealthThreshold(
        ResolvedState s,
        float targetMaxHp,
        float leviathanMaxHp)
    {
        if (s == null)
            return 0f;

        float threshold = s.SwallowTargetOwnHealthThreshold;
        if (targetMaxHp > 0f && leviathanMaxHp > targetMaxHp)
        {
            float advantage = leviathanMaxHp / targetMaxHp - 1f;
            threshold += advantage * s.SwallowSizeAdvantageThresholdGain;
        }

        return Mathf.Clamp01(Mathf.Min(threshold, s.SwallowMaximumHealthThreshold));
    }

    public static bool IsSwallowEligible(
        ResolvedState s,
        float targetCurrentHp,
        float targetMaxHp,
        float leviathanMaxHp)
    {
        if (s == null || !s.SwallowEnabled)
            return false;

        return PassesEitherThreshold(
            targetCurrentHp,
            targetMaxHp,
            leviathanMaxHp,
            GetSwallowTargetOwnHealthThreshold(s, targetMaxHp, leviathanMaxHp),
            s.SwallowVsLeviathanMaxHealthThreshold);
    }

    private static bool PassesEitherThreshold(
        float current,
        float targetMax,
        float leviathanMax,
        float ownThreshold,
        float leviathanThreshold)
    {
        bool own = ownThreshold > 0f && targetMax > 0f &&
            current <= targetMax * ownThreshold;
        bool relative = leviathanThreshold > 0f && leviathanMax > 0f &&
            current <= leviathanMax * leviathanThreshold;
        return own || relative;
    }

    // =========================================================================
    // PREY / HUNT STREAK RUNTIME PRIMITIVES
    // =========================================================================

    private sealed class RuntimeState
    {
        public CoreCombat.CombatEntityKey OwnerKey;
        public ulong CombatEventCursor;
        public float RuntimeStartedAt;
        public int HuntStacks;
        public float HuntExpiry;
        public int PreyKillSequence;
        public int LungeKillSequence;
        public Assault Source;
        public bool Lunging;
        public float LungeEnd;
        public float CooldownSeconds;
        public bool CooldownResetForCurrentLunge;
        public readonly HashSet<uint> CooldownResetEligibleEvents = new HashSet<uint>();
        public Vector2 PreviousPosition;
        public readonly HashSet<GameShip> HitTargets = new HashSet<GameShip>();
        public readonly HashSet<GameShip> NearbyTargets = new HashSet<GameShip>();
        public readonly HashSet<GameShip> ContactTargets = new HashSet<GameShip>();
        public readonly Damageable.DamageData[] DamagePacket = new Damageable.DamageData[8];
        public int ThrillStacks;
        public float NextThrillQuery;
        public ResolvedState ThrillConfiguration;
        public int HullRevision = -1;
        public readonly List<GameShip> Sections = new List<GameShip>();
        public readonly List<Collider2D> Hulls = new List<Collider2D>();
    }

    public struct PreyKillResult
    {
        public bool Qualified; // Prey Kill; LungeKill can independently be true.
        public bool LungeKill;
        public int HuntStreakStacks;
        public int PreyKillSequence;
        public int LungeKillSequence;
    }

    private static readonly Dictionary<GameShip, RuntimeState> RuntimeByOwner =
        new Dictionary<GameShip, RuntimeState>();
    private static readonly List<GameShip> OwnerScratch = new List<GameShip>();

    /// <summary>
    /// Explicitly applies Predator's source-qualified OwnerTarget Prey state.
    /// Direct lunges normally call this only after an authoritative qualifying
    /// outcome, so rejected/immune contacts never create or refresh Prey.
    /// </summary>
    public static bool MarkPrey(GameShip owner, GameShip target)
    {
        if (!IsOwnerActive(owner) || target == null || ReferenceEquals(owner, target))
            return false;

        ResolvedState s = GetResolvedState(owner);
        if (s == null || s.PreyDurationSeconds <= 0f)
            return false;

        GetRuntime(owner);
        bool applied = CoreCombatState.Apply(
            owner,
            target,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget,
            s.PreyDurationSeconds);
        if (applied)
            LeviathanPredatorPresentation.TrackPreyTarget(owner, target);
        return applied;
    }

    public static bool IsPrey(GameShip owner, GameShip target)
    {
        if (owner == null || target == null)
            return false;

        return CoreCombatState.Has(
            owner,
            target,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget);
    }

    public static float GetPreyRemainingSeconds(GameShip owner, GameShip target)
    {
        if (owner == null || target == null)
            return 0f;

        return CoreCombatState.GetRemainingSeconds(
            owner,
            target,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget);
    }

    /// <summary>
    /// Prey Kill = marked target dies before mark expiry, regardless of source.
    /// lungeKill separately records the stricter "Predator lunge was lethal" fact.
    /// </summary>
    public static bool TryRegisterPreyKill(
        GameShip owner,
        GameShip target,
        bool lungeKill,
        out PreyKillResult result)
    {
        result = default(PreyKillResult);
        if (!IsOwnerActive(owner))
            return false;

        ResolvedState s = GetResolvedState(owner);
        CoreCombat.CombatEntityKey targetKey;
        if (s == null || target == null ||
            !CoreCombat.TryGetEntityKey(target, out targetKey))
        {
            return false;
        }

        RuntimeState runtime = GetRuntime(owner);
        if (runtime == null || !runtime.OwnerKey.IsValid)
            return false;

        bool preyKill = CoreCombatState.Has(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget);

        if (!preyKill && !lungeKill)
            return false;

        return RegisterSemanticKill(
            runtime,
            s,
            targetKey,
            preyKill,
            lungeKill,
            Time.time,
            out result);
    }

    private static bool RegisterSemanticKill(
        RuntimeState runtime,
        ResolvedState s,
        CoreCombat.CombatEntityKey targetKey,
        bool preyKill,
        bool lungeKill,
        float occurredAt,
        out PreyKillResult result)
    {
        result = default(PreyKillResult);
        if (runtime == null || s == null || !runtime.OwnerKey.IsValid || !targetKey.IsValid)
            return false;

        bool newPreyKill = preyKill &&
            !CoreCombatHistory.WasObservedSince(
                runtime.OwnerKey,
                targetKey,
                CoreCombat.Semantics.PredatorPreyKill,
                runtime.RuntimeStartedAt);

        bool newLungeKill = lungeKill &&
            !CoreCombatHistory.WasObservedSince(
                runtime.OwnerKey,
                targetKey,
                CoreCombat.Semantics.PredatorLungeKill,
                runtime.RuntimeStartedAt);

        if (!newPreyKill && !newLungeKill)
            return false;

        if (newPreyKill)
        {
            CoreCombatHistory.RecordSemanticMarker(
                runtime.OwnerKey,
                targetKey,
                CoreCombat.Semantics.PredatorPreyKill,
                occurredAt,
                true);
        }

        if (newLungeKill)
        {
            CoreCombatHistory.RecordSemanticMarker(
                runtime.OwnerKey,
                targetKey,
                CoreCombat.Semantics.PredatorLungeKill,
                occurredAt,
                true);
        }

        if (newPreyKill)
        {
            CoreCombatState.Remove(
                runtime.OwnerKey,
                targetKey,
                CoreCombat.Semantics.PredatorPrey,
                CoreCombatState.Scope.OwnerTarget);
        }

        result = RegisterKill(runtime, s, newPreyKill, newLungeKill);
        return true;
    }

    private static void ProcessCombatEvents(GameShip owner, RuntimeState runtime)
    {
        if (owner == null || runtime == null || !runtime.OwnerKey.IsValid)
            return;

        CoreCombatHistory.MeaningfulEvent evt;
        while (CoreCombatHistory.TryReadNextMeaningfulEvent(
            runtime.OwnerKey,
            ref runtime.CombatEventCursor,
            out evt))
        {
            if (evt.Kind != CoreCombatHistory.MeaningfulEventKind.CombatOutcome ||
                !evt.Semantic.Equals(CoreCombat.Semantics.PredatorDirectLunge) ||
                evt.OccurredAt < runtime.RuntimeStartedAt)
            {
                continue;
            }

            HandleDirectLungeOutcome(owner, runtime, evt);
        }
    }

    private static void HandleDirectLungeOutcome(
        GameShip owner,
        RuntimeState runtime,
        CoreCombatHistory.MeaningfulEvent evt)
    {
        bool damaged =
            (evt.Outcomes & CoreCombat.OutcomeFlags.Damaged) != 0;
        bool destroyed =
            (evt.Outcomes & CoreCombat.OutcomeFlags.Destroyed) != 0;

        // Preserve current Predator semantics: status-only/rejected outcomes do
        // not create Prey. GuaranteedOutcome is still deferred, so a remote
        // zero-damage rejection simply expires from shared pending correlation.
        if (!damaged && !destroyed)
            return;

        ResolvedState s = GetResolvedState(owner);
        if (s == null)
            return;

        bool hadPrey = CoreCombatState.Has(
            runtime.OwnerKey,
            evt.Target,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget);

        if (destroyed)
        {
            // A valid lethal first direct lunge qualifies as a Prey Kill even
            // though the target was intentionally not Prey during damage math.
            PreyKillResult ignored;
            RegisterSemanticKill(
                runtime,
                s,
                evt.Target,
                hadPrey || s.PreyDurationSeconds > 0f,
                true,
                evt.OccurredAt,
                out ignored);
            return;
        }

        if (s.PreyDurationSeconds <= 0f)
            return;

        CoreCombatState.ApplyAt(
            runtime.OwnerKey,
            evt.Target,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget,
            evt.OccurredAt,
            evt.EventId,
            s.PreyDurationSeconds);

        // The target can die after authority processed this lunge but before
        // the result returns to the source owner. Reconcile against the shared
        // semantic death observation using authored occurrence time, not result
        // arrival time.
        float deathAt = CoreCombatHistory.GetLastObservationTime(
            runtime.OwnerKey,
            evt.Target,
            CoreCombat.Semantics.PredatorTargetDeathObserved);

        if (deathAt >= evt.OccurredAt &&
            deathAt < evt.OccurredAt + s.PreyDurationSeconds)
        {
            PreyKillResult ignored;
            RegisterSemanticKill(
                runtime,
                s,
                evt.Target,
                true,
                false,
                deathAt,
                out ignored);
        }
    }

    private static void ResetCooldownForOriginatingLunge(RuntimeState runtime)
    {
        if (runtime == null)
            return;

        runtime.CooldownResetForCurrentLunge = true;

        // During the lunge, EndLunge will suppress the cooldown that belongs to
        // this attack. If the authoritative result arrives after EndLunge, clear
        // the already-applied cooldown immediately. Eligible EventIds are
        // discarded when a newer lunge starts, so a late old result cannot clear
        // the cooldown of a later attack.
        if (!runtime.Lunging &&
            runtime.Source != null &&
            runtime.Source.parentShip != null)
        {
            runtime.Source.SetCooldown(0f);
        }
    }

    private static PreyKillResult RegisterKill(RuntimeState runtime, ResolvedState s, bool preyKill, bool lungeKill)
    {
        float now = Time.time;
        if (runtime.HuntStacks <= 0 || now >= runtime.HuntExpiry)
            runtime.HuntStacks = 0;

        if (preyKill)
        {
            runtime.HuntStacks = (int)Math.Min(int.MaxValue,
                (long)runtime.HuntStacks + s.HuntStacksPerPreyKill);
            if (s.HuntMaxStacks > 0)
                runtime.HuntStacks = Mathf.Min(runtime.HuntStacks, s.HuntMaxStacks);
            runtime.HuntExpiry = now + s.HuntStreakDurationSeconds;
            runtime.PreyKillSequence++;
        }
        if (lungeKill) runtime.LungeKillSequence++;

        PreyKillResult result = new PreyKillResult();
        result.Qualified = preyKill;
        result.LungeKill = lungeKill;
        result.HuntStreakStacks = runtime.HuntStacks;
        result.PreyKillSequence = runtime.PreyKillSequence;
        result.LungeKillSequence = runtime.LungeKillSequence;
        return result;
    }

    public static int GetHuntStreakStacks(GameShip owner)
    {
        RuntimeState s;
        if (owner == null || !RuntimeByOwner.TryGetValue(owner, out s) || s == null)
            return 0;

        if (s.HuntStacks > 0 && Time.time >= s.HuntExpiry)
        {
            s.HuntStacks = 0;
            s.HuntExpiry = 0f;
        }
        ResolvedState configuration = GetResolvedState(owner);
        if (configuration == null) return 0;
        if (configuration.HuntMaxStacks > 0)
            s.HuntStacks = Mathf.Min(s.HuntStacks, configuration.HuntMaxStacks);
        return s.HuntStacks;
    }

    public static float GetHuntStreakRemainingSeconds(GameShip owner)
    {
        RuntimeState s;
        if (owner == null ||
            !RuntimeByOwner.TryGetValue(owner, out s) ||
            s == null ||
            GetHuntStreakStacks(owner) <= 0)
        {
            return 0f;
        }

        return Mathf.Max(0f, s.HuntExpiry - Time.time);
    }

    // =========================================================================
    // CROSS-SKILL SEMANTIC QUERIES
    // =========================================================================

    public static float GetConstrictorDamageMultiplierAgainst(GameShip owner, GameShip target)
    {
        ResolvedState s = GetResolvedState(owner);
        return s != null && IsPrey(owner, target)
            ? Mathf.Max(0f, 1f + s.ConstrictorDamageVsPreyPercent)
            : 1f;
    }

    public static float GetDroneDamageMultiplierAgainst(GameShip owner, GameShip target)
    {
        ResolvedState s = GetResolvedState(owner);
        return s != null && IsPrey(owner, target)
            ? Mathf.Max(0f, 1f + s.DroneDamageVsPreyPercent)
            : 1f;
    }

    public static float GetDroneTargetPriorityBonusAgainst(GameShip owner, GameShip target)
    {
        ResolvedState s = GetResolvedState(owner);
        return s != null && IsPrey(owner, target) ? s.DroneTargetPriorityVsPrey : 0f;
    }

    public static float GetStarfireDamageMultiplierAgainst(GameShip owner, GameShip target)
    {
        ResolvedState s = GetResolvedState(owner);
        return s != null && IsPrey(owner, target)
            ? Mathf.Max(0f, 1f + s.StarfireDamageVsPreyPercent)
            : 1f;
    }

    public static float GetStarfireStatusChanceBonusAgainst(GameShip owner, GameShip target)
    {
        ResolvedState s = GetResolvedState(owner);
        return s != null && IsPrey(owner, target) ? s.StarfireStatusChanceVsPrey : 0f;
    }

    // =========================================================================
    // NATIVE LUNGE / CONTACT INTEGRATION
    // =========================================================================

    // Verified against Assault.StartAttack/GetDamageData and NetCombat.RouteDamage.
    // Bind private native boundaries once, rather than reflecting/boxing each hit.
    private delegate bool RoutePacket(GameShip target, Damageable.DamageType type,
        Damageable.DamageData[] packet, float status, int crit, Vector2 position,
        GameShip owner, bool bypass, float knockback, Activatable source,
        float impaleDps, float impaleDuration, bool forceLocal, float impaleRotation);
    private delegate void RelayPacket(Activatable source, GameShip owner,
        GameShip target, Damageable.DamageData[] packet, Vector2 position, bool bypass);
    private static readonly Type NativeDamageable = typeof(GameShip).GetInterface("StarVortex.IDamageable");
    private static readonly RoutePacket RouteNative = AccessTools.MethodDelegate<RoutePacket>(
        AccessTools.Method(typeof(NetCombat), "RouteDamage", new[] {
            NativeDamageable, typeof(Damageable.DamageType), typeof(Damageable.DamageData[]),
            typeof(float), typeof(int), typeof(Vector2), typeof(GameShip), typeof(bool),
            typeof(float), typeof(Activatable), typeof(float), typeof(float), typeof(bool), typeof(float) }));
    private static readonly Action<Activatable, GameShip> ApplyNativeCrit =
        AccessTools.MethodDelegate<Action<Activatable, GameShip>>(
            AccessTools.Method(typeof(Activatable), "ApplyOnCritStatusEffects"));
    private static readonly Action<Assault, GameShip, Vector2> NativeLeech =
        AccessTools.MethodDelegate<Action<Assault, GameShip, Vector2>>(
            AccessTools.Method(typeof(Assault), "LeechGladiatorHull"));
    private static readonly RelayPacket NativeRelay = AccessTools.MethodDelegate<RelayPacket>(
        AccessTools.Method(typeof(Conduit), "RelayHit", new[] { typeof(Activatable),
            typeof(GameShip), NativeDamageable, typeof(Damageable.DamageData[]), typeof(Vector2), typeof(bool) }));
    private static Collider2D[] Overlaps = new Collider2D[64];
    private static RaycastHit2D[] SweepHits = new RaycastHit2D[64];
    private static readonly Modifier.Type[] DamageChannels = {
        Modifier.Type.DamageVsHealth, Modifier.Type.DamageVsShield,
        Modifier.Type.DamageVsBurning, Modifier.Type.DamageVsCorroding,
        Modifier.Type.DamageVsDisabled, Modifier.Type.DamageVsFrozen,
        Modifier.Type.DamageVsRadioactive
    };

    public static bool IsOwnerActive(GameShip owner)
    {
        return owner != null && owner.health > 0f && !owner.IsNetRemote() &&
            WorldController.instance != null &&
            ReferenceEquals(WorldController.instance.GetCurrentPlayerShip(), owner) &&
            GetResolvedState(owner) != null;
    }

    private static Assault FindSource(GameShip owner)
    {
        if (owner == null || owner.slots == null) return null;
        for (int i = 0; i < owner.slots.Length; i++)
        {
            Assault source = owner.slots[i]?.equippable as Assault;
            if (source != null) return source;
        }
        return null;
    }

    private static bool ValidEnemy(GameShip owner, GameShip target)
    {
        if (target == null || target == owner || target.health <= 0f ||
            !target.gameObject.activeInHierarchy || !Faction.IsHostile(owner.faction, target.faction) ||
            !target.CanBeDamagedBy(owner, false)) return false;
        GameShip sectionOwner;
        LeviathanGrowth.AnatomyRole role;
        return !LeviathanGrowth.TryGetSectionContext(target, out sectionOwner, out role) ||
            !ReferenceEquals(sectionOwner, owner);
    }

    public static CombatContext GetCombatContext(GameShip owner, GameShip target = null)
    {
        CombatContext context = new CombatContext();
        if (!IsOwnerActive(owner)) return context;
        RuntimeState runtime = GetRuntime(owner);
        ResolvedState configuration = GetResolvedState(owner);
        if (!ReferenceEquals(runtime.ThrillConfiguration, configuration) ||
            Time.time >= runtime.NextThrillQuery)
        {
            runtime.ThrillConfiguration = configuration;
            runtime.NextThrillQuery = Time.time + Tuning.ThrillQueryIntervalSeconds;
            runtime.ThrillStacks = QueryThrill(owner, runtime, configuration);
        }
        context.TargetIsPrey = IsPrey(owner, target);
        context.HuntStreakStacks = GetHuntStreakStacks(owner);
        context.ThrillStacks = runtime.ThrillStacks;
        return context;
    }

    private static int QueryThrill(GameShip owner, RuntimeState runtime, ResolvedState s)
    {
        if (!s.ThrillEnabled || s.ThrillRadiusMeters <= 0f) return 0;
        float radius = s.ThrillRadiusMeters * Tuning.WorldUnitsPerMeter;
        int revision = LeviathanGrowth.GetAnatomy(owner).Revision;
        if (runtime.HullRevision != revision)
        {
            runtime.HullRevision = revision;
            runtime.Hulls.Clear();
            LeviathanGrowth.CollectScalingShips(owner, runtime.Sections);
            runtime.Sections.Add(owner);
            foreach (GameShip section in runtime.Sections)
            {
                if (section == null) continue;
                foreach (Collider2D hull in section.GetComponentsInChildren<Collider2D>(true))
                    if (IsHull(hull) && hull.GetComponentInParent<GameShip>() == section)
                        runtime.Hulls.Add(hull);
            }
            runtime.Sections.Clear();
        }
        runtime.NearbyTargets.Clear();
        foreach (Collider2D hull in runtime.Hulls)
        {
            if (hull == null || !hull.enabled || !hull.gameObject.activeInHierarchy) continue;
            int count = QueryOverlap(hull.bounds.center, hull.bounds.extents.magnitude + radius);
            for (int i = 0; i < count; i++)
            {
                Collider2D enemyHull = Overlaps[i];
                if (!IsHull(enemyHull)) continue;
                GameShip target = enemyHull.GetComponentInParent<GameShip>();
                if (!ValidEnemy(owner, target) || runtime.NearbyTargets.Contains(target) ||
                    target.HealthMax <= 0 || target.health > target.HealthMax * s.ThrillEnemyHealthThreshold) continue;
                ColliderDistance2D gap = hull.Distance(enemyHull);
                if (gap.isValid && (gap.isOverlapped || gap.distance <= radius))
                    runtime.NearbyTargets.Add(target);
            }
            Array.Clear(Overlaps, 0, count);
        }
        int stacks = Mathf.Clamp(runtime.NearbyTargets.Count - s.ThrillRequiredEnemies + 1,
            0, s.ThrillMaxStacks);
        runtime.NearbyTargets.Clear();
        return stacks;
    }

    private static bool IsHull(Collider2D collider)
    {
        return collider != null && !collider.gameObject.CompareTag("Shield") &&
            !collider.gameObject.CompareTag("Projectile") && !collider.gameObject.CompareTag("Container");
    }

    private static int QueryOverlap(Vector2 position, float radius)
    {
        int count;
        // Grow only on saturation; never silently drop contacts in crowded fights.
        while ((count = Physics2D.OverlapCircleNonAlloc(position, radius, Overlaps)) == Overlaps.Length)
            Array.Resize(ref Overlaps, Overlaps.Length * 2);
        return count;
    }

    public static EffectiveStats GetCurrentStats(GameShip owner)
    {
        return ResolveEffectiveStats(IsOwnerActive(owner) ? GetResolvedState(owner) : null,
            GetCombatContext(owner));
    }

    // Called at the native StartAttack boundary, after native CanActivate checks.
    // The first equipped Assault remains the source, just as GetLungeSource does.
    public static bool StartAttack(Assault source)
    {
        GameShip owner = source.parentShip;
        if (owner != null && owner.IsRemotePlayer() && GetResolvedState(owner) != null) return false;
        if (!IsOwnerActive(owner)) return true;
        RuntimeState runtime = GetRuntime(owner);
        if (source != FindSource(owner) || runtime.Lunging) return false;
        EffectiveStats stats = GetCurrentStats(owner);
        float distance = source.Range;
        float duration = source.Duration;
        ApplyLungeKinematics(ref distance, ref duration, stats);
        if (distance <= 0f || !owner.Lunge(owner.transform.right, distance, duration)) return false;
        runtime.Source = source;
        runtime.Lunging = true;
        runtime.LungeEnd = Time.time + duration;
        runtime.CooldownSeconds = source.Cooldown * stats.CooldownMultiplier;
        runtime.CooldownResetForCurrentLunge = false;
        // A result from an older lunge must never reset this lunge's eventual
        // cooldown. Any still-pending old EventIds become intentionally stale.
        runtime.CooldownResetEligibleEvents.Clear();
        runtime.PreviousPosition = owner.transform.position;
        runtime.HitTargets.Clear();
        return false;
    }

    private static void EndLunge(GameShip owner, RuntimeState runtime)
    {
        if (!runtime.Lunging) return;
        runtime.Lunging = false;
        if (owner != null) owner.lungeTimer = 0f;
        if (runtime.Source != null && runtime.Source.parentShip != null)
        {
            runtime.Source.SetCooldown(
                runtime.CooldownResetForCurrentLunge ? 0f : runtime.CooldownSeconds);
        }
        runtime.HitTargets.Clear();
    }

    private static void TickLunge(GameShip owner, RuntimeState runtime)
    {
        if (!runtime.Lunging) return;
        Assault source = runtime.Source;
        int slot = source == null ? -1 : source.GetSlotIndex();
        if (source != FindSource(owner) || slot < 0 || slot >= owner.slots.Length ||
            !owner.slots[slot].enabled || owner.IsDisabled() || owner.IsWeaponsOffline() ||
            !owner.IsVisible() || owner.IsDodging())
        {
            EndLunge(owner, runtime);
            return;
        }
        Vector2 position = owner.transform.position;
        Vector2 delta = position - runtime.PreviousPosition;
        float radius = owner.GetShieldWorldRadius();
        // Swept head contact prevents tunneling between physics updates.
        if (delta.sqrMagnitude > 0f)
        {
            int count;
            while ((count = Physics2D.CircleCastNonAlloc(runtime.PreviousPosition, radius,
                delta.normalized, SweepHits, delta.magnitude)) == SweepHits.Length)
                Array.Resize(ref SweepHits, SweepHits.Length * 2);
            for (int i = 0; i < count; i++)
                HitCollider(owner, runtime, SweepHits[i].collider);
            Array.Clear(SweepHits, 0, count);
        }
        int overlaps = QueryOverlap(position, radius);
        // Hit resolution can query Thrill, so finish the shared overlap query
        // before routing any damage by copying target identities into a set.
        runtime.ContactTargets.Clear();
        for (int i = 0; i < overlaps; i++)
        {
            GameShip target = Overlaps[i].GetComponentInParent<GameShip>();
            if (target != null) runtime.ContactTargets.Add(target);
        }
        Array.Clear(Overlaps, 0, overlaps);
        foreach (GameShip target in runtime.ContactTargets)
            HitTarget(owner, runtime, target);
        runtime.ContactTargets.Clear();
        runtime.PreviousPosition = position;
        if (Time.time >= runtime.LungeEnd || owner.lungeTimer <= 0f)
            EndLunge(owner, runtime);
    }

    private static void HitCollider(GameShip owner, RuntimeState runtime, Collider2D collider)
    {
        if (collider != null) HitTarget(owner, runtime, collider.GetComponentInParent<GameShip>());
    }

    private static void HitTarget(GameShip owner, RuntimeState runtime, GameShip target)
    {
        if (!runtime.Lunging || !IsOwnerActive(owner) || !ValidEnemy(owner, target) ||
            target.IsDodging() || !runtime.HitTargets.Add(target)) return;
        Assault source = runtime.Source;
        // Snapshot Vs Prey before this DirectLunge transaction is authored.
        // Prey is applied only after a qualifying authoritative outcome.
        CombatContext combatContext = GetCombatContext(owner, target);
        ResolvedState configuration = GetResolvedState(owner);
        EffectiveStats stats = ResolveEffectiveStats(configuration, combatContext);
        bool crit = Modifier.CritRoll(Mathf.Max(0f, source.GetCritChance() + stats.CritChanceBonus), target);
        if (crit) ApplyNativeCrit(source, owner);
        if (!ValidEnemy(owner, target)) return;
        // Damage is the native aggregate before its 0.2-second tick / blade split.
        // Keep all eight resolved channels, native DPS and on-crit ordering.
        float damage = source.Damage * stats.DamageMultiplier * (crit ? 1f + source.GetCritModifier() : 1f);
        if (damage <= 0f) return;
        float dps = source.CalculateDPS(Activatable.Modified.Global);
        runtime.DamagePacket[0] = new Damageable.DamageData(damage, dps);
        for (int i = 0; i < DamageChannels.Length; i++)
            runtime.DamagePacket[i + 1] = new Damageable.DamageData(DamageChannels[i],
                source.ApplyModifier(DamageChannels[i], damage, false, true) - damage, dps);
        Vector2 point = target.transform.position;
        target.SetLastDamageDirection((point - (Vector2)owner.transform.position).normalized);
        target.lastDamagedByWeaponName = source.GetName(false, false);
        target.lastDamagedByShipName = owner.GetName();
        target.lastDamagedByFaction = owner.faction;
        bool bypass = source.HasCustomizer(Customizer.Type.BypassDamageLimit);
        CoreCombat.AcknowledgementMode acknowledgement =
            CoreCombat.SupportsGuaranteedOutcome
                ? CoreCombat.AcknowledgementMode.GuaranteedOutcome
                : CoreCombat.AcknowledgementMode.NativeResult;

        CoreCombat.DamageScope combatScope = CoreCombat.BeginDamage(
            owner,
            target,
            CoreCombat.Semantics.PredatorDirectLunge,
            default(CoreCombat.ContributorKey),
            acknowledgement,
            CoreCombat.TrackingFlags.Summary |
                CoreCombat.TrackingFlags.MeaningfulOutcome,
            0,
            owner);

        LeviathanPredatorPresentation.TrackPreyTarget(owner, target);
        if (combatScope.EventId != 0U &&
            combatContext.TargetIsPrey &&
            configuration != null &&
            configuration.ResetCooldownOnPreyDamage)
        {
            runtime.CooldownResetEligibleEvents.Add(combatScope.EventId);
        }

        bool killed;
        try
        {
            killed = RouteNative(target, source.damageType, runtime.DamagePacket,
                Mathf.Max(0f, source.StatusEffectChance + stats.StatusChanceBonus), crit ? 1 : 0,
                point, owner, bypass, 0f, source, 0f, 0f, false, 0f);
        }
        finally
        {
            CoreCombat.EndDamage(combatScope);
        }

        // Local-authority outcomes have committed by the time RouteDamage
        // returns. Remote-authority outcomes are consumed on a later FixedTick.
        ProcessCombatEvents(owner, runtime);
        NativeRelay(source, owner, target, runtime.DamagePacket, point, bypass);
        if (target != null && !target.IsNetRemote()) NativeLeech(source, target, point);
        if (killed && target != null && !target.IsDrone())
        {
            float shed = source.ApplyModifierToPercentage(Modifier.Type.OnEnemyDeathShed, 0f, true);
            if (shed > 0f) target.ShedStatusEffects(shed, true, owner);
            Projectile.ProcessDeathSurge(source, owner, point, dps);
        }
    }

    internal static void TargetDied(GameShip target, bool voluntary)
    {
        if (voluntary || target.isBeingDestroyed || target.health > 0f || WorldController.instance == null)
            return;

        GameShip owner = WorldController.instance.GetCurrentPlayerShip();
        RuntimeState runtime;
        CoreCombat.CombatEntityKey targetKey;
        if (!IsOwnerActive(owner) ||
            !RuntimeByOwner.TryGetValue(owner, out runtime) || runtime == null ||
            !runtime.OwnerKey.IsValid ||
            !CoreCombat.TryGetEntityKey(target, out targetKey))
        {
            return;
        }

        bool prey = CoreCombatState.Has(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget);
        bool pendingDirect = CoreCombat.HasPendingEvent(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorDirectLunge);

        if (!prey && !pendingDirect)
            return;

        float now = Time.time;
        CoreCombatHistory.RecordSemanticMarker(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorTargetDeathObserved,
            now,
            false);

        if (prey)
        {
            PreyKillResult ignored;
            RegisterSemanticKill(runtime, GetResolvedState(owner), targetKey,
                true, false, now, out ignored);
        }
    }

    public static void GetKillSequences(GameShip owner, out int preyKills, out int lungeKills)
    {
        RuntimeState runtime;
        if (owner != null && RuntimeByOwner.TryGetValue(owner, out runtime))
        { preyKills = runtime.PreyKillSequence; lungeKills = runtime.LungeKillSequence; }
        else { preyKills = 0; lungeKills = 0; }
    }

    public static void UpdateCooldown(Activatable source)
    {
        if (!(source is Assault) || !IsOwnerActive(source.parentShip) ||
            source != FindSource(source.parentShip) || source.cooldownTimer <= 0f) return;
        // Prefix on native UpdateCooldown: native subsequently subtracts one dt.
        source.cooldownTimer = Mathf.Max(0f, source.cooldownTimer + Time.deltaTime *
            (1f - GetCurrentStats(source.parentShip).CooldownRecoveryMultiplier));
    }

    // =========================================================================
    // CONTROLLER LIFECYCLE
    // =========================================================================

    public static void Cancel()
    {
        LeviathanPredatorPresentation.ResetAll();
        foreach (KeyValuePair<GameShip, RuntimeState> pair in RuntimeByOwner)
        {
            EndLunge(pair.Key, pair.Value);
            if (pair.Value != null && pair.Value.OwnerKey.IsValid)
            {
                CoreCombat.ResetOwnerSkillRuntime(
                    pair.Value.OwnerKey,
                    CoreCombat.SkillIds.Predator);
            }
        }
        RuntimeByOwner.Clear();
        ResolvedStateCache.Clear();
        OwnerScratch.Clear();
        Array.Clear(Overlaps, 0, Overlaps.Length);
        Array.Clear(SweepHits, 0, SweepHits.Length);
    }

    public static void CancelForPlayer(GameShip player)
    {
        if (player == null)
            return;
        LeviathanPredatorPresentation.ResetOwner(player);
        RuntimeState runtime;
        if (RuntimeByOwner.TryGetValue(player, out runtime) && runtime != null)
        {
            EndLunge(player, runtime);
            if (runtime.OwnerKey.IsValid)
            {
                CoreCombat.ResetOwnerSkillRuntime(
                    runtime.OwnerKey,
                    CoreCombat.SkillIds.Predator);
            }
        }
        RuntimeByOwner.Remove(player);
        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot != null) ResolvedStateCache.Remove(pilot);
    }

    /// <summary>
    /// Controller owns the fixed-step and world/player teardown boundaries.
    /// </summary>
    public static void FixedTick()
    {
        if (RuntimeByOwner.Count == 0)
            return;

        float now = Time.time;
        OwnerScratch.Clear();

        foreach (GameShip owner in RuntimeByOwner.Keys) OwnerScratch.Add(owner);
        for (int ownerIndex = 0; ownerIndex < OwnerScratch.Count; ownerIndex++)
        {
            GameShip owner = OwnerScratch[ownerIndex];
            RuntimeState state;
            if (!RuntimeByOwner.TryGetValue(owner, out state)) continue;
            if (!IsOwnerActive(owner) || state == null)
            {
                if (state != null)
                {
                    EndLunge(owner, state);
                    if (state.OwnerKey.IsValid)
                    {
                        CoreCombat.ResetOwnerSkillRuntime(
                            state.OwnerKey,
                            CoreCombat.SkillIds.Predator);
                    }
                }
                LeviathanPredatorPresentation.ResetOwner(owner);
                RuntimeByOwner.Remove(owner);
                continue;
            }

            if (state.HuntStacks > 0 && now >= state.HuntExpiry)
            {
                state.HuntStacks = 0;
                state.HuntExpiry = 0f;
            }
            ProcessCombatEvents(owner, state);
            LeviathanPredatorPresentation.FixedTick(owner);
            // Warm conditional state before contact collection uses its buffers.
            GetCombatContext(owner);
            TickLunge(owner, state);
            // Slot 3: lunge-active only. Native replication already carries motion.
            CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(CoreNetwork.SlotPredator);
            writer.Bool(state.Lunging);
            CoreNetwork.EndSlot(writer);
        }

        OwnerScratch.Clear();
    }

    // LeviathanController's high-speed resistance must not slow native lunges.
    public static bool IsPredatorLunging(GameShip player)
    {
        if (player != null && player.IsRemotePlayer())
        {
            CoreNetwork.SlotReader reader;
            return CoreNetwork.HasSynchronizedSpecialization(player, CoreClassId.Leviathan) &&
                CoreNetwork.TryReadSlot(player, CoreNetwork.SlotPredator, out reader) && reader.Bool();
        }
        RuntimeState runtime;
        return player != null && RuntimeByOwner.TryGetValue(player, out runtime) &&
            runtime.Lunging && player.lungeTimer > 0f;
    }

    // =========================================================================
    // SMALL HELPERS
    // =========================================================================

    internal static void RemoteTargetDied(NetWorldBridge bridge, MsgEntityDeath death)
    {
        if (death.voluntary || death.starId != bridge.starId)
            return;

        GameShip owner = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();
        RuntimeState runtime;
        if (!IsOwnerActive(owner) ||
            !RuntimeByOwner.TryGetValue(owner, out runtime) || runtime == null ||
            !runtime.OwnerKey.IsValid || death.netId == 0)
        {
            return;
        }

        CoreCombat.CombatEntityKey targetKey =
            CoreCombat.ForNetworkEntity(death.netId);

        bool prey = CoreCombatState.Has(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorPrey,
            CoreCombatState.Scope.OwnerTarget);
        bool pendingDirect = CoreCombat.HasPendingEvent(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorDirectLunge);

        if (!prey && !pendingDirect)
            return;

        float now = Time.time;
        CoreCombatHistory.RecordSemanticMarker(
            runtime.OwnerKey,
            targetKey,
            CoreCombat.Semantics.PredatorTargetDeathObserved,
            now,
            false);

        if (prey)
        {
            PreyKillResult ignored;
            RegisterSemanticKill(runtime, GetResolvedState(owner), targetKey,
                true, false, now, out ignored);
        }
    }

    private static RuntimeState GetRuntime(GameShip owner)
    {
        RuntimeState s;
        if (!RuntimeByOwner.TryGetValue(owner, out s) || s == null)
        {
            s = new RuntimeState();
            s.RuntimeStartedAt = Time.time;
            CoreCombat.TryGetEntityKey(owner, out s.OwnerKey);

            // The meaningful ring is bounded owner history, not skill runtime
            // state. A replacement Predator runtime starts at the current tail
            // so pre-reset outcomes/markers cannot be replayed into it.
            if (s.OwnerKey.IsValid)
            {
                CoreCombatHistory.MeaningfulEvent ignored;
                while (CoreCombatHistory.TryReadNextMeaningfulEvent(
                    s.OwnerKey,
                    ref s.CombatEventCursor,
                    out ignored))
                {
                }
            }

            RuntimeByOwner[owner] = s;
        }
        return s;
    }

    private static float Percent(Pilot pilot, CoreSpecializationKnob knob)
    {
        return CoreSpecializationRuntime.GetKnobMultiplier(pilot, knob) - 1f;
    }

    private static float Flat(Pilot pilot, CoreSpecializationKnob knob)
    {
        return CoreSpecializationRuntime.GetKnobFlat(pilot, knob);
    }

    private static float Apply(Pilot pilot, CoreSpecializationKnob knob, float baseline)
    {
        return CoreSpecializationRuntime.ApplyKnob(pilot, knob, baseline);
    }

    private static bool Has(Pilot pilot, CoreSpecializationFlag flag)
    {
        return CoreSpecializationRuntime.HasFlag(pilot, flag);
    }

    private static float Pos(float value)
    {
        return Mathf.Max(0f, value);
    }

    private static int NonNegativeInt(float value)
    {
        return Mathf.Max(0, Mathf.RoundToInt(value));
    }
}

[HarmonyPatch(typeof(Assault), "StartAttack")]
internal static class LeviathanPredatorStartPatch
{
    public static bool Prefix(Assault __instance) => LeviathanPredatorRuntime.StartAttack(__instance);
}

[HarmonyPatch(typeof(Assault), "DoDamageTick")]
internal static class LeviathanPredatorBladePatch
{
    public static bool Prefix(Assault __instance) =>
        !(LeviathanPredatorRuntime.IsOwnerActive(__instance.parentShip) ||
          (__instance.parentShip != null && __instance.parentShip.IsRemotePlayer() &&
           LeviathanPredatorRuntime.GetResolvedState(__instance.parentShip) != null));
}

[HarmonyPatch(typeof(Assault), "Unequip")]
internal static class LeviathanPredatorUnequipPatch
{
    public static void Prefix(Assault __instance) =>
        LeviathanPredatorRuntime.CancelForPlayer(__instance.parentShip);
}

[HarmonyPatch(typeof(Activatable), "UpdateCooldown")]
internal static class LeviathanPredatorCooldownPatch
{
    public static void Prefix(Activatable __instance) => LeviathanPredatorRuntime.UpdateCooldown(__instance);
}

[HarmonyPatch(typeof(GameShip), "Destroyed")]
internal static class LeviathanPredatorDeathPatch
{
    public static void Prefix(GameShip __instance, bool voluntary) =>
        LeviathanPredatorRuntime.TargetDied(__instance, voluntary);
}

[HarmonyPatch(typeof(GameShip), "get_MaxSpeed")]
internal static class LeviathanPredatorMovePatch
{
    public static void Postfix(GameShip __instance, ref float __result) =>
        __result *= LeviathanPredatorRuntime.GetCurrentStats(__instance).MoveSpeedMultiplier;
}

[HarmonyPatch(typeof(GameShip), "get_TurnSpeed")]
internal static class LeviathanPredatorTurnPatch
{
    public static void Postfix(GameShip __instance, ref float __result) =>
        __result *= LeviathanPredatorRuntime.GetCurrentStats(__instance).TurnSpeedMultiplier;
}

[HarmonyPatch(typeof(Thruster), "get_AccelerationFactor")]
internal static class LeviathanPredatorAccelerationPatch
{
    public static void Postfix(Thruster __instance, ref float __result) =>
        __result *= LeviathanPredatorRuntime.GetCurrentStats(__instance.parentShip).AccelerationMultiplier;
}

[HarmonyPatch(typeof(NetWorldBridge), "OnEntityDeath")]
internal static class LeviathanPredatorRemoteDeathPatch
{
    public static void Prefix(NetWorldBridge __instance, MsgEntityDeath death)
        => LeviathanPredatorRuntime.RemoteTargetDied(__instance, death);
}
