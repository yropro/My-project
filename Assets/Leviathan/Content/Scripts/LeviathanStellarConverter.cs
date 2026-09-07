using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using static StarVortex.Damageable;

// Stellar Converter: first Special-slot Laser, otherwise first Primary-slot Laser,
// becomes a charged burst beam.
// Native damage type, chaining, leech, piercing and attribution are preserved.
// Normal release during charge cancels. If ship CC/offline interrupts an already-started
// charge, that one shot remains committed and finishes its charge + output.

public static class LeviathanStellarConverterTuning
{
    // Baseline specialization profile. These five values define the root node.
    public const float BaselineChargeSeconds = 1.00f;
    public const float BaselinePulseSeconds = 1.00f;
    public const float BaselineDamageMultiplier = 3.20f;
    public const float BaselineWidthMultiplier = 10.00f;
    public const float BaselineRangeMultiplier = 1.10f;

    // Existing Converter presentation/crit behavior retained by the new baseline.
    public const float BaselineCritChanceMultiplier = 1.10f;
    public const float BaselineCritDamageMultiplier = 1.00f;
    public const float BaselineDebuffChanceMultiplier = 1.00f;
    public const float BaselineBrightnessMultiplier = 0.91f;
    public const float HitboxWidthMultiplier = 1.20f;

    // Historical targets used by the heavy branch.
    public const float OldRank3ChargeSeconds = 1.50f;
    public const float OldRank3PulseSeconds = 1.20f;
    public const float OldRank3DamageMultiplier = 4.45f;
    public const float OldRank3WidthMultiplier = 20.00f;
    public const float OldRank3RangeMultiplier = 1.35f;

    public const float OldRank5PulseSeconds = 1.40f;
    public const float OldRank5DamageMultiplier = 4.90f;
    public const float OldRank5WidthMultiplier = 35.00f;
    public const float OldRank5RangeMultiplier = 1.50f;

    // Deep Capacitors deliberately takes longer than historical rank 5. This
    // damage multiplier preserves the old rank-5 cycle-average damage:
    // 4.9 * 1.4 / (1.5 + 1.4) == 5.744827... * 1.4 / (2.0 + 1.4).
    public const float DeepCapacitorsChargeSeconds = 2.00f;
    public const float DeepCapacitorsDamageMultiplier = 5.744828f;

    // Continuous Conversion hard overrides after ordinary tree modifiers.
    public const float ContinuousDamageMultiplier = 1.00f;
    public const float ContinuousWidthMultiplier = 2.00f;
    public const float ContinuousRandomStatusChance = 0.05f;

    // Star Vortex displays 20 meters per Unity world unit.
    public const float WorldUnitsPerMeter = 1f / 20f;

    // Shared manifestation cadence. The three Deep Capacitors capstones all
    // wait this long after output before another charge can begin.
    public const float ManifestationCooldownSeconds = 3.00f;

    // Shared gravity behavior. PullStrength is a multiplier on the vanilla
    // Black Hole projectile's serialized pull power. 1.0 = native Black Hole feel.
    // The native Black Hole does not reduce pull against bosses.
    public const float GravityBossPullMultiplier = 1.00f;
    public const float GravityPullFalloffExponent = 1.00f;

    // Singularity: lazy drifting gravity well + sustained Halo damage.
    public const float SingularityTravelSpeedMetersPerSecond = 45f;
    public const float SingularityLifetimeSeconds = 7.00f;
    public const float SingularityPullRadiusMeters = 120f;
    public const float SingularityPullStrength = 1.00f;
    public const float SingularityHaloRadiusMeters = 25f;
    public const float SingularityDamageFraction = 0.80f;
    public const float SingularityVisualScale = 0.45f;
    // 0 = preserve VisualScale behavior. Positive values force the visible
    // black-hole art to this radius in meters.
    public const float SingularityVisualRadiusMeters = 0f;
    public const float SingularityHaloOpacity = 0.70f;

    // Dying Star: same gravity seed, then one delayed integrated Converter explosion.
    public const float DyingStarTravelSpeedMetersPerSecond = 45f;
    public const float DyingStarFuseSeconds = 5.00f;
    public const float DyingStarPullRadiusMeters = 100f;
    public const float DyingStarPullStrength = 1.00f;
    public const float DyingStarExplosionRadiusMeters = 125f;
    public const float DyingStarDamageFraction = 1.00f;
    public const float DyingStarVisualScale = 0.80f;
    // Explicit visible black-hole radius; independent from pull radius.
    public const float DyingStarVisualRadiusMeters = 27f;
    public const float DyingStarExplosionExpansionSpeedMetersPerSecond = 100f;
    // Final rendered explosion radius. Mechanical damage radius remains the
    // independent DyingStarExplosionRadiusMeters value above.
    public const float DyingStarExplosionVisualRadiusMeters = 125f;
    public const float DyingStarExplosionVisualScale = 1.00f;
    public const float DyingStarExplosionVisualOpacity = 1.00f;
    public const float DyingStarExplosionSoundLeadSeconds = 0.50f;
    public const string DyingStarExplosionPrefabNameOverride = "";

    // Event Horizon: native Converter beam remains; an invisible gravity corridor
    // grows from the muzzle toward this independently-moving pull tip.
    public const float EventHorizonPullTipSpeedMetersPerSecond = 100f;
    public const float EventHorizonPullRadiusMeters = 35f;
    public const float EventHorizonPullStrength = 1.00f;
    // 0 keeps the gravity tip invisible. Set this above zero to render the
    // vanilla black-hole art at the moving pull tip with this radius in meters.
    public const float EventHorizonVisualRadiusMeters = 0f;

    // Hard safety ceiling for manually enlarged manifestation visuals.
    // Mechanical radii are not clamped by this.
    public const float MaxManifestationVisualRadiusMeters = 300f;

    // Visual fallback rings only.
    public const int GravityFallbackCircleSegments = 48;
}

public static class LeviathanStellarConverter
{
    // =========================================================================
    // SPECIALIZATION INTERFACE
    // =========================================================================
    // Tree files point at these knobs/flags. Stellar Converter owns their meaning
    // and applies them inside its normal resolved-state logic.

    public static class Knobs
    {
        public static readonly LeviathanSpecializationKnob ChargeTime =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.charge_time", "Charge Time", "s");

        public static readonly LeviathanSpecializationKnob PulseDuration =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.pulse_duration", "Pulse Duration", "s");

        public static readonly LeviathanSpecializationKnob DamageMultiplier =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.damage_multiplier", "Damage Multiplier", "x");

        public static readonly LeviathanSpecializationKnob WidthMultiplier =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.width_multiplier", "Width Multiplier", "x");

        public static readonly LeviathanSpecializationKnob RangeMultiplier =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.range_multiplier", "Range Multiplier", "x");

        public static readonly LeviathanSpecializationKnob CritChance =
            LeviathanSpecializationKnob.PercentagePoints(
                "stellar_converter.crit_chance", "Critical Chance");

        public static readonly LeviathanSpecializationKnob StatusChance =
            LeviathanSpecializationKnob.PercentagePoints(
                "stellar_converter.status_chance", "Status Chance");

        public static readonly LeviathanSpecializationKnob ChainTargets =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.chain_targets", "Chain Targets");

        public static readonly LeviathanSpecializationKnob FinalRangePercent =
            LeviathanSpecializationKnob.Percent(
                "stellar_converter.final_range_percent", "Final Converter Range");

        public static readonly LeviathanSpecializationKnob ManifestationCooldown =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.manifestation.cooldown",
                "Manifestation Cooldown",
                "s");

        // Manifestation tuning. Tree files can point at these like any other knobs.
        public static readonly LeviathanSpecializationKnob SingularitySpeed =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.speed", "Singularity Speed", "m/s");
        public static readonly LeviathanSpecializationKnob SingularityLifetime =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.lifetime", "Singularity Lifetime", "s");
        public static readonly LeviathanSpecializationKnob SingularityPullRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.pull_radius", "Singularity Pull Radius", "m");
        public static readonly LeviathanSpecializationKnob SingularityPullStrength =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.pull_strength", "Singularity Pull Strength", "x");
        public static readonly LeviathanSpecializationKnob SingularityVisualScale =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.visual_scale", "Singularity Visual Scale", "x");
        public static readonly LeviathanSpecializationKnob SingularityVisualRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.visual_radius", "Singularity Visual Radius", "m");
        public static readonly LeviathanSpecializationKnob SingularityHaloRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.singularity.halo_radius", "Singularity Halo Radius", "m");
        public static readonly LeviathanSpecializationKnob SingularityDamage =
            LeviathanSpecializationKnob.PercentagePoints(
                "stellar_converter.singularity.damage", "Singularity Integrated Damage");

        public static readonly LeviathanSpecializationKnob DyingStarSpeed =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.speed", "Dying Star Speed", "m/s");
        public static readonly LeviathanSpecializationKnob DyingStarFuse =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.fuse", "Dying Star Fuse", "s");
        public static readonly LeviathanSpecializationKnob DyingStarPullRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.pull_radius", "Dying Star Pull Radius", "m");
        public static readonly LeviathanSpecializationKnob DyingStarPullStrength =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.pull_strength", "Dying Star Pull Strength", "x");
        public static readonly LeviathanSpecializationKnob DyingStarVisualScale =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.visual_scale", "Dying Star Visual Scale", "x");
        public static readonly LeviathanSpecializationKnob DyingStarVisualRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.visual_radius", "Dying Star Visual Radius", "m");
        public static readonly LeviathanSpecializationKnob DyingStarExplosionRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.explosion_radius", "Dying Star Explosion Radius", "m");
        public static readonly LeviathanSpecializationKnob DyingStarExplosionVisualRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.dying_star.explosion_visual_radius",
                "Dying Star Explosion Visual Radius",
                "m");
        public static readonly LeviathanSpecializationKnob DyingStarDamage =
            LeviathanSpecializationKnob.PercentagePoints(
                "stellar_converter.dying_star.damage", "Dying Star Integrated Damage");

        public static readonly LeviathanSpecializationKnob EventHorizonTipSpeed =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.event_horizon.tip_speed", "Event Horizon Tip Speed", "m/s");
        public static readonly LeviathanSpecializationKnob EventHorizonPullRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.event_horizon.pull_radius", "Event Horizon Pull Radius", "m");
        public static readonly LeviathanSpecializationKnob EventHorizonPullStrength =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.event_horizon.pull_strength", "Event Horizon Pull Strength", "x");
        public static readonly LeviathanSpecializationKnob EventHorizonVisualRadius =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.event_horizon.visual_radius",
                "Event Horizon Tip Visual Radius",
                "m");

        // Boolean-like numeric knob so a multi-rank node can enable piercing only
        // at a specific rank without making runtime logic depend on a node id.
        public static readonly LeviathanSpecializationKnob Piercing =
            LeviathanSpecializationKnob.Flat(
                "stellar_converter.piercing", "Piercing");
    }

    public static class Flags
    {
        public static readonly LeviathanSpecializationFlag Continuous =
            LeviathanSpecializationFlag.Create(
                "stellar_converter.mode.continuous", "Continuous Conversion");

        public static readonly LeviathanSpecializationFlag ArcCascade =
            LeviathanSpecializationFlag.Create(
                "stellar_converter.mode.arc_cascade", "Arc Cascade");

        public static readonly LeviathanSpecializationFlag FractalCascade =
            LeviathanSpecializationFlag.Create(
                "stellar_converter.mode.fractal_cascade", "Fractal Cascade");

        public static readonly LeviathanSpecializationFlag Singularity =
            LeviathanSpecializationFlag.Create(
                "stellar_converter.mode.singularity", "Singularity");

        public static readonly LeviathanSpecializationFlag DyingStar =
            LeviathanSpecializationFlag.Create(
                "stellar_converter.mode.dying_star", "Dying Star");

        public static readonly LeviathanSpecializationFlag EventHorizon =
            LeviathanSpecializationFlag.Create(
                "stellar_converter.mode.event_horizon", "Event Horizon");
    }

    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5.

    public const int MaxRank = 5;

    // Time the source Laser must be held before the burst commits.
    private static readonly float[] ChargeDurationByRank =
    {
        1.50f, // Rank 1
        1.50f, // Rank 2
        1.50f, // Rank 3
        1.50f, // Rank 4
        1.50f  // Rank 5
    };

    // Once committed, native beam firing continues for this full duration.
    private static readonly float[] OutputDurationByRank =
    {
        1.00f, // Rank 1
        1.10f, // Rank 2
        1.20f, // Rank 3
        1.30f, // Rank 4
        1.40f  // Rank 5
    };

    // Multiplier on the source Laser's complete native DamageData packet.
    private static readonly float[] DamageMultiplierByRank =
    {
        4.000f, // Rank 1
        4.225f, // Rank 2
        4.450f, // Rank 3
        4.675f, // Rank 4
        4.900f  // Rank 5
    };

    // Multiplier on native crit chance while the burst is firing.
    private static readonly float[] CritChanceMultiplierByRank =
    {
        1.10f, // Rank 1
        1.10f, // Rank 2
        1.10f, // Rank 3
        1.10f, // Rank 4
        1.10f  // Rank 5
    };

    // Multiplier on the native crit BONUS portion while the burst is firing.
    private static readonly float[] CritDamageMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on native status/debuff chance while the burst is firing.
    private static readonly float[] DebuffChanceMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on the source Laser's native MaxRange result, injected at the same
    // Equippable.ApplyModifier(MaxRange) boundary used by Striker. Beam raycasts,
    // chain range and drawing therefore inherit it through vanilla code.
    private static readonly float[] LengthMultiplierByRank =
    {
        1.100f, // Rank 1
        1.275f, // Rank 2
        1.350f, // Rank 3
        1.425f, // Rank 4
        1.500f  // Rank 5
    };

    // Multiplier on visual beam width and the matching CircleCast radius.
    private static readonly float[] WidthMultiplierByRank =
    {
        10.00f, // Rank 1
        15.0f, // Rank 2
        20.00f, // Rank 3
        25.0f, // Rank 4
        35.00f  // Rank 5
    };

    // Extra mechanical width only. 1.20 = hitbox radius reaches 20% farther
    // from the beam centerline than the rendered beam edge.
    private const float HitboxWidthMultiplier =
        LeviathanStellarConverterTuning.HitboxWidthMultiplier;

    // Visual brightness only. 1.0 = vanilla brightness.
    private static readonly float[] BrightnessMultiplierByRank =
    {
    0.91f, // Rank 1
    0.91f, // Rank 2
    0.91f, // Rank 3
    0.91f, // Rank 4
    0.91f  // Rank 5
    };

    private static readonly bool[] PiercingByRank =
    {
    false, // Rank 1
    false, // Rank 2
    true, // Rank 3
    true, // Rank 4
    true  // Rank 5
    };

    // =========================================================================
    // NATIVE MEMBERS
    // =========================================================================

    private static readonly FieldInfo ActivatableActiveField =
        AccessTools.Field(typeof(Activatable), "active");

    private static readonly FieldInfo BeamParentWeaponField =
        AccessTools.Field(typeof(Beam), "parentBeamWeapon");

    private static readonly FieldInfo BeamMaxWidthField =
        AccessTools.Field(typeof(Beam), "maxWidth");

    private static readonly FieldInfo BeamAdditionalMaxWidthField =
        AccessTools.Field(typeof(Beam), "additionalMaxWidth");

    private static readonly FieldInfo BeamMaxEndWidthField =
        AccessTools.Field(typeof(Beam), "maxEndWidth");

    private static readonly FieldInfo BeamLineRendererField =
        AccessTools.Field(typeof(Beam), "beamLineRenderer");

    private static readonly FieldInfo BeamEndLineRendererField =
        AccessTools.Field(typeof(Beam), "beamEndLineRenderer");

    private static readonly FieldInfo BeamSnapChangeField =
        AccessTools.Field(typeof(Beam), "snapChange");

    private static readonly FieldInfo BeamBasePiercingField =
        AccessTools.Field(typeof(Beam), "basePiercing");

    private static readonly MethodInfo NativePhysicsRaycastMethod =
        AccessTools.Method(
            typeof(PhysicsController),
            "Raycast",
            new Type[]
            {
                typeof(Vector2),
                typeof(Vector2),
                typeof(float)
            }
        );

    private static readonly MethodInfo StellarPhysicsRaycastMethod =
        AccessTools.Method(
            typeof(LeviathanStellarConverter),
            "RaycastForStellarBeam",
            new Type[]
            {
                typeof(PhysicsController),
                typeof(Vector2),
                typeof(Vector2),
                typeof(float)
            }
        );

    // =========================================================================
    // RUNTIME STATE
    // =========================================================================

    private enum Phase
    {
        Idle,
        Charging,
        Firing,
        Recovery
    }

    private static Laser sourceLaser;
    private static Phase phase;
    private static float phaseTimer;
    private static bool inputHeld;
    private static bool forceCompleteCurrentShot;

    private sealed class RemoteState
    {
        public Phase phase;
        public float phaseTimer;
        public bool inputHeld;
        public bool forceCompleteCurrentShot;
    }

    private static readonly Dictionary<Laser, RemoteState> RemoteStates =
        new Dictionary<Laser, RemoteState>();

    // Tracks the exact native activation route currently invoking Activatable.
    // Primary sources are driven by the PrimaryWeapon type route. Special sources
    // are driven by their own activatable index (or an explicit Special type call).
    [ThreadStatic]
    private static int inputStartDepth;

    [ThreadStatic]
    private static GameShip inputStartShip;

    [ThreadStatic]
    private static bool inputStartByType;

    [ThreadStatic]
    private static Item.Type inputStartType;

    [ThreadStatic]
    private static int inputStartIndex;

    [ThreadStatic]
    private static int inputStopDepth;

    [ThreadStatic]
    private static GameShip inputStopShip;

    [ThreadStatic]
    private static bool inputStopByType;

    [ThreadStatic]
    private static bool inputStopTypeHasValue;

    [ThreadStatic]
    private static Item.Type inputStopType;

    [ThreadStatic]
    private static int inputStopIndex;

    // Scoped only to native Beam raycast methods.
    [ThreadStatic]
    private static Beam currentCastBeam;

    public struct BeamCastState
    {
        public Beam previous;
    }

    public struct BeamPiercingState
    {
        public bool changed;
        public bool originalBasePiercing;
    }

    private struct BeamColors
    {
        public Color start;
        public Color end;
    }

    private static readonly Dictionary<LineRenderer, BeamColors>
        OriginalBeamColors =
            new Dictionary<LineRenderer, BeamColors>();

    public sealed class ResolvedState
    {
        public float ChargeSeconds;
        public float PulseSeconds;
        public float DamageMultiplier;
        public float WidthMultiplier;
        public float RangeMultiplier;
        public float CritChanceMultiplier;
        public float CritChanceBonus;
        public float CritDamageMultiplier;
        public float DebuffChanceMultiplier;
        public float DebuffChanceBonus;
        public float BrightnessMultiplier;
        public bool Piercing;
        public bool SuppressPiercing;
        public int ExtraChainTargets;
        public bool Continuous;
        public bool Singularity;
        public bool DyingStar;
        public bool EventHorizon;
        public float RandomBasicStatusChance;
        public float ManifestationCooldownSeconds;

        public float SingularitySpeed;
        public float SingularityLifetime;
        public float SingularityPullRadius;
        public float SingularityPullStrength;
        public float SingularityVisualScale;
        public float SingularityVisualRadius;
        public float SingularityHaloRadius;
        public float SingularityDamageFraction;

        public float DyingStarSpeed;
        public float DyingStarFuse;
        public float DyingStarPullRadius;
        public float DyingStarPullStrength;
        public float DyingStarVisualScale;
        public float DyingStarVisualRadius;
        public float DyingStarExplosionRadius;
        public float DyingStarExplosionVisualRadius;
        public float DyingStarDamageFraction;

        public float EventHorizonTipSpeed;
        public float EventHorizonPullRadius;
        public float EventHorizonPullStrength;
        public float EventHorizonVisualRadius;
    }

    private enum GravityProjectileMode
    {
        Singularity,
        DyingStar
    }

    private sealed class GravityProjectileShot
    {
        public GravityProjectileMode mode;
        public Laser source;
        public GameShip owner;
        public Vector2 position;
        public Vector2 direction;
        public float traveled;
        public float maxTravel;
        public float travelSpeed;
        public float age;
        public float lifetime;
        public float pullRadius;
        public float pullStrength;
        public float pullFalloffExponent;
        public float visualScale;
        public float visualRadius;
        public float haloRadius;
        public float integratedDamageFraction;
        public float damageTickRate;
        public float damageTickTimer;
        public float damageFractionPerTick;
        public float explosionRadius;
        public float currentExplosionRadius;
        public float explosionExpansionSpeed;
        public bool exploding;
        public bool explosionSoundPlayed;

        public GameObject blackHoleVisual;
        public Vector3 blackHoleBaseScale = Vector3.one;
        public float blackHoleBaseVisualRadius;
        public GameObject haloVisual;
        public float haloVisualBaseRadius = 1f;
        public LineRenderer fallbackRing;
        public ExplosiveArea explosionVisual;
        public Vector3 explosionVisualBaseScale = Vector3.one;
        public float explosionVisualBaseRadius;
        public float explosionVisualTargetRadius;
        public readonly HashSet<GameShip> hitShips = new HashSet<GameShip>();
    }

    private static GravityProjectileShot gravityProjectileShot;
    private static float eventHorizonPullTipDistance;
    private static GameObject eventHorizonTipVisual;
    private static Vector3 eventHorizonTipVisualBaseScale = Vector3.one;
    private static float eventHorizonTipVisualBaseRadius;

    private static readonly FieldInfo BeamWeaponBeamScriptField =
        AccessTools.Field(typeof(BeamWeapon), "beamScript");

    private static readonly FieldInfo MirrorGameObjectField =
        AccessTools.Field(typeof(Equippable), "mirrorGameObject");

    private static readonly FieldInfo DebuffAndDirectImmuneField =
        AccessTools.Field(typeof(GameShip), "debuffAndDirectImmune");

    private static readonly MethodInfo RouteDamageMethod =
        typeof(NetCombat)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "RouteDamage" &&
                m.GetParameters().Length == 14 &&
                m.GetParameters()[2].ParameterType == typeof(DamageData[]));

    private static readonly MethodInfo ConduitRelayHitMethod =
        typeof(Conduit)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "RelayHit" &&
                m.GetParameters().Length == 6 &&
                m.GetParameters()[3].ParameterType == typeof(DamageData[]));

    private static GameObject cachedBlackHoleVisualPrefab;
    private static bool searchedBlackHoleVisualPrefab;
    private static float cachedNativeBlackHolePullPower;
    private static float cachedNativeBlackHolePullRadius;

    private static GameObject cachedHaloFieldPrefab;
    private static float cachedHaloFieldRadius = 1f;
    private static bool searchedHaloField;

    private static GameObject cachedExplosiveAreaPrefab;
    private static bool searchedExplosiveAreaPrefab;
    private static Material cachedGravityFallbackMaterial;

    private static Pilot resolvedPilot;
    private static int resolvedRank = -1;
    private static int resolvedFrame = -1;
    private static ResolvedState resolvedLocalState;

    private static readonly HashSet<GameShip> GravityTargetScratch =
        new HashSet<GameShip>();

    private static readonly Damageable.DamageType[] RandomBasicStatusTypes =
    {
        Damageable.DamageType.Cold,
        Damageable.DamageType.Corrosive,
        Damageable.DamageType.Electric,
        Damageable.DamageType.Thermal,
        Damageable.DamageType.Radiation
    };


    // =========================================================================
    // SOURCE / RANK
    // =========================================================================

    private static bool TryGetVisualRank(GameShip player, out int rank)
    {
        rank = 0;

        if (player == null || !player.IsAnyPlayerShip())
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1)
        {
            return false;
        }

        // Specialization persistence is intentionally current-player data. Use it
        // for the local owner only; remote replicas retain the legacy native-rank
        // fallback until specialization choices are explicitly network-synced.
        if (WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == player &&
            LeviathanSpecializationRuntime.IsTreeActive(
                pilot,
                LeviathanStellarConverterTree.TreeId))
        {
            rank = 1;
            return true;
        }

        rank = Mathf.Clamp(
            pilot.GetUpgradeLevel(LeviathanMod.StellarConverterUpgrade),
            0,
            MaxRank
        );

        return rank >= 1;
    }

    public static bool TryGetRank(GameShip player, out int rank)
    {
        rank = 0;

        if (player == null ||
            LeviathanMod.Controller == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != player ||
            LeviathanMod.Controller.GetActiveSectionCount(player) < 5)
        {
            return false;
        }

        return TryGetVisualRank(player, out rank);
    }

    public static Laser FindSourceLaser(GameShip player)
    {
        if (player == null || player.slots == null)
            return null;

        Laser primaryFallback = null;

        // A Laser deliberately placed in a Special slot takes priority. There are
        // very few Special-slot Lasers, so using one there is treated as explicit
        // intent to make it the Stellar Converter source.
        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];

            if (slot == null || slot.equippable == null)
                continue;

            Laser laser = slot.equippable as Laser;
            if (laser == null)
                continue;

            if (slot.type == Item.Type.Special)
                return laser;

            if (primaryFallback == null &&
                slot.type == Item.Type.PrimaryWeapon)
            {
                primaryFallback = laser;
            }
        }

        return primaryFallback;
    }

    private static bool TryGetSourceSlotType(
        Laser laser,
        out Item.Type slotType)
    {
        slotType = Item.Type.PrimaryWeapon;

        GameShip player = laser == null ? null : laser.parentShip;
        if (player == null || player.slots == null)
            return false;

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];

            if (slot != null &&
                ReferenceEquals(slot.equippable, laser))
            {
                slotType = slot.type;
                return true;
            }
        }

        return false;
    }

    private static int FindActivatableIndex(
        GameShip player,
        Laser laser)
    {
        if (player == null ||
            laser == null ||
            player.activatables == null)
        {
            return -1;
        }

        for (int i = 0; i < player.activatables.Count; i++)
        {
            if (ReferenceEquals(player.activatables[i], laser))
                return i;
        }

        return -1;
    }

    private static bool TryGetContext(
        Laser laser,
        out GameShip player,
        out int rank)
    {
        player = laser == null ? null : laser.parentShip;
        rank = 0;

        if (laser == null ||
            player == null ||
            !TryGetRank(player, out rank))
        {
            return false;
        }

        return ReferenceEquals(FindSourceLaser(player), laser);
    }

    private static bool TryGetRemoteSourceContext(
        Laser laser,
        out GameShip player,
        out int rank)
    {
        player = laser == null ? null : laser.parentShip;
        rank = 0;

        if (laser == null ||
            player == null ||
            !player.IsRemotePlayer() ||
            !TryGetVisualRank(player, out rank))
        {
            return false;
        }

        return ReferenceEquals(FindSourceLaser(player), laser);
    }

    private static bool IsFiringSource(
        BeamWeapon beamWeapon,
        out int rank)
    {
        rank = 0;

        Laser laser = beamWeapon as Laser;
        GameShip player;

        if (laser == null)
            return false;

        if (ReferenceEquals(sourceLaser, laser) &&
            phase == Phase.Firing &&
            TryGetContext(laser, out player, out rank))
        {
            return true;
        }

        RemoteState remoteState;

        return TryGetRemoteSourceContext(laser, out player, out rank) &&
            RemoteStates.TryGetValue(laser, out remoteState) &&
            remoteState.phase == Phase.Firing;
    }


    private static bool IsSpecializationProfile(GameShip player)
    {
        if (player == null || WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != player)
        {
            return false;
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        return pilot != null &&
            LeviathanSpecializationRuntime.IsTreeActive(
                pilot,
                LeviathanStellarConverterTree.TreeId);
    }

    private static float ApplyKnob(
        Pilot pilot,
        LeviathanSpecializationKnob knob,
        float baseValue)
    {
        return pilot == null
            ? baseValue
            : LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                knob,
                baseValue);
    }

    private static ResolvedState GetResolvedState(GameShip player, int rank)
    {
        if (!IsSpecializationProfile(player))
            return BuildLegacyState(rank);

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        int frame = Time.frameCount;
        if (resolvedLocalState != null &&
            resolvedFrame == frame &&
            resolvedRank == rank &&
            ReferenceEquals(resolvedPilot, pilot))
        {
            return resolvedLocalState;
        }

        ResolvedState state = new ResolvedState();
        state.ChargeSeconds = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.ChargeTime,
            LeviathanStellarConverterTuning.BaselineChargeSeconds));
        state.PulseSeconds = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.PulseDuration,
            LeviathanStellarConverterTuning.BaselinePulseSeconds));
        state.DamageMultiplier = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.DamageMultiplier,
            LeviathanStellarConverterTuning.BaselineDamageMultiplier));
        state.WidthMultiplier = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.WidthMultiplier,
            LeviathanStellarConverterTuning.BaselineWidthMultiplier));
        state.RangeMultiplier = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.RangeMultiplier,
            LeviathanStellarConverterTuning.BaselineRangeMultiplier));
        state.RangeMultiplier *= Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.FinalRangePercent));

        state.CritChanceMultiplier =
            LeviathanStellarConverterTuning.BaselineCritChanceMultiplier;
        state.CritChanceBonus = LeviathanSpecializationRuntime.GetKnobFlat(
            pilot,
            Knobs.CritChance);
        state.CritDamageMultiplier =
            LeviathanStellarConverterTuning.BaselineCritDamageMultiplier;
        state.DebuffChanceMultiplier =
            LeviathanStellarConverterTuning.BaselineDebuffChanceMultiplier;
        state.DebuffChanceBonus = LeviathanSpecializationRuntime.GetKnobFlat(
            pilot,
            Knobs.StatusChance);
        state.BrightnessMultiplier =
            LeviathanStellarConverterTuning.BaselineBrightnessMultiplier;
        state.ExtraChainTargets = Mathf.Max(
            0,
            Mathf.RoundToInt(LeviathanSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.ChainTargets)));

        state.Continuous = LeviathanSpecializationRuntime.HasFlag(
            pilot,
            Flags.Continuous);
        bool arcCascade = LeviathanSpecializationRuntime.HasFlag(
            pilot,
            Flags.ArcCascade);
        bool fractalCascade = LeviathanSpecializationRuntime.HasFlag(
            pilot,
            Flags.FractalCascade);
        state.Singularity = LeviathanSpecializationRuntime.HasFlag(
            pilot,
            Flags.Singularity);
        state.DyingStar = LeviathanSpecializationRuntime.HasFlag(
            pilot,
            Flags.DyingStar);
        state.EventHorizon = LeviathanSpecializationRuntime.HasFlag(
            pilot,
            Flags.EventHorizon);

        state.ManifestationCooldownSeconds = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.ManifestationCooldown,
            LeviathanStellarConverterTuning.ManifestationCooldownSeconds));

        state.SingularitySpeed = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.SingularitySpeed,
            LeviathanStellarConverterTuning.SingularityTravelSpeedMetersPerSecond));
        state.SingularityLifetime = Mathf.Max(0.05f, ApplyKnob(
            pilot, Knobs.SingularityLifetime,
            LeviathanStellarConverterTuning.SingularityLifetimeSeconds));
        state.SingularityPullRadius = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.SingularityPullRadius,
            LeviathanStellarConverterTuning.SingularityPullRadiusMeters));
        state.SingularityPullStrength = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.SingularityPullStrength,
            LeviathanStellarConverterTuning.SingularityPullStrength));
        state.SingularityVisualScale = Mathf.Max(0.01f, ApplyKnob(
            pilot, Knobs.SingularityVisualScale,
            LeviathanStellarConverterTuning.SingularityVisualScale));
        state.SingularityVisualRadius = Mathf.Clamp(ApplyKnob(
            pilot, Knobs.SingularityVisualRadius,
            LeviathanStellarConverterTuning.SingularityVisualRadiusMeters),
            0f,
            LeviathanStellarConverterTuning.MaxManifestationVisualRadiusMeters);
        state.SingularityHaloRadius = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.SingularityHaloRadius,
            LeviathanStellarConverterTuning.SingularityHaloRadiusMeters));
        state.SingularityDamageFraction = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.SingularityDamage,
            LeviathanStellarConverterTuning.SingularityDamageFraction));

        state.DyingStarSpeed = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.DyingStarSpeed,
            LeviathanStellarConverterTuning.DyingStarTravelSpeedMetersPerSecond));
        state.DyingStarFuse = Mathf.Max(0.05f, ApplyKnob(
            pilot, Knobs.DyingStarFuse,
            LeviathanStellarConverterTuning.DyingStarFuseSeconds));
        state.DyingStarPullRadius = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.DyingStarPullRadius,
            LeviathanStellarConverterTuning.DyingStarPullRadiusMeters));
        state.DyingStarPullStrength = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.DyingStarPullStrength,
            LeviathanStellarConverterTuning.DyingStarPullStrength));
        state.DyingStarVisualScale = Mathf.Max(0.01f, ApplyKnob(
            pilot, Knobs.DyingStarVisualScale,
            LeviathanStellarConverterTuning.DyingStarVisualScale));
        state.DyingStarVisualRadius = Mathf.Clamp(ApplyKnob(
            pilot, Knobs.DyingStarVisualRadius,
            LeviathanStellarConverterTuning.DyingStarVisualRadiusMeters),
            0f,
            LeviathanStellarConverterTuning.MaxManifestationVisualRadiusMeters);
        state.DyingStarExplosionRadius = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.DyingStarExplosionRadius,
            LeviathanStellarConverterTuning.DyingStarExplosionRadiusMeters));
        state.DyingStarExplosionVisualRadius = Mathf.Clamp(ApplyKnob(
            pilot, Knobs.DyingStarExplosionVisualRadius,
            LeviathanStellarConverterTuning.DyingStarExplosionVisualRadiusMeters),
            0f,
            LeviathanStellarConverterTuning.MaxManifestationVisualRadiusMeters);
        state.DyingStarDamageFraction = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.DyingStarDamage,
            LeviathanStellarConverterTuning.DyingStarDamageFraction));

        state.EventHorizonTipSpeed = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.EventHorizonTipSpeed,
            LeviathanStellarConverterTuning.EventHorizonPullTipSpeedMetersPerSecond));
        state.EventHorizonPullRadius = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.EventHorizonPullRadius,
            LeviathanStellarConverterTuning.EventHorizonPullRadiusMeters));
        state.EventHorizonPullStrength = Mathf.Max(0f, ApplyKnob(
            pilot, Knobs.EventHorizonPullStrength,
            LeviathanStellarConverterTuning.EventHorizonPullStrength));
        state.EventHorizonVisualRadius = Mathf.Clamp(ApplyKnob(
            pilot, Knobs.EventHorizonVisualRadius,
            LeviathanStellarConverterTuning.EventHorizonVisualRadiusMeters),
            0f,
            LeviathanStellarConverterTuning.MaxManifestationVisualRadiusMeters);

        state.Piercing = LeviathanSpecializationRuntime.GetKnobFlat(
            pilot,
            Knobs.Piercing) >= 0.5f;
        state.SuppressPiercing = state.Continuous || arcCascade || fractalCascade;

        if (state.Continuous)
        {
            state.ChargeSeconds = 0f;
            state.DamageMultiplier =
                LeviathanStellarConverterTuning.ContinuousDamageMultiplier;
            state.WidthMultiplier =
                LeviathanStellarConverterTuning.ContinuousWidthMultiplier;
            state.CritChanceMultiplier = 1f;
            state.Piercing = false;
            state.SuppressPiercing = true;
            state.RandomBasicStatusChance =
                LeviathanStellarConverterTuning.ContinuousRandomStatusChance;
        }

        resolvedPilot = pilot;
        resolvedRank = rank;
        resolvedFrame = frame;
        resolvedLocalState = state;
        return state;
    }

    private static ResolvedState BuildLegacyState(int rank)
    {
        ResolvedState state = new ResolvedState();
        state.ChargeSeconds = GetRankValue(ChargeDurationByRank, rank);
        state.PulseSeconds = GetRankValue(OutputDurationByRank, rank);
        state.DamageMultiplier = GetRankValue(DamageMultiplierByRank, rank);
        state.WidthMultiplier = GetRankValue(WidthMultiplierByRank, rank);
        state.RangeMultiplier = GetRankValue(LengthMultiplierByRank, rank);
        state.CritChanceMultiplier = GetRankValue(
            CritChanceMultiplierByRank,
            rank);
        state.CritDamageMultiplier = GetRankValue(
            CritDamageMultiplierByRank,
            rank);
        state.DebuffChanceMultiplier = GetRankValue(
            DebuffChanceMultiplierByRank,
            rank);
        state.BrightnessMultiplier = GetRankValue(
            BrightnessMultiplierByRank,
            rank);
        state.Piercing = PiercingByRank[
            Mathf.Clamp(rank - 1, 0, PiercingByRank.Length - 1)];
        state.ManifestationCooldownSeconds = 0f;
        return state;
    }

    private static ResolvedState GetResolvedState(Laser laser, int rank)
    {
        return GetResolvedState(laser == null ? null : laser.parentShip, rank);
    }

    private static bool IsManifestation(ResolvedState resolved)
    {
        return resolved != null &&
            (resolved.Singularity ||
             resolved.DyingStar ||
             resolved.EventHorizon);
    }

    // =========================================================================
    // INPUT / STATE MACHINE
    // =========================================================================

    public static void EnterInputStartByType(
        GameShip ship,
        Item.Type type)
    {
        if (inputStartDepth == 0)
        {
            inputStartShip = ship;
            inputStartByType = true;
            inputStartType = type;
            inputStartIndex = -1;
        }

        inputStartDepth++;
    }

    public static void EnterInputStartByIndex(
        GameShip ship,
        int index)
    {
        if (inputStartDepth == 0)
        {
            inputStartShip = ship;
            inputStartByType = false;
            inputStartIndex = index;
        }

        inputStartDepth++;
    }

    public static void ExitInputStart()
    {
        if (inputStartDepth <= 0)
            return;

        inputStartDepth--;

        if (inputStartDepth == 0)
        {
            inputStartShip = null;
            inputStartByType = false;
            inputStartIndex = -1;
        }
    }

    public static void EnterInputStopByType(
        GameShip ship,
        Item.Type? type)
    {
        if (inputStopDepth == 0)
        {
            inputStopShip = ship;
            inputStopByType = true;
            inputStopTypeHasValue = type.HasValue;
            inputStopType = type.GetValueOrDefault();
            inputStopIndex = -1;
        }

        inputStopDepth++;
    }

    public static void EnterInputStopByIndex(
        GameShip ship,
        int index)
    {
        if (inputStopDepth == 0)
        {
            inputStopShip = ship;
            inputStopByType = false;
            inputStopTypeHasValue = false;
            inputStopIndex = index;
        }

        inputStopDepth++;
    }

    public static void ExitInputStop()
    {
        if (inputStopDepth <= 0)
            return;

        inputStopDepth--;

        if (inputStopDepth == 0)
        {
            inputStopShip = null;
            inputStopByType = false;
            inputStopTypeHasValue = false;
            inputStopIndex = -1;
        }
    }

    private static bool IsStartInputForSource(Laser laser)
    {
        if (laser == null ||
            inputStartDepth <= 0 ||
            laser.parentShip != inputStartShip)
        {
            return false;
        }

        Item.Type slotType;
        if (!TryGetSourceSlotType(laser, out slotType))
            return false;

        if (inputStartByType)
            return inputStartType == slotType;

        int sourceIndex = FindActivatableIndex(inputStartShip, laser);
        return sourceIndex >= 0 && inputStartIndex == sourceIndex;
    }

    private static bool IsStopInputForSource(Laser laser)
    {
        if (laser == null ||
            inputStopDepth <= 0 ||
            laser.parentShip != inputStopShip)
        {
            return false;
        }

        if (inputStopByType)
        {
            // StopActivating(null) is the native global shutdown path and must
            // reach either kind of Stellar source.
            if (!inputStopTypeHasValue)
                return true;

            Item.Type slotType;
            return TryGetSourceSlotType(laser, out slotType) &&
                inputStopType == slotType;
        }

        int sourceIndex = FindActivatableIndex(inputStopShip, laser);
        return sourceIndex >= 0 && inputStopIndex == sourceIndex;
    }

    public static bool InterceptNativeActivate(Activatable activatable)
    {
        if (inputStartDepth <= 0)
            return false;

        Laser laser = activatable as Laser;
        GameShip player;
        int rank;

        if (laser == null || laser.parentShip != inputStartShip)
            return false;

        bool localSource = TryGetContext(laser, out player, out rank);
        bool remoteSource = false;

        if (!localSource)
            remoteSource = TryGetRemoteSourceContext(laser, out player, out rank);

        if (!localSource && !remoteSource)
            return false;

        // Linked/native activation paths can traverse into unrelated slots. Only
        // the selected source's own trigger may drive Stellar's custom state.
        if (!IsStartInputForSource(laser))
        {
            if (localSource)
                SetNativeActive(laser, phase == Phase.Firing);
            else
            {
                RemoteState existing = GetRemoteState(laser);
                SetNativeActive(laser, existing.phase == Phase.Firing);
            }

            return true;
        }

        if (localSource)
        {
            SelectSource(laser);
            inputHeld = true;
            ResolvedState resolved = GetResolvedState(player, rank);

            if (resolved.Continuous)
            {
                phase = Phase.Firing;
                phaseTimer = 0f;
                SetNativeActive(laser, laser.CanActivate());
                return true;
            }

            if (phase == Phase.Idle)
            {
                phase = Phase.Charging;
                phaseTimer = 0f;
            }

            SetNativeActive(laser, false);
            return true;
        }

        RemoteState state = GetRemoteState(laser);
        state.inputHeld = true;

        if (state.phase == Phase.Idle)
        {
            state.phase = Phase.Charging;
            state.phaseTimer = 0f;
        }

        SetNativeActive(laser, false);
        return true;
    }

    public static bool InterceptNativeDeactivate(Activatable activatable)
    {
        if (inputStopDepth <= 0)
            return false;

        Laser laser = activatable as Laser;
        GameShip player;
        int rank;

        if (laser == null || laser.parentShip != inputStopShip)
            return false;

        bool localSource = TryGetContext(laser, out player, out rank);
        bool remoteSource = false;

        if (!localSource)
            remoteSource = TryGetRemoteSourceContext(laser, out player, out rank);

        if (!localSource && !remoteSource)
            return false;

        // Suppress deactivation cross-talk from linked/unrelated activatables.
        // Global StopActivating(null) is still accepted for CC/offline shutdown.
        if (!IsStopInputForSource(laser))
        {
            if (localSource)
                SetNativeActive(laser, phase == Phase.Firing);
            else
            {
                RemoteState existing = GetRemoteState(laser);
                SetNativeActive(laser, existing.phase == Phase.Firing);
            }

            return true;
        }

        if (localSource)
        {
            SelectSource(laser);
            ResolvedState resolved = GetResolvedState(player, rank);

            if (resolved.Continuous)
            {
                inputHeld = false;
                forceCompleteCurrentShot = false;
                phase = Phase.Idle;
                phaseTimer = 0f;
                SetNativeActive(laser, false);
                return true;
            }

            if (IsControlInterrupted(player) && phase != Phase.Idle)
            {
                inputHeld = false;
                forceCompleteCurrentShot = true;
                SetNativeActive(laser, phase == Phase.Firing);
                return true;
            }

            inputHeld = false;

            if (phase == Phase.Charging && !forceCompleteCurrentShot)
            {
                phase = Phase.Idle;
                phaseTimer = 0f;
                SetNativeActive(laser, false);
            }

            return true;
        }

        RemoteState state = GetRemoteState(laser);

        if (IsControlInterrupted(player) && state.phase != Phase.Idle)
        {
            state.inputHeld = false;
            state.forceCompleteCurrentShot = true;
            SetNativeActive(laser, state.phase == Phase.Firing);
            return true;
        }

        state.inputHeld = false;

        if (state.phase == Phase.Charging &&
            !state.forceCompleteCurrentShot)
        {
            state.phase = Phase.Idle;
            state.phaseTimer = 0f;
            SetNativeActive(laser, false);
        }

        return true;
    }

    public static void PrepareFixedUpdate(Laser laser)
    {
        GameShip player;
        int rank;

        if (TryGetContext(laser, out player, out rank))
        {
            if (IsTerminalStopped(player))
            {
                if (ReferenceEquals(sourceLaser, laser))
                    ResetLocalSource();
                else
                    SetNativeActive(laser, false);

                return;
            }

            SelectSource(laser);
            ResolvedState resolved = GetResolvedState(player, rank);
            UpdateGravityProjectileShot();

            if (resolved.Continuous)
            {
                phase = inputHeld && laser.CanActivate()
                    ? Phase.Firing
                    : Phase.Idle;
                phaseTimer = 0f;
                forceCompleteCurrentShot = false;
                SetNativeActive(laser, phase == Phase.Firing);
                return;
            }

            if (phase == Phase.Charging &&
                !inputHeld &&
                !forceCompleteCurrentShot)
            {
                phase = Phase.Idle;
                phaseTimer = 0f;
            }

            SetNativeActive(laser, phase == Phase.Firing);
            return;
        }

        if (TryGetRemoteSourceContext(laser, out player, out rank))
        {
            RemoteState state = GetRemoteState(laser);

            if (IsTerminalStopped(player))
            {
                ResetRemoteSource(laser);
                return;
            }

            if (state.phase == Phase.Charging &&
                !state.inputHeld &&
                !state.forceCompleteCurrentShot)
            {
                state.phase = Phase.Idle;
                state.phaseTimer = 0f;
            }

            SetNativeActive(laser, state.phase == Phase.Firing);
            return;
        }

        if (ReferenceEquals(sourceLaser, laser))
            ResetLocalSource();

        RemoteStates.Remove(laser);
    }

    public static void CompleteFixedUpdate(Laser laser)
    {
        GameShip player;
        int rank;

        if (ReferenceEquals(sourceLaser, laser) &&
            TryGetContext(laser, out player, out rank))
        {
            AdvanceLocalState(laser, rank);
            return;
        }

        if (TryGetRemoteSourceContext(laser, out player, out rank))
        {
            RemoteState state;

            if (RemoteStates.TryGetValue(laser, out state))
                AdvanceRemoteState(laser, state, rank);
        }
    }

    private static void AdvanceLocalState(Laser laser, int rank)
    {
        GameShip player = laser == null ? null : laser.parentShip;
        ResolvedState resolved = GetResolvedState(player, rank);

        if (resolved.Continuous)
            return;

        if (phase == Phase.Charging)
        {
            if (!inputHeld && !forceCompleteCurrentShot)
            {
                phase = Phase.Idle;
                phaseTimer = 0f;
                return;
            }

            if (!laser.CanActivate())
            {
                phaseTimer = 0f;
                return;
            }

            phaseTimer += Time.fixedDeltaTime;

            if (phaseTimer >= resolved.ChargeSeconds)
            {
                phaseTimer = 0f;

                if (resolved.Singularity)
                {
                    FireGravityProjectile(
                        laser, player, resolved, GravityProjectileMode.Singularity);
                    phase = Phase.Recovery;
                    SetNativeActive(laser, false);
                }
                else if (resolved.DyingStar)
                {
                    FireGravityProjectile(
                        laser, player, resolved, GravityProjectileMode.DyingStar);
                    phase = Phase.Recovery;
                    SetNativeActive(laser, false);
                }
                else
                {
                    phase = Phase.Firing;
                    eventHorizonPullTipDistance = 0f;
                }
            }

            return;
        }

        if (phase == Phase.Recovery)
        {
            phaseTimer += Time.fixedDeltaTime;
            float recoverySeconds = IsManifestation(resolved)
                ? resolved.ManifestationCooldownSeconds
                : resolved.PulseSeconds;

            if (phaseTimer >= recoverySeconds)
            {
                bool queueNext = inputHeld && !forceCompleteCurrentShot;
                phase = queueNext ? Phase.Charging : Phase.Idle;
                phaseTimer = 0f;
                forceCompleteCurrentShot = false;
            }
            return;
        }

        if (phase != Phase.Firing)
            return;

        if (!laser.CanActivate())
        {
            SetNativeActive(laser, false);

            if (resolved.EventHorizon)
                phase = Phase.Recovery;
            else
                phase = inputHeld ? Phase.Charging : Phase.Idle;

            phaseTimer = 0f;
            forceCompleteCurrentShot = false;
            eventHorizonPullTipDistance = 0f;
            DestroyEventHorizonTipVisual();
            return;
        }

        if (resolved.EventHorizon)
            UpdateEventHorizonBeamPull(laser, player, resolved);

        phaseTimer += Time.fixedDeltaTime;

        if (phaseTimer >= resolved.PulseSeconds)
        {
            if (resolved.EventHorizon)
            {
                phase = Phase.Recovery;
            }
            else
            {
                bool queueNext = inputHeld && !forceCompleteCurrentShot;
                phase = queueNext ? Phase.Charging : Phase.Idle;
            }

            phaseTimer = 0f;
            forceCompleteCurrentShot = false;
            eventHorizonPullTipDistance = 0f;
            DestroyEventHorizonTipVisual();
            SetNativeActive(laser, false);
        }
    }

    private static void AdvanceRemoteState(
        Laser laser,
        RemoteState state,
        int rank)
    {
        if (state.phase == Phase.Charging)
        {
            if (!state.inputHeld && !state.forceCompleteCurrentShot)
            {
                state.phase = Phase.Idle;
                state.phaseTimer = 0f;
                return;
            }

            if (!laser.CanActivate())
            {
                state.phaseTimer = 0f;
                return;
            }

            state.phaseTimer += Time.fixedDeltaTime;

            if (state.phaseTimer >=
                GetRankValue(ChargeDurationByRank, rank))
            {
                state.phase = Phase.Firing;
                state.phaseTimer = 0f;
            }

            return;
        }

        if (state.phase != Phase.Firing)
            return;

        if (!laser.CanActivate())
        {
            SetNativeActive(laser, false);
            state.phase = state.inputHeld ? Phase.Charging : Phase.Idle;
            state.phaseTimer = 0f;
            state.forceCompleteCurrentShot = false;
            return;
        }

        state.phaseTimer += Time.fixedDeltaTime;

        if (state.phaseTimer >=
            GetRankValue(OutputDurationByRank, rank))
        {
            bool queueNext =
                state.inputHeld &&
                !state.forceCompleteCurrentShot;

            state.phase = queueNext ? Phase.Charging : Phase.Idle;
            state.phaseTimer = 0f;
            state.forceCompleteCurrentShot = false;
        }
    }

    private static void SelectSource(Laser laser)
    {
        if (ReferenceEquals(sourceLaser, laser))
            return;

        DestroyGravityProjectileShot();
        DestroyEventHorizonTipVisual();

        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = laser;
        eventHorizonPullTipDistance = 0f;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
        forceCompleteCurrentShot = false;
    }

    private static RemoteState GetRemoteState(Laser laser)
    {
        RemoteState state;

        if (!RemoteStates.TryGetValue(laser, out state))
        {
            state = new RemoteState();
            RemoteStates[laser] = state;
        }

        return state;
    }

    private static void ResetLocalSource()
    {
        DestroyGravityProjectileShot();
        DestroyEventHorizonTipVisual();

        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = null;
        eventHorizonPullTipDistance = 0f;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
        forceCompleteCurrentShot = false;
    }

    private static void ResetRemoteSource(Laser laser)
    {
        if (laser == null)
            return;

        SetNativeActive(laser, false);
        RemoteStates.Remove(laser);
    }

    public static void ResetSource(Laser laser)
    {
        if (ReferenceEquals(sourceLaser, laser))
            ResetLocalSource();

        ResetRemoteSource(laser);
    }

    public static void Reset()
    {
        ResetLocalSource();

        foreach (KeyValuePair<Laser, RemoteState> pair in RemoteStates)
        {
            if (pair.Key != null)
                SetNativeActive(pair.Key, false);
        }

        RemoteStates.Clear();
        OriginalBeamColors.Clear();

        if (cachedGravityFallbackMaterial != null)
        {
            UnityEngine.Object.Destroy(cachedGravityFallbackMaterial);
            cachedGravityFallbackMaterial = null;
        }
    }

    // Stellar deliberately toggles the native active flag across charge/output
    // phases, so publish its actual input-held state on the network bit belonging
    // to the selected source. Primary uses shared bit 0; Special uses its native
    // activatable-index bit. Remote clients can then reconstruct the same cycle.
    public static void ScaleNetworkSourceInput(
        GameShip ship,
        ref uint activeSlots)
    {
        Laser laser = FindSourceLaser(ship);
        GameShip player;
        int rank;

        if (laser == null ||
            !ReferenceEquals(sourceLaser, laser) ||
            !TryGetContext(laser, out player, out rank))
        {
            return;
        }

        Item.Type slotType;
        if (!TryGetSourceSlotType(laser, out slotType))
            return;

        uint bit;

        if (slotType == Item.Type.PrimaryWeapon)
        {
            bit = 1U;
        }
        else if (slotType == Item.Type.Special)
        {
            int activatableIndex = FindActivatableIndex(ship, laser);
            if (activatableIndex < 0 || activatableIndex >= 31)
                return;

            bit = 1U << (activatableIndex + 1);
        }
        else
        {
            return;
        }

        if (inputHeld)
            activeSlots |= bit;
        else
            activeSlots &= ~bit;
    }

    private static void SetNativeActive(Laser laser, bool active)
    {
        if (laser == null || ActivatableActiveField == null)
            return;

        ActivatableActiveField.SetValue(laser, active);
    }

    private static bool IsTerminalStopped(GameShip player)
    {
        return player == null || player.health <= 0f;
    }

    private static bool IsControlInterrupted(GameShip player)
    {
        return player != null &&
            (player.IsDisabled() || player.IsWeaponsOffline());
    }

    // Native BeamWeapon deactivation fades the rendered Beam over several frames,
    // and BeamWeapon.FixedUpdate still calls Beam.DoDamageTick during that fade.
    // Stellar Converter's output window is exact: the selected source may deal
    // beam damage only while the converter phase itself is Firing.
    public static bool AllowBeamDamageTick(Beam beam)
    {
        if (beam == null || BeamParentWeaponField == null)
            return true;

        Laser laser = BeamParentWeaponField.GetValue(beam) as Laser;
        if (laser == null)
            return true;

        GameShip player;
        int rank;

        if (TryGetContext(laser, out player, out rank))
        {
            return ReferenceEquals(sourceLaser, laser) &&
                phase == Phase.Firing &&
                !IsTerminalStopped(player);
        }

        // Remote replicas run the same Converter phase machine locally. This
        // avoids the vanilla Primary active-slot bit latching Stellar on whenever
        // any other Primary weapon remains held.
        if (TryGetRemoteSourceContext(laser, out player, out rank))
        {
            RemoteState state;

            return RemoteStates.TryGetValue(laser, out state) &&
                state.phase == Phase.Firing &&
                !IsTerminalStopped(player);
        }

        return true;
    }

    // =========================================================================
    // NATIVE STAT SCALING
    // =========================================================================

    public static void ScaleDamagePacket(
        BeamWeapon beamWeapon,
        ref DamageData[] damageData)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank) ||
            damageData == null ||
            damageData.Length == 0)
        {
            return;
        }

        Laser laser = beamWeapon as Laser;
        float multiplier = GetResolvedState(laser, rank).DamageMultiplier;

        if (Mathf.Approximately(multiplier, 1f))
            return;

        DamageData[] scaled = new DamageData[damageData.Length];

        for (int i = 0; i < damageData.Length; i++)
        {
            DamageData datum = damageData[i];
            datum.damage *= multiplier;
            datum.dps *= multiplier;
            scaled[i] = datum;
        }

        damageData = scaled;
    }

    public static void ScaleCritChance(BeamWeapon beamWeapon, ref float value)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        Laser laser = beamWeapon as Laser;
        ResolvedState resolved = GetResolvedState(laser, rank);
        value = Mathf.Clamp01(
            value * resolved.CritChanceMultiplier + resolved.CritChanceBonus
        );
    }

    public static void ScaleCritModifier(BeamWeapon beamWeapon, ref float value)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        Laser laser = beamWeapon as Laser;
        value *= GetResolvedState(laser, rank).CritDamageMultiplier;
    }

    public static void ScaleDebuffChance(BeamWeapon beamWeapon, ref float value)
    {
        int rank;

        if (!IsFiringSource(beamWeapon, out rank))
            return;

        Laser laser = beamWeapon as Laser;
        ResolvedState resolved = GetResolvedState(laser, rank);
        value = Mathf.Clamp01(
            value * resolved.DebuffChanceMultiplier + resolved.DebuffChanceBonus
        );
    }

    // Native Beam.DoDamageTick recomputes piercing every damage tick as
    // basePiercing || GetJuggernautPiercing(). OR Stellar's rank flag into that
    // native decision so vanilla piercing damage, hit ordering, visuals, chaining
    // and attribution remain in control.
    public static BeamPiercingState PrepareBeamPiercing(Beam beam)
    {
        BeamPiercingState state = new BeamPiercingState();
        if (beam == null || BeamBasePiercingField == null)
            return state;

        Laser laser;
        int rank;
        if (!TryGetBeamContext(beam, out laser, out rank) ||
            !GetResolvedState(laser, rank).SuppressPiercing)
        {
            return state;
        }

        object raw = BeamBasePiercingField.GetValue(beam);
        if (!(raw is bool))
            return state;

        state.originalBasePiercing = (bool)raw;
        if (!state.originalBasePiercing)
            return state;

        state.changed = true;
        BeamBasePiercingField.SetValue(beam, false);
        return state;
    }

    public static void RestoreBeamPiercing(
        Beam beam,
        BeamPiercingState state)
    {
        if (state.changed && beam != null && BeamBasePiercingField != null)
        {
            BeamBasePiercingField.SetValue(
                beam,
                state.originalBasePiercing);
        }
    }

    public static void ScalePiercing(Beam beam, ref bool value)
    {
        Laser laser;
        int rank;

        if (!TryGetBeamContext(beam, out laser, out rank))
            return;

        ResolvedState resolved = GetResolvedState(laser, rank);
        if (resolved.SuppressPiercing)
        {
            value = false;
            return;
        }

        if (resolved.Piercing)
            value = true;
    }

    public static void ScaleChainTargets(
        BeamWeapon beamWeapon,
        ref int value)
    {
        Laser laser = beamWeapon as Laser;
        if (laser == null)
            return;

        GameShip player;
        int rank;
        bool valid = TryGetContext(laser, out player, out rank);
        if (!valid)
            valid = TryGetRemoteSourceContext(laser, out player, out rank);
        if (!valid)
            return;

        ResolvedState resolved = GetResolvedState(laser, rank);
        if (resolved.ExtraChainTargets > 0)
            value = resolved.ExtraChainTargets;
    }

    // Native Striker range is injected through Equippable.ApplyModifier(MaxRange).
    // Use that exact stat boundary for only the selected source Laser, so Beam's
    // own raycasts, chaining and rendering all consume the scaled MaxRange.
    public static void ScaleMaxRange(
        Equippable equippable,
        Modifier.Type modifierType,
        bool includeParentShip,
        ref float value)
    {
        if (modifierType != Modifier.Type.MaxRange || !includeParentShip)
            return;

        Laser laser = equippable as Laser;
        GameShip player;
        int rank;

        if (laser == null)
            return;

        bool valid = TryGetContext(laser, out player, out rank);

        if (!valid)
            valid = TryGetRemoteSourceContext(laser, out player, out rank);

        if (!valid)
            return;

        value *= GetResolvedState(laser, rank).RangeMultiplier;
    }

    // =========================================================================
    // BEAM WIDTH / CAST
    // =========================================================================

    public static BeamCastState BeginBeamCast(Beam beam)
    {
        BeamCastState state = new BeamCastState
        {
            previous = currentCastBeam
        };

        currentCastBeam = beam;
        return state;
    }

    public static void EndBeamCast(BeamCastState state)
    {
        currentCastBeam = state.previous;
    }

    // Replaces the two native Beam raycasts only.
    public static RaycastHit2D[] RaycastForStellarBeam(
        PhysicsController physics,
        Vector2 origin,
        Vector2 direction,
        float maxRange)
    {
        if (physics == null)
            return new RaycastHit2D[0];

        float radius;

        if (TryGetMechanicalBeamRadius(currentCastBeam, out radius) &&
            radius > 0f)
        {
            return physics.CircleCast(origin, radius, direction, maxRange);
        }

        return physics.Raycast(origin, direction, maxRange);
    }

    public static void ScaleVisualWidth(Beam beam)
    {
        LineRenderer line =
            BeamLineRendererField == null
                ? null
                : BeamLineRendererField.GetValue(beam)
                    as LineRenderer;

        LineRenderer end =
            BeamEndLineRendererField == null
                ? null
                : BeamEndLineRendererField.GetValue(beam)
                    as LineRenderer;

        Laser laser;
        int rank;

        if (!TryGetBeamContext(beam, out laser, out rank))
        {
            RestoreBeamBrightness(line);
            RestoreBeamBrightness(end);
            RestoreBeamBrightness(beam.additionalBeam);
            return;
        }

        ResolvedState resolved = GetResolvedState(laser, rank);
        float multiplier = resolved.WidthMultiplier;
        float brightness = resolved.BrightnessMultiplier;

        float visualState = GetVisualState(beam);

        float maxWidth =
            GetFloat(BeamMaxWidthField, beam);

        float maxEndWidth =
            GetFloat(BeamMaxEndWidthField, beam);

        float additionalMaxWidth =
            GetFloat(BeamAdditionalMaxWidthField, beam);

        if (line != null)
        {
            line.widthMultiplier =
                visualState * maxWidth * multiplier;
        }

        if (end != null)
        {
            end.widthMultiplier =
                visualState * maxEndWidth * multiplier;
        }

        if (beam.additionalBeam != null)
        {
            beam.additionalBeam.widthMultiplier =
                visualState * additionalMaxWidth * multiplier;
        }

        ScaleBeamBrightness(line, brightness);
        ScaleBeamBrightness(end, brightness);
        ScaleBeamBrightness(
            beam.additionalBeam,
            brightness
        );
    }

    private static void ScaleBeamBrightness(
    LineRenderer renderer,
    float multiplier)
    {
        if (renderer == null)
            return;

        BeamColors original;

        if (!OriginalBeamColors.TryGetValue(renderer, out original))
        {
            original = new BeamColors
            {
                start = renderer.startColor,
                end = renderer.endColor
            };

            OriginalBeamColors[renderer] = original;
        }

        renderer.startColor = new Color(
            original.start.r * multiplier,
            original.start.g * multiplier,
            original.start.b * multiplier,
            original.start.a
        );

        renderer.endColor = new Color(
            original.end.r * multiplier,
            original.end.g * multiplier,
            original.end.b * multiplier,
            original.end.a
        );
    }

    private static void RestoreBeamBrightness(LineRenderer renderer)
    {
        if (renderer == null)
            return;

        BeamColors original;

        if (!OriginalBeamColors.TryGetValue(renderer, out original))
            return;

        renderer.startColor = original.start;
        renderer.endColor = original.end;
    }


    private static bool TryGetMechanicalBeamRadius(
        Beam beam,
        out float radius)
    {
        radius = 0f;

        Laser laser;
        int rank;

        if (!TryGetBeamContext(beam, out laser, out rank))
            return false;

        float baseWidth = GetFloat(BeamMaxWidthField, beam);

        if (beam.additionalBeam != null)
        {
            baseWidth = Mathf.Max(
                baseWidth,
                GetFloat(BeamAdditionalMaxWidthField, beam)
            );
        }

        radius = 0.5f *
            baseWidth *
            GetVisualState(beam) *
            GetResolvedState(laser, rank).WidthMultiplier *
            HitboxWidthMultiplier;

        return radius > 0f;
    }

    private static bool TryGetBeamContext(
        Beam beam,
        out Laser laser,
        out int rank)
    {
        laser = null;
        rank = 0;

        if (beam == null || BeamParentWeaponField == null)
            return false;

        laser = BeamParentWeaponField.GetValue(beam) as Laser;

        return laser != null &&
            IsFiringSource(laser, out rank);
    }

    private static float GetVisualState(Beam beam)
    {
        float state = Mathf.Clamp01(beam.GetCurrentState());

        bool snapChange =
            BeamSnapChangeField != null &&
            (bool)BeamSnapChangeField.GetValue(beam);

        if (snapChange && state > 0f && state < 1f)
            state = 0.1f;

        return state;
    }

    private static float GetFloat(FieldInfo field, object target)
    {
        if (field == null || target == null)
            return 0f;

        object value = field.GetValue(target);
        return value is float ? (float)value : 0f;
    }

    public static IEnumerable<CodeInstruction> ReplaceNativeBeamRaycasts(
        IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            MethodInfo called = instruction.operand as MethodInfo;

            if (called != null &&
                NativePhysicsRaycastMethod != null &&
                StellarPhysicsRaycastMethod != null &&
                called.Module == NativePhysicsRaycastMethod.Module &&
                called.MetadataToken == NativePhysicsRaycastMethod.MetadataToken)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = StellarPhysicsRaycastMethod;
            }

            yield return instruction;
        }
    }


    private static bool TryGetLaserPose(
        Laser laser,
        out Vector2 origin,
        out Vector2 direction)
    {
        origin = Vector2.zero;
        direction = Vector2.right;

        if (laser == null)
            return false;

        Beam beam = BeamWeaponBeamScriptField == null
            ? null
            : BeamWeaponBeamScriptField.GetValue(laser) as Beam;

        Transform sourceTransform = null;

        if (beam != null)
            sourceTransform = beam.transform;
        else if (laser.gameObject != null)
            sourceTransform = laser.gameObject.transform;
        else if (laser.parentShip != null)
            sourceTransform = laser.parentShip.transform;

        if (sourceTransform == null)
            return false;

        origin = (Vector2)sourceTransform.position;
        direction = (Vector2)sourceTransform.right;

        if (direction.sqrMagnitude <= 0.0001f)
        {
            if (laser.parentShip == null)
                return false;

            direction = (Vector2)laser.parentShip.transform.right;
        }

        if (direction.sqrMagnitude <= 0.0001f)
            return false;

        direction.Normalize();
        return true;
    }

    private static void FireGravityProjectile(
        Laser laser,
        GameShip player,
        ResolvedState resolved,
        GravityProjectileMode mode)
    {
        if (laser == null || player == null || resolved == null)
            return;

        DestroyGravityProjectileShot();

        Vector2 origin;
        Vector2 direction;
        if (!TryGetLaserPose(laser, out origin, out direction))
            return;
        if (direction.sqrMagnitude <= 0.0001f)
            direction = Vector2.right;
        direction.Normalize();

        GravityProjectileShot shot = new GravityProjectileShot();
        shot.mode = mode;
        shot.source = laser;
        shot.owner = player;
        shot.position = origin;
        shot.direction = direction;
        shot.maxTravel = Mathf.Max(0.01f, Mathf.Abs(laser.MaxRange));
        shot.pullFalloffExponent =
            LeviathanStellarConverterTuning.GravityPullFalloffExponent;

        if (mode == GravityProjectileMode.Singularity)
        {
            shot.travelSpeed = resolved.SingularitySpeed *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.lifetime = resolved.SingularityLifetime;
            shot.pullRadius = resolved.SingularityPullRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.pullStrength = resolved.SingularityPullStrength;
            shot.visualScale = resolved.SingularityVisualScale;
            shot.visualRadius = resolved.SingularityVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.haloRadius = resolved.SingularityHaloRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.integratedDamageFraction = resolved.SingularityDamageFraction;
            shot.damageTickRate = GetBeamTickRateSeconds(player, laser);
            int ticks = Mathf.Max(1, Mathf.CeilToInt(
                shot.lifetime / Mathf.Max(0.0001f, shot.damageTickRate)));
            shot.damageFractionPerTick =
                shot.integratedDamageFraction / ticks;
        }
        else
        {
            shot.travelSpeed = resolved.DyingStarSpeed *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.lifetime = resolved.DyingStarFuse;
            shot.pullRadius = resolved.DyingStarPullRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.pullStrength = resolved.DyingStarPullStrength;
            shot.visualScale = resolved.DyingStarVisualScale;
            shot.visualRadius = resolved.DyingStarVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.integratedDamageFraction = resolved.DyingStarDamageFraction;
            shot.explosionRadius = resolved.DyingStarExplosionRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.explosionVisualTargetRadius =
                resolved.DyingStarExplosionVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.currentExplosionRadius = Mathf.Max(
                0.01f,
                LeviathanStellarConverterTuning.WorldUnitsPerMeter * 1.5f);
            shot.explosionExpansionSpeed =
                LeviathanStellarConverterTuning.DyingStarExplosionExpansionSpeedMetersPerSecond *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        }

        gravityProjectileShot = shot;
        BuildGravityProjectileVisual(shot);
        UpdateGravityProjectileVisual(shot);

        // Full-duration exposure should total exactly the configured integrated
        // fraction, so Singularity gets one of its evenly-divided ticks immediately.
        if (mode == GravityProjectileMode.Singularity)
            DamageSingularityHalo(shot);
    }

    private static void UpdateGravityProjectileShot()
    {
        GravityProjectileShot shot = gravityProjectileShot;
        if (shot == null)
            return;

        if (shot.source == null || shot.owner == null ||
            shot.owner.health <= 0f ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != shot.owner)
        {
            DestroyGravityProjectileShot();
            return;
        }

        float delta = Time.fixedDeltaTime;

        if (!shot.exploding)
        {
            shot.age += delta;

            float step = Mathf.Max(0f, shot.travelSpeed * delta);
            float remaining = Mathf.Max(0f, shot.maxTravel - shot.traveled);
            float move = Mathf.Min(step, remaining);
            shot.position += shot.direction * move;
            shot.traveled += move;

            PullHostilesInGravityCircle(
                shot.owner,
                shot.position,
                shot.pullRadius,
                shot.pullStrength,
                shot.pullFalloffExponent);

            if (shot.mode == GravityProjectileMode.DyingStar)
                TryPlayDyingStarExplosionLeadAudio(shot);

            if (shot.mode == GravityProjectileMode.Singularity)
            {
                shot.damageTickTimer += delta;
                while (shot.damageTickTimer >= shot.damageTickRate)
                {
                    shot.damageTickTimer -= shot.damageTickRate;
                    DamageSingularityHalo(shot);
                }

                if (shot.age >= shot.lifetime ||
                    shot.traveled >= shot.maxTravel - 0.0001f)
                {
                    DestroyGravityProjectileShot();
                    return;
                }
            }
            else if (shot.age >= shot.lifetime ||
                shot.traveled >= shot.maxTravel - 0.0001f)
            {
                BeginDyingStarExplosion(shot);
            }
        }
        else
        {
            shot.currentExplosionRadius = Mathf.Min(
                shot.explosionRadius,
                shot.currentExplosionRadius + shot.explosionExpansionSpeed * delta);
            DamageDyingStarTargets(shot);

            if (shot.currentExplosionRadius >= shot.explosionRadius - 0.0001f)
            {
                UpdateGravityProjectileVisual(shot);
                DestroyGravityProjectileShot();
                return;
            }
        }

        UpdateGravityProjectileVisual(shot);
    }

    private static void UpdateEventHorizonBeamPull(
        Laser laser,
        GameShip owner,
        ResolvedState resolved)
    {
        if (laser == null || owner == null || resolved == null ||
            PhysicsController.instance == null)
        {
            return;
        }

        Vector2 origin;
        Vector2 direction;
        if (!TryGetLaserPose(laser, out origin, out direction))
            return;
        if (direction.sqrMagnitude <= 0.0001f)
            return;
        direction.Normalize();

        float maxDistance = Mathf.Max(0f, Mathf.Abs(laser.MaxRange));
        float speed = resolved.EventHorizonTipSpeed *
            LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        eventHorizonPullTipDistance = Mathf.Min(
            maxDistance,
            eventHorizonPullTipDistance + Mathf.Max(0f, speed) * Time.fixedDeltaTime);

        if (eventHorizonPullTipDistance <= 0.0001f)
            return;

        float radius = resolved.EventHorizonPullRadius *
            LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        Vector2 tip = origin + direction * eventHorizonPullTipDistance;

        UpdateEventHorizonTipVisual(
            tip,
            resolved.EventHorizonVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter);

        PullHostilesInGravityCorridor(
            owner,
            origin,
            tip,
            radius,
            resolved.EventHorizonPullStrength,
            LeviathanStellarConverterTuning.GravityPullFalloffExponent);
    }

    private static void PullHostilesInGravityCircle(
        GameShip owner,
        Vector2 center,
        float radius,
        float strength,
        float falloffExponent)
    {
        if (owner == null || PhysicsController.instance == null ||
            radius <= 0f || strength <= 0f)
        {
            return;
        }

        Collider2D[] colliders = PhysicsController.instance.OverlapCircle(center, radius);
        if (colliders == null)
            return;

        GravityTargetScratch.Clear();
        for (int i = 0; i < colliders.Length; i++)
        {
            GameShip target = GetHostilePullTarget(owner, colliders[i]);
            if (target == null || !GravityTargetScratch.Add(target))
                continue;

            float distance = Vector2.Distance(target.transform.position, center);
            float proximity = Mathf.Clamp01(1f - distance / Mathf.Max(0.0001f, radius));
            proximity = Mathf.Pow(proximity, Mathf.Max(0.01f, falloffExponent));
            PullTargetTowards(owner, target, center, strength * proximity);
        }
    }

    private static void PullHostilesInGravityCorridor(
        GameShip owner,
        Vector2 start,
        Vector2 tip,
        float radius,
        float strength,
        float falloffExponent)
    {
        if (owner == null || PhysicsController.instance == null ||
            radius <= 0f || strength <= 0f)
        {
            return;
        }

        Vector2 delta = tip - start;
        float length = delta.magnitude;
        if (length <= 0.0001f)
            return;

        Vector2 direction = delta / length;
        RaycastHit2D[] hits = PhysicsController.instance.CircleCast(
            start,
            radius,
            direction,
            length);
        if (hits == null)
            return;

        GravityTargetScratch.Clear();
        for (int i = 0; i < hits.Length; i++)
        {
            GameShip target = GetHostilePullTarget(owner, hits[i].collider);
            if (target == null || !GravityTargetScratch.Add(target))
                continue;

            Vector2 targetPosition = target.transform.position;
            Vector2 closest = ClosestPointOnSegment(start, tip, targetPosition);
            float distanceFromBlade = Vector2.Distance(targetPosition, closest);
            float proximity = Mathf.Clamp01(
                1f - distanceFromBlade / Mathf.Max(0.0001f, radius));
            proximity = Mathf.Pow(proximity, Mathf.Max(0.01f, falloffExponent));

            PullTargetTowards(owner, target, tip, strength * proximity);
        }
    }

    private static GameShip GetHostilePullTarget(
        GameShip owner,
        Collider2D collider)
    {
        if (owner == null || collider == null)
            return null;

        GameObject obj = collider.gameObject;
        if (obj.CompareTag("Shield") && obj.transform.parent != null)
            obj = obj.transform.parent.gameObject;

        GameShip target;
        if (!GameShip.TryGetShip(obj, out target) || target == null ||
            target == owner || target.IsDrone() || target.immovable ||
            target.GetAbsoluteParent() == owner.GetAbsoluteParent() ||
            !Faction.IsHostile(owner.faction, target.faction))
        {
            return null;
        }

        // Match native Gladiator authority behavior so another player's movement
        // authority does not immediately overwrite this local force.
        if (NetSession.InSession && target.IsNetRemote())
            return null;

        AIShip ai = AIController.instance == null
            ? null
            : AIController.instance.GetAIShip(target);
        if (ai is AttachedAIShip || ai is OrbitAIShip)
            return null;
        if (AIController.instance != null && AIController.instance.IsFormationSlaved(target))
            return null;

        return target;
    }

    private static void PullTargetTowards(
        GameShip owner,
        GameShip target,
        Vector2 point,
        float strength)
    {
        if (owner == null || target == null || strength <= 0f)
            return;

        Rigidbody2D body = target.GetRigidBody();
        if (body == null)
            return;

        Vector2 direction = point - body.position;
        if (direction.sqrMagnitude <= 0.000001f)
            return;
        direction.Normalize();

        ResolveBlackHoleVisualPrefab();
        float nativePower = cachedNativeBlackHolePullPower;
        if (nativePower <= 0f)
            nativePower = Modifier.baseKnockback;

        float power = nativePower * strength;
        if (target.GetShipClass() == Ship.Class.Boss)
            power *= LeviathanStellarConverterTuning.GravityBossPullMultiplier;

        // Exact vanilla BlackHole.Pull force character: faster targets get a
        // stronger correction, and AddForce uses normal Force mode rather than
        // an impulse. The caller supplies the native linear proximity factor.
        float velocityFactor = Mathf.Max(1f, body.velocity.magnitude - 1f);
        body.AddForce(direction * power * velocityFactor * Time.deltaTime);
    }

    private static Vector2 ClosestPointOnSegment(
        Vector2 a,
        Vector2 b,
        Vector2 point)
    {
        Vector2 ab = b - a;
        float sqr = ab.sqrMagnitude;
        if (sqr <= 0.000001f)
            return a;

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / sqr);
        return a + ab * t;
    }

    private static void DamageSingularityHalo(GravityProjectileShot shot)
    {
        if (shot == null || shot.source == null || shot.owner == null ||
            PhysicsController.instance == null || shot.haloRadius <= 0f ||
            shot.damageFractionPerTick <= 0f)
        {
            return;
        }

        Collider2D[] colliders = PhysicsController.instance.OverlapCircle(
            shot.position,
            shot.haloRadius);
        if (colliders == null)
            return;

        GravityTargetScratch.Clear();
        for (int i = 0; i < colliders.Length; i++)
        {
            GameObject obj = colliders[i] == null ? null : colliders[i].gameObject;
            if (obj == null)
                continue;
            if (obj.CompareTag("Shield") && obj.transform.parent != null)
                obj = obj.transform.parent.gameObject;

            GameShip target;
            if (!GameShip.TryGetShip(obj, out target) || target == null ||
                target == shot.owner || !GravityTargetScratch.Add(target) ||
                target.GetAbsoluteParent() == shot.owner.GetAbsoluteParent() ||
                !Faction.IsHostile(shot.owner.faction, target.faction) ||
                !target.CanBeDamagedBy(shot.owner, false) || target.IsDodging())
            {
                continue;
            }

            Vector2 hit = colliders[i].ClosestPoint(shot.position);
            ApplyIntegratedConverterDamage(
                shot.source,
                shot.owner,
                target,
                hit,
                shot.damageFractionPerTick);
        }
    }

    private static void TryPlayDyingStarExplosionLeadAudio(
        GravityProjectileShot shot)
    {
        if (shot == null ||
            shot.mode != GravityProjectileMode.DyingStar ||
            shot.explosionSoundPlayed)
        {
            return;
        }

        float lead = Mathf.Max(
            0f,
            LeviathanStellarConverterTuning.DyingStarExplosionSoundLeadSeconds);

        float fuseRemaining = Mathf.Max(0f, shot.lifetime - shot.age);

        float rangeRemaining = Mathf.Max(0f, shot.maxTravel - shot.traveled);
        float rangeSeconds = shot.travelSpeed > 0.0001f
            ? rangeRemaining / shot.travelSpeed
            : float.PositiveInfinity;

        float secondsToDetonation = Mathf.Min(fuseRemaining, rangeSeconds);
        if (secondsToDetonation > lead)
            return;

        // Play at the predicted detonation position so the positional sound
        // remains aligned even though the seed travels during the 0.5s lead.
        float predictionSeconds = Mathf.Max(0f, secondsToDetonation);
        float predictedMove = Mathf.Min(
            rangeRemaining,
            Mathf.Max(0f, shot.travelSpeed) * predictionSeconds);
        Vector2 predictedPosition =
            shot.position + shot.direction * predictedMove;

        LeviathanAudioRuntime.PlayEventHorizonExplosion(predictedPosition);
        shot.explosionSoundPlayed = true;
    }

    private static void BeginDyingStarExplosion(GravityProjectileShot shot)
    {
        if (shot == null || shot.exploding)
            return;

        shot.exploding = true;
        shot.hitShips.Clear();

        // Normal case is pre-cued DyingStarExplosionSoundLeadSeconds early.
        // If the projectile is destroyed/reaches range too abruptly to pre-cue,
        // still guarantee one sound at the actual detonation point.
        if (!shot.explosionSoundPlayed)
        {
            LeviathanAudioRuntime.PlayEventHorizonExplosion(shot.position);
            shot.explosionSoundPlayed = true;
        }

        TryBuildDyingStarExplosionVisual(shot);
        DamageDyingStarTargets(shot);
    }

    private static void DamageDyingStarTargets(GravityProjectileShot shot)
    {
        if (shot == null || shot.owner == null || shot.source == null ||
            PhysicsController.instance == null)
        {
            return;
        }

        Collider2D[] colliders = PhysicsController.instance.OverlapCircle(
            shot.position,
            shot.currentExplosionRadius);
        if (colliders == null)
            return;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider2D collider = colliders[i];
            if (collider == null)
                continue;

            GameObject obj = collider.gameObject;
            if (obj.CompareTag("Shield") && obj.transform.parent != null)
                obj = obj.transform.parent.gameObject;

            GameShip target;
            if (!GameShip.TryGetShip(obj, out target) || target == null ||
                target == shot.owner || shot.hitShips.Contains(target) ||
                target.GetAbsoluteParent() == shot.owner.GetAbsoluteParent() ||
                !Faction.IsHostile(shot.owner.faction, target.faction) ||
                !target.CanBeDamagedBy(shot.owner, false))
            {
                continue;
            }

            Vector2 closest = collider.ClosestPoint(shot.position);
            if ((closest - shot.position).sqrMagnitude >
                shot.currentExplosionRadius * shot.currentExplosionRadius + 0.0001f)
            {
                continue;
            }

            shot.hitShips.Add(target);
            if (!target.IsDodging())
            {
                ApplyIntegratedConverterDamage(
                    shot.source,
                    shot.owner,
                    target,
                    closest,
                    shot.integratedDamageFraction);
            }
        }
    }

    private static void ApplyIntegratedConverterDamage(
        Laser source,
        GameShip owner,
        GameShip target,
        Vector2 hitPosition,
        float integratedShotFraction)
    {
        if (source == null || owner == null || target == null ||
            RouteDamageMethod == null || integratedShotFraction <= 0f)
        {
            return;
        }

        int rank;
        GameShip contextOwner;
        if (!TryGetContext(source, out contextOwner, out rank))
            return;

        ResolvedState resolved = GetResolvedState(contextOwner, rank);
        float tickRate = GetBeamTickRateSeconds(owner, source);
        float integratedTicks = Mathf.Max(0f, resolved.PulseSeconds / tickRate);

        float critChance = Mathf.Clamp01(
            source.GetCritChance() * resolved.CritChanceMultiplier +
            resolved.CritChanceBonus);
        bool crit = Modifier.CritRoll(critChance, target);
        DamageData[] native = source.GetDamageData(crit, false);
        if (native == null || native.Length == 0)
            return;

        float multiplier =
            resolved.DamageMultiplier *
            integratedTicks *
            integratedShotFraction;

        if (MirrorGameObjectField != null &&
            MirrorGameObjectField.GetValue(source) as UnityEngine.Object != null)
        {
            multiplier *= 2f;
        }

        DamageData[] damage = new DamageData[native.Length];
        for (int i = 0; i < native.Length; i++)
        {
            DamageData datum = native[i];
            datum.damage *= multiplier;
            datum.dps *= multiplier;
            damage[i] = datum;
        }

        float statusChance = Mathf.Clamp01(
            source.GetStatusEffectChance() * resolved.DebuffChanceMultiplier +
            resolved.DebuffChanceBonus);
        bool bypass = source.HasCustomizer(Customizer.Type.BypassDamageLimit);
        Vector2 direction = (hitPosition - (Vector2)owner.transform.position).normalized;
        if (direction.sqrMagnitude <= 0.0001f)
            direction = source.gameObject != null
                ? (Vector2)source.gameObject.transform.right
                : Vector2.right;

        target.SetLastDamageDirection(direction);
        target.lastDamagedByWeaponName = source.GetName(false, false);
        target.lastDamagedByShipName = owner.GetName();
        target.lastDamagedByFaction = owner.faction;

        RouteDamageMethod.Invoke(
            null,
            new object[]
            {
                target,
                source.damageType,
                damage,
                statusChance,
                crit,
                hitPosition,
                owner,
                bypass,
                0f,
                source,
                0f,
                0f,
                false,
                0f
            });

        if (ConduitRelayHitMethod != null)
        {
            ConduitRelayHitMethod.Invoke(
                null,
                new object[] { source, owner, target, damage, hitPosition, bypass });
        }
    }

    private static float GetBeamTickRateSeconds(GameShip owner, Laser source)
    {
        float tickRate = owner == null
            ? 0.2f
            : owner.ApplyModifier(
                Modifier.Type.BeamTickRate,
                Item.Category.None,
                0.2f,
                true);

        if (owner != null && source != null &&
            source.damageType == Damageable.DamageType.Kinetic)
        {
            int kineticRate = GameShip.GetPlayerSourceUpgradeValue(
                owner,
                Upgrade.Key.JuggernautKineticRate);
            if (kineticRate > 0)
                tickRate /= 1f + kineticRate / 100f;
        }

        return Mathf.Max(0.0001f, tickRate);
    }

    private static void BuildGravityProjectileVisual(GravityProjectileShot shot)
    {
        if (shot == null)
            return;

        ResolveBlackHoleVisualPrefab();
        if (cachedBlackHoleVisualPrefab != null)
        {
            GameObject obj = CustomObject.Instantiate<GameObject>(
                cachedBlackHoleVisualPrefab,
                shot.position,
                Quaternion.identity);
            if (obj != null)
            {
                shot.blackHoleVisual = obj;
                shot.blackHoleBaseScale = obj.transform.localScale;
                shot.blackHoleBaseVisualRadius = MeasureVisibleRadius(obj);

                BlackHole blackHole = obj.GetComponent<BlackHole>();
                if (blackHole != null)
                    blackHole.enabled = false;

                Collider2D[] colliders = obj.GetComponentsInChildren<Collider2D>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null)
                        colliders[i].enabled = false;

                if (shot.visualRadius > 0.0001f &&
                    shot.blackHoleBaseVisualRadius > 0.0001f)
                {
                    float radiusScale =
                        shot.visualRadius / shot.blackHoleBaseVisualRadius;
                    obj.transform.localScale =
                        shot.blackHoleBaseScale * radiusScale;
                }
                else
                {
                    obj.transform.localScale = shot.blackHoleBaseScale *
                        Mathf.Max(0.01f, shot.visualScale);
                }

                obj.SetActive(true);
            }
        }

        if (shot.mode == GravityProjectileMode.Singularity)
            BuildSingularityHaloVisual(shot);
    }

    private static void BuildSingularityHaloVisual(GravityProjectileShot shot)
    {
        if (shot == null || shot.haloRadius <= 0f)
            return;

        ResolveHaloFieldPrefab();
        Color tint = ResolveSourceLaserColor(shot.source);
        tint.a = Mathf.Clamp01(
            LeviathanStellarConverterTuning.SingularityHaloOpacity);

        if (cachedHaloFieldPrefab != null)
        {
            GameObject obj = CustomObject.Instantiate<GameObject>(
                cachedHaloFieldPrefab,
                shot.position,
                Quaternion.identity);
            if (obj != null)
            {
                shot.haloVisual = obj;
                shot.haloVisualBaseRadius = Mathf.Max(0.0001f, cachedHaloFieldRadius);
                Collider2D[] colliders = obj.GetComponentsInChildren<Collider2D>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null)
                        colliders[i].enabled = false;
                ApplyGravityFieldColor(obj, tint);
                obj.SetActive(true);
                return;
            }
        }

        GameObject fallback = new GameObject("Leviathan Singularity Halo");
        LineRenderer line = fallback.AddComponent<LineRenderer>();
        Material material = GetGravityFallbackMaterial();
        if (material != null)
            line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.loop = true;
        line.positionCount = Mathf.Max(
            12,
            LeviathanStellarConverterTuning.GravityFallbackCircleSegments);
        line.widthMultiplier = 0.035f;
        line.startColor = tint;
        line.endColor = tint;
        shot.haloVisual = fallback;
        shot.fallbackRing = line;
    }

    private static void ResolveBlackHoleVisualPrefab()
    {
        if (searchedBlackHoleVisualPrefab)
            return;
        searchedBlackHoleVisualPrefab = true;

        try
        {
            Dictionary<GameObject, int> counts = new Dictionary<GameObject, int>();
            LauncherItemBase[] launchers = Resources.LoadAll<LauncherItemBase>("Base/Items");
            if (launchers != null)
            {
                for (int i = 0; i < launchers.Length; i++)
                    if (launchers[i] != null)
                        CountBlackHoleVisualPrefabs(launchers[i].projectiles, counts);
            }

            LauncherLegendary[] legendaries = Resources.LoadAll<LauncherLegendary>("Base/Items");
            if (legendaries != null)
            {
                for (int i = 0; i < legendaries.Length; i++)
                    if (legendaries[i] != null)
                        CountBlackHoleVisualPrefabs(legendaries[i].projectiles, counts);
            }

            int best = 0;
            foreach (KeyValuePair<GameObject, int> pair in counts)
            {
                if (pair.Key == null || pair.Value <= best)
                    continue;
                cachedBlackHoleVisualPrefab = pair.Key;
                best = pair.Value;
            }

            if (cachedBlackHoleVisualPrefab == null)
            {
                Debug.LogWarning(
                    "[Leviathan] Converter manifestations could not resolve a vanilla BlackHole visual prefab.");
            }
            else
            {
                float powerTotal = 0f;
                float radiusTotal = 0f;
                int sampleCount = 0;

                if (launchers != null)
                {
                    for (int i = 0; i < launchers.Length; i++)
                    {
                        if (launchers[i] != null)
                        {
                            CaptureNativeBlackHolePullSettings(
                                launchers[i].projectiles,
                                cachedBlackHoleVisualPrefab,
                                ref powerTotal,
                                ref radiusTotal,
                                ref sampleCount);
                        }
                    }
                }

                if (legendaries != null)
                {
                    for (int i = 0; i < legendaries.Length; i++)
                    {
                        if (legendaries[i] != null)
                        {
                            CaptureNativeBlackHolePullSettings(
                                legendaries[i].projectiles,
                                cachedBlackHoleVisualPrefab,
                                ref powerTotal,
                                ref radiusTotal,
                                ref sampleCount);
                        }
                    }
                }

                if (sampleCount > 0)
                {
                    cachedNativeBlackHolePullPower = powerTotal / sampleCount;
                    cachedNativeBlackHolePullRadius = radiusTotal / sampleCount;
                    Debug.Log(
                        "[Leviathan] Converter gravity using vanilla BlackHole pull power " +
                        cachedNativeBlackHolePullPower.ToString("0.###") +
                        " (native radius " +
                        cachedNativeBlackHolePullRadius.ToString("0.###") + ").");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[Leviathan] Converter BlackHole visual lookup failed: " + ex.Message);
        }
    }

    private static void CountBlackHoleVisualPrefabs(
        LauncherItemBase.FactionProjectile[] projectiles,
        Dictionary<GameObject, int> counts)
    {
        if (projectiles == null || counts == null)
            return;

        HashSet<GameObject> seen = new HashSet<GameObject>();
        for (int i = 0; i < projectiles.Length; i++)
        {
            GameObject projectileObject = projectiles[i] == null
                ? null
                : projectiles[i].gameObject;
            if (projectileObject == null)
                continue;

            BlackHoleProjectile projectile =
                projectileObject.GetComponent<BlackHoleProjectile>();
            GameObject prefab = projectile == null ? null : projectile.blackHolePrefab;
            if (prefab == null || !seen.Add(prefab))
                continue;

            int count;
            counts.TryGetValue(prefab, out count);
            counts[prefab] = count + 1;
        }
    }

    private static void CaptureNativeBlackHolePullSettings(
        LauncherItemBase.FactionProjectile[] projectiles,
        GameObject selectedBlackHolePrefab,
        ref float powerTotal,
        ref float radiusTotal,
        ref int sampleCount)
    {
        if (projectiles == null || selectedBlackHolePrefab == null)
            return;

        for (int i = 0; i < projectiles.Length; i++)
        {
            GameObject projectileObject = projectiles[i] == null
                ? null
                : projectiles[i].gameObject;
            if (projectileObject == null)
                continue;

            BlackHoleProjectile projectile =
                projectileObject.GetComponent<BlackHoleProjectile>();
            if (projectile == null ||
                projectile.blackHolePrefab != selectedBlackHolePrefab ||
                projectile.power <= 0f)
            {
                continue;
            }

            powerTotal += projectile.power;
            radiusTotal += Mathf.Max(0f, projectile.radius);
            sampleCount++;
        }
    }

    private static void TryBuildDyingStarExplosionVisual(GravityProjectileShot shot)
    {
        if (shot == null)
            return;

        ResolveCommonExplosiveAreaPrefab();
        if (cachedExplosiveAreaPrefab == null)
            return;

        if (shot.blackHoleVisual != null)
        {
            CustomObject.Destroy(shot.blackHoleVisual);
            shot.blackHoleVisual = null;
        }
        if (shot.haloVisual != null)
        {
            if (shot.fallbackRing != null)
                UnityEngine.Object.Destroy(shot.haloVisual);
            else
                CustomObject.Destroy(shot.haloVisual);
            shot.haloVisual = null;
            shot.fallbackRing = null;
        }

        GameObject obj = null;

        if (PoolController.instance != null)
        {
            obj = PoolController.instance.GetObject(
                cachedExplosiveAreaPrefab,
                shot.position,
                Quaternion.identity,
                false);
        }

        if (obj == null)
        {
            obj = CustomObject.Instantiate<GameObject>(
                cachedExplosiveAreaPrefab,
                shot.position,
                Quaternion.identity);
        }

        if (obj == null)
            return;

        ExplosiveArea area = obj.GetComponent<ExplosiveArea>();
        if (area == null)
        {
            CustomObject.Destroy(obj);
            return;
        }

        shot.explosionVisual = area;
        shot.explosionVisualBaseScale = area.transform.localScale;
        shot.explosionVisualBaseRadius = MeasureVisibleRadius(obj);

        // Dying Star drives expansion explicitly from meters. Disable the
        // prefab's own multiplicative growth so it cannot double-grow between
        // Converter updates.
        area.expansionRate = 0f;

        Color tint = ResolveSourceLaserColor(shot.source);
        ApplyDyingStarExplosionColor(
            obj,
            tint,
            LeviathanStellarConverterTuning.DyingStarExplosionVisualOpacity);

        float remaining = shot.explosionExpansionSpeed <= 0f
            ? 0.05f
            : Mathf.Max(
                0.05f,
                (shot.explosionRadius - shot.currentExplosionRadius) /
                shot.explosionExpansionSpeed);
        area.SetDestroyTime(remaining + 0.50f, remaining + 0.40f);
        ApplyDyingStarExplosionVisualScale(shot);
    }

    public static GameObject GetCommonExplosiveAreaVisualPrefab()
    {
        ResolveCommonExplosiveAreaPrefab();
        return cachedExplosiveAreaPrefab;
    }

    private static void ResolveCommonExplosiveAreaPrefab()
    {
        if (searchedExplosiveAreaPrefab)
            return;

        searchedExplosiveAreaPrefab = true;

        try
        {
            Dictionary<GameObject, int> counts =
                new Dictionary<GameObject, int>();

            LauncherItemBase[] launchers =
                Resources.LoadAll<LauncherItemBase>("Base/Items");
            if (launchers != null)
            {
                for (int i = 0; i < launchers.Length; i++)
                {
                    if (launchers[i] == null)
                        continue;
                    CountExplosiveAreaPrefabs(launchers[i].projectiles, counts);
                }
            }

            LauncherLegendary[] legendaries =
                Resources.LoadAll<LauncherLegendary>("Base/Items");
            if (legendaries != null)
            {
                for (int i = 0; i < legendaries.Length; i++)
                {
                    if (legendaries[i] == null)
                        continue;
                    CountExplosiveAreaPrefabs(legendaries[i].projectiles, counts);
                }
            }

            int bestCount = 0;
            string overrideName =
                LeviathanStellarConverterTuning.DyingStarExplosionPrefabNameOverride;

            if (!string.IsNullOrEmpty(overrideName))
            {
                foreach (KeyValuePair<GameObject, int> pair in counts)
                {
                    if (pair.Key != null && string.Equals(
                        pair.Key.name,
                        overrideName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        cachedExplosiveAreaPrefab = pair.Key;
                        bestCount = pair.Value;
                        break;
                    }
                }
            }

            if (cachedExplosiveAreaPrefab == null)
            {
                foreach (KeyValuePair<GameObject, int> pair in counts)
                {
                    if (pair.Key == null || pair.Value <= bestCount)
                        continue;
                    cachedExplosiveAreaPrefab = pair.Key;
                    bestCount = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[Leviathan] Dying Star explosion visual lookup failed: " + ex.Message);
        }
    }

    private static void CountExplosiveAreaPrefabs(
        LauncherItemBase.FactionProjectile[] projectiles,
        Dictionary<GameObject, int> counts)
    {
        if (projectiles == null || counts == null)
            return;

        HashSet<GameObject> seen = new HashSet<GameObject>();
        for (int i = 0; i < projectiles.Length; i++)
        {
            GameObject projectileObject = projectiles[i] == null
                ? null
                : projectiles[i].gameObject;
            if (projectileObject == null)
                continue;

            GameObject areaPrefab = null;
            ExplosiveProjectile explosiveProjectile =
                projectileObject.GetComponent<ExplosiveProjectile>();
            if (explosiveProjectile != null)
                areaPrefab = explosiveProjectile.explosiveAreaPrefab;

            if (areaPrefab == null)
            {
                Mine mine = projectileObject.GetComponent<Mine>();
                if (mine != null)
                    areaPrefab = mine.explosiveAreaPrefab;
            }

            if (areaPrefab == null || !seen.Add(areaPrefab))
                continue;

            int count;
            counts.TryGetValue(areaPrefab, out count);
            counts[areaPrefab] = count + 1;
        }
    }

    private static void ApplyDyingStarExplosionVisualScale(GravityProjectileShot shot)
    {
        if (shot == null || shot.explosionVisual == null)
            return;

        shot.explosionVisual.transform.position = shot.position;

        float progress = shot.explosionRadius <= 0.0001f
            ? 1f
            : Mathf.Clamp01(
                shot.currentExplosionRadius / shot.explosionRadius);

        float finalVisualRadius = shot.explosionVisualTargetRadius > 0.0001f
            ? shot.explosionVisualTargetRadius
            : shot.explosionRadius;

        float currentVisualRadius =
            finalVisualRadius *
            progress *
            Mathf.Max(
                0f,
                LeviathanStellarConverterTuning.DyingStarExplosionVisualScale);

        if (shot.explosionVisualBaseRadius > 0.0001f)
        {
            float scale =
                currentVisualRadius / shot.explosionVisualBaseRadius;
            shot.explosionVisual.SetScale(
                shot.explosionVisualBaseScale * scale);
            return;
        }

        // Fallback mirrors native ExplosiveProjectile behavior if renderer
        // bounds could not be measured.
        float diameter = currentVisualRadius * 2f;
        shot.explosionVisual.SetScale(
            new Vector3(diameter, diameter, diameter));
    }

    private static float MeasureVisibleRadius(GameObject obj)
    {
        if (obj == null)
            return 0f;

        float radius = 0f;

        // Sprite bounds are stable immediately and are preferable to particle
        // bounds, which can be empty or enormous depending on simulation state.
        SpriteRenderer[] sprites =
            obj.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] == null || !sprites[i].enabled)
                continue;

            Bounds bounds = sprites[i].bounds;
            radius = Mathf.Max(
                radius,
                Mathf.Max(bounds.extents.x, bounds.extents.y));
        }

        if (radius > 0.0001f)
            return radius;

        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled ||
                renderer is ParticleSystemRenderer)
            {
                continue;
            }

            Bounds bounds = renderer.bounds;
            radius = Mathf.Max(
                radius,
                Mathf.Max(bounds.extents.x, bounds.extents.y));
        }

        return radius;
    }

    private static Material GetGravityFallbackMaterial()
    {
        if (cachedGravityFallbackMaterial != null)
            return cachedGravityFallbackMaterial;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            return null;

        cachedGravityFallbackMaterial = new Material(shader);
        cachedGravityFallbackMaterial.name =
            "Leviathan Gravity Fallback Shared";
        return cachedGravityFallbackMaterial;
    }

    private static void UpdateEventHorizonTipVisual(
        Vector2 position,
        float visualRadius)
    {
        if (visualRadius <= 0.0001f)
        {
            DestroyEventHorizonTipVisual();
            return;
        }

        ResolveBlackHoleVisualPrefab();
        if (cachedBlackHoleVisualPrefab == null)
            return;

        if (eventHorizonTipVisual == null)
        {
            GameObject obj = CustomObject.Instantiate<GameObject>(
                cachedBlackHoleVisualPrefab,
                position,
                Quaternion.identity);
            if (obj == null)
                return;

            BlackHole blackHole = obj.GetComponent<BlackHole>();
            if (blackHole != null)
                blackHole.enabled = false;

            Collider2D[] colliders =
                obj.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = false;
            }

            eventHorizonTipVisual = obj;
            eventHorizonTipVisualBaseScale = obj.transform.localScale;
            eventHorizonTipVisualBaseRadius = MeasureVisibleRadius(obj);
            obj.SetActive(true);
        }

        eventHorizonTipVisual.transform.position = position;

        if (eventHorizonTipVisualBaseRadius > 0.0001f)
        {
            float scale =
                visualRadius / eventHorizonTipVisualBaseRadius;
            eventHorizonTipVisual.transform.localScale =
                eventHorizonTipVisualBaseScale * scale;
        }
    }

    private static void DestroyEventHorizonTipVisual()
    {
        if (eventHorizonTipVisual == null)
            return;

        CustomObject.Destroy(eventHorizonTipVisual);
        eventHorizonTipVisual = null;
        eventHorizonTipVisualBaseScale = Vector3.one;
        eventHorizonTipVisualBaseRadius = 0f;
    }

    private static void ResolveHaloFieldPrefab()
    {
        if (searchedHaloField)
            return;
        searchedHaloField = true;

        try
        {
            HaloItemBase[] halos = Resources.LoadAll<HaloItemBase>("Base/Items");
            if (halos == null)
                return;

            for (int i = 0; i < halos.Length; i++)
            {
                if (halos[i] == null || halos[i].field == null)
                    continue;
                cachedHaloFieldPrefab = halos[i].field;
                cachedHaloFieldRadius = Mathf.Max(0.0001f, halos[i].fieldRadius);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[Leviathan] Singularity Halo visual lookup failed: " + ex.Message);
        }
    }

    private static Color ResolveSourceLaserColor(Laser source)
    {
        Damageable.DamageType damageType = source == null
            ? Damageable.DamageType.Kinetic
            : source.damageType;

        if (Palette.instance != null)
        {
            try
            {
                Color paletteColor = Palette.instance.GetDamageTypeColor(
                    damageType,
                    false);
                paletteColor.a = 1f;
                return paletteColor;
            }
            catch
            {
            }
        }

        switch (damageType)
        {
            case Damageable.DamageType.Cold:
                return new Color(0.40f, 0.98f, 0.98f, 1f);
            case Damageable.DamageType.Corrosive:
                return new Color(0.70f, 0.40f, 0.99f, 1f);
            case Damageable.DamageType.Electric:
                return new Color(0.99f, 0.92f, 0.40f, 1f);
            case Damageable.DamageType.Thermal:
                return new Color(0.99f, 0.40f, 0.43f, 1f);
            case Damageable.DamageType.Radiation:
                return new Color(0.40f, 0.99f, 0.43f, 1f);
            default:
                return Color.white;
        }
    }

    private static void ApplyDyingStarExplosionColor(
        GameObject obj,
        Color tint,
        float opacity)
    {
        if (obj == null)
            return;

        float alphaMultiplier = Mathf.Clamp01(opacity);
        SpriteRenderer[] sprites = obj.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] == null)
                continue;
            Color original = sprites[i].color;
            sprites[i].color = new Color(
                tint.r, tint.g, tint.b, original.a * alphaMultiplier);
        }

        ParticleSystem[] particles = obj.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i] == null)
                continue;
            ParticleSystem.MainModule main = particles[i].main;
            main.startColor = new Color(tint.r, tint.g, tint.b, alphaMultiplier);
        }

        LineRenderer[] lines = obj.GetComponentsInChildren<LineRenderer>(true);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] == null)
                continue;
            Color start = lines[i].startColor;
            Color endColor = lines[i].endColor;
            lines[i].startColor = new Color(
                tint.r, tint.g, tint.b, start.a * alphaMultiplier);
            lines[i].endColor = new Color(
                tint.r, tint.g, tint.b, endColor.a * alphaMultiplier);
        }
    }

    private static void ApplyGravityFieldColor(GameObject obj, Color tint)
    {
        if (obj == null)
            return;

        float alpha = Mathf.Clamp01(tint.a);
        SpriteRenderer[] sprites = obj.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < sprites.Length; i++)
            if (sprites[i] != null)
                sprites[i].color = new Color(tint.r, tint.g, tint.b, alpha);

        ParticleSystem[] particles = obj.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i] == null)
                continue;
            ParticleSystem.MainModule main = particles[i].main;
            main.startColor = new Color(tint.r, tint.g, tint.b, alpha);
        }

        LineRenderer[] lines = obj.GetComponentsInChildren<LineRenderer>(true);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] == null)
                continue;
            Color c = new Color(tint.r, tint.g, tint.b, alpha);
            lines[i].startColor = c;
            lines[i].endColor = c;
        }
    }

    private static void UpdateGravityProjectileVisual(GravityProjectileShot shot)
    {
        if (shot == null)
            return;

        if (shot.blackHoleVisual != null)
            shot.blackHoleVisual.transform.position = shot.position;

        if (shot.explosionVisual != null)
        {
            ApplyDyingStarExplosionVisualScale(shot);
            return;
        }

        if (shot.haloVisual != null)
        {
            shot.haloVisual.transform.position = shot.position;
            if (shot.fallbackRing != null)
            {
                int count = shot.fallbackRing.positionCount;
                for (int i = 0; i < count; i++)
                {
                    float angle = Mathf.PI * 2f * i / count;
                    shot.fallbackRing.SetPosition(
                        i,
                        shot.position + new Vector2(
                            Mathf.Cos(angle), Mathf.Sin(angle)) * shot.haloRadius);
                }
            }
            else
            {
                float scale = shot.haloRadius /
                    Mathf.Max(0.0001f, shot.haloVisualBaseRadius);
                shot.haloVisual.transform.localScale = new Vector3(scale, scale, scale);
            }
        }
    }

    private static void DestroyGravityProjectileVisual(GravityProjectileShot shot)
    {
        if (shot == null)
            return;

        if (shot.explosionVisual != null)
        {
            shot.explosionVisual.PoolDestroy();
            shot.explosionVisual = null;
        }

        if (shot.blackHoleVisual != null)
        {
            CustomObject.Destroy(shot.blackHoleVisual);
            shot.blackHoleVisual = null;
        }

        if (shot.haloVisual != null)
        {
            if (shot.fallbackRing != null)
                UnityEngine.Object.Destroy(shot.haloVisual);
            else
                CustomObject.Destroy(shot.haloVisual);
            shot.haloVisual = null;
            shot.fallbackRing = null;
        }
    }

    private static void DestroyGravityProjectileShot()
    {
        if (gravityProjectileShot != null)
            DestroyGravityProjectileVisual(gravityProjectileShot);
        gravityProjectileShot = null;
    }

    // Continuous Conversion's extra random status is intentionally local-authority
    // only for now. The specialization save itself is not network-synced, so remote
    // victim authority cannot reconstruct this keystone safely. Native status chance
    // bonuses still travel in the ordinary damage event in multiplayer.
    public static void TryApplyContinuousRandomStatus(
        GameShip target,
        BeamWeapon sourceWeapon,
        DamageData[] damageData)
    {
        Laser laser = sourceWeapon as Laser;
        if (target == null || laser == null || target.IsNetRemote())
            return;

        GameShip player;
        int rank;
        if (!TryGetContext(laser, out player, out rank))
            return;

        ResolvedState resolved = GetResolvedState(player, rank);
        if (!resolved.Continuous || resolved.RandomBasicStatusChance <= 0f ||
            target.IsDrone() || target.health <= 0f)
        {
            return;
        }

        if (DebuffAndDirectImmuneField != null)
        {
            object rawImmune = DebuffAndDirectImmuneField.GetValue(target);
            if (rawImmune is bool && (bool)rawImmune)
                return;
        }

        if (target.shield != null && target.shield.IsWardActive())
            return;

        float chance = resolved.RandomBasicStatusChance;
        if (player.pilot != null && target.pilot != null)
        {
            int levelDelta = player.pilot.GetLevel(0) - target.pilot.GetLevel(0);
            if (levelDelta < -9)
                chance *= 0.25f;
            else if (levelDelta < -4)
                chance *= 0.50f;
            else if (levelDelta < -3)
                chance *= 0.75f;
        }

        if (UnityEngine.Random.Range(0f, 1f) > chance)
            return;

        Damageable.DamageType type = RandomBasicStatusTypes[
            UnityEngine.Random.Range(0, RandomBasicStatusTypes.Length)];

        // Match the same basic shield/hull gating used by GameShip.Damage.
        if (target.shield != null && target.shield.damageType == type &&
            !GameShip.PlayerSourceHasUpgrade(
                player,
                Upgrade.Key.CatalystSunderShields))
        {
            return;
        }
        if (target.lastHealthDamage <= 0f &&
            type != Damageable.DamageType.Electric &&
            type != Damageable.DamageType.Radiation)
        {
            return;
        }

        float dps = damageData == null
            ? laser.CalculateDPS(Activatable.Modified.Global)
            : DamageData.GetDamageData(Modifier.Type.Damage, damageData).dps;
        StatusEffect effect = StatusEffect.GetEffectForDamageType(
            type,
            dps,
            player,
            chance);
        if (effect != null)
            target.AddStatusEffect(effect);
    }

    private static float GetRankValue(float[] values, int rank)
    {
        if (values == null || values.Length == 0)
            return 0f;

        int index = Mathf.Clamp(rank - 1, 0, values.Length - 1);
        return values[index];
    }
}

// =============================================================================
// INPUT CONTEXT
// =============================================================================

[HarmonyPatch]
public static class LeviathanStellarConverterStartByTypePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StartActivating",
            new Type[] { typeof(Item.Type) }
        );
    }

    public static void Prefix(GameShip __instance, Item.Type __0)
    {
        LeviathanStellarConverter.EnterInputStartByType(
            __instance,
            __0
        );
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStart();
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterStartByIndexPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StartActivating",
            new Type[] { typeof(int) }
        );
    }

    public static void Prefix(GameShip __instance, int __0)
    {
        LeviathanStellarConverter.EnterInputStartByIndex(
            __instance,
            __0
        );
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStart();
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterStopByTypePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StopActivating",
            new Type[] { typeof(Item.Type?) }
        );
    }

    public static void Prefix(GameShip __instance, Item.Type? __0)
    {
        LeviathanStellarConverter.EnterInputStopByType(
            __instance,
            __0
        );
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStop();
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterStopByIndexPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "StopActivating",
            new Type[] { typeof(int) }
        );
    }

    public static void Prefix(GameShip __instance, int __0)
    {
        LeviathanStellarConverter.EnterInputStopByIndex(
            __instance,
            __0
        );
    }

    public static void Postfix()
    {
        LeviathanStellarConverter.ExitInputStop();
    }
}

// RemoteShipDriver derives activation bits from Activatable.active. Stellar
// deliberately toggles that flag across charge/output phases, so publish the
// owner's actual input latch on the selected Primary or Special source bit.
[HarmonyPatch(typeof(RemoteShipDriver), "BuildActiveSlotsMask")]
public static class LeviathanStellarConverterNetworkSourceInputPatch
{
    public static void Postfix(GameShip __0, ref uint __result)
    {
        LeviathanStellarConverter.ScaleNetworkSourceInput(
            __0,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(Activatable), "Activate")]
public static class LeviathanStellarConverterActivatePatch
{
    public static bool Prefix(Activatable __instance)
    {
        return !LeviathanStellarConverter.InterceptNativeActivate(__instance);
    }
}

[HarmonyPatch(typeof(Activatable), "Deactivate")]
public static class LeviathanStellarConverterDeactivatePatch
{
    public static bool Prefix(Activatable __instance)
    {
        return !LeviathanStellarConverter.InterceptNativeDeactivate(__instance);
    }
}

// Stellar output is mechanically bounded to the converter firing phase. Native
// Beam deactivation fades visually and can otherwise keep ticking damage.
[HarmonyPatch(typeof(Beam), "DoDamageTick")]
public static class LeviathanStellarConverterBeamDamageWindowPatch
{
    public static bool Prefix(
        Beam __instance,
        out LeviathanStellarConverter.BeamPiercingState __state)
    {
        __state = default(LeviathanStellarConverter.BeamPiercingState);

        if (!LeviathanStellarConverter.AllowBeamDamageTick(__instance))
            return false;

        __state = LeviathanStellarConverter.PrepareBeamPiercing(__instance);
        return true;
    }

    public static void Postfix(
        Beam __instance,
        LeviathanStellarConverter.BeamPiercingState __state)
    {
        LeviathanStellarConverter.RestoreBeamPiercing(
            __instance,
            __state);
    }
}

// =============================================================================
// SOURCE LASER STATE
// =============================================================================

[HarmonyPatch(typeof(BeamWeapon), "FixedUpdate")]
public static class LeviathanStellarConverterBeamWeaponFixedUpdatePatch
{
    public static void Prefix(BeamWeapon __instance)
    {
        Laser laser = __instance as Laser;

        if (laser != null)
            LeviathanStellarConverter.PrepareFixedUpdate(laser);
    }

    public static void Postfix(BeamWeapon __instance)
    {
        Laser laser = __instance as Laser;

        if (laser != null)
            LeviathanStellarConverter.CompleteFixedUpdate(laser);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "Unequip")]
public static class LeviathanStellarConverterBeamWeaponUnequipPatch
{
    public static void Prefix(BeamWeapon __instance)
    {
        Laser source = __instance as Laser;

        if (source != null &&
            ReferenceEquals(
                LeviathanStellarConverter.FindSourceLaser(source.parentShip),
                source
            ))
        {
            LeviathanStellarConverter.ResetSource(source);
        }
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class LeviathanStellarConverterWorldDestroyPatch
{
    public static void Prefix()
    {
        LeviathanStellarConverter.Reset();
    }
}

// =============================================================================
// DAMAGE / ROLLS / RANGE
// =============================================================================

[HarmonyPatch(typeof(BeamWeapon), "GetDamageData")]
public static class LeviathanStellarConverterDamagePatch
{
    public static void Postfix(
        BeamWeapon __instance,
        ref DamageData[] __result)
    {
        LeviathanStellarConverter.ScaleDamagePacket(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(BeamWeapon), "GetCritChance")]
public static class LeviathanStellarConverterCritChancePatch
{
    public static void Postfix(BeamWeapon __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleCritChance(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "GetCritModifier")]
public static class LeviathanStellarConverterCritModifierPatch
{
    public static void Postfix(BeamWeapon __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleCritModifier(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "GetStatusEffectChance")]
public static class LeviathanStellarConverterDebuffChancePatch
{
    public static void Postfix(BeamWeapon __instance, ref float __result)
    {
        LeviathanStellarConverter.ScaleDebuffChance(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(BeamWeapon), "get_ChainTargets")]
public static class LeviathanStellarConverterChainTargetsPatch
{
    public static void Postfix(BeamWeapon __instance, ref int __result)
    {
        LeviathanStellarConverter.ScaleChainTargets(__instance, ref __result);
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterContinuousStatusPatch
{
    public static MethodBase TargetMethod()
    {
        return typeof(NetCombat)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "RouteDamage" &&
                m.GetParameters().Length == 14 &&
                m.GetParameters()[2].ParameterType == typeof(DamageData[]));
    }

    public static void Postfix(object[] __args)
    {
        if (__args == null || __args.Length < 10)
            return;

        GameShip target = __args[0] as GameShip;
        BeamWeapon source = __args[9] as BeamWeapon;
        DamageData[] damage = __args[2] as DamageData[];
        if (target != null && source != null)
        {
            LeviathanStellarConverter.TryApplyContinuousRandomStatus(
                target,
                source,
                damage);
        }
    }
}

// Beam.DoDamageTick asks DamageBeam.GetJuggernautPiercing every tick before
// choosing its native single-hit or piercing path. Preserve any native piercing
// result and add Stellar Converter piercing at the configured ranks.
[HarmonyPatch]
public static class LeviathanStellarConverterPiercingPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(DamageBeam),
            "GetJuggernautPiercing",
            Type.EmptyTypes
        );
    }

    public static void Postfix(DamageBeam __instance, ref bool __result)
    {
        LeviathanStellarConverter.ScalePiercing(__instance, ref __result);
    }
}

// Match the game's Striker Laser/Bolt/Torch Range implementation at the native
// MaxRange modifier boundary, while guarding to Stellar Converter's one source.
[HarmonyPatch]
public static class LeviathanStellarConverterRangePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Equippable),
            "ApplyModifier",
            new Type[]
            {
                typeof(Modifier.Type),
                typeof(float),
                typeof(bool),
                typeof(bool)
            }
        );
    }

    public static void Postfix(
        Equippable __instance,
        Modifier.Type __0,
        bool __3,
        ref float __result)
    {
        LeviathanStellarConverter.ScaleMaxRange(
            __instance,
            __0,
            __3,
            ref __result
        );
    }
}

// =============================================================================
// WIDTH: NATIVE BEAM RAYCAST -> MATCHING CIRCLECAST
// =============================================================================

[HarmonyPatch]
public static class LeviathanStellarConverterBeamRaycastPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "GetRaycastHit",
            new Type[]
            {
                typeof(bool),
                typeof(PhysicsController.Hit)
            }
        );
    }

    public static void Prefix(
        Beam __instance,
        ref LeviathanStellarConverter.BeamCastState __state)
    {
        __state = LeviathanStellarConverter.BeginBeamCast(__instance);
    }

    public static void Postfix(
        LeviathanStellarConverter.BeamCastState __state)
    {
        LeviathanStellarConverter.EndBeamCast(__state);
    }

    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanStellarConverter.ReplaceNativeBeamRaycasts(instructions);
    }
}

[HarmonyPatch]
public static class LeviathanStellarConverterBeamPiercingRaycastPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "GetAllPiercingHits",
            Type.EmptyTypes
        );
    }

    public static void Prefix(
        Beam __instance,
        ref LeviathanStellarConverter.BeamCastState __state)
    {
        __state = LeviathanStellarConverter.BeginBeamCast(__instance);
    }

    public static void Postfix(
        LeviathanStellarConverter.BeamCastState __state)
    {
        LeviathanStellarConverter.EndBeamCast(__state);
    }

    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return LeviathanStellarConverter.ReplaceNativeBeamRaycasts(instructions);
    }
}

// Beam.AdjustCurrentState is where vanilla writes widthMultiplier from maxWidth.
// Reapply the absolute Stellar width immediately after that native assignment,
// then again after DrawBeam as a render-time guard. ScaleVisualWidth is absolute,
// so the two hooks cannot compound.
[HarmonyPatch]
public static class LeviathanStellarConverterBeamStateWidthPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "AdjustCurrentState",
            new Type[]
            {
                typeof(float),
                typeof(PhysicsController.Hit)
            }
        );
    }

    public static void Postfix(Beam __instance)
    {
        LeviathanStellarConverter.ScaleVisualWidth(__instance);
    }
}

[HarmonyPatch(typeof(Beam), "DrawBeam")]
public static class LeviathanStellarConverterBeamVisualWidthPatch
{
    public static void Postfix(Beam __instance)
    {
        LeviathanStellarConverter.ScaleVisualWidth(__instance);
    }
}
