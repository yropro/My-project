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
// Normal release during charge cancels. Dying Star release after launch manually
// detonates the active seed. If ship CC/offline interrupts an already-started
// charge, that one shot remains committed and finishes its charge + output.

public static class LeviathanStellarConverterTuning
{
    // Baseline specialization profile. Source Laser native/rolled stats are
    // resolved first; these values establish Stellar Converter's baseline.
    // Tree damage/range percentages then modify this baseline additively.
    public const float BaselineChargeSeconds = 1.00f;
    public const float BaselinePulseSeconds = 1.00f;
    public const float BaselineDamageMultiplier = 3.20f;
    public const float BaselineWidthMultiplier = 10.00f;
    public const float BaselineRangeMultiplier = 1.10f;

    // Baseline charge presentation.
    public const float ChargeOrbStartingSizeFraction = 0.05f;
    public const float ChargeOrbOpacityMultiplier = 1.00f;

    // Structure presentation only. Mechanical per-body/per-tail values are
    // ordinary specialization knobs authored by the tree.
    //
    // Feeder width is expressed as a fraction of the resolved main Converter
    // beam width. Feeders ramp from Min -> Max across the actual charge-up and
    // are fully grown when charging completes.
    public const float ConvergenceFeederMinWidthFraction = 0.08f;
    public const float ConvergenceFeederMaxWidthFraction = 0.50f;
    public const float ConvergenceFeederOpacityMultiplier = 1.00f;

    // Existing Converter presentation/crit behavior retained by the new baseline.
    public const float BaselineCritChanceMultiplier = 1.10f;
    public const float BaselineCritDamageMultiplier = 1.00f;
    public const float BaselineDebuffChanceMultiplier = 1.00f;
    public const float BaselineBrightnessMultiplier = 0.91f;
    public const float HitboxWidthMultiplier = 1.20f;

    // Star Vortex displays 20 meters per Unity world unit.
    public const float WorldUnitsPerMeter = 1f / 20f;

    // Shared manifestation cadence. The three Deep Capacitors capstones all
    // wait this long after output before another charge can begin.
    public const float ManifestationCooldownSeconds = 3.00f;

    // Shared gravity behavior. PullStrength is a multiplier on the vanilla
    // Black Hole projectile's serialized pull power. 1.0 = native Black Hole feel.
    // The native Black Hole does not reduce pull against bosses.
    public const float GravityBossPullMultiplier = 1.00f;
    public const float GravityPullFalloffExponent = 0.70f;

    // Scales only the vanilla Black Hole target-speed amplification.
    // 1.00 = native max(1, speed - 1) behavior.
    // 0.00 = no extra pull from target velocity.
    public const float GravityVelocityScaling = 1.00f;

    // Singularity: lazy drifting gravity well + sustained Halo damage.
    public const float SingularityTravelSpeedMetersPerSecond = 45f;
    public const float SingularityLifetimeSeconds = 7.00f;
    public const float SingularityPullRadiusMeters = 160f;
    public const float SingularityPullStrength = 1.25f;
    public const float SingularityHaloRadiusMeters = 50f;
    public const float SingularityDamageFraction = 1.15f;
    public const float SingularityVisualScale = 0.45f;
    // Visible black-hole radius, independent from pull/halo radius.
    public const float SingularityVisualRadiusMeters = 40f;
    public const float SingularityHaloOpacity = 1.00f;

    // Dying Star: same gravity seed, then one delayed integrated Converter explosion.
    public const float DyingStarTravelSpeedMetersPerSecond = 45f;
    public const float DyingStarFuseSeconds = 5.00f;
    public const float DyingStarPullRadiusMeters = 160f;
    public const float DyingStarPullStrength = 1.25f;
    public const float DyingStarExplosionRadiusMeters = 125f;
    public const float DyingStarDamageFraction = 1.30f;

    // Manual detonation matures from this shared minimum to full power. The same
    // scale drives integrated damage, mechanical radius and rendered radius.
    // Exponent > 1 backloads growth and rewards holding the projectile longer.
    public const float DyingStarMinimumDetonationScale = 0.30f;
    public const float DyingStarDetonationCurveExponent = 2.00f;

    public const float DyingStarVisualScale = 0.80f;
    // Explicit visible black-hole radius; independent from pull radius.
    public const float DyingStarVisualRadiusMeters = 27f;
    public const float DyingStarExplosionExpansionSpeedMetersPerSecond = 80f;
    // Final rendered explosion radius. Mechanical damage radius remains the
    // independent DyingStarExplosionRadiusMeters value above.
    public const float DyingStarExplosionVisualRadiusMeters = 185f;
    public const float DyingStarExplosionVisualScale = 1.00f;
    public const float DyingStarExplosionVisualOpacity = 1.00f;
    public const float DyingStarExplosionSoundLeadSeconds = 0.50f;
    public const string DyingStarExplosionPrefabNameOverride = "";

    // Event Horizon: native Converter beam remains; an invisible gravity corridor
    // grows from the muzzle toward this independently-moving pull tip.
    public const float EventHorizonPullTipSpeedMetersPerSecond = 100f;
    public const float EventHorizonPullRadiusMeters = 160f;
    public const float EventHorizonPullStrength = 1.25f;
    // 0 keeps the gravity tip invisible. Set this above zero to render the
    // vanilla black-hole art at the moving pull tip with this radius in meters.
    public const float EventHorizonVisualRadiusMeters = 0.5f;

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
        public static readonly CoreSpecializationKnob ChargeTime =
            CoreSpecializationKnob.Flat(
                "stellar_converter.charge_time", "Charge Time", "s");

        public static readonly CoreSpecializationKnob PulseDuration =
            CoreSpecializationKnob.Flat(
                "stellar_converter.pulse_duration", "Pulse Duration", "s");

        public static readonly CoreSpecializationKnob FinalDamagePercent =
            CoreSpecializationKnob.Percent(
                "stellar_converter.final_damage_percent",
                "Final Converter Damage");

        public static readonly CoreSpecializationKnob WidthMultiplier =
            CoreSpecializationKnob.Flat(
                "stellar_converter.width_multiplier", "Width Multiplier", "x");

        public static readonly CoreSpecializationKnob CritChanceMultiplier =
            CoreSpecializationKnob.Flat(
                "stellar_converter.crit_chance_multiplier",
                "Critical Chance Multiplier",
                "x");

        public static readonly CoreSpecializationKnob CritChance =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.crit_chance", "Critical Chance");

        public static readonly CoreSpecializationKnob StatusChance =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.status_chance", "Status Chance");

        public static readonly CoreSpecializationKnob RandomBasicStatusChance =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.random_basic_status_chance",
                "Random Basic Status Chance");

        public static readonly CoreSpecializationKnob OffElementRandomStatusChance =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.off_element_random_status_chance",
                "Off-Element Random Status Chance");

        public static readonly CoreSpecializationKnob DebuffSpreadOnKillChance =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.debuff_spread_on_kill_chance",
                "Spread Debuffs on Kill");

        public static readonly CoreSpecializationKnob ForkTargets =
            CoreSpecializationKnob.Flat(
                "stellar_converter.fork_targets",
                "Fork Targets");

        public static readonly CoreSpecializationKnob ForkDamage =
            CoreSpecializationKnob.Flat(
                "stellar_converter.fork_damage",
                "Fork Damage",
                "x");

        public static readonly CoreSpecializationKnob ForkChainFraction =
            CoreSpecializationKnob.Flat(
                "stellar_converter.fork_chain_fraction",
                "Fork Chain Fraction",
                "x");

        public static readonly CoreSpecializationKnob ForkConeDegrees =
            CoreSpecializationKnob.Flat(
                "stellar_converter.fork_cone_degrees",
                "Fork Cone",
                "deg");

        public static readonly CoreSpecializationKnob BodySegmentDamagePercent =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.body_segment_damage_percent",
                "Damage per Body Segment");

        public static readonly CoreSpecializationKnob TailDamagePercent =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.tail_damage_percent",
                "Damage per Tail");

        public static readonly CoreSpecializationKnob ChainTargets =
            CoreSpecializationKnob.Flat(
                "stellar_converter.chain_targets", "Chain Targets");

        public static readonly CoreSpecializationKnob FinalRangePercent =
            CoreSpecializationKnob.Percent(
                "stellar_converter.final_range_percent", "Final Converter Range");

        public static readonly CoreSpecializationKnob HeatGenerationPercent =
            CoreSpecializationKnob.Percent(
                "stellar_converter.heat_generation_percent",
                "Converter Heat Generation");

        public static readonly CoreSpecializationKnob ManifestationCooldown =
            CoreSpecializationKnob.Flat(
                "stellar_converter.manifestation.cooldown",
                "Manifestation Cooldown",
                "s");

        public static readonly CoreSpecializationKnob GravityVelocityScaling =
            CoreSpecializationKnob.Flat(
                "stellar_converter.gravity.velocity_scaling",
                "Gravity Velocity Scaling",
                "x");

        // Manifestation tuning. Tree files can point at these like any other knobs.
        public static readonly CoreSpecializationKnob SingularitySpeed =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.speed", "Singularity Speed", "m/s");
        public static readonly CoreSpecializationKnob SingularityLifetime =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.lifetime", "Singularity Lifetime", "s");
        public static readonly CoreSpecializationKnob SingularityPullRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.pull_radius", "Singularity Pull Radius", "m");
        public static readonly CoreSpecializationKnob SingularityPullStrength =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.pull_strength", "Singularity Pull Strength", "x");
        public static readonly CoreSpecializationKnob SingularityVisualScale =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.visual_scale", "Singularity Visual Scale", "x");
        public static readonly CoreSpecializationKnob SingularityVisualRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.visual_radius", "Singularity Visual Radius", "m");
        public static readonly CoreSpecializationKnob SingularityHaloRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.singularity.halo_radius", "Singularity Halo Radius", "m");
        public static readonly CoreSpecializationKnob SingularityDamage =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.singularity.damage", "Singularity Integrated Damage");

        public static readonly CoreSpecializationKnob DyingStarSpeed =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.speed", "Dying Star Speed", "m/s");
        public static readonly CoreSpecializationKnob DyingStarFuse =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.fuse", "Dying Star Fuse", "s");
        public static readonly CoreSpecializationKnob DyingStarPullRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.pull_radius", "Dying Star Pull Radius", "m");
        public static readonly CoreSpecializationKnob DyingStarPullStrength =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.pull_strength", "Dying Star Pull Strength", "x");
        public static readonly CoreSpecializationKnob DyingStarVisualScale =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.visual_scale", "Dying Star Visual Scale", "x");
        public static readonly CoreSpecializationKnob DyingStarVisualRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.visual_radius", "Dying Star Visual Radius", "m");
        public static readonly CoreSpecializationKnob DyingStarExplosionRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.explosion_radius", "Dying Star Explosion Radius", "m");
        public static readonly CoreSpecializationKnob DyingStarExplosionVisualRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.dying_star.explosion_visual_radius",
                "Dying Star Explosion Visual Radius",
                "m");
        public static readonly CoreSpecializationKnob DyingStarDamage =
            CoreSpecializationKnob.PercentagePoints(
                "stellar_converter.dying_star.damage", "Dying Star Integrated Damage");

        public static readonly CoreSpecializationKnob EventHorizonTipSpeed =
            CoreSpecializationKnob.Flat(
                "stellar_converter.event_horizon.tip_speed", "Event Horizon Tip Speed", "m/s");
        public static readonly CoreSpecializationKnob EventHorizonPullRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.event_horizon.pull_radius", "Event Horizon Pull Radius", "m");
        public static readonly CoreSpecializationKnob EventHorizonPullStrength =
            CoreSpecializationKnob.Flat(
                "stellar_converter.event_horizon.pull_strength", "Event Horizon Pull Strength", "x");
        public static readonly CoreSpecializationKnob EventHorizonVisualRadius =
            CoreSpecializationKnob.Flat(
                "stellar_converter.event_horizon.visual_radius",
                "Event Horizon Tip Visual Radius",
                "m");

        // Boolean-like numeric knob so a multi-rank node can enable piercing only
        // at a specific rank without making runtime logic depend on a node id.
        public static readonly CoreSpecializationKnob Piercing =
            CoreSpecializationKnob.Flat(
                "stellar_converter.piercing", "Piercing");
    }

    public static class Flags
    {
        public static readonly CoreSpecializationFlag Continuous =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.continuous", "Continuous Conversion");

        public static readonly CoreSpecializationFlag SpectrumSaturation =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.spectrum_saturation",
                "Spectrum Saturation");

        public static readonly CoreSpecializationFlag ArcCascade =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.arc_cascade", "Arc Cascade");

        public static readonly CoreSpecializationFlag FractalCascade =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.fractal_cascade", "Fractal Cascade");

        public static readonly CoreSpecializationFlag Forking =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.forking", "Forking");

        public static readonly CoreSpecializationFlag Singularity =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.singularity", "Singularity");

        public static readonly CoreSpecializationFlag DyingStar =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.dying_star", "Dying Star");

        public static readonly CoreSpecializationFlag EventHorizon =
            CoreSpecializationFlag.Create(
                "stellar_converter.mode.event_horizon", "Event Horizon");

        public static readonly CoreSpecializationFlag Conduction =
            CoreSpecializationFlag.Create(
                "stellar_converter.structure.conduction", "Conduction");

        public static readonly CoreSpecializationFlag Convergence =
            CoreSpecializationFlag.Create(
                "stellar_converter.structure.convergence", "Convergence");
    }

    // Extra mechanical width only. 1.20 = hitbox radius reaches 20% farther
    // from the beam centerline than the rendered beam edge.
    private const float HitboxWidthMultiplier =
        LeviathanStellarConverterTuning.HitboxWidthMultiplier;

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

    private static readonly FieldInfo BeamChangeRateField =
        AccessTools.Field(typeof(Beam), "changeRate");

    private static readonly FieldInfo BeamHitObstructionsField =
        AccessTools.Field(typeof(Beam), "hitObstructions");

    private static readonly FieldInfo BeamDelayDamageTickField =
        AccessTools.Field(typeof(Beam), "delayDamageTick");

    private static readonly FieldInfo BeamGlobalLastHitField =
        AccessTools.Field(typeof(Beam), "globalLastHit");

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

    // Dying Star is one launch per physical trigger press. Once the seed launches,
    // holding the trigger cannot begin another charge until a real release occurs.
    private static bool dyingStarNeedsRelease;

    private static int manifestationShotSequence;

    // STELLAR CONVERTER DYNAMIC SLOT
    // byte 0  phase
    // byte 1  normalized phase progress
    //         Charging = charge progress; Firing = pulse progress
    // byte 2  projectile launch sequence
    // byte 3  flags: bit0 projectile active, bit1 Dying Star detonating
    // byte 4  normalized progress toward the projectile's natural end boundary
    // byte 5  Dying Star detonation scale (1.0 for non-Dying-Star states)
    // byte 6  Dying Star explosion expansion progress
    //
    // Width, range, manifestation selection, Convergence and all other static
    // presentation values come from the replicated specialization on the remote
    // Pilot. Gameplay remains owner-authoritative.

    private sealed class ConverterPresentationState
    {
        public GameObject chargeOrbVisual;
        public LineRenderer chargeOrbRenderer;
        public readonly List<GameObject> feederVisuals =
            new List<GameObject>();
        public readonly List<LineRenderer> feederRenderers =
            new List<LineRenderer>();
        public readonly List<GameShip> tailScratch =
            new List<GameShip>();

        // Presentation-only ramp. This belongs to each local/remote presentation
        // instance so co-op observers smooth their own visuals without affecting
        // authoritative gameplay state.
        public float feederGrowthProgress;

        public GameObject eventHorizonTipVisual;
        public Vector3 eventHorizonTipVisualBaseScale = Vector3.one;
    }

    private sealed class RemoteState
    {
        public Phase phase;
        public float phaseProgress;
        public bool hasNetworkState;
        public int shotSequence = -1;
        public bool projectileActive;
        public bool detonating;
        public float projectileProgress;
        public float detonationScale = 1f;
        public float explosionProgress;
        public GravityProjectileShot remoteVisualShot;
        public ForkShot forkShot;
        public readonly ConverterPresentationState presentation =
            new ConverterPresentationState();
    }

    private sealed class ForkShot
    {
        public Laser source;
        public GameShip owner;
        public Beam sourceBeam;
        public GameShip rootTarget;
        public Vector2 rootHitPosition;
        public readonly HashSet<GameShip> visitedTargets =
            new HashSet<GameShip>();
        public readonly List<ForkBranch> branches =
            new List<ForkBranch>();
        public bool repeatLocked;
    }

    private sealed class ForkBranch
    {
        public GameShip currentTarget;
        public readonly List<ForkSegment> segments =
            new List<ForkSegment>();
    }

    private sealed class ForkSegment
    {
        public Beam beam;
        public GameShip fromTarget;
        public GameShip target;
        public bool rootFork;
    }

    private sealed class ForkBeamState
    {
        public ForkShot shot;
        public ForkSegment segment;
        public readonly PhysicsController.Hit forcedHit =
            new PhysicsController.Hit();
    }

    private static ForkShot LocalForkShot;

    private static readonly Dictionary<Beam, ForkBeamState> ForkBeamStates =
        new Dictionary<Beam, ForkBeamState>();

    private static readonly List<GameShip> ForkCandidateScratch =
        new List<GameShip>();

    private static readonly HashSet<GameShip> ForkCandidateSeenScratch =
        new HashSet<GameShip>();

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
        public float FinalDamageMultiplier;
        public float FinalRangeMultiplier;
        public float AdditionalHeatFraction;
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
        public bool Conduction;
        public bool Convergence;
        public float RandomBasicStatusChance;
        public float OffElementRandomStatusChance;
        public float DebuffSpreadOnKillChance;
        public bool SpectrumSaturation;
        public bool Forking;
        public int ForkTargets;
        public float ForkDamageFraction;
        public float ForkChainFraction;
        public float ForkConeDegrees;
        public float ManifestationCooldownSeconds;
        public float GravityVelocityScaling;

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
        public float DyingStarMinimumDetonationScale;
        public float DyingStarDetonationCurveExponent;

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
        public Vector2 launchOrigin;
        public Vector2 direction;
        public float traveled;
        public float maxTravel;
        public float travelSpeed;
        public float age;
        public float lifetime;
        public float pullRadius;
        public float pullStrength;
        public float pullFalloffExponent;
        public float velocityScaling;
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
        public float minimumDetonationScale = 1f;
        public float detonationCurveExponent = 1f;
        public float detonationScale = 1f;
        public bool exploding;
        public bool explosionSoundPlayed;

        public GameObject blackHoleVisual;
        public Vector3 blackHoleBaseScale = Vector3.one;
        public GameObject haloVisual;
        public float haloVisualBaseRadius = 1f;
        public LineRenderer fallbackRing;
        public ExplosiveArea explosionVisual;
        public float explosionVisualTargetRadius;
        public readonly HashSet<GameShip> hitShips = new HashSet<GameShip>();
    }

    private static GravityProjectileShot gravityProjectileShot;
    private static float eventHorizonPullTipDistance;

    private static readonly ConverterPresentationState LocalPresentation =
        new ConverterPresentationState();

    private static readonly FieldInfo BeamWeaponBeamScriptField =
        AccessTools.Field(typeof(BeamWeapon), "beamScript");

    private static readonly FieldInfo BeamWeaponChainTargetsField =
        AccessTools.Field(typeof(BeamWeapon), "chainTargets");

    private static readonly FieldInfo MirrorGameObjectField =
        AccessTools.Field(typeof(Equippable), "mirrorGameObject");

    private static readonly FieldInfo DebuffAndDirectImmuneField =
        AccessTools.Field(typeof(GameShip), "debuffAndDirectImmune");

    private static readonly FieldInfo GameShipHeatPerSecondField =
        AccessTools.Field(typeof(GameShip), "heatPerSecond");

    private static readonly MethodInfo RouteDamageMethod =
        typeof(NetCombat)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
                m.Name == "RouteDamage" &&
                m.GetParameters().Length == 14 &&
                m.GetParameters()[2].ParameterType == typeof(DamageData[]) &&
                m.GetParameters()[4].ParameterType == typeof(int));

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

    private sealed class ResolvedCacheEntry
    {
        public int configurationRevision;
        public int registryRevision;
        public int anatomyRevision;
        public ResolvedState state;
    }

    private static readonly Dictionary<Pilot, ResolvedCacheEntry>
        ResolvedStateCache =
            new Dictionary<Pilot, ResolvedCacheEntry>();

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
    // SOURCE / SPECIALIZATION
    // =========================================================================

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
        out GameShip player)
    {
        player = laser == null ? null : laser.parentShip;

        if (laser == null ||
            player == null ||
            WorldController.instance == null ||
            !ReferenceEquals(
                WorldController.instance.GetCurrentPlayerShip(),
                player) ||
            !IsSpecializationProfile(player))
        {
            return false;
        }

        return ReferenceEquals(FindSourceLaser(player), laser);
    }

    private static bool TryGetRemoteSourceContext(
        Laser laser,
        out GameShip player)
    {
        player = laser == null ? null : laser.parentShip;

        if (laser == null ||
            player == null ||
            !player.IsRemotePlayer() ||
            !IsSpecializationProfile(player))
        {
            return false;
        }

        return ReferenceEquals(FindSourceLaser(player), laser);
    }

    private static bool IsFiringSource(BeamWeapon beamWeapon)
    {
        Laser laser = beamWeapon as Laser;
        GameShip player;

        if (laser == null)
            return false;

        if (ReferenceEquals(sourceLaser, laser) &&
            phase == Phase.Firing &&
            TryGetContext(laser, out player))
        {
            return true;
        }

        RemoteState remoteState;

        return TryGetRemoteSourceContext(laser, out player) &&
            RemoteStates.TryGetValue(laser, out remoteState) &&
            remoteState.phase == Phase.Firing;
    }

    private static bool IsSpecializationProfile(GameShip player)
    {
        if (player == null || !player.IsAnyPlayerShip())
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey) < 1)
        {
            return false;
        }

        bool localOwner =
            WorldController.instance != null &&
            ReferenceEquals(
                WorldController.instance.GetCurrentPlayerShip(),
                player);

        bool synchronizedRemote =
            player.IsRemotePlayer() &&
            CoreNetwork.HasSynchronizedSpecialization(player, CoreClassId.Leviathan);

        return (localOwner || synchronizedRemote) &&
            CoreSpecializationRuntime.IsTreeActive(
                pilot,
                LeviathanStellarConverterTree.TreeId);
    }

    private static float ApplyKnob(
        Pilot pilot,
        CoreSpecializationKnob knob,
        float baseValue)
    {
        return pilot == null
            ? baseValue
            : CoreSpecializationRuntime.ApplyKnob(
                pilot,
                knob,
                baseValue);
    }

    private static ResolvedState GetResolvedState(GameShip player)
    {
        if (!IsSpecializationProfile(player))
            return null;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null)
            return null;

        LeviathanGrowth.AnatomySnapshot anatomy =
            LeviathanGrowth.GetAnatomy(player);
        int bodySegments = anatomy.BodySegmentCount;
        int tails = anatomy.TailCount;

        int configurationRevision =
            CoreSpecializationRuntime.ConfigurationRevision;
        int registryRevision =
            CoreSpecializationRegistry.Revision;
        int anatomyRevision = anatomy.Revision;

        ResolvedCacheEntry cached;
        if (ResolvedStateCache.TryGetValue(pilot, out cached) &&
            cached != null &&
            cached.state != null &&
            cached.configurationRevision == configurationRevision &&
            cached.registryRevision == registryRevision &&
            cached.anatomyRevision == anatomyRevision)
        {
            return cached.state;
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
        state.FinalDamageMultiplier = Mathf.Max(
            0f,
            CoreSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.FinalDamagePercent));
        state.DamageMultiplier =
            LeviathanStellarConverterTuning.BaselineDamageMultiplier *
            state.FinalDamageMultiplier;

        state.WidthMultiplier = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.WidthMultiplier,
            LeviathanStellarConverterTuning.BaselineWidthMultiplier));

        state.FinalRangeMultiplier = Mathf.Max(
            0f,
            CoreSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.FinalRangePercent));
        state.RangeMultiplier =
            LeviathanStellarConverterTuning.BaselineRangeMultiplier *
            state.FinalRangeMultiplier;

        state.AdditionalHeatFraction = Mathf.Max(
            -1f,
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.HeatGenerationPercent));

        state.CritChanceMultiplier = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.CritChanceMultiplier,
            LeviathanStellarConverterTuning.BaselineCritChanceMultiplier));
        state.CritChanceBonus = CoreSpecializationRuntime.GetKnobFlat(
            pilot,
            Knobs.CritChance);
        state.CritDamageMultiplier =
            LeviathanStellarConverterTuning.BaselineCritDamageMultiplier;
        state.DebuffChanceMultiplier =
            LeviathanStellarConverterTuning.BaselineDebuffChanceMultiplier;
        state.DebuffChanceBonus = CoreSpecializationRuntime.GetKnobFlat(
            pilot,
            Knobs.StatusChance);
        state.RandomBasicStatusChance = Mathf.Clamp01(
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.RandomBasicStatusChance));
        state.OffElementRandomStatusChance = Mathf.Clamp01(
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.OffElementRandomStatusChance));
        state.DebuffSpreadOnKillChance = Mathf.Clamp01(
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.DebuffSpreadOnKillChance));
        state.ForkTargets = Mathf.Max(
            0,
            Mathf.RoundToInt(CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.ForkTargets)));
        state.ForkDamageFraction = Mathf.Max(
            0f,
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.ForkDamage));
        state.ForkChainFraction = Mathf.Max(
            0f,
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.ForkChainFraction));
        state.ForkConeDegrees = Mathf.Clamp(
            CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.ForkConeDegrees),
            0f,
            360f);
        state.BrightnessMultiplier =
            LeviathanStellarConverterTuning.BaselineBrightnessMultiplier;
        state.ExtraChainTargets = Mathf.Max(
            0,
            Mathf.RoundToInt(CoreSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.ChainTargets)));

        state.Continuous = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.Continuous);
        state.SpectrumSaturation = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.SpectrumSaturation);
        bool arcCascade = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.ArcCascade);
        bool fractalCascade = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.FractalCascade);
        state.Forking = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.Forking);
        state.Singularity = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.Singularity);
        state.DyingStar = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.DyingStar);
        state.EventHorizon = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.EventHorizon);
        state.Conduction = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.Conduction);
        state.Convergence = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.Convergence);

        float structureDamageBonus = 0f;

        if (state.Conduction)
        {
            float perBodySegment =
                CoreSpecializationRuntime.GetKnobFlat(
                    pilot,
                    Knobs.BodySegmentDamagePercent);
            structureDamageBonus += bodySegments * perBodySegment;
        }

        if (state.Convergence)
        {
            float perTail =
                CoreSpecializationRuntime.GetKnobFlat(
                    pilot,
                    Knobs.TailDamagePercent);
            structureDamageBonus += tails * perTail;
        }

        if (!Mathf.Approximately(structureDamageBonus, 0f))
        {
            // Structural flags provide anatomy-dependent behavior only. Their
            // numeric values are ordinary tree knobs in the shared damage bucket.
            state.FinalDamageMultiplier = Mathf.Max(
                0f,
                state.FinalDamageMultiplier + structureDamageBonus);
            state.DamageMultiplier =
                LeviathanStellarConverterTuning.BaselineDamageMultiplier *
                state.FinalDamageMultiplier;
        }

        state.ManifestationCooldownSeconds = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.ManifestationCooldown,
            LeviathanStellarConverterTuning.ManifestationCooldownSeconds));
        state.GravityVelocityScaling = Mathf.Max(0f, ApplyKnob(
            pilot,
            Knobs.GravityVelocityScaling,
            LeviathanStellarConverterTuning.GravityVelocityScaling));

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
        state.DyingStarMinimumDetonationScale = Mathf.Clamp01(
            LeviathanStellarConverterTuning.DyingStarMinimumDetonationScale);
        state.DyingStarDetonationCurveExponent = Mathf.Max(
            0.01f,
            LeviathanStellarConverterTuning.DyingStarDetonationCurveExponent);

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

        state.Piercing = CoreSpecializationRuntime.GetKnobFlat(
            pilot,
            Knobs.Piercing) >= 0.5f;
        state.SuppressPiercing =
            state.Continuous || arcCascade || fractalCascade;

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            ResolvedStateCache[pilot] = cached;
        }

        cached.configurationRevision = configurationRevision;
        cached.registryRevision = registryRevision;
        cached.anatomyRevision = anatomyRevision;
        cached.state = state;
        return state;
    }

    private static ResolvedState GetResolvedState(Laser laser)
    {
        return GetResolvedState(laser == null ? null : laser.parentShip);
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

    private static bool IsExplicitPlayerRelease()
    {
        // StopActivating(null) is a global shutdown/interrupt path, not a player
        // choosing to detonate Dying Star.
        return !(inputStopByType && !inputStopTypeHasValue);
    }

    public static bool InterceptNativeActivate(Activatable activatable)
    {
        if (inputStartDepth <= 0)
            return false;

        Laser laser = activatable as Laser;
        GameShip player;
        if (laser == null || laser.parentShip != inputStartShip)
            return false;

        bool localSource = TryGetContext(laser, out player);
        bool remoteSource = false;

        if (!localSource)
            remoteSource = TryGetRemoteSourceContext(laser, out player);

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
                SetNativeActive(
                    laser,
                    existing.hasNetworkState &&
                    existing.phase == Phase.Firing &&
                    IsRemoteBeamOutput(GetResolvedState(player)));
            }

            return true;
        }

        if (localSource)
        {
            SelectSource(laser);
            inputHeld = true;
            ResolvedState resolved = GetResolvedState(player);

            if (resolved.DyingStar && dyingStarNeedsRelease)
            {
                SetNativeActive(laser, false);
                return true;
            }

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
        SetNativeActive(
            laser,
            state.hasNetworkState &&
            state.phase == Phase.Firing &&
            IsRemoteBeamOutput(GetResolvedState(player)));
        return true;
    }

    public static bool InterceptNativeDeactivate(Activatable activatable)
    {
        if (inputStopDepth <= 0)
            return false;

        Laser laser = activatable as Laser;
        GameShip player;
        if (laser == null || laser.parentShip != inputStopShip)
            return false;

        bool localSource = TryGetContext(laser, out player);
        bool remoteSource = false;

        if (!localSource)
            remoteSource = TryGetRemoteSourceContext(laser, out player);

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
                SetNativeActive(
                    laser,
                    existing.hasNetworkState &&
                    existing.phase == Phase.Firing &&
                    IsRemoteBeamOutput(GetResolvedState(player)));
            }

            return true;
        }

        if (localSource)
        {
            SelectSource(laser);
            ResolvedState resolved = GetResolvedState(player);

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

            bool explicitPlayerRelease = IsExplicitPlayerRelease();
            bool manualDyingStarDetonation =
                resolved.DyingStar &&
                explicitPlayerRelease &&
                gravityProjectileShot != null &&
                gravityProjectileShot.mode == GravityProjectileMode.DyingStar &&
                ReferenceEquals(gravityProjectileShot.source, laser) &&
                !gravityProjectileShot.exploding;

            inputHeld = false;

            // A genuine release rearms Dying Star for the next press whether the
            // current seed detonates manually now or had already detonated naturally.
            if (resolved.DyingStar && explicitPlayerRelease)
                dyingStarNeedsRelease = false;

            if (manualDyingStarDetonation)
                BeginDyingStarExplosion(gravityProjectileShot);

            if (phase == Phase.Charging && !forceCompleteCurrentShot)
            {
                phase = Phase.Idle;
                phaseTimer = 0f;
                SetNativeActive(laser, false);
            }

            return true;
        }

        RemoteState state = GetRemoteState(laser);
        SetNativeActive(
            laser,
            state.hasNetworkState &&
            state.phase == Phase.Firing &&
            IsRemoteBeamOutput(GetResolvedState(player)));
        return true;
    }

    private static bool IsRemoteBeamOutput(ResolvedState resolved)
    {
        return resolved != null &&
            !resolved.Singularity &&
            !resolved.DyingStar;
    }

    private static float GetPhaseProgress(ResolvedState resolved)
    {
        if (resolved == null)
            return 0f;

        if (phase == Phase.Charging && resolved.ChargeSeconds > 0.0001f)
        {
            return Mathf.Clamp01(
                phaseTimer / resolved.ChargeSeconds);
        }

        if (phase == Phase.Firing && resolved.PulseSeconds > 0.0001f)
        {
            return Mathf.Clamp01(
                phaseTimer / resolved.PulseSeconds);
        }

        return 0f;
    }

    private static float GetProjectileNaturalProgress(
        GravityProjectileShot shot)
    {
        if (shot == null)
            return 0f;

        float timeProgress = shot.lifetime <= 0.0001f
            ? 1f
            : shot.age / shot.lifetime;
        float rangeProgress = shot.maxTravel <= 0.0001f
            ? 1f
            : shot.traveled / shot.maxTravel;

        return Mathf.Clamp01(Mathf.Max(timeProgress, rangeProgress));
    }

    private static float GetDyingStarExplosionProgress(
        GravityProjectileShot shot)
    {
        if (shot == null ||
            shot.mode != GravityProjectileMode.DyingStar ||
            !shot.exploding ||
            shot.explosionRadius <= 0.0001f)
        {
            return 0f;
        }

        return Mathf.Clamp01(
            shot.currentExplosionRadius / shot.explosionRadius);
    }

    private static void PublishNetworkState(ResolvedState resolved)
    {
        if (resolved == null)
            return;

        bool projectileActive = gravityProjectileShot != null;
        if (phase == Phase.Idle && !projectileActive)
            return;

        CoreNetwork.SlotWriter writer =
            CoreNetwork.BeginSlot(
                CoreNetwork.SlotStellarConverter);

        writer.Byte((byte)phase);
        writer.Percent(GetPhaseProgress(resolved));
        writer.Sequence(manifestationShotSequence);
        writer.Flags(
            projectileActive,
            projectileActive && gravityProjectileShot.exploding);
        writer.Percent(
            GetProjectileNaturalProgress(gravityProjectileShot));
        writer.Percent(
            projectileActive &&
            gravityProjectileShot.mode == GravityProjectileMode.DyingStar
                ? gravityProjectileShot.detonationScale
                : 1f);
        writer.Percent(
            GetDyingStarExplosionProgress(gravityProjectileShot));

        CoreNetwork.EndSlot(writer);
    }

    private static bool ReadRemoteNetworkState(
        Laser laser,
        GameShip player,
        ResolvedState resolved,
        RemoteState state)
    {
        if (laser == null || player == null || resolved == null || state == null)
            return false;

        CoreNetwork.SlotReader reader;
        if (!CoreNetwork.TryReadSlot(
                player,
                CoreNetwork.SlotStellarConverter,
                out reader))
        {
            state.hasNetworkState = false;
            state.phase = Phase.Idle;
            state.phaseProgress = 0f;
            state.projectileActive = false;
            state.detonating = false;
            state.projectileProgress = 0f;
            state.detonationScale = 1f;
            state.explosionProgress = 0f;
            DestroyRemoteGravityVisual(state);
            HideConverterPresentation(state.presentation);
            SetNativeActive(laser, false);
            return false;
        }

        int rawPhase = reader.Byte();
        Phase incomingPhase =
            rawPhase >= (int)Phase.Idle && rawPhase <= (int)Phase.Recovery
                ? (Phase)rawPhase
                : Phase.Idle;

        float incomingPhaseProgress = reader.Percent();
        int incomingSequence = reader.Sequence();
        byte flags = reader.FlagsByte();
        bool incomingProjectileActive = (flags & (1 << 0)) != 0;
        bool incomingDetonating = (flags & (1 << 1)) != 0;
        float incomingProjectileProgress = reader.Percent();
        float incomingDetonationScale = reader.Percent();
        float incomingExplosionProgress = reader.Percent();

        bool phaseChanged =
            !state.hasNetworkState ||
            state.phase != incomingPhase;
        bool sequenceChanged =
            state.hasNetworkState &&
            state.shotSequence != incomingSequence;

        state.hasNetworkState = true;
        state.phase = incomingPhase;
        state.phaseProgress = phaseChanged
            ? incomingPhaseProgress
            : Mathf.Max(state.phaseProgress, incomingPhaseProgress);
        state.shotSequence = incomingSequence;
        state.projectileActive = incomingProjectileActive;
        state.detonating = incomingDetonating;
        state.projectileProgress = incomingProjectileProgress;
        state.detonationScale = incomingDetonationScale;
        state.explosionProgress = incomingExplosionProgress;

        bool projectileMode = resolved.Singularity || resolved.DyingStar;

        if (incomingProjectileActive && projectileMode)
        {
            bool wrongMode =
                state.remoteVisualShot != null &&
                ((resolved.Singularity &&
                  state.remoteVisualShot.mode != GravityProjectileMode.Singularity) ||
                 (resolved.DyingStar &&
                  state.remoteVisualShot.mode != GravityProjectileMode.DyingStar));

            if (sequenceChanged || wrongMode || state.remoteVisualShot == null)
            {
                SpawnRemoteGravityVisual(
                    laser,
                    state,
                    resolved);
            }

            ReconcileRemoteGravityProgress(
                state.remoteVisualShot,
                incomingProjectileProgress);

            if (resolved.DyingStar &&
                incomingDetonating &&
                state.remoteVisualShot != null &&
                !state.remoteVisualShot.exploding)
            {
                BeginRemoteDyingStarExplosion(
                    state.remoteVisualShot,
                    incomingDetonationScale);
            }

            if (resolved.DyingStar &&
                incomingDetonating &&
                state.remoteVisualShot != null &&
                state.remoteVisualShot.exploding)
            {
                state.remoteVisualShot.currentExplosionRadius =
                    Mathf.Max(
                        state.remoteVisualShot.currentExplosionRadius,
                        Mathf.Clamp01(incomingExplosionProgress) *
                            state.remoteVisualShot.explosionRadius);
            }
        }
        else
        {
            DestroyRemoteGravityVisual(state);
        }

        bool beamOutput =
            incomingPhase == Phase.Firing &&
            IsRemoteBeamOutput(resolved);

        SetNativeActive(laser, beamOutput);
        return true;
    }

    private static void AdvanceRemotePresentationProgress(
        RemoteState state,
        ResolvedState resolved)
    {
        if (state == null || !state.hasNetworkState || resolved == null)
            return;

        float duration = 0f;

        if (state.phase == Phase.Charging)
            duration = resolved.ChargeSeconds;
        else if (state.phase == Phase.Firing)
            duration = resolved.PulseSeconds;

        if (duration <= 0.0001f)
            return;

        // The 20 Hz slot remains authoritative for phase transitions. Progress
        // simply advances between snapshots so charge/tip VFX stay smooth; a new
        // snapshot may move it forward, but repeated reads never rewind it.
        state.phaseProgress = Mathf.Clamp01(
            state.phaseProgress + Time.fixedDeltaTime / duration);
    }

    public static void PrepareFixedUpdate(Laser laser)
    {
        GameShip player;
        if (TryGetContext(laser, out player))
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
            ResolvedState resolved = GetResolvedState(player);
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

        if (TryGetRemoteSourceContext(laser, out player))
        {
            RemoteState state = GetRemoteState(laser);

            if (IsTerminalStopped(player))
            {
                ResetRemoteSource(laser);
                return;
            }

            ResolvedState resolved = GetResolvedState(player);
            ReadRemoteNetworkState(
                laser,
                player,
                resolved,
                state);
            AdvanceRemotePresentationProgress(state, resolved);
            UpdateRemoteGravityVisual(state);
            return;
        }

        if (ReferenceEquals(sourceLaser, laser))
            ResetLocalSource();

        if (RemoteStates.ContainsKey(laser))
            ResetRemoteSource(laser);
    }


    public static void CompleteFixedUpdate(Laser laser)
    {
        GameShip player;
        if (ReferenceEquals(sourceLaser, laser) &&
            TryGetContext(laser, out player))
        {
            AdvanceLocalState(laser);
            ResolvedState resolved = GetResolvedState(player);
            UpdateForking(
                ref LocalForkShot,
                laser,
                player,
                resolved,
                phase);
            PublishNetworkState(resolved);
            UpdateConverterPresentation(
                LocalPresentation,
                laser,
                player,
                resolved,
                phase,
                GetPhaseProgress(resolved));
            return;
        }

        if (TryGetRemoteSourceContext(laser, out player))
        {
            RemoteState state;
            if (!RemoteStates.TryGetValue(laser, out state))
                return;

            ResolvedState resolved = GetResolvedState(player);
            UpdateForking(
                ref state.forkShot,
                laser,
                player,
                resolved,
                state.phase);
            UpdateConverterPresentation(
                state.presentation,
                laser,
                player,
                resolved,
                state.phase,
                state.phaseProgress);
        }
    }


    private static void AdvanceLocalState(Laser laser)
    {
        GameShip player = laser == null ? null : laser.parentShip;
        ResolvedState resolved = GetResolvedState(player);

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

                    // Consume this press at launch. Recovery may finish while the
                    // mouse is still held, but another Dying Star cannot charge
                    // until InterceptNativeDeactivate observes a real release.
                    dyingStarNeedsRelease = true;
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
                bool queueNext =
                    inputHeld &&
                    !forceCompleteCurrentShot &&
                    !(resolved.DyingStar && dyingStarNeedsRelease);

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
            DestroyEventHorizonTipVisual(LocalPresentation);
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
            DestroyEventHorizonTipVisual(LocalPresentation);
            SetNativeActive(laser, false);
        }
    }


    private static void SelectSource(Laser laser)
    {
        if (ReferenceEquals(sourceLaser, laser))
            return;

        DestroyGravityProjectileShot();
        DestroyEventHorizonTipVisual(LocalPresentation);
        DestroyConverterPresentation(LocalPresentation);
        DestroyForkShot(ref LocalForkShot);

        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = laser;
        eventHorizonPullTipDistance = 0f;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
        forceCompleteCurrentShot = false;
        dyingStarNeedsRelease = false;
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
        DestroyEventHorizonTipVisual(LocalPresentation);
        DestroyConverterPresentation(LocalPresentation);
        DestroyForkShot(ref LocalForkShot);

        if (sourceLaser != null)
            SetNativeActive(sourceLaser, false);

        sourceLaser = null;
        eventHorizonPullTipDistance = 0f;
        phase = Phase.Idle;
        phaseTimer = 0f;
        inputHeld = false;
        forceCompleteCurrentShot = false;
        dyingStarNeedsRelease = false;
    }

    private static void ResetRemoteSource(Laser laser)
    {
        if (laser == null)
            return;

        RemoteState state;
        if (RemoteStates.TryGetValue(laser, out state))
        {
            DestroyRemoteGravityVisual(state);
            DestroyConverterPresentation(state.presentation);
            DestroyForkShot(ref state.forkShot);
        }

        GameShip remoteShip = laser.parentShip;
        Pilot remotePilot = remoteShip == null
            ? null
            : GameShip.GetPlayerSourcePilot(remoteShip);
        if (remotePilot != null)
            ResolvedStateCache.Remove(remotePilot);

        SetNativeActive(laser, false);
        RemoteStates.Remove(laser);
    }


    public static void ForgetRemoteShip(GameShip remoteShip)
    {
        if (remoteShip == null)
            return;

        while (true)
        {
            Laser sourceToRemove = null;

            foreach (KeyValuePair<Laser, RemoteState> pair in RemoteStates)
            {
                if (pair.Key != null &&
                    ReferenceEquals(pair.Key.parentShip, remoteShip))
                {
                    sourceToRemove = pair.Key;
                    break;
                }
            }

            if (sourceToRemove == null)
                break;

            ResetRemoteSource(sourceToRemove);
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(remoteShip);
        if (pilot != null)
            ResolvedStateCache.Remove(pilot);
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
            RemoteState remote = pair.Value;
            DestroyRemoteGravityVisual(remote);
            DestroyConverterPresentation(remote.presentation);
            DestroyForkShot(ref remote.forkShot);

            if (pair.Key != null)
                SetNativeActive(pair.Key, false);
        }

        RemoteStates.Clear();
        ResolvedStateCache.Clear();
        manifestationShotSequence = 0;
        OriginalBeamColors.Clear();

        if (cachedGravityFallbackMaterial != null)
        {
            UnityEngine.Object.Destroy(cachedGravityFallbackMaterial);
            cachedGravityFallbackMaterial = null;
        }

        cachedBlackHoleVisualPrefab = null;
        searchedBlackHoleVisualPrefab = false;
        cachedNativeBlackHolePullPower = 0f;
        cachedNativeBlackHolePullRadius = 0f;

        cachedHaloFieldPrefab = null;
        cachedHaloFieldRadius = 1f;
        searchedHaloField = false;

        cachedExplosiveAreaPrefab = null;
        searchedExplosiveAreaPrefab = false;
    }

    // Native active is an implementation detail of the selected Laser. Co-op
    // presentation is published through CoreNetwork.SlotStellarConverter;
    // remote replicas never infer Converter phase from native active bits.

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
        if (TryGetContext(laser, out player))
        {
            return ReferenceEquals(sourceLaser, laser) &&
                phase == Phase.Firing &&
                !IsTerminalStopped(player);
        }

        if (TryGetRemoteSourceContext(laser, out player))
        {
            // Remote Converter beams are reconstructed presentation only. The
            // owner already performs authoritative damage and hit registration.
            return false;
        }

        return true;
    }


    // =========================================================================
    // HEAT
    // =========================================================================

    public struct HeatRateState
    {
        public bool changed;
        public float originalHeatPerSecond;
    }

    // Tree heat is an additive percentage relative to the source Laser's
    // baseline Converter heat contribution. Positive and negative values combine.
    public static HeatRateState PrepareShipHeatRate(GameShip player)
    {
        HeatRateState state = new HeatRateState();

        if (player == null || GameShipHeatPerSecondField == null)
            return state;

        if (!IsSpecializationProfile(player))
            return state;

        Laser laser = FindSourceLaser(player);
        if (laser == null || laser.durability == 0 || !laser.IsActive())
            return state;

        ResolvedState resolved = GetResolvedState(player);
        if (Mathf.Approximately(resolved.AdditionalHeatFraction, 0f))
            return state;

        object raw = GameShipHeatPerSecondField.GetValue(player);
        if (!(raw is float))
            return state;

        float original = (float)raw;
        float adjusted =
            original +
            laser.heatPerSecond * resolved.AdditionalHeatFraction;

        if (Mathf.Approximately(adjusted, original))
            return state;

        state.changed = true;
        state.originalHeatPerSecond = original;
        GameShipHeatPerSecondField.SetValue(player, Mathf.Max(0f, adjusted));
        return state;
    }

    public static void RestoreShipHeatRate(
        GameShip player,
        HeatRateState state)
    {
        if (!state.changed ||
            player == null ||
            GameShipHeatPerSecondField == null)
        {
            return;
        }

        GameShipHeatPerSecondField.SetValue(
            player,
            state.originalHeatPerSecond);
    }

    private static void ApplyManifestationHeat(
        GameShip player,
        Laser laser,
        ResolvedState resolved)
    {
        if (player == null ||
            laser == null ||
            resolved == null ||
            laser.heatPerSecond <= 0f)
        {
            return;
        }

        // Projectile manifestations replace the native beam, so pay one full
        // resolved Converter pulse's baseline heat at launch. Tree heat is an
        // additive percentage against that same baseline:
        // -10% => 0.90x, +25% and -10% together => 1.15x.
        float heatMultiplier =
            Mathf.Max(0f, 1f + resolved.AdditionalHeatFraction);

        float manifestationHeat =
            laser.heatPerSecond *
            Mathf.Max(0f, resolved.PulseSeconds) *
            heatMultiplier;

        if (manifestationHeat <= 0f)
            return;

        float capacity = Mathf.Max(0f, player.HeatCapacity);
        player.heat = Mathf.Min(
            capacity,
            player.heat + manifestationHeat);

        if (capacity > 0f &&
            player.heat >= capacity &&
            !player.HasStatusEffect(StatusEffect.Type.Overheated))
        {
            player.AddStatusEffect(new OverheatedStatusEffect());
        }
    }

    // =========================================================================
    // NATIVE STAT SCALING
    // =========================================================================

    public static void ScaleDamagePacket(
        BeamWeapon beamWeapon,
        ref DamageData[] damageData)
    {
        if (!IsFiringSource(beamWeapon) ||
            damageData == null ||
            damageData.Length == 0)
        {
            return;
        }

        Laser laser = beamWeapon as Laser;
        float multiplier = GetResolvedState(laser).DamageMultiplier;

        if (Mathf.Approximately(multiplier, 1f))
            return;

        DamageData[] scaled = new DamageData[damageData.Length];

        for (int i = 0; i < damageData.Length; i++)
        {
            DamageData datum = damageData[i];
            // Native chain beams reduce per-tick damage but intentionally keep
            // their source DPS metadata for status-effect strength. Preserve
            // that behavior and correct only the actual damage amount.
            datum.damage *= multiplier;
            scaled[i] = datum;
        }

        damageData = scaled;
    }

    public static void ScaleCritChance(BeamWeapon beamWeapon, ref float value)
    {
        if (!IsFiringSource(beamWeapon))
            return;

        Laser laser = beamWeapon as Laser;
        ResolvedState resolved = GetResolvedState(laser);
        value = Mathf.Clamp01(
            value * resolved.CritChanceMultiplier + resolved.CritChanceBonus
        );
    }

    public static void ScaleCritModifier(BeamWeapon beamWeapon, ref float value)
    {
        if (!IsFiringSource(beamWeapon))
            return;

        Laser laser = beamWeapon as Laser;
        value *= GetResolvedState(laser).CritDamageMultiplier;
    }

    public static void ScaleDebuffChance(BeamWeapon beamWeapon, ref float value)
    {
        if (!IsFiringSource(beamWeapon))
            return;

        Laser laser = beamWeapon as Laser;
        ResolvedState resolved = GetResolvedState(laser);
        value = Mathf.Clamp01(
            value * resolved.DebuffChanceMultiplier + resolved.DebuffChanceBonus
        );
    }

    // Native Beam.DoDamageTick recomputes piercing every damage tick as
    // basePiercing || GetJuggernautPiercing(). Apply Converter suppression to that
    // native decision so vanilla piercing damage, hit ordering, visuals, chaining
    // and attribution remain in control.
    public static BeamPiercingState PrepareBeamPiercing(Beam beam)
    {
        BeamPiercingState state = new BeamPiercingState();
        if (beam == null || BeamBasePiercingField == null)
            return state;

        Laser laser;
        if (!TryGetBeamContext(beam, out laser) ||
            !GetResolvedState(laser).SuppressPiercing)
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
        if (!TryGetBeamContext(beam, out laser))
            return;

        ResolvedState resolved = GetResolvedState(laser);
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
        bool valid = TryGetContext(laser, out player);
        if (!valid)
            valid = TryGetRemoteSourceContext(laser, out player);
        if (!valid)
            return;

        ResolvedState resolved = GetResolvedState(laser);

        // Forking replaces the native one-child chain graph with three managed
        // native DamageBeam roots. Each root owns its reduced chain budget.
        if (resolved.Forking)
        {
            value = 0;
            return;
        }

        // Converter tree chain count is additive to the source laser's native
        // resolved chain count. This preserves chain-capable source weapons
        // instead of replacing their native ChainCount progression.
        if (resolved.ExtraChainTargets != 0)
        {
            value = Mathf.Max(
                0,
                value + resolved.ExtraChainTargets);
        }
    }

    private static int GetNativeResolvedChainTargets(Laser laser)
    {
        if (laser == null || !laser.chainable)
            return 0;

        // Verified against the current Star Vortex BeamWeapon implementation.
        // Fail closed rather than inventing a second approximation if that
        // native contract changes in a future game build.
        if (BeamWeaponChainTargetsField == null)
            return 0;

        object raw = BeamWeaponChainTargetsField.GetValue(laser);
        if (!(raw is int))
            return 0;

        int baseChainTargets = (int)raw;

        return Mathf.Max(
            0,
            laser.ApplyModifier(
                Modifier.Type.ChainCount,
                baseChainTargets,
                false,
                true));
    }

    private static int GetFinalResolvedChainTargets(
        Laser laser,
        ResolvedState resolved)
    {
        if (resolved == null)
            return GetNativeResolvedChainTargets(laser);

        return Mathf.Max(
            0,
            GetNativeResolvedChainTargets(laser) +
            resolved.ExtraChainTargets);
    }

    public static void ScaleDebuffSpreadOnKill(
        Equippable equippable,
        Modifier.Type modifierType,
        bool includeParentShip,
        ref float value)
    {
        if (modifierType != Modifier.Type.OnEnemyDeathShed ||
            !includeParentShip)
        {
            return;
        }

        Laser laser = equippable as Laser;
        GameShip player;
        if (laser == null || !TryGetContext(laser, out player))
            return;

        ResolvedState resolved = GetResolvedState(player);
        if (resolved.DebuffSpreadOnKillChance <= 0f)
            return;

        value = Mathf.Clamp01(
            value + resolved.DebuffSpreadOnKillChance);
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
        if (laser == null)
            return;

        bool valid = TryGetContext(laser, out player);

        if (!valid)
            valid = TryGetRemoteSourceContext(laser, out player);

        if (!valid)
            return;

        value *= GetResolvedState(laser).RangeMultiplier;
    }

    // =========================================================================
    // FORKING
    // =========================================================================

    private static void UpdateForking(
        ref ForkShot shot,
        Laser laser,
        GameShip owner,
        ResolvedState resolved,
        Phase currentPhase)
    {
        if (laser == null ||
            owner == null ||
            resolved == null ||
            !resolved.Forking ||
            currentPhase != Phase.Firing ||
            resolved.ForkTargets <= 0 ||
            resolved.ForkDamageFraction <= 0f)
        {
            DestroyForkShot(ref shot);
            return;
        }

        Beam sourceBeam = BeamWeaponBeamScriptField == null
            ? null
            : BeamWeaponBeamScriptField.GetValue(laser) as Beam;
        if (sourceBeam == null)
        {
            DestroyForkShot(ref shot);
            return;
        }

        PhysicsController.Hit rootHit = sourceBeam.GetCachedRaycastHit();
        GameShip rootTarget;
        if (!TryGetShipFromBeamHit(rootHit, out rootTarget) ||
            !IsValidForkTarget(owner, rootTarget))
        {
            DestroyForkShot(ref shot);
            return;
        }

        if (shot == null ||
            !ReferenceEquals(shot.source, laser) ||
            !ReferenceEquals(shot.sourceBeam, sourceBeam) ||
            !ReferenceEquals(shot.rootTarget, rootTarget))
        {
            DestroyForkShot(ref shot);
            shot = BuildForkShot(
                laser,
                owner,
                sourceBeam,
                rootTarget,
                rootHit.point,
                resolved);
        }
        else
        {
            shot.rootHitPosition = rootHit.point;
        }

        TickForkShot(shot);
    }

    private static ForkShot BuildForkShot(
        Laser laser,
        GameShip owner,
        Beam sourceBeam,
        GameShip rootTarget,
        Vector2 rootHitPosition,
        ResolvedState resolved)
    {
        ForkShot shot = new ForkShot
        {
            source = laser,
            owner = owner,
            sourceBeam = sourceBeam,
            rootTarget = rootTarget,
            rootHitPosition = rootHitPosition
        };

        shot.visitedTargets.Add(rootTarget);

        Vector2 forward = sourceBeam.transform == null
            ? Vector2.right
            : (Vector2)sourceBeam.transform.right;
        if (forward.sqrMagnitude <= 0.0001f)
            forward = Vector2.right;
        forward.Normalize();

        int rootCount = Mathf.Max(0, resolved.ForkTargets);
        float chainRange = Mathf.Max(
            0f,
            laser.MaxRange * laser.ChainRange);

        for (int i = 0; i < rootCount; i++)
        {
            GameShip target = SelectForkTarget(
                shot,
                rootHitPosition,
                forward,
                resolved.ForkConeDegrees,
                chainRange,
                true,
                rootTarget);

            if (target == null)
                break;

            shot.visitedTargets.Add(target);
            shot.repeatLocked = false;

            ForkBranch branch = new ForkBranch
            {
                currentTarget = target
            };

            ForkSegment rootSegment = CreateForkSegment(
                shot,
                sourceBeam,
                null,
                target,
                true);

            if (rootSegment == null)
                continue;

            branch.segments.Add(rootSegment);
            shot.branches.Add(branch);
        }

        int resolvedChainTargets =
            GetFinalResolvedChainTargets(laser, resolved);

        int chainBudget = Mathf.Max(
            0,
            Mathf.CeilToInt(
                resolvedChainTargets *
                resolved.ForkChainFraction));

        // Build by chain depth across all branches. This prevents branch zero
        // from consuming every fresh target before its sibling forks expand.
        for (int depth = 0; depth < chainBudget; depth++)
        {
            bool createdAny = false;

            for (int i = 0; i < shot.branches.Count; i++)
            {
                ForkBranch branch = shot.branches[i];
                if (branch == null || branch.currentTarget == null)
                    continue;

                Vector2 origin =
                    GetForkOriginPoint(
                        branch.currentTarget,
                        (Vector2)branch.currentTarget.transform.position);

                GameShip next = SelectForkTarget(
                    shot,
                    origin,
                    Vector2.right,
                    360f,
                    chainRange,
                    false,
                    branch.currentTarget);

                if (next == null)
                    continue;

                bool repeat = shot.visitedTargets.Contains(next);
                if (repeat)
                {
                    if (shot.repeatLocked)
                        continue;

                    shot.repeatLocked = true;
                }
                else
                {
                    shot.visitedTargets.Add(next);
                    shot.repeatLocked = false;
                }

                ForkSegment segment = CreateForkSegment(
                    shot,
                    sourceBeam,
                    branch.currentTarget,
                    next,
                    false);

                if (segment == null)
                    continue;

                branch.segments.Add(segment);
                branch.currentTarget = next;
                createdAny = true;
            }

            if (!createdAny)
                break;
        }

        if (shot.branches.Count == 0)
        {
            DestroyForkShot(ref shot);
            return null;
        }

        return shot;
    }

    private static GameShip SelectForkTarget(
        ForkShot shot,
        Vector2 origin,
        Vector2 forward,
        float coneDegrees,
        float range,
        bool requireNew,
        GameShip fromTarget)
    {
        if (shot == null ||
            shot.owner == null ||
            PhysicsController.instance == null ||
            range <= 0f)
        {
            return null;
        }

        ForkCandidateScratch.Clear();
        ForkCandidateSeenScratch.Clear();

        Collider2D[] colliders =
            PhysicsController.instance.OverlapCircle(origin, range);
        if (colliders == null)
            return null;

        float halfCone = Mathf.Clamp(coneDegrees, 0f, 360f) * 0.5f;
        bool useCone = coneDegrees < 359.9f;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider2D collider = colliders[i];
            if (collider == null)
                continue;

            GameObject obj = collider.gameObject;
            if (obj != null &&
                obj.CompareTag("Shield") &&
                obj.transform.parent != null)
            {
                obj = obj.transform.parent.gameObject;
            }

            GameShip candidate;
            if (obj == null ||
                !GameShip.TryGetShip(obj, out candidate) ||
                candidate == null ||
                ReferenceEquals(candidate, fromTarget) ||
                !IsValidForkTarget(shot.owner, candidate) ||
                !ForkCandidateSeenScratch.Add(candidate))
            {
                continue;
            }

            Vector2 delta =
                (Vector2)candidate.transform.position - origin;
            if (delta.sqrMagnitude <= 0.0001f)
                continue;

            if (useCone &&
                Vector2.Angle(forward, delta) > halfCone)
            {
                continue;
            }

            ForkCandidateScratch.Add(candidate);
        }

        GameShip bestNew = null;
        float bestNewDistance = float.PositiveInfinity;
        GameShip bestRepeat = null;
        float bestRepeatDistance = float.PositiveInfinity;

        for (int i = 0; i < ForkCandidateScratch.Count; i++)
        {
            GameShip candidate = ForkCandidateScratch[i];
            float distance =
                ((Vector2)candidate.transform.position - origin)
                .sqrMagnitude;

            if (!shot.visitedTargets.Contains(candidate))
            {
                if (distance < bestNewDistance)
                {
                    bestNew = candidate;
                    bestNewDistance = distance;
                }
            }
            else if (!requireNew &&
                !shot.repeatLocked &&
                distance < bestRepeatDistance)
            {
                bestRepeat = candidate;
                bestRepeatDistance = distance;
            }
        }

        if (requireNew || shot.repeatLocked)
            return bestNew;

        // When repeats are unlocked, preserve the aggressive native-style
        // nearest-target feel. A repeat may win over a fresh target, but doing
        // so globally locks every branch out of another repeat until any branch
        // reaches a target never hit anywhere in this Forking graph.
        if (bestNew == null)
            return bestRepeat;
        if (bestRepeat == null)
            return bestNew;

        return bestRepeatDistance < bestNewDistance
            ? bestRepeat
            : bestNew;
    }

    private static bool IsValidForkTarget(
        GameShip owner,
        GameShip target)
    {
        if (owner == null ||
            target == null ||
            target.health <= 0f ||
            target.IsDrone() ||
            target.IsDodging())
        {
            return false;
        }

        GameShip ownerRoot = owner.GetAbsoluteParent();
        GameShip targetRoot = target.GetAbsoluteParent();
        if (ReferenceEquals(ownerRoot, targetRoot))
            return false;

        return Faction.IsHostile(owner.faction, target.faction) &&
            target.CanBeDamagedBy(ownerRoot, false);
    }

    private static bool TryGetShipFromBeamHit(
        PhysicsController.Hit hit,
        out GameShip ship)
    {
        ship = null;
        if (hit == null || hit.transform == null)
            return false;

        GameObject obj = hit.transform.gameObject;
        if (obj != null &&
            obj.CompareTag("Shield") &&
            obj.transform.parent != null)
        {
            obj = obj.transform.parent.gameObject;
        }

        return obj != null &&
            GameShip.TryGetShip(obj, out ship) &&
            ship != null;
    }

    private static ForkSegment CreateForkSegment(
        ForkShot shot,
        Beam sourceBeam,
        GameShip fromTarget,
        GameShip target,
        bool rootFork)
    {
        if (shot == null ||
            shot.source == null ||
            shot.owner == null ||
            sourceBeam == null ||
            target == null ||
            PoolController.instance == null)
        {
            return null;
        }

        GameObject prefab = shot.source.GetBeamPrefab();
        if (prefab == null)
            return null;

        Vector2 origin = fromTarget == null
            ? shot.rootHitPosition
            : GetForkOriginPoint(
                fromTarget,
                (Vector2)target.transform.position);

        GameObject obj = PoolController.instance.GetObject(
            prefab,
            origin,
            Quaternion.identity,
            false);
        if (obj == null)
            return null;

        Beam beam;
        if (!obj.TryGetComponent<Beam>(out beam) || beam == null)
        {
            CustomObject.Destroy(obj);
            return null;
        }

        float changeRate = GetFloat(
            BeamChangeRateField,
            sourceBeam);
        bool snapChange = GetBool(
            BeamSnapChangeField,
            sourceBeam);
        bool hitObstructions = GetBool(
            BeamHitObstructionsField,
            sourceBeam);
        bool delayDamageTick = GetBool(
            BeamDelayDamageTickField,
            sourceBeam);
        bool globalLastHit = GetBool(
            BeamGlobalLastHitField,
            sourceBeam);

        beam.Init(
            shot.source,
            shot.owner,
            false,
            0f,
            0,
            changeRate,
            snapChange,
            hitObstructions,
            false,
            delayDamageTick,
            globalLastHit,
            prefab);
        beam.enabled = false;
        beam.Activate();

        ForkSegment segment = new ForkSegment
        {
            beam = beam,
            fromTarget = fromTarget,
            target = target,
            rootFork = rootFork
        };

        ForkBeamStates[beam] = new ForkBeamState
        {
            shot = shot,
            segment = segment
        };

        return segment;
    }

    private static void TickForkShot(ForkShot shot)
    {
        if (shot == null)
            return;

        for (int i = 0; i < shot.branches.Count; i++)
        {
            ForkBranch branch = shot.branches[i];
            if (branch == null)
                continue;

            for (int j = 0; j < branch.segments.Count; j++)
            {
                ForkSegment segment = branch.segments[j];
                if (segment == null ||
                    segment.beam == null ||
                    segment.target == null ||
                    segment.target.health <= 0f)
                {
                    continue;
                }

                Vector2 origin = segment.fromTarget == null
                    ? shot.rootHitPosition
                    : GetForkOriginPoint(
                        segment.fromTarget,
                        (Vector2)segment.target.transform.position);
                Vector2 targetPoint =
                    GetForkTargetPoint(segment.target, origin);
                Vector2 direction = targetPoint - origin;
                if (direction.sqrMagnitude <= 0.0001f)
                    continue;

                segment.beam.transform.position = origin;
                segment.beam.transform.rotation =
                    Quaternion.Euler(
                        0f,
                        0f,
                        Mathf.Atan2(direction.y, direction.x) *
                            Mathf.Rad2Deg);

                segment.beam.ClearRaycastHit();
                segment.beam.DoDamageTick();
                segment.beam.UpdatePosition();
                segment.beam.UpdateState();
            }
        }
    }

    public static void DrawForking(Laser laser)
    {
        if (laser == null)
            return;

        ForkShot shot = null;

        if (ReferenceEquals(sourceLaser, laser))
        {
            shot = LocalForkShot;
        }
        else
        {
            RemoteState remote;
            if (RemoteStates.TryGetValue(laser, out remote))
                shot = remote.forkShot;
        }

        if (shot == null)
            return;

        for (int i = 0; i < shot.branches.Count; i++)
        {
            ForkBranch branch = shot.branches[i];
            if (branch == null)
                continue;

            for (int j = 0; j < branch.segments.Count; j++)
            {
                ForkSegment segment = branch.segments[j];
                if (segment != null && segment.beam != null)
                    segment.beam.DrawBeam();
            }
        }
    }

    public static bool TryOverrideForkBeamHit(
        Beam beam,
        ref PhysicsController.Hit result)
    {
        ForkBeamState state;
        if (beam == null ||
            !ForkBeamStates.TryGetValue(beam, out state) ||
            state == null ||
            state.segment == null)
        {
            return false;
        }

        ForkSegment segment = state.segment;
        GameShip target = segment.target;
        state.forcedHit.Free();

        if (target == null ||
            target.health <= 0f ||
            target.transform == null)
        {
            result = state.forcedHit;
            return true;
        }

        Vector2 origin = (Vector2)beam.transform.position;
        Vector2 point = GetForkTargetPoint(target, origin);
        Collider2D collider = null;
        target.TryGetComponent<Collider2D>(out collider);

        state.forcedHit.Set(
            point,
            Vector2.Distance(origin, point),
            collider,
            target.transform);
        result = state.forcedHit;
        return true;
    }

    public static void ScaleForkDamagePacket(
        DamageBeam beam,
        bool crit,
        ref DamageData[] damage)
    {
        ForkBeamState state;
        if (beam == null ||
            !ForkBeamStates.TryGetValue(beam, out state) ||
            state == null ||
            state.segment == null ||
            !state.segment.rootFork ||
            state.shot == null ||
            state.shot.source == null)
        {
            return;
        }

        ResolvedState resolved = GetResolvedState(state.shot.owner);
        if (resolved == null)
            return;

        // Root forks are 70% (or the configured ForkDamage knob) of the fully
        // resolved MAIN Converter packet, independent of the source weapon's
        // native ChainDamage. Descendants remain normal non-primary DamageBeams
        // and therefore inherit the source weapon's native ChainDamage.
        DamageData[] mainPacket =
            state.shot.source.GetDamageData(crit, false);
        if (mainPacket == null)
            return;

        DamageData[] scaled = new DamageData[mainPacket.Length];
        for (int i = 0; i < mainPacket.Length; i++)
        {
            DamageData datum = mainPacket[i];
            datum.damage *= resolved.ForkDamageFraction;

            // Match native chaining semantics: status-effect DPS metadata is
            // inherited from the resolved source and is not reduced by chain
            // damage/fork damage.
            scaled[i] = datum;
        }

        damage = scaled;
    }

    private static Vector2 GetForkOriginPoint(
        GameShip fromTarget,
        Vector2 toward)
    {
        if (fromTarget == null || fromTarget.transform == null)
            return toward;

        Vector2 center = (Vector2)fromTarget.transform.position;
        Collider2D collider;
        if (fromTarget.TryGetComponent<Collider2D>(out collider) &&
            collider != null)
        {
            return collider.ClosestPoint(toward);
        }

        return center;
    }

    private static Vector2 GetForkTargetPoint(
        GameShip target,
        Vector2 origin)
    {
        if (target == null || target.transform == null)
            return origin;

        Collider2D collider;
        if (target.TryGetComponent<Collider2D>(out collider) &&
            collider != null)
        {
            return collider.ClosestPoint(origin);
        }

        return (Vector2)target.transform.position;
    }

    private static bool GetBool(
        FieldInfo field,
        object target)
    {
        if (field == null || target == null)
            return false;

        object value = field.GetValue(target);
        return value is bool && (bool)value;
    }

    private static void DestroyForkShot(ref ForkShot shot)
    {
        if (shot == null)
            return;

        for (int i = 0; i < shot.branches.Count; i++)
        {
            ForkBranch branch = shot.branches[i];
            if (branch == null)
                continue;

            for (int j = 0; j < branch.segments.Count; j++)
            {
                ForkSegment segment = branch.segments[j];
                if (segment == null || segment.beam == null)
                    continue;

                ForkBeamStates.Remove(segment.beam);
                segment.beam.ForceDeactivate();
                if (segment.beam.gameObject != null)
                    CustomObject.Destroy(segment.beam.gameObject);
                segment.beam = null;
            }

            branch.segments.Clear();
        }

        shot.branches.Clear();
        shot.visitedTargets.Clear();
        shot = null;
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
        if (!TryGetBeamContext(beam, out laser))
        {
            RestoreBeamBrightness(line);
            RestoreBeamBrightness(end);
            RestoreBeamBrightness(beam.additionalBeam);
            return;
        }

        ResolvedState resolved = GetResolvedState(laser);
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
        if (!TryGetBeamContext(beam, out laser))
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
            GetResolvedState(laser).WidthMultiplier *
            HitboxWidthMultiplier;

        return radius > 0f;
    }

    private static bool TryGetBeamContext(
        Beam beam,
        out Laser laser)
    {
        laser = null;

        if (beam == null || BeamParentWeaponField == null)
            return false;

        laser = BeamParentWeaponField.GetValue(beam) as Laser;

        return laser != null &&
            IsFiringSource(laser);
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



    private static void UpdateConverterPresentation(
        ConverterPresentationState presentation,
        Laser laser,
        GameShip player,
        ResolvedState resolved,
        Phase presentationPhase,
        float phaseProgress)
    {
        if (presentation == null ||
            laser == null ||
            player == null ||
            resolved == null)
        {
            HideConverterPresentation(presentation);
            return;
        }

        Beam beam = BeamWeaponBeamScriptField == null
            ? null
            : BeamWeaponBeamScriptField.GetValue(laser) as Beam;

        LineRenderer sourceLine =
            beam == null || BeamLineRendererField == null
                ? null
                : BeamLineRendererField.GetValue(beam) as LineRenderer;

        Vector2 origin;
        Vector2 direction;

        if (sourceLine == null ||
            !TryGetLaserPose(laser, out origin, out direction))
        {
            HideConverterPresentation(presentation);
            return;
        }

        float mainWidth = GetResolvedPresentationBeamWidth(
            beam,
            sourceLine,
            resolved,
            presentationPhase);

        bool charging =
            presentationPhase == Phase.Charging &&
            resolved.ChargeSeconds > 0.0001f;

        if (charging)
        {
            float growth = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.Clamp01(phaseProgress));
            float sizeFraction = Mathf.Lerp(
                Mathf.Clamp01(
                    LeviathanStellarConverterTuning.ChargeOrbStartingSizeFraction),
                1f,
                growth);

            EnsureChargeOrbRenderer(presentation, sourceLine);

            if (presentation.chargeOrbRenderer != null)
            {
                CopyBeamLineAppearance(
                    sourceLine,
                    presentation.chargeOrbRenderer,
                    LeviathanStellarConverterTuning.ChargeOrbOpacityMultiplier);

                presentation.chargeOrbVisual.SetActive(true);

                Vector2 perpendicular =
                    new Vector2(-direction.y, direction.x);

                // A tiny round-capped line segment renders as a compact disc.
                float halfStub = Mathf.Max(
                    0.00025f,
                    mainWidth * 0.0025f);

                presentation.chargeOrbRenderer.SetPosition(
                    0,
                    origin - perpendicular * halfStub);
                presentation.chargeOrbRenderer.SetPosition(
                    1,
                    origin + perpendicular * halfStub);
                presentation.chargeOrbRenderer.widthMultiplier =
                    Mathf.Max(0.0001f, mainWidth * sizeFraction);
            }
        }
        else if (presentation.chargeOrbVisual != null)
        {
            presentation.chargeOrbVisual.SetActive(false);
        }

        bool showFeeders =
            resolved.Convergence &&
            (presentationPhase == Phase.Charging ||
             presentationPhase == Phase.Firing);

        if (showFeeders)
        {
            GetActiveTailShips(player, presentation.tailScratch);

            EnsureConvergenceFeederCount(
                presentation,
                presentation.tailScratch.Count,
                sourceLine);

            // Tie feeder growth directly to Converter charge progress so they
            // reach full width exactly when charge-up completes. Once firing
            // begins they remain fully grown for the output phase.
            if (presentationPhase == Phase.Charging)
            {
                presentation.feederGrowthProgress =
                    Mathf.Clamp01(phaseProgress);
            }
            else if (presentationPhase == Phase.Firing)
            {
                presentation.feederGrowthProgress = 1f;
            }

            float feederGrowth = Mathf.SmoothStep(
                0f,
                1f,
                presentation.feederGrowthProgress);

            float minWidthFraction = Mathf.Max(
                0f,
                LeviathanStellarConverterTuning.ConvergenceFeederMinWidthFraction);

            float maxWidthFraction = Mathf.Max(
                minWidthFraction,
                LeviathanStellarConverterTuning.ConvergenceFeederMaxWidthFraction);

            float feederWidthFraction = Mathf.Lerp(
                minWidthFraction,
                maxWidthFraction,
                feederGrowth);

            float feederWidth = Mathf.Max(
                0.0001f,
                mainWidth * feederWidthFraction);

            for (int i = 0; i < presentation.feederRenderers.Count; i++)
            {
                bool active = i < presentation.tailScratch.Count;
                GameObject feederObject = presentation.feederVisuals[i];

                if (!active)
                {
                    if (feederObject != null)
                        feederObject.SetActive(false);

                    continue;
                }

                GameShip tail = presentation.tailScratch[i];

                if (tail == null)
                {
                    if (feederObject != null)
                        feederObject.SetActive(false);

                    continue;
                }

                LineRenderer feeder = presentation.feederRenderers[i];

                CopyBeamLineAppearance(
                    sourceLine,
                    feeder,
                    LeviathanStellarConverterTuning.ConvergenceFeederOpacityMultiplier);

                feederObject.SetActive(true);
                feeder.widthMultiplier = feederWidth;
                feeder.SetPosition(0, tail.transform.position);
                feeder.SetPosition(1, origin);
            }
        }
        else
        {
            HideConvergenceFeeders(presentation);
        }

        if (resolved.EventHorizon && presentationPhase == Phase.Firing)
        {
            float elapsed =
                Mathf.Clamp01(phaseProgress) * resolved.PulseSeconds;
            float tipSpeed =
                resolved.EventHorizonTipSpeed *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            float tipDistance = Mathf.Min(
                Mathf.Max(0f, Mathf.Abs(laser.MaxRange)),
                Mathf.Max(0f, tipSpeed) * elapsed);

            if (tipDistance > 0.0001f)
            {
                UpdateEventHorizonTipVisual(
                    presentation,
                    origin + direction * tipDistance,
                    resolved.EventHorizonVisualRadius *
                        LeviathanStellarConverterTuning.WorldUnitsPerMeter);
            }
            else
            {
                DestroyEventHorizonTipVisual(presentation);
            }
        }
        else
        {
            DestroyEventHorizonTipVisual(presentation);
        }
    }

    private static float GetResolvedPresentationBeamWidth(
        Beam beam,
        LineRenderer sourceLine,
        ResolvedState resolved,
        Phase presentationPhase)
    {
        if (beam == null || resolved == null)
            return 0.01f;

        if (presentationPhase == Phase.Firing &&
            sourceLine != null &&
            sourceLine.widthMultiplier > 0.0001f)
        {
            return sourceLine.widthMultiplier;
        }

        float baseWidth = GetFloat(BeamMaxWidthField, beam);

        if (beam.additionalBeam != null)
        {
            baseWidth = Mathf.Max(
                baseWidth,
                GetFloat(BeamAdditionalMaxWidthField, beam));
        }

        return Mathf.Max(
            0.0001f,
            baseWidth * Mathf.Max(0f, resolved.WidthMultiplier));
    }

    private static void EnsureChargeOrbRenderer(
        ConverterPresentationState presentation,
        LineRenderer sourceLine)
    {
        if (presentation == null || presentation.chargeOrbRenderer != null)
            return;

        presentation.chargeOrbVisual = new GameObject(
            "Leviathan Converter Charge Orb");

        presentation.chargeOrbRenderer =
            presentation.chargeOrbVisual.AddComponent<LineRenderer>();

        presentation.chargeOrbRenderer.useWorldSpace = true;
        presentation.chargeOrbRenderer.positionCount = 2;
        presentation.chargeOrbRenderer.numCapVertices = 20;
        presentation.chargeOrbRenderer.numCornerVertices = 20;

        CopyBeamLineAppearance(
            sourceLine,
            presentation.chargeOrbRenderer,
            1f);
    }

    private static void EnsureConvergenceFeederCount(
        ConverterPresentationState presentation,
        int count,
        LineRenderer sourceLine)
    {
        if (presentation == null)
            return;

        while (presentation.feederRenderers.Count < count)
        {
            GameObject obj =
                new GameObject(
                    "Leviathan Converter Convergence Feeder");

            LineRenderer line =
                obj.AddComponent<LineRenderer>();

            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;

            CopyBeamLineAppearance(sourceLine, line, 1f);

            presentation.feederVisuals.Add(obj);
            presentation.feederRenderers.Add(line);
        }
    }

    private static void CopyBeamLineAppearance(
        LineRenderer source,
        LineRenderer destination,
        float opacityMultiplier)
    {
        if (source == null || destination == null)
            return;

        destination.sharedMaterial = source.sharedMaterial;
        destination.textureMode = source.textureMode;
        destination.alignment = source.alignment;
        destination.sortingLayerID = source.sortingLayerID;
        destination.sortingOrder = source.sortingOrder;

        float opacity = Mathf.Max(0f, opacityMultiplier);

        Color start = source.startColor;
        Color end = source.endColor;

        start.a *= opacity;
        end.a *= opacity;

        destination.startColor = start;
        destination.endColor = end;
    }

    private static void GetActiveTailShips(
        GameShip player,
        List<GameShip> output)
    {
        LeviathanGrowth.CollectTailShips(player, output);
    }

    private static void HideConvergenceFeeders(
        ConverterPresentationState presentation)
    {
        if (presentation == null)
            return;

        for (int i = 0; i < presentation.feederVisuals.Count; i++)
        {
            if (presentation.feederVisuals[i] != null)
                presentation.feederVisuals[i].SetActive(false);
        }

        presentation.tailScratch.Clear();
        presentation.feederGrowthProgress = 0f;
    }

    private static void HideConverterPresentation(
        ConverterPresentationState presentation)
    {
        if (presentation == null)
            return;

        if (presentation.chargeOrbVisual != null)
            presentation.chargeOrbVisual.SetActive(false);

        HideConvergenceFeeders(presentation);
        DestroyEventHorizonTipVisual(presentation);
    }

    private static void DestroyConverterPresentation(
        ConverterPresentationState presentation)
    {
        if (presentation == null)
            return;

        if (presentation.chargeOrbVisual != null)
            UnityEngine.Object.Destroy(presentation.chargeOrbVisual);

        presentation.chargeOrbVisual = null;
        presentation.chargeOrbRenderer = null;

        for (int i = 0; i < presentation.feederVisuals.Count; i++)
        {
            if (presentation.feederVisuals[i] != null)
                UnityEngine.Object.Destroy(presentation.feederVisuals[i]);
        }

        presentation.feederVisuals.Clear();
        presentation.feederRenderers.Clear();
        presentation.tailScratch.Clear();
        DestroyEventHorizonTipVisual(presentation);
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
        shot.launchOrigin = origin;
        shot.direction = direction;
        shot.maxTravel = Mathf.Max(0.01f, Mathf.Abs(laser.MaxRange));
        shot.pullFalloffExponent =
            LeviathanStellarConverterTuning.GravityPullFalloffExponent;
        shot.velocityScaling = resolved.GravityVelocityScaling;

        if (mode == GravityProjectileMode.Singularity)
        {
            shot.travelSpeed = resolved.SingularitySpeed *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.lifetime = resolved.SingularityLifetime;
            shot.pullRadius =
                resolved.SingularityPullRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.pullStrength = resolved.SingularityPullStrength;
            shot.visualScale = resolved.SingularityVisualScale;
            shot.visualRadius = resolved.SingularityVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.haloRadius =
                resolved.SingularityHaloRadius *
                resolved.FinalRangeMultiplier *
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
            shot.pullRadius =
                resolved.DyingStarPullRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.pullStrength = resolved.DyingStarPullStrength;
            shot.visualScale = resolved.DyingStarVisualScale;
            shot.visualRadius = resolved.DyingStarVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.integratedDamageFraction = resolved.DyingStarDamageFraction;
            shot.minimumDetonationScale =
                resolved.DyingStarMinimumDetonationScale;
            shot.detonationCurveExponent =
                resolved.DyingStarDetonationCurveExponent;
            shot.explosionRadius =
                resolved.DyingStarExplosionRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.explosionVisualTargetRadius =
                resolved.DyingStarExplosionVisualRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.currentExplosionRadius = Mathf.Max(
                0.01f,
                LeviathanStellarConverterTuning.WorldUnitsPerMeter * 1.5f);
            shot.explosionExpansionSpeed =
                LeviathanStellarConverterTuning.DyingStarExplosionExpansionSpeedMetersPerSecond *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        }

        gravityProjectileShot = shot;
        manifestationShotSequence++;
        BuildGravityProjectileVisual(shot);
        UpdateGravityProjectileVisual(shot);
        ApplyManifestationHeat(player, laser, resolved);

        // Full-duration exposure should total exactly the configured integrated
        // fraction, so Singularity gets one of its evenly-divided ticks immediately.
        if (mode == GravityProjectileMode.Singularity)
            DamageSingularityHalo(shot);
    }

    private static void SpawnRemoteGravityVisual(
        Laser laser,
        RemoteState state,
        ResolvedState resolved)
    {
        if (laser == null ||
            state == null ||
            resolved == null ||
            laser.parentShip == null)
        {
            return;
        }

        DestroyRemoteGravityVisual(state);

        Vector2 origin;
        Vector2 direction;
        if (!TryGetLaserPose(laser, out origin, out direction))
            return;

        if (direction.sqrMagnitude <= 0.0001f)
            direction = Vector2.right;
        direction.Normalize();

        GravityProjectileShot shot = new GravityProjectileShot();
        shot.source = laser;
        shot.owner = laser.parentShip;
        shot.position = origin;
        shot.launchOrigin = origin;
        shot.direction = direction;
        shot.maxTravel = Mathf.Max(0.01f, Mathf.Abs(laser.MaxRange));
        shot.pullFalloffExponent =
            LeviathanStellarConverterTuning.GravityPullFalloffExponent;
        shot.velocityScaling = resolved.GravityVelocityScaling;

        if (resolved.Singularity)
        {
            shot.mode = GravityProjectileMode.Singularity;
            shot.travelSpeed =
                resolved.SingularitySpeed *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.lifetime = resolved.SingularityLifetime;
            shot.visualScale = resolved.SingularityVisualScale;
            shot.visualRadius =
                resolved.SingularityVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.haloRadius =
                resolved.SingularityHaloRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        }
        else if (resolved.DyingStar)
        {
            shot.mode = GravityProjectileMode.DyingStar;
            shot.travelSpeed =
                resolved.DyingStarSpeed *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.lifetime = resolved.DyingStarFuse;
            shot.visualScale = resolved.DyingStarVisualScale;
            shot.visualRadius =
                resolved.DyingStarVisualRadius *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.minimumDetonationScale =
                resolved.DyingStarMinimumDetonationScale;
            shot.detonationCurveExponent =
                resolved.DyingStarDetonationCurveExponent;
            shot.explosionRadius =
                resolved.DyingStarExplosionRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.explosionVisualTargetRadius =
                resolved.DyingStarExplosionVisualRadius *
                resolved.FinalRangeMultiplier *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
            shot.currentExplosionRadius = Mathf.Max(
                0.01f,
                LeviathanStellarConverterTuning.WorldUnitsPerMeter * 1.5f);
            shot.explosionExpansionSpeed =
                LeviathanStellarConverterTuning.DyingStarExplosionExpansionSpeedMetersPerSecond *
                LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        }
        else
        {
            return;
        }

        state.remoteVisualShot = shot;
        BuildGravityProjectileVisual(shot);
        UpdateGravityProjectileVisual(shot);
    }


    private static void ReconcileRemoteGravityProgress(
        GravityProjectileShot shot,
        float normalizedProgress)
    {
        if (shot == null || shot.exploding)
            return;

        float progress = Mathf.Clamp01(normalizedProgress);
        float timeToRange = shot.travelSpeed <= 0.0001f
            ? float.PositiveInfinity
            : shot.maxTravel / shot.travelSpeed;
        float naturalDuration = Mathf.Min(
            Mathf.Max(0.0001f, shot.lifetime),
            timeToRange);

        if (float.IsInfinity(naturalDuration))
            naturalDuration = Mathf.Max(0.0001f, shot.lifetime);

        float authoritativeAge = naturalDuration * progress;
        float authoritativeTravel = Mathf.Min(
            shot.maxTravel,
            Mathf.Max(0f, shot.travelSpeed) * authoritativeAge);

        if (authoritativeAge <= shot.age &&
            authoritativeTravel <= shot.traveled)
        {
            return;
        }

        shot.age = Mathf.Max(shot.age, authoritativeAge);
        shot.traveled = Mathf.Max(shot.traveled, authoritativeTravel);
        shot.position =
            shot.launchOrigin + shot.direction * shot.traveled;
    }

    private static void UpdateRemoteGravityVisual(RemoteState state)
    {
        if (state == null || state.remoteVisualShot == null)
            return;

        GravityProjectileShot shot = state.remoteVisualShot;
        if (shot.source == null ||
            shot.owner == null ||
            shot.owner.health <= 0f)
        {
            DestroyRemoteGravityVisual(state);
            return;
        }

        float delta = Time.fixedDeltaTime;

        if (!shot.exploding)
        {
            // Interpolate between 20 Hz authoritative snapshots. The next slot
            // update reconciles position/age back to owner progress.
            shot.age += delta;

            float step = Mathf.Max(0f, shot.travelSpeed * delta);
            float remaining = Mathf.Max(0f, shot.maxTravel - shot.traveled);
            float move = Mathf.Min(step, remaining);
            shot.position += shot.direction * move;
            shot.traveled += move;

            if (shot.mode == GravityProjectileMode.DyingStar)
                TryPlayDyingStarExplosionLeadAudio(shot);
        }
        else
        {
            shot.currentExplosionRadius = Mathf.Min(
                shot.explosionRadius,
                shot.currentExplosionRadius +
                    shot.explosionExpansionSpeed * delta);

            // Do not end the replica from a guessed owner timer. The owner keeps
            // projectileActive in the slot until the authoritative lifecycle ends.
            if (!state.projectileActive)
            {
                DestroyRemoteGravityVisual(state);
                return;
            }
        }

        UpdateGravityProjectileVisual(shot);
    }


    private static void BeginRemoteDyingStarExplosion(
        GravityProjectileShot shot,
        float detonationScale)
    {
        if (shot == null || shot.exploding)
            return;

        ApplyDyingStarDetonationScaleValue(
            shot,
            detonationScale);
        shot.exploding = true;

        if (!shot.explosionSoundPlayed)
        {
            LeviathanAudioRuntime.PlayEventHorizonExplosion(shot.position);
            shot.explosionSoundPlayed = true;
        }

        TryBuildDyingStarExplosionVisual(shot);
    }


    private static void DestroyRemoteGravityVisual(RemoteState state)
    {
        if (state == null || state.remoteVisualShot == null)
            return;

        DestroyGravityProjectileVisual(state.remoteVisualShot);
        state.remoteVisualShot = null;
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
                shot.pullFalloffExponent,
                shot.velocityScaling);

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

        float radius =
            resolved.EventHorizonPullRadius *
            resolved.FinalRangeMultiplier *
            LeviathanStellarConverterTuning.WorldUnitsPerMeter;
        Vector2 tip = origin + direction * eventHorizonPullTipDistance;

        PullHostilesInGravityCorridor(
            owner,
            origin,
            tip,
            radius,
            resolved.EventHorizonPullStrength,
            LeviathanStellarConverterTuning.GravityPullFalloffExponent,
            resolved.GravityVelocityScaling);
    }

    private static void PullHostilesInGravityCircle(
        GameShip owner,
        Vector2 center,
        float radius,
        float strength,
        float falloffExponent,
        float velocityScaling)
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
            PullTargetTowards(
                owner,
                target,
                center,
                strength * proximity,
                velocityScaling);
        }
    }

    private static void PullHostilesInGravityCorridor(
        GameShip owner,
        Vector2 start,
        Vector2 tip,
        float radius,
        float strength,
        float falloffExponent,
        float velocityScaling)
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

            PullTargetTowards(
                owner,
                target,
                tip,
                strength * proximity,
                velocityScaling);
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
        float strength,
        float velocityScaling)
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

        // 1.00 exactly preserves vanilla max(1, speed - 1):
        // max(1, speed - 1) == 1 + max(0, speed - 2).
        // Scaling changes only the extra velocity-driven amplification.
        float speedContribution = Mathf.Max(
            0f,
            body.velocity.magnitude - 2f);
        float velocityFactor =
            1f + speedContribution * Mathf.Max(0f, velocityScaling);

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

    private static float GetDyingStarMaturity(GravityProjectileShot shot)
    {
        if (shot == null)
            return 0f;

        float fuseProgress = shot.lifetime <= 0.0001f
            ? 1f
            : shot.age / shot.lifetime;

        float rangeProgress = shot.maxTravel <= 0.0001f
            ? 1f
            : shot.traveled / shot.maxTravel;

        // Natural detonation occurs at whichever limit is reached first, so the
        // larger normalized progress is the correct maturity toward that event.
        return Mathf.Clamp01(Mathf.Max(fuseProgress, rangeProgress));
    }

    private static float GetDyingStarDetonationScale(
        GravityProjectileShot shot)
    {
        if (shot == null)
            return 1f;

        float minimum = Mathf.Clamp01(shot.minimumDetonationScale);
        float exponent = Mathf.Max(0.01f, shot.detonationCurveExponent);
        float maturity = GetDyingStarMaturity(shot);
        float curved = Mathf.Pow(maturity, exponent);

        return Mathf.Lerp(minimum, 1f, curved);
    }

    private static void ApplyDyingStarDetonationScale(
        GravityProjectileShot shot)
    {
        if (shot == null || shot.mode != GravityProjectileMode.DyingStar)
            return;

        ApplyDyingStarDetonationScaleValue(
            shot,
            GetDyingStarDetonationScale(shot));
    }


    private static void ApplyDyingStarDetonationScaleValue(
        GravityProjectileShot shot,
        float scale)
    {
        if (shot == null || shot.mode != GravityProjectileMode.DyingStar)
            return;

        scale = Mathf.Clamp01(scale);
        shot.detonationScale = scale;
        shot.integratedDamageFraction *= scale;
        shot.explosionRadius *= scale;
        shot.explosionVisualTargetRadius *= scale;
        shot.currentExplosionRadius = Mathf.Min(
            shot.currentExplosionRadius,
            shot.explosionRadius);
    }

    private static void BeginDyingStarExplosion(GravityProjectileShot shot)
    {
        if (shot == null || shot.exploding)
            return;

        ApplyDyingStarDetonationScale(shot);
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
        GameShip contextOwner;
        if (!TryGetContext(source, out contextOwner))
            return;

        ResolvedState resolved = GetResolvedState(contextOwner);
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

        object routeResult = RouteDamageMethod.Invoke(
            null,
            new object[]
            {
                target,
                source.damageType,
                damage,
                statusChance,
                crit ? 1 : 0,
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

        bool killed =
            routeResult is bool &&
            (bool)routeResult;

        if (killed && !target.IsDrone())
        {
            // Use the native modifier boundary so Contagion stacks additively
            // with any vanilla OnEnemyDeathShed already present on the weapon
            // or ship. The Converter patch contributes its normal tree knob.
            float shedChance = source.ApplyModifierToPercentage(
                Modifier.Type.OnEnemyDeathShed,
                0f,
                true);
            if (shedChance > 0f)
                target.ShedStatusEffects(shedChance, true, owner);
        }

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

                BlackHole blackHole = obj.GetComponent<BlackHole>();
                if (blackHole != null)
                    blackHole.enabled = false;

                Collider2D[] colliders = obj.GetComponentsInChildren<Collider2D>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null)
                        colliders[i].enabled = false;

                ApplyBlackHoleVisualRadius(
                    obj,
                    shot.blackHoleBaseScale,
                    shot.visualRadius,
                    shot.visualScale);

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

        // Match vanilla ExplosiveProjectile.Explode exactly:
        // ExplosiveArea local scale is the desired WORLD radius * 2.
        float diameter = Mathf.Max(0.001f, currentVisualRadius * 2f);
        shot.explosionVisual.SetScale(
            new Vector3(diameter, diameter, diameter));
    }

    private static void ApplyBlackHoleVisualRadius(
        GameObject obj,
        Vector3 baseScale,
        float desiredVisualRadiusWorldUnits,
        float fallbackScale)
    {
        if (obj == null)
            return;

        ResolveBlackHoleVisualPrefab();

        // BlackHoleProjectile.radius is the only stable native size reference
        // carried by the weapon itself. Scale the vanilla black-hole prefab
        // relative to its own native field radius instead of trying to measure
        // particle renderer bounds.
        if (desiredVisualRadiusWorldUnits > 0.0001f &&
            cachedNativeBlackHolePullRadius > 0.0001f)
        {
            float relativeScale =
                desiredVisualRadiusWorldUnits /
                cachedNativeBlackHolePullRadius;

            // Prevent a bad resource sample from producing a catastrophic
            // particle-system scale.
            relativeScale = Mathf.Clamp(relativeScale, 0.01f, 4f);
            obj.transform.localScale = baseScale * relativeScale;
            return;
        }

        obj.transform.localScale =
            baseScale * Mathf.Clamp(fallbackScale, 0.01f, 4f);
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
        ConverterPresentationState presentation,
        Vector2 position,
        float visualRadius)
    {
        if (presentation == null)
            return;

        if (visualRadius <= 0.0001f)
        {
            DestroyEventHorizonTipVisual(presentation);
            return;
        }

        ResolveBlackHoleVisualPrefab();
        if (cachedBlackHoleVisualPrefab == null)
            return;

        if (presentation.eventHorizonTipVisual == null)
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

            presentation.eventHorizonTipVisual = obj;
            presentation.eventHorizonTipVisualBaseScale =
                obj.transform.localScale;
            obj.SetActive(true);
        }

        presentation.eventHorizonTipVisual.transform.position = position;

        ApplyBlackHoleVisualRadius(
            presentation.eventHorizonTipVisual,
            presentation.eventHorizonTipVisualBaseScale,
            visualRadius,
            1f);
    }


    private static void DestroyEventHorizonTipVisual(
        ConverterPresentationState presentation)
    {
        if (presentation == null ||
            presentation.eventHorizonTipVisual == null)
        {
            return;
        }

        CustomObject.Destroy(presentation.eventHorizonTipVisual);
        presentation.eventHorizonTipVisual = null;
        presentation.eventHorizonTipVisualBaseScale = Vector3.one;
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

    // Continuous Conversion's custom random statuses remain local-authority-only.
    // Remote Converter presentation never rolls gameplay, and remote-owned targets
    // are rejected rather than mutating a non-authoritative replica.
    public static void TryApplyContinuousRandomStatus(
        GameShip target,
        BeamWeapon sourceWeapon,
        DamageData[] damageData)
    {
        Laser laser = sourceWeapon as Laser;
        if (target == null || laser == null || target.IsNetRemote())
            return;

        GameShip player;
        if (!TryGetContext(laser, out player))
            return;

        ResolvedState resolved = GetResolvedState(player);
        if (!resolved.Continuous ||
            target.IsDrone() ||
            target.health <= 0f)
        {
            return;
        }

        if (!CanApplyCustomStatus(target))
            return;

        float dps = damageData == null
            ? laser.CalculateDPS(Activatable.Modified.Global)
            : DamageData.GetDamageData(Modifier.Type.Damage, damageData).dps;

        TryRollRandomBasicStatus(
            target,
            laser,
            player,
            dps,
            resolved.RandomBasicStatusChance,
            false);

        if (resolved.SpectrumSaturation)
        {
            TryRollRandomBasicStatus(
                target,
                laser,
                player,
                dps,
                resolved.OffElementRandomStatusChance,
                true);
        }
    }

    private static bool CanApplyCustomStatus(GameShip target)
    {
        if (target == null)
            return false;

        if (DebuffAndDirectImmuneField != null)
        {
            object rawImmune = DebuffAndDirectImmuneField.GetValue(target);
            if (rawImmune is bool && (bool)rawImmune)
                return false;
        }

        return target.shield == null || !target.shield.IsWardActive();
    }

    private static void TryRollRandomBasicStatus(
        GameShip target,
        Laser laser,
        GameShip player,
        float dps,
        float baseChance,
        bool excludeSourceDamageType)
    {
        float chance = GetLevelAdjustedStatusChance(
            player,
            target,
            baseChance);
        if (chance <= 0f ||
            UnityEngine.Random.Range(0f, 1f) > chance)
        {
            return;
        }

        Damageable.DamageType type;
        if (!TryChooseRandomBasicStatusType(
            laser,
            excludeSourceDamageType,
            out type))
        {
            return;
        }

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

        StatusEffect effect = StatusEffect.GetEffectForDamageType(
            type,
            dps,
            player,
            chance);
        if (effect != null)
            target.AddStatusEffect(effect);
    }

    private static float GetLevelAdjustedStatusChance(
        GameShip player,
        GameShip target,
        float chance)
    {
        chance = Mathf.Clamp01(chance);
        if (chance <= 0f ||
            player == null ||
            target == null ||
            player.pilot == null ||
            target.pilot == null)
        {
            return chance;
        }

        int levelDelta = player.pilot.GetLevel(0) - target.pilot.GetLevel(0);
        if (levelDelta < -9)
            chance *= 0.25f;
        else if (levelDelta < -4)
            chance *= 0.50f;
        else if (levelDelta < -3)
            chance *= 0.75f;

        return chance;
    }

    private static bool TryChooseRandomBasicStatusType(
        Laser laser,
        bool excludeSourceDamageType,
        out Damageable.DamageType type)
    {
        type = default(Damageable.DamageType);
        if (RandomBasicStatusTypes.Length == 0)
            return false;

        int start = UnityEngine.Random.Range(
            0,
            RandomBasicStatusTypes.Length);

        for (int i = 0; i < RandomBasicStatusTypes.Length; i++)
        {
            Damageable.DamageType candidate =
                RandomBasicStatusTypes[
                    (start + i) % RandomBasicStatusTypes.Length];

            if (!excludeSourceDamageType ||
                laser == null ||
                candidate != laser.damageType)
            {
                type = candidate;
                return true;
            }
        }

        return false;
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

[HarmonyPatch(typeof(BeamWeapon), "LateUpdate")]
public static class LeviathanStellarConverterForkLateUpdatePatch
{
    public static void Postfix(BeamWeapon __instance)
    {
        Laser laser = __instance as Laser;
        if (laser != null)
            LeviathanStellarConverter.DrawForking(laser);
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

[HarmonyPatch(typeof(RemoteShipDriver), "DestroyRep")]
public static class LeviathanStellarConverterRemoteRepDestroyPatch
{
    public static void Prefix(GameShip __0)
    {
        LeviathanStellarConverter.ForgetRemoteShip(__0);
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

// Converter heat tradeoffs modify only the selected source Laser's contribution.
// VeryLow priority nests safely with other temporary GameShip.UpdateHeat patches.
[HarmonyPatch(typeof(GameShip), "UpdateHeat")]
[HarmonyPriority(Priority.VeryLow)]
public static class LeviathanStellarConverterHeatPatch
{
    public static void Prefix(
        GameShip __instance,
        out LeviathanStellarConverter.HeatRateState __state)
    {
        __state =
            LeviathanStellarConverter.PrepareShipHeatRate(__instance);
    }

    public static void Postfix(
        GameShip __instance,
        LeviathanStellarConverter.HeatRateState __state)
    {
        LeviathanStellarConverter.RestoreShipHeatRate(
            __instance,
            __state);
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

[HarmonyPatch(
    typeof(Equippable),
    "ApplyModifierToPercentage",
    new Type[]
    {
        typeof(Modifier.Type),
        typeof(float),
        typeof(bool)
    })]
public static class LeviathanStellarConverterDebuffSpreadPatch
{
    public static void Postfix(
        Equippable __instance,
        Modifier.Type __0,
        bool __2,
        ref float __result)
    {
        LeviathanStellarConverter.ScaleDebuffSpreadOnKill(
            __instance,
            __0,
            __2,
            ref __result);
    }
}

[HarmonyPatch(typeof(Beam), "GetCachedRaycastHit")]
public static class LeviathanStellarConverterForkTargetPatch
{
    public static bool Prefix(
        Beam __instance,
        ref PhysicsController.Hit __result)
    {
        return !LeviathanStellarConverter.TryOverrideForkBeamHit(
            __instance,
            ref __result);
    }
}

[HarmonyPatch(typeof(DamageBeam), "GetDamageData")]
public static class LeviathanStellarConverterForkDamagePatch
{
    public static void Postfix(
        DamageBeam __instance,
        ref bool crit,
        ref DamageData[] __result)
    {
        LeviathanStellarConverter.ScaleForkDamagePacket(
            __instance,
            crit,
            ref __result);
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
                m.GetParameters()[2].ParameterType == typeof(DamageData[]) &&
                m.GetParameters()[4].ParameterType == typeof(int));
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
