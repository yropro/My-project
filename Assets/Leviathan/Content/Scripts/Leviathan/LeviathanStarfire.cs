using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static StarVortex.Damageable;

/// <summary>
/// Canonical Starfire specialization tuning.
///
/// Starfire no longer supports the old native-rank implementation. The skill tree
/// always resolves one normalized projectile-breath baseline from either a Primary
/// Torch or a Fuzzy/Flame thrower source.
/// </summary>
public static class LeviathanStarfireTuning
{
    public const float WorldUnitsPerMeter = 1f / 20f;

    // Source-family normalization.
    public const float BaselineCritChance = 0.15f;
    public const float BaselineStatusChance = 0.10f;
    public const float TorchDamageNormalization = 0.50f;
    public const float TorchFamilyDamageMultiplier = 1.10f;
    public const float ThrowerDamageNormalization = 2.22f;
    public const float ThrowerStatusChanceBonus = 0.05f;
    public const float ThrowerProjectileSizeMultiplier = 1.09f;

    // Normalized Starfire spray geometry. Torch uses a 25-degree total cone.
    // Thrower sources are the broader family at a 31-degree total cone.
    public const float BaselineFiringHalfAngleDegrees = 12.5f;
    public const float ThrowerFiringAngleMultiplier = 1.24f;

    // Native Cryo Torch / Cryo Gun heat is 5 / 3 per second respectively.
    // Starfire normalizes both source families to the midpoint before tree tuning.
    public const float BaselineHeatPerSecond = 4f;

    // Starfire baseline itself is neutral. Family normalization above establishes
    // the source baseline; tree nodes modify it afterward.
    public const float BaselineHeatMultiplier = 1f;
    public const float BaselineLengthMultiplier = 1f;
    public const float BaselineWidthMultiplier = 1f;
    public const float BaselineDamageMultiplier = 1f;
    public const float BaselineProjectileSizeMultiplier = 1f;

    // Persistent Breath Power.
    public const float BaselineFullSizeHoldSeconds = 1.50f;
    public const float BaselineRetreatSeconds = 2.00f;
    public const float BaselineActiveDrainRate = 1.00f;
    public const float BaselineRecoverySecondsPerSecond = 0.50f;
    public const float BaselineRecoveryDelaySeconds = 0.00f;
    public const float BaselineRecoveryCurveExponent = 1.00f;
    public const float BaselineMinimumDamageFraction = 0.25f;
    public const float BaselineMinimumLengthFraction = 0.00f;
    public const float BaselineMinimumWidthFraction = 0.00f;
    public const float BaselineMinimumVelocityFraction = 0.25f;

    // Velocity begins falling at 60% of the normalized damage-falloff rate,
    // then smoothly catches up near exhaustion so both reach their 25% floors.
    public const float BaselineVelocityFalloffRate = 0.60f;

    public const float BaselineFalloffCurveExponent = 2.00f;
    public const float BaselineStartupDelaySeconds = 0.00f;
}

/// <summary>
/// Starfire converts the first equipped Primary Torch or Fuzzy/Flame thrower into
/// one normalized native-projectile breath weapon.
///
/// The actual source weapon still owns damage type, rolled/local modifiers,
/// ship/global modifiers, crit modifier, DamageData semantics, customizers,
/// Conduit, leech/on-kill behavior and attribution. Starfire replaces only the
/// authored family baselines that are intentionally normalized here.
/// </summary>
public static class LeviathanStarfireRuntime
{
    private const float WorldUnitsPerMeter = LeviathanStarfireTuning.WorldUnitsPerMeter;

    public static class Knobs
    {
        public static readonly CoreSpecializationKnob HeatGeneration =
            CoreSpecializationKnob.Percent(
                "starfire.heat_generation",
                "Heat Generation"
            );

        public static readonly CoreSpecializationKnob Length =
            CoreSpecializationKnob.Percent(
                "starfire.length",
                "Length"
            );

        public static readonly CoreSpecializationKnob Width =
            CoreSpecializationKnob.Percent(
                "starfire.width",
                "Width"
            );

        public static readonly CoreSpecializationKnob Damage =
            CoreSpecializationKnob.Percent(
                "starfire.damage",
                "Damage"
            );

        public static readonly CoreSpecializationKnob StatusChance =
            CoreSpecializationKnob.PercentagePoints(
                "starfire.status_chance",
                "Status Chance"
            );

        public static readonly CoreSpecializationKnob ProjectileSize =
            CoreSpecializationKnob.Percent(
                "starfire.projectile_size",
                "Projectile Size"
            );

        public static readonly CoreSpecializationKnob FullSizeHoldSeconds =
            CoreSpecializationKnob.Flat(
                "starfire.full_size_hold_seconds",
                "Full-Power Capacity",
                "s"
            );

        public static readonly CoreSpecializationKnob RetreatSeconds =
            CoreSpecializationKnob.Flat(
                "starfire.retreat_seconds",
                "Falloff Capacity",
                "s"
            );

        public static readonly CoreSpecializationKnob ActiveDrainRate =
            CoreSpecializationKnob.Percent(
                "starfire.active_drain_rate",
                "Breath Drain Rate"
            );

        public static readonly CoreSpecializationKnob RecoveryRate =
            CoreSpecializationKnob.Percent(
                "starfire.recovery_rate",
                "Breath Recovery Rate"
            );

        public static readonly CoreSpecializationKnob DamageFalloffCurveExponent =
            CoreSpecializationKnob.Flat(
                "starfire.damage_falloff_curve_exponent",
                "Damage Falloff Curve Exponent"
            );

        public static readonly CoreSpecializationKnob LengthFalloffCurveExponent =
            CoreSpecializationKnob.Flat(
                "starfire.length_falloff_curve_exponent",
                "Length Falloff Curve Exponent"
            );

        public static readonly CoreSpecializationKnob WidthFalloffCurveExponent =
            CoreSpecializationKnob.Flat(
                "starfire.width_falloff_curve_exponent",
                "Width Falloff Curve Exponent"
            );

        public static readonly CoreSpecializationKnob StartupDelaySeconds =
            CoreSpecializationKnob.Flat(
                "starfire.startup_delay_seconds",
                "Startup Delay",
                "s"
            );

        public static readonly CoreSpecializationKnob FiringHalfAngleDegrees =
            CoreSpecializationKnob.Flat(
                "starfire.firing_half_angle_degrees",
                "Firing Half-Angle",
                "°"
            );

        public static readonly CoreSpecializationKnob RechargePullRadius =
            CoreSpecializationKnob.Flat(
                "starfire.recharge_pull_radius",
                "Recharge Pull Radius",
                "m"
            );

        public static readonly CoreSpecializationKnob RechargePullStrength =
            CoreSpecializationKnob.Flat(
                "starfire.recharge_pull_strength",
                "Recharge Pull Strength"
            );

        public static readonly CoreSpecializationKnob RechargePullFalloffExponent =
            CoreSpecializationKnob.Flat(
                "starfire.recharge_pull_falloff_exponent",
                "Recharge Pull Falloff Exponent"
            );

        public static readonly CoreSpecializationKnob RechargePullMaxSpeed =
            CoreSpecializationKnob.Flat(
                "starfire.recharge_pull_max_speed",
                "Recharge Pull Max Speed",
                "m/s"
            );

        public static readonly CoreSpecializationKnob BlastWaveArcDegrees =
            CoreSpecializationKnob.Flat(
                "starfire.blast_wave_arc_degrees",
                "Blast Wave Arc",
                "°"
            );

        public static readonly CoreSpecializationKnob BlastWaveDamageMultiplier =
            CoreSpecializationKnob.Multiplier(
                "starfire.blast_wave_damage_multiplier",
                "Blast Wave Damage"
            );

        public static readonly CoreSpecializationKnob BlastWaveSpeed =
            CoreSpecializationKnob.Flat(
                "starfire.blast_wave_speed",
                "Blast Wave Speed",
                "m/s"
            );

        public static readonly CoreSpecializationKnob BlastWaveRange =
            CoreSpecializationKnob.Flat(
                "starfire.blast_wave_range",
                "Blast Wave Range",
                "m"
            );

        public static readonly CoreSpecializationKnob BlastWaveWidth =
            CoreSpecializationKnob.Flat(
                "starfire.blast_wave_width",
                "Blast Wave Front Thickness",
                "m"
            );

        public static readonly CoreSpecializationKnob BlastWaveVisualOpacity =
            CoreSpecializationKnob.PercentagePoints(
                "starfire.blast_wave_visual_opacity",
                "Blast Wave Visual Opacity"
            );

        public static readonly CoreSpecializationKnob BlastWaveStartRadius =
            CoreSpecializationKnob.Flat(
                "starfire.blast_wave_start_radius",
                "Blast Wave Start Radius",
                "m"
            );

        public static readonly CoreSpecializationKnob BlastWaveEndRadius =
            CoreSpecializationKnob.Flat(
                "starfire.blast_wave_end_radius",
                "Blast Wave End Radius",
                "m"
            );
    }

    public static class Flags
    {
        public static readonly CoreSpecializationFlag RechargePull =
            CoreSpecializationFlag.Create(
                "starfire.recharge_pull",
                "Recharge Pull"
            );

        public static readonly CoreSpecializationFlag BlastWave =
            CoreSpecializationFlag.Create(
                "starfire.blast_wave",
                "Blast Wave"
            );
    }

    public enum StarfireSourceFamily
    {
        None,
        Torch,
        Thrower
    }

    // Starfire tree content may condition an ordinary knob contribution on the
    // currently equipped source family without creating TorchDamage/ThrowerDamage
    // duplicates. The effect itself is still a normal specialization knob effect.
    public sealed class FamilyKnobEffect
    {
        public readonly string NodeId;
        public readonly StarfireSourceFamily Family;
        internal readonly CoreSpecializationEffect Effect;

        public FamilyKnobEffect(
            string nodeId,
            StarfireSourceFamily family,
            CoreTreeDsl.Effect effect)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentException("Node id is required.", "nodeId");
            if (family == StarfireSourceFamily.None)
                throw new ArgumentException("A concrete source family is required.", "family");
            if (effect == null || effect.Inner == null)
                throw new ArgumentNullException("effect");

            NodeId = nodeId;
            Family = family;
            Effect = effect.Inner;
        }

        internal void ValidateForNode(CoreSpecializationNode node)
        {
            if (node == null)
                throw new InvalidOperationException(
                    "Family-conditioned Starfire effect references missing node '" +
                    NodeId + "'."
                );

            Effect.ValidateForNode(node.MaxRank);
        }
    }

    public sealed class ResolvedStarfireState
    {
        public StarfireSourceFamily SourceFamily;

        public float HeatMultiplier;
        public float LengthMultiplier;
        public float WidthMultiplier;
        public float DamageMultiplier;
        public float StatusChanceBonus;
        public float ProjectileSizeMultiplier;
        public float FiringHalfAngleDegrees;
        public float StartupDelaySeconds;

        public float FullSizeHoldSeconds;
        public float RetreatSeconds;
        public float CapacitySeconds;
        public float ActiveDrainRate;
        public float RecoverySecondsPerSecond;
        public float RecoveryDelaySeconds;
        public float RecoveryCurveExponent;
        public float MinimumDamageFraction;
        public float MinimumLengthFraction;
        public float MinimumWidthFraction;
        public float MinimumVelocityFraction;
        public float VelocityFalloffRate;
        public float DamageFalloffCurveExponent;
        public float LengthFalloffCurveExponent;
        public float WidthFalloffCurveExponent;

        public bool RechargePullEnabled;
        public float RechargePullRadius;
        public float RechargePullStrength;
        public float RechargePullFalloffExponent;
        public float RechargePullMaxSpeed;

        public bool BlastWaveEnabled;
        public float BlastWaveArcDegrees;
        public float BlastWaveDamageMultiplier;
        public float BlastWaveSpeed;
        public float BlastWaveRange;
        public float BlastWaveFrontThickness;
        public float BlastWaveVisualOpacity;
        public float BlastWaveStartRadius;
        public float BlastWaveEndRadius;
        public float BlastWaveKnockback;
    }

    public struct BreathIconInitState
    {
        public bool changedCooldown;
        public float originalBaseCooldown;
    }

    private sealed class BreathState
    {
        public bool initialized;
        public float remainingSeconds;
        public float capacitySeconds;
        public float lastUpdateTime;
        public float idleStartedTime = -1f;
        public float startupStartedTime = -1f;
        public float torchVolleyAccumulator;
        public float activeSeconds;
        public bool blastFiredThisActivation;
    }

    private sealed class ThrowerProfile
    {
        public Launcher template;
        public GameObject projectilePrefab;
        public float baseReloadTime;
        public float baseRechargeSeconds;
        public int baseShotCount;
        public float baseVelocity;
        public float baseLifetime;
        public bool inheritParentVelocity;
        public ChargingLauncher.ChargeType chargeType;
        public float chargeSeconds;
        public float unchargedMultiplier = 1f;
    }

    private struct StarfireProjectileContext
    {
        public Activatable source;
        public GameShip owner;
        public StarfireSourceFamily family;
        public float nonCritDamage;
        public float expectedDps;
        public float critChance;
        public float critModifier;
        public float statusChance;
        public float knockback;
        public bool consumed;
    }

    private sealed class BlastWaveState
    {
        public Activatable sourceWeapon;
        public float integratedDamageSeconds;
        public GameShip owner;
        public Vector2 origin;
        public Vector2 direction;
        public float startedTime;
        public float previousRadius;
        public float maxRange;
        public float speed;
        public float radialThickness;
        public float startRadius;
        public float endRadius;
        public float halfArcDegrees;
        public GameObject visualObject;
        public Mesh visualMesh;
        public MeshRenderer visualRenderer;
        public SpriteRenderer visualAnimationRenderer;
        public Animator visualAnimationAnimator;
        public Sprite visualLastSprite;
        public bool visualFlipX;
        public bool visualFlipY;
        public Vector3[] visualVertices;
        public Vector2[] visualUvs;
        public MaterialPropertyBlock visualPropertyBlock;
        public LineRenderer fallbackLineRenderer;
        public readonly HashSet<GameShip> hitShips = new HashSet<GameShip>();
    }

    private sealed class BlastWaveExplosionVisualSource
    {
        public Sprite sprite;
        public Material material;
        public RuntimeAnimatorController animatorController;
        public bool flipX;
        public bool flipY;
        public int sortingLayerID;
        public int sortingOrder;
    }

    private static readonly FieldInfo MainSpikeField =
        AccessTools.Field(typeof(Torch), "mainSpike");
    private static readonly FieldInfo MirrorSpikeField =
        AccessTools.Field(typeof(Torch), "mirrorSpike");
    private static readonly FieldInfo ChargeField =
        AccessTools.Field(typeof(Torch), "charge");
    private static readonly FieldInfo GameShipHeatPerSecondField =
        AccessTools.Field(typeof(GameShip), "heatPerSecond");
    private static readonly FieldInfo ActivatableActiveField =
        AccessTools.Field(typeof(Activatable), "active");
    private static readonly FieldInfo LauncherProjectilePrefabField =
        AccessTools.Field(typeof(Launcher), "projectilePrefab");
    private static readonly FieldInfo FlameStartScaleField =
        AccessTools.Field(typeof(FlameProjectile), "startScale");

    private static readonly MethodInfo RouteDamageMethod = FindRouteDamageMethod();
    private static readonly MethodInfo ConduitRelayHitMethod = FindConduitRelayHitMethod();
    private static readonly MethodInfo TorchGladiatorLeechMethod =
        AccessTools.Method(
            typeof(Torch),
            "LeechGladiatorHull",
            new Type[] { typeof(GameShip), typeof(Vector2) }
        );

    private static readonly Dictionary<Activatable, BreathState> BreathStates =
        new Dictionary<Activatable, BreathState>();
    private static readonly Dictionary<Launcher, bool> ThrowerEligibilityCache =
        new Dictionary<Launcher, bool>();
    private static readonly Dictionary<Damageable.DamageType, ThrowerProfile>
        TorchThrowerProfiles =
            new Dictionary<Damageable.DamageType, ThrowerProfile>();
    private static readonly Dictionary<Launcher, ThrowerProfile>
        LauncherProfiles = new Dictionary<Launcher, ThrowerProfile>();
    private static readonly Dictionary<Torch, Launcher> TorchProxyLaunchers =
        new Dictionary<Torch, Launcher>();
    private static readonly Dictionary<Projectile, StarfireProjectileContext>
        StarfireProjectiles =
            new Dictionary<Projectile, StarfireProjectileContext>();
    private static readonly Dictionary<Activatable, float> MuzzleOffsetCache =
        new Dictionary<Activatable, float>();
    private static readonly HashSet<Torch> SuppressedTorchSpikes =
        new HashSet<Torch>();
    private static readonly List<GameObject> ProjectileIgnoreCache =
        new List<GameObject>(4);
    private static readonly HashSet<GameShip> RechargePullTargets =
        new HashSet<GameShip>();
    private static readonly Dictionary<Activatable, BlastWaveState> BlastWaves =
        new Dictionary<Activatable, BlastWaveState>();

    private static BlastWaveExplosionVisualSource blastWaveExplosionVisualSource;
    private static Material blastWaveFallbackMaterial;
    private static bool searchedThrowerProfiles;
    private static bool sceneCleanupRegistered;
    private static bool warnedNoThrowerProfile;

    private static readonly int MainTexShaderId = Shader.PropertyToID("_MainTex");
    private static readonly int ColorShaderId = Shader.PropertyToID("_Color");
    private static readonly int RendererColorShaderId =
        Shader.PropertyToID("_RendererColor");

    private static Pilot resolvedStatePilot;
    private static Activatable resolvedStateSource;
    private static StarfireSourceFamily resolvedStateFamily =
        StarfireSourceFamily.None;
    private static int resolvedStateRevision = -1;
    private static ResolvedStarfireState resolvedState;

    [ThreadStatic]
    private static object[] routeDamageInvokeArgs;

    [ThreadStatic]
    private static object[] conduitInvokeArgs;

    [ThreadStatic]
    private static object[] torchLeechInvokeArgs;

    private static MethodInfo FindRouteDamageMethod()
    {
        MethodInfo[] methods = typeof(NetCombat).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        for (int i = 0; i < methods.Length; i++)
        {
            ParameterInfo[] parameters = methods[i].GetParameters();
            if (methods[i].Name == "RouteDamage" &&
                parameters.Length == 14 &&
                parameters[2].ParameterType == typeof(DamageData[]) &&
                parameters[4].ParameterType == typeof(int))
            {
                return methods[i];
            }
        }
        return null;
    }

    private static MethodInfo FindConduitRelayHitMethod()
    {
        MethodInfo[] methods = typeof(Conduit).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        for (int i = 0; i < methods.Length; i++)
        {
            ParameterInfo[] parameters = methods[i].GetParameters();
            if (methods[i].Name == "RelayHit" &&
                parameters.Length == 6 &&
                parameters[3].ParameterType == typeof(DamageData[]))
            {
                return methods[i];
            }
        }
        return null;
    }

    private static bool RouteNormalizedDamage(
        GameShip target,
        Damageable.DamageType damageType,
        DamageData[] damage,
        float statusChance,
        bool crit,
        Vector2 hitPoint,
        GameShip owner,
        bool bypassDamageLimit,
        float knockback,
        Activatable source,
        float impaleRotation)
    {
        if (RouteDamageMethod == null || target == null)
            return false;

        object[] args = routeDamageInvokeArgs;
        if (args == null)
        {
            args = new object[14];
            routeDamageInvokeArgs = args;
        }

        args[0] = target;
        args[1] = damageType;
        args[2] = damage;
        args[3] = statusChance;
        args[4] = crit ? 1 : 0;
        args[5] = hitPoint;
        args[6] = owner;
        args[7] = bypassDamageLimit;
        args[8] = knockback;
        args[9] = source;
        args[10] = 0f;
        args[11] = 0f;
        args[12] = false;
        args[13] = impaleRotation;

        return (bool)RouteDamageMethod.Invoke(null, args);
    }

    private static void RelayNormalizedConduit(
        Activatable source,
        GameShip owner,
        GameShip target,
        DamageData[] damage,
        Vector2 hitPoint,
        bool bypassDamageLimit)
    {
        if (ConduitRelayHitMethod == null || target == null)
            return;

        object[] args = conduitInvokeArgs;
        if (args == null)
        {
            args = new object[6];
            conduitInvokeArgs = args;
        }

        args[0] = source;
        args[1] = owner;
        args[2] = target;
        args[3] = damage;
        args[4] = hitPoint;
        args[5] = bypassDamageLimit;
        ConduitRelayHitMethod.Invoke(null, args);
    }

    public static void EnsureSceneCleanupHook()
    {
        if (sceneCleanupRegistered)
            return;

        sceneCleanupRegistered = true;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        CleanupTransientState();
    }

    public static void CleanupTransientState()
    {
        foreach (KeyValuePair<Activatable, BlastWaveState> pair in BlastWaves)
            CleanupBlastWaveVisual(pair.Value);

        BlastWaves.Clear();
        BreathStates.Clear();
        ThrowerEligibilityCache.Clear();
        TorchThrowerProfiles.Clear();
        LauncherProfiles.Clear();
        TorchProxyLaunchers.Clear();
        StarfireProjectiles.Clear();
        MuzzleOffsetCache.Clear();
        SuppressedTorchSpikes.Clear();
        RechargePullTargets.Clear();
        searchedThrowerProfiles = false;
        warnedNoThrowerProfile = false;
        blastWaveExplosionVisualSource = null;

        if (blastWaveFallbackMaterial != null)
        {
            UnityEngine.Object.Destroy(blastWaveFallbackMaterial);
            blastWaveFallbackMaterial = null;
        }

        resolvedStatePilot = null;
        resolvedStateSource = null;
        resolvedStateFamily = StarfireSourceFamily.None;
        resolvedStateRevision = -1;
        resolvedState = null;
    }

    private static bool IsCurrentPlayer(GameShip player)
    {
        return player != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == player;
    }

    private static bool IsStarfireActive(GameShip player)
    {
        if (!IsCurrentPlayer(player))
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);
        if (pilot == null || !LeviathanGrowth.IsGrowthActive(player))
            return false;

        return CoreSpecializationRuntime.IsTreeActive(
            pilot,
            LeviathanStarfireTree.TreeId
        );
    }

    private static Projectile GetLauncherProjectile(Launcher launcher)
    {
        if (launcher == null)
            return null;

        Projectile projectile = LauncherProjectilePrefabField == null
            ? null
            : LauncherProjectilePrefabField.GetValue(launcher) as Projectile;
        if (projectile != null)
            return projectile;

        GameObject prefab;
        try
        {
            prefab = launcher.GetProjectile();
        }
        catch
        {
            return null;
        }

        if (prefab == null)
            return null;

        prefab.TryGetComponent<Projectile>(out projectile);
        return projectile;
    }

    // Star Vortex's flamethrower-style spray family is represented by Primary
    // LauncherCone weapons using FuzzyProjectile (including FlameProjectile).
    // Detect the family structurally rather than by item name so flame, cryo,
    // plasma and future native throwers using the same delivery model are covered.
    private static bool IsThrowerLauncher(Launcher launcher)
    {
        if (launcher == null || launcher.type != Item.Type.PrimaryWeapon)
            return false;

        bool cached;
        if (ThrowerEligibilityCache.TryGetValue(launcher, out cached))
            return cached;

        bool eligible =
            launcher.category == Item.Category.LauncherCone &&
            GetLauncherProjectile(launcher) is FuzzyProjectile;
        ThrowerEligibilityCache[launcher] = eligible;
        return eligible;
    }

    private static StarfireSourceFamily GetSourceFamily(Equippable equippable)
    {
        Torch torch = equippable as Torch;
        if (torch != null && torch.type == Item.Type.PrimaryWeapon)
            return StarfireSourceFamily.Torch;

        Launcher launcher = equippable as Launcher;
        if (launcher != null && IsThrowerLauncher(launcher))
            return StarfireSourceFamily.Thrower;

        return StarfireSourceFamily.None;
    }

    private static Activatable FindSource(GameShip player)
    {
        if (player == null || player.slots == null)
            return null;

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];
            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            if (GetSourceFamily(slot.equippable) != StarfireSourceFamily.None)
                return slot.equippable as Activatable;
        }

        return null;
    }

    private static bool TryGetSourceContext(
        Activatable source,
        out GameShip player,
        out StarfireSourceFamily family,
        out ResolvedStarfireState resolved)
    {
        player = source == null ? null : source.parentShip;
        family = StarfireSourceFamily.None;
        resolved = null;

        if (source == null ||
            player == null ||
            !IsStarfireActive(player) ||
            !ReferenceEquals(FindSource(player), source))
        {
            return false;
        }

        family = GetSourceFamily(source);
        if (family == StarfireSourceFamily.None)
            return false;

        resolved = GetResolvedStarfireState();
        return true;
    }

    public static void PrepareBreathIcon(
        Activatable source,
        out BreathIconInitState state)
    {
        state = new BreathIconInitState();

        GameShip player;
        StarfireSourceFamily family;
        ResolvedStarfireState resolved;
        if (!TryGetSourceContext(
            source,
            out player,
            out family,
            out resolved) ||
            source.Cooldown > 0f)
        {
            return;
        }

        // ActivatableIcon destroys its native cooldown Slider when Cooldown is
        // zero. Temporarily expose a harmless value so Starfire can reuse the
        // exact native overlay for Breath Power, then restore the real weapon.
        state.changedCooldown = true;
        state.originalBaseCooldown = source.BaseCooldown;
        source.BaseCooldown = 1f;
    }

    public static void RestoreBreathIcon(
        Activatable source,
        BreathIconInitState state)
    {
        if (source != null && state.changedCooldown)
            source.BaseCooldown = state.originalBaseCooldown;
    }

    public static bool TryGetBreathIconOverlay(
        Activatable source,
        out float overlay)
    {
        overlay = 0f;

        GameShip player;
        StarfireSourceFamily family;
        ResolvedStarfireState resolved;
        if (!TryGetSourceContext(
            source,
            out player,
            out family,
            out resolved))
        {
            return false;
        }

        BreathState breath;
        if (!BreathStates.TryGetValue(source, out breath) ||
            breath == null ||
            breath.capacitySeconds <= 0.0001f)
        {
            return true;
        }

        overlay = 1f - Mathf.Clamp01(
            breath.remainingSeconds / breath.capacitySeconds
        );
        return true;
    }

    private static bool IsSourceFiring(Activatable source)
    {
        if (source == null ||
            !source.IsActive() ||
            source.durability == 0 ||
            source.overheated ||
            source.OnCooldown())
        {
            return false;
        }

        GameShip player = source.parentShip;
        if (player == null ||
            player.IsDodging() ||
            !player.IsVisible() ||
            player.IsDisabled() ||
            player.IsWeaponsOffline())
        {
            return false;
        }

        if (player.slots != null)
        {
            for (int i = 0; i < player.slots.Length; i++)
            {
                Slot slot = player.slots[i];
                if (slot != null && ReferenceEquals(slot.equippable, source))
                    return slot.enabled;
            }
        }

        return true;
    }

    public static void SuppressNonSourceActivation(Activatable source)
    {
        if (source == null || ActivatableActiveField == null)
            return;

        GameShip player = source.parentShip;
        if (!IsStarfireActive(player) ||
            GetSourceFamily(source) == StarfireSourceFamily.None)
        {
            return;
        }

        if (!ReferenceEquals(FindSource(player), source))
            ActivatableActiveField.SetValue(source, false);
    }

    private static void SearchThrowerProfiles()
    {
        if (searchedThrowerProfiles)
            return;

        searchedThrowerProfiles = true;
        LauncherItemBase[] itemBases =
            ModContent.LoadAll<LauncherItemBase>("Base/Items");

        for (int i = 0; i < itemBases.Length; i++)
        {
            LauncherItemBase itemBase = itemBases[i];
            if (itemBase == null)
                continue;

            Launcher template = FindLauncherTemplate(itemBase);
            if (template == null)
                continue;

            GameObject prefab = itemBase.GetProjectileObject(Faction.playerFaction);
            if (prefab == null)
                continue;

            FuzzyProjectile fuzzy;
            if (!prefab.TryGetComponent<FuzzyProjectile>(out fuzzy) ||
                template.type != Item.Type.PrimaryWeapon ||
                template.category != Item.Category.LauncherCone)
            {
                continue;
            }

            ThrowerProfile profile = BuildThrowerProfile(
                template,
                prefab
            );
            if (profile == null)
                continue;

            if (!TorchThrowerProfiles.ContainsKey(template.damageType))
                TorchThrowerProfiles[template.damageType] = profile;
        }
    }

    private static Launcher FindLauncherTemplate(LauncherItemBase itemBase)
    {
        FieldInfo[] fields = itemBase.GetType().GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
        for (int i = 0; i < fields.Length; i++)
        {
            if (!typeof(Launcher).IsAssignableFrom(fields[i].FieldType))
                continue;

            Launcher launcher = fields[i].GetValue(itemBase) as Launcher;
            if (launcher != null)
                return launcher;
        }
        return null;
    }

    private static ThrowerProfile BuildThrowerProfile(
        Launcher launcher,
        GameObject prefab)
    {
        if (launcher == null || prefab == null)
            return null;

        ThrowerProfile profile = new ThrowerProfile();
        profile.template = launcher;
        profile.projectilePrefab = prefab;
        profile.baseReloadTime = Mathf.Max(0.0001f, launcher.BaseReloadTime);
        profile.baseRechargeSeconds = Mathf.Max(0f, launcher.BaseRechargeSeconds);
        profile.baseShotCount = Mathf.Max(1, launcher.BaseShotCount);
        profile.baseVelocity = Mathf.Max(0.01f, launcher.BaseVelocity);
        profile.baseLifetime = Mathf.Max(0.01f, launcher.BaseAutoDestroyTime);
        profile.inheritParentVelocity = launcher.inheritParentVelocity;

        ChargingLauncher charging = launcher as ChargingLauncher;
        if (charging != null)
        {
            profile.chargeType = charging.chargeType;
            profile.chargeSeconds = Mathf.Max(0f, charging.chargingTime);
            profile.unchargedMultiplier = Mathf.Clamp01(
                charging.unchargedMulitplier
            );
        }
        else
        {
            profile.chargeType = ChargingLauncher.ChargeType.Velocity;
            profile.chargeSeconds = 0f;
            profile.unchargedMultiplier = 1f;
        }

        return profile;
    }

    private static ThrowerProfile GetThrowerProfile(Activatable source)
    {
        Launcher launcher = source as Launcher;
        if (launcher != null)
        {
            ThrowerProfile cached;
            if (LauncherProfiles.TryGetValue(launcher, out cached))
                return cached;

            Projectile projectile = GetLauncherProjectile(launcher);
            FuzzyProjectile fuzzy = projectile as FuzzyProjectile;
            if (fuzzy == null)
                return null;

            GameObject prefab = projectile.gameObject;
            cached = BuildThrowerProfile(launcher, prefab);
            LauncherProfiles[launcher] = cached;
            return cached;
        }

        Torch torch = source as Torch;
        if (torch == null)
            return null;

        SearchThrowerProfiles();
        ThrowerProfile profile;
        if (TorchThrowerProfiles.TryGetValue(torch.damageType, out profile))
            return profile;

        foreach (KeyValuePair<Damageable.DamageType, ThrowerProfile> pair
            in TorchThrowerProfiles)
        {
            if (!warnedNoThrowerProfile)
            {
                warnedNoThrowerProfile = true;
                Debug.LogWarning(
                    "[Leviathan] Starfire could not find a matching elemental " +
                    "thrower profile; using the first Flame thrower profile."
                );
            }
            return pair.Value;
        }

        return null;
    }

    private static Launcher GetProjectileParentLauncher(
        Activatable source,
        ThrowerProfile profile,
        GameShip owner)
    {
        Launcher launcher = source as Launcher;
        if (launcher != null)
            return launcher;

        Torch torch = source as Torch;
        if (torch == null || profile == null || profile.template == null)
            return null;

        Launcher proxy;
        if (!TorchProxyLaunchers.TryGetValue(torch, out proxy) || proxy == null)
        {
            proxy = profile.template.Clone() as Launcher;
            if (proxy == null)
                return null;
            TorchProxyLaunchers[torch] = proxy;
        }

        proxy.parentShip = owner;

        // Projectile.Init immediately asks its parent Launcher for a weapon name.
        // ItemBase-backed launcher templates discovered from LauncherItemBase are
        // not equipped Item instances, so their clone has no resolvable ItemBase.
        // Supply the real Torch name directly; GetName() returns nameOverwrite
        // before touching ItemBase, while Starfire still owns hit attribution.
        proxy.nameOverwrite = torch.GetName(false, false);
        return proxy;
    }

    private static float GetSourceBaseCritChance(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.BaseCritChance;
        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : launcher.BaseCritChance;
    }

    private static float GetSourceLocalCritChance(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.LocalCritChance;
        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : launcher.LocalCritChance;
    }

    private static float GetSourceGlobalCritChance(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.CritChance;
        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : launcher.CritChance;
    }

    private static float GetSourceBaseStatusChance(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.BaseStatusEffectChance;
        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : launcher.BaseStatusEffectChance;
    }

    private static float GetSourceLocalStatusChance(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.LocalStatusEffectChance;
        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : launcher.LocalStatusEffectChance;
    }

    private static float GetSourceGlobalStatusChance(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.StatusEffectChance;
        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : launcher.StatusEffectChance;
    }

    private static float GetNormalizedCritChance(Activatable source)
    {
        float localDelta =
            GetSourceLocalCritChance(source) - GetSourceBaseCritChance(source);
        float globalDelta =
            GetSourceGlobalCritChance(source) - GetSourceLocalCritChance(source);

        return Mathf.Clamp01(
            LeviathanStarfireTuning.BaselineCritChance +
            localDelta +
            globalDelta
        );
    }

    private static float GetNormalizedStatusChance(
        Activatable source,
        StarfireSourceFamily family,
        ResolvedStarfireState resolved)
    {
        float localDelta =
            GetSourceLocalStatusChance(source) -
            GetSourceBaseStatusChance(source);
        float globalDelta =
            GetSourceGlobalStatusChance(source) -
            GetSourceLocalStatusChance(source);
        float familyBonus = family == StarfireSourceFamily.Thrower
            ? LeviathanStarfireTuning.ThrowerStatusChanceBonus
            : 0f;

        return Mathf.Clamp01(
            LeviathanStarfireTuning.BaselineStatusChance +
            familyBonus +
            localDelta +
            globalDelta +
            (resolved == null ? 0f : resolved.StatusChanceBonus)
        );
    }

    private static float GetNormalizedCritModifier(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return Mathf.Max(0f, torch.CritModifier);

        Launcher launcher = source as Launcher;
        return launcher == null ? 0f : Mathf.Max(0f, launcher.CritModifier);
    }

    private static Damageable.DamageType GetSourceDamageType(Activatable source)
    {
        Torch torch = source as Torch;
        if (torch != null)
            return torch.damageType;
        Launcher launcher = source as Launcher;
        return launcher == null
            ? Damageable.DamageType.Kinetic
            : launcher.damageType;
    }

    private static float GetTorchRateFactor(Torch torch)
    {
        if (torch == null || torch.parentShip == null)
            return 1f;

        float interval = torch.parentShip.ApplyModifier(
            Modifier.Type.BeamTickRate,
            Item.Category.None,
            0.2f,
            true
        );
        if (interval <= 0.0001f)
            return 1f;

        return Mathf.Max(0.01f, 0.2f / interval);
    }

    private static float GetThrowerProjectilesPerSecond(Launcher launcher)
    {
        if (launcher == null || launcher.parentShip == null)
            return 0f;

        float interval = Mathf.Max(
            launcher.ReloadTime,
            launcher.RechargeSeconds
        );
        interval = Mathf.Max(0.0001f, interval);
        float weaponSpeed = Mathf.Max(
            0.0001f,
            launcher.parentShip.GetWeaponSpeedPerc()
        );
        return Mathf.Max(1, launcher.ShotCount) * weaponSpeed / interval;
    }

    private static float GetProfileProjectilesPerSecond(ThrowerProfile profile)
    {
        if (profile == null)
            return 0f;

        float interval = Mathf.Max(
            profile.baseReloadTime,
            profile.baseRechargeSeconds
        );
        return Mathf.Max(1, profile.baseShotCount) /
            Mathf.Max(0.0001f, interval);
    }

    private static float GetNormalizedActiveNonCritDps(
        Activatable source,
        StarfireSourceFamily family)
    {
        Torch torch = source as Torch;
        if (family == StarfireSourceFamily.Torch && torch != null)
        {
            return Mathf.Max(0f, torch.Damage) *
                LeviathanStarfireTuning.TorchDamageNormalization *
                LeviathanStarfireTuning.TorchFamilyDamageMultiplier *
                GetTorchRateFactor(torch);
        }

        Launcher launcher = source as Launcher;
        if (family == StarfireSourceFamily.Thrower && launcher != null)
        {
            return Mathf.Max(0f, launcher.Damage) *
                LeviathanStarfireTuning.ThrowerDamageNormalization *
                GetThrowerProjectilesPerSecond(launcher);
        }

        return 0f;
    }

    private static float GetTorchRangeFactor(Torch torch)
    {
        if (torch == null || torch.BaseMaxRange <= 0.0001f)
            return 1f;
        return Mathf.Max(0.01f, torch.MaxRange / torch.BaseMaxRange);
    }

    private static float GetMuzzleOffset(Activatable source)
    {
        float cached;
        if (MuzzleOffsetCache.TryGetValue(source, out cached))
            return cached;

        cached = 0f;
        if (source != null && source.gameObject != null)
        {
            SpriteRenderer renderer =
                source.gameObject.GetComponent<SpriteRenderer>();
            if (renderer != null && renderer.sprite != null)
                cached = renderer.sprite.bounds.max.x;
        }

        MuzzleOffsetCache[source] = cached;
        return cached;
    }

    private static Vector2 GetMuzzlePosition(Activatable source)
    {
        if (source == null || source.gameObject == null)
            return Vector2.zero;

        return source.gameObject.transform.TransformPoint(
            new Vector3(GetMuzzleOffset(source), 0f, 0f)
        );
    }

    private static Vector2 GetSourceDirection(Activatable source)
    {
        if (source == null || source.gameObject == null)
            return Vector2.right;

        Vector2 direction = source.gameObject.transform.right;
        return direction.sqrMagnitude <= 0.0001f
            ? Vector2.right
            : direction.normalized;
    }

    private static SpriteRenderer GetSourceRenderer(Activatable source)
    {
        if (source == null || source.gameObject == null)
            return null;
        return source.gameObject.GetComponent<SpriteRenderer>();
    }

    private static BreathState GetOrCreateBreathState(
        Activatable source,
        ResolvedStarfireState resolved)
    {
        if (source == null || resolved == null)
            return null;

        BreathState state;
        if (!BreathStates.TryGetValue(source, out state) || state == null)
        {
            state = new BreathState();
            BreathStates[source] = state;
        }

        float capacity = Mathf.Max(0f, resolved.CapacitySeconds);
        if (!state.initialized)
        {
            state.initialized = true;
            state.capacitySeconds = capacity;
            state.remainingSeconds = capacity;
            state.lastUpdateTime = Time.time;
            state.idleStartedTime = IsSourceFiring(source) ? -1f : Time.time;
            return state;
        }

        if (!Mathf.Approximately(state.capacitySeconds, capacity))
        {
            float fraction = state.capacitySeconds <= 0.0001f
                ? 1f
                : Mathf.Clamp01(
                    state.remainingSeconds / state.capacitySeconds
                );
            state.capacitySeconds = capacity;
            state.remainingSeconds = capacity * fraction;
        }

        return state;
    }

    private static float EvaluateBreathComponent(
        float falloffProgress,
        float minimumFraction,
        float curveExponent)
    {
        float curved = Mathf.Pow(
            Mathf.Clamp01(falloffProgress),
            Mathf.Max(0.01f, curveExponent)
        );
        return Mathf.Lerp(
            1f,
            Mathf.Clamp01(minimumFraction),
            curved
        );
    }

    private static float GetVelocityFalloffMultiplier(
        float damageFraction,
        ResolvedStarfireState resolved)
    {
        if (resolved == null)
            return 1f;

        float minimumDamage = Mathf.Clamp01(resolved.MinimumDamageFraction);
        float minimumVelocity = Mathf.Clamp01(resolved.MinimumVelocityFraction);
        float damageRange = Mathf.Max(0.0001f, 1f - minimumDamage);
        float damageFalloffProgress = Mathf.Clamp01(
            (1f - Mathf.Clamp(damageFraction, minimumDamage, 1f)) /
            damageRange
        );

        // Start at the configured fraction of damage's falloff rate, then blend
        // toward full progress as the reservoir empties so velocity still reaches
        // its own floor instead of asymptoting above it.
        float rate = Mathf.Clamp01(resolved.VelocityFalloffRate);
        float velocityFalloffProgress = damageFalloffProgress *
            (rate + (1f - rate) * damageFalloffProgress);

        return Mathf.Lerp(
            1f,
            minimumVelocity,
            velocityFalloffProgress
        );
    }

    private static float GetBreathFalloffProgress(
        BreathState state,
        ResolvedStarfireState resolved)
    {
        if (state == null || resolved == null)
            return 0f;

        float retreat = Mathf.Max(0f, resolved.RetreatSeconds);
        if (retreat <= 0.0001f)
            return state.remainingSeconds <= 0.0001f ? 1f : 0f;
        if (state.remainingSeconds >= retreat)
            return 0f;

        return Mathf.Clamp01(1f - state.remainingSeconds / retreat);
    }

    private static float CalculateRemainingDamageSeconds(
        BreathState breath,
        ResolvedStarfireState state)
    {
        if (breath == null || state == null ||
            breath.remainingSeconds <= 0.0001f)
        {
            return 0f;
        }

        float drainRate = Mathf.Max(0.0001f, state.ActiveDrainRate);
        float remaining = Mathf.Max(0f, breath.remainingSeconds);
        float retreat = Mathf.Max(0f, state.RetreatSeconds);
        if (retreat <= 0.0001f)
            return remaining / drainRate;

        float fullReservoir = Mathf.Max(0f, remaining - retreat);
        float falloffReservoir = Mathf.Min(remaining, retreat);
        float exponent = Mathf.Max(0.01f, state.DamageFalloffCurveExponent);
        float minimum = Mathf.Clamp01(state.MinimumDamageFraction);
        float normalizedRemaining = Mathf.Clamp01(
            falloffReservoir / retreat
        );
        float falloffIntegral = falloffReservoir -
            (1f - minimum) *
            retreat /
            (exponent + 1f) *
            (1f - Mathf.Pow(
                1f - normalizedRemaining,
                exponent + 1f
            ));

        return Mathf.Max(
            0f,
            (fullReservoir + falloffIntegral) / drainRate
        );
    }

    private static bool IsInsideStartupDelay(
        BreathState state,
        ResolvedStarfireState resolved)
    {
        if (state == null || resolved == null ||
            resolved.StartupDelaySeconds <= 0f)
        {
            return false;
        }

        if (state.startupStartedTime < 0f)
            state.startupStartedTime = Time.time;

        return Time.time - state.startupStartedTime <
            resolved.StartupDelaySeconds;
    }

    private static int GetTorchShotCount(ThrowerProfile profile)
    {
        return profile == null ? 0 : Mathf.Max(1, profile.baseShotCount);
    }

    private static float GetTorchVolleyInterval(
        Torch torch,
        ThrowerProfile profile)
    {
        if (torch == null || profile == null)
            return float.PositiveInfinity;

        float baseInterval = Mathf.Max(
            profile.baseReloadTime,
            profile.baseRechargeSeconds
        );
        return Mathf.Max(
            0.0001f,
            baseInterval / GetTorchRateFactor(torch)
        );
    }

    private static float GetTorchProjectileVelocity(
        ThrowerProfile profile,
        BreathState breath)
    {
        if (profile == null)
            return 0f;

        float velocity = profile.baseVelocity;
        if (profile.chargeSeconds <= 0.0001f ||
            profile.chargeType != ChargingLauncher.ChargeType.Velocity)
        {
            return velocity;
        }

        float t = Mathf.Clamp01(
            breath.activeSeconds / profile.chargeSeconds
        );
        float multiplier = Mathf.Lerp(
            profile.unchargedMultiplier,
            1f,
            t
        );
        return velocity * multiplier;
    }

    private static float GetProjectileLifetime(
        Activatable source,
        ThrowerProfile profile,
        ResolvedStarfireState resolved,
        float lengthFraction)
    {
        Launcher launcher = source as Launcher;
        float lifetime = launcher != null
            ? Mathf.Max(0.01f, launcher.AutoDestroyTime)
            : profile == null
                ? 0f
                : Mathf.Max(0.01f, profile.baseLifetime) *
                    GetTorchRangeFactor(source as Torch);

        return Mathf.Max(
            0.01f,
            lifetime *
            resolved.LengthMultiplier *
            Mathf.Clamp01(lengthFraction)
        );
    }

    private static float GetFiringHalfAngle(
        StarfireSourceFamily family,
        ResolvedStarfireState resolved,
        float widthFraction)
    {
        float angle = Mathf.Max(0f, resolved.FiringHalfAngleDegrees) *
            resolved.WidthMultiplier *
            Mathf.Clamp01(widthFraction);

        if (family == StarfireSourceFamily.Thrower)
            angle *= LeviathanStarfireTuning.ThrowerFiringAngleMultiplier;

        return angle;
    }

    private static DamageData[] BuildNormalizedDamagePacket(
        Activatable source,
        bool crit,
        float nonCritDamage,
        float expectedDps,
        float critModifier)
    {
        if (source == null || nonCritDamage <= 0f)
            return null;

        DamageData[] packet = null;
        Torch torch = source as Torch;
        if (torch != null)
        {
            float originalCharge = 1f;
            object raw = ChargeField == null ? null : ChargeField.GetValue(torch);
            if (raw is float)
                originalCharge = (float)raw;

            try
            {
                if (ChargeField != null)
                    ChargeField.SetValue(torch, 1f);
                packet = torch.GetDamageData(crit);
            }
            finally
            {
                if (ChargeField != null)
                    ChargeField.SetValue(torch, originalCharge);
            }
        }
        else
        {
            Launcher launcher = source as Launcher;
            if (launcher != null)
                packet = launcher.GetDamageData(crit, false);
        }

        if (packet == null || packet.Length == 0)
            return null;

        float nativePrimary = DamageData.GetDamageData(
            Modifier.Type.Damage,
            packet
        ).damage;
        if (Mathf.Abs(nativePrimary) <= 0.0001f)
            return null;

        float targetPrimary = nonCritDamage *
            (crit ? 1f + critModifier : 1f);
        float scale = targetPrimary / nativePrimary;

        for (int i = 0; i < packet.Length; i++)
        {
            DamageData datum = packet[i];
            datum.damage *= scale;
            datum.dps = expectedDps;
            packet[i] = datum;
        }

        return packet;
    }

    private static void ApplyTorchLeechIfApplicable(
        Activatable source,
        GameShip target,
        Vector2 hitPoint)
    {
        Torch torch = source as Torch;
        if (torch == null || target == null || target.IsNetRemote() ||
            TorchGladiatorLeechMethod == null)
        {
            return;
        }

        object[] args = torchLeechInvokeArgs;
        if (args == null)
        {
            args = new object[2];
            torchLeechInvokeArgs = args;
        }

        args[0] = target;
        args[1] = hitPoint;
        TorchGladiatorLeechMethod.Invoke(torch, args);
    }

    private static bool ApplyProjectileDamage(
        Projectile projectile,
        StarfireProjectileContext context,
        GameShip target,
        Vector2 hitPoint)
    {
        if (projectile == null ||
            context.source == null || context.owner == null ||
            target == null || context.nonCritDamage <= 0f)
        {
            return false;
        }

        bool crit = Modifier.CritRoll(context.critChance, target);
        DamageData[] packet = BuildNormalizedDamagePacket(
            context.source,
            crit,
            context.nonCritDamage,
            context.expectedDps,
            context.critModifier
        );
        if (packet == null || RouteDamageMethod == null)
            return false;

        bool bypassDamageLimit = context.source.HasCustomizer(
            Customizer.Type.BypassDamageLimit
        );
        Vector2 direction = projectile.rigidBody == null ||
            projectile.rigidBody.velocity.sqrMagnitude <= 0.0001f
            ? GetSourceDirection(context.source)
            : projectile.rigidBody.velocity.normalized;

        target.SetLastDamageDirection(direction);
        target.lastDamagedByWeaponName =
            context.source.GetName(false, false);
        target.lastDamagedByShipName = context.owner.GetName();
        target.lastDamagedByFaction = context.owner.faction;

        bool destroyed = RouteNormalizedDamage(
            target,
            GetSourceDamageType(context.source),
            packet,
            context.statusChance,
            crit,
            hitPoint,
            context.owner,
            bypassDamageLimit,
            context.knockback,
            context.source,
            projectile.transform.eulerAngles.z
        );

        RelayNormalizedConduit(
            context.source,
            context.owner,
            target,
            packet,
            hitPoint,
            bypassDamageLimit
        );

        ApplyTorchLeechIfApplicable(context.source, target, hitPoint);

        if (context.knockback > 0f)
        {
            Modifier.Knockback(
                context.knockback,
                target.GetRigidBody(),
                projectile.transform.position,
                context.owner
            );
        }

        if (!target.IsDrone() && destroyed)
        {
            float shedChance = context.source.ApplyModifierToPercentage(
                Modifier.Type.OnEnemyDeathShed,
                0f,
                true
            );
            if (shedChance > 0f)
                target.ShedStatusEffects(shedChance, true, context.owner);

            Projectile.ProcessDeathSurge(
                context.source,
                context.owner,
                target.transform.position,
                context.expectedDps
            );
        }

        return true;
    }

    public static bool TryHandleProjectileHit(
        Projectile projectile,
        GameObject hitObject,
        Vector2 hitPoint,
        out bool result)
    {
        result = false;

        StarfireProjectileContext context;
        if (projectile == null ||
            !StarfireProjectiles.TryGetValue(projectile, out context))
        {
            return false;
        }

        if (context.consumed ||
            context.owner == null || context.source == null)
        {
            result = true;
            return true;
        }

        if (hitObject == null || hitObject == context.owner.gameObject)
        {
            result = false;
            return true;
        }

        GameObject targetObject = hitObject;
        if (targetObject.CompareTag("Shield") &&
            targetObject.transform.parent != null)
        {
            targetObject = targetObject.transform.parent.gameObject;
        }

        GameShip target;
        if (!GameShip.TryGetShip(targetObject, out target) || target == null)
        {
            context.consumed = true;
            StarfireProjectiles[projectile] = context;
            projectile.ScheduleDestroy();
            result = true;
            return true;
        }

        if (target == context.owner ||
            !Faction.IsHostile(context.owner.faction, target.faction) ||
            !target.CanBeDamagedBy(context.owner, false) ||
            target.IsDodging())
        {
            result = false;
            return true;
        }

        context.consumed = true;
        StarfireProjectiles[projectile] = context;
        ApplyProjectileDamage(projectile, context, target, hitPoint);
        projectile.ScheduleDestroy();
        result = true;
        return true;
    }

    public static bool ShouldSuppressBorrowedProjectileSideEffects(
        Projectile projectile)
    {
        StarfireProjectileContext context;
        return projectile != null &&
            StarfireProjectiles.TryGetValue(projectile, out context) &&
            context.family == StarfireSourceFamily.Torch;
    }

    public static void ForgetStarfireProjectile(Projectile projectile)
    {
        if (projectile != null)
            StarfireProjectiles.Remove(projectile);
    }

    private static void SpawnProjectile(
        Activatable source,
        GameShip owner,
        StarfireSourceFamily family,
        ThrowerProfile profile,
        ResolvedStarfireState resolved,
        float damageFraction,
        float lengthFraction,
        float widthFraction,
        float velocity,
        int shotCount)
    {
        if (source == null || owner == null || profile == null ||
            profile.projectilePrefab == null ||
            PoolController.instance == null || shotCount <= 0)
        {
            return;
        }

        Launcher parentLauncher = GetProjectileParentLauncher(
            source,
            profile,
            owner
        );
        if (parentLauncher == null)
            return;

        float halfAngle = GetFiringHalfAngle(
            family,
            resolved,
            widthFraction
        );
        float lifetime = GetProjectileLifetime(
            source,
            profile,
            resolved,
            lengthFraction
        );
        float critChance = GetNormalizedCritChance(source);
        float critModifier = GetNormalizedCritModifier(source);
        float statusChance = GetNormalizedStatusChance(source, family, resolved);
        float normalizedDps = GetNormalizedActiveNonCritDps(source, family) *
            resolved.DamageMultiplier *
            damageFraction;
        float expectedDps = normalizedDps *
            (1f + critChance * critModifier);

        float nonCritDamage;
        Torch torch = source as Torch;
        if (torch != null)
        {
            float referenceRate = GetProfileProjectilesPerSecond(profile);
            nonCritDamage = referenceRate <= 0.0001f
                ? 0f
                : Mathf.Max(0f, torch.Damage) *
                    LeviathanStarfireTuning.TorchDamageNormalization *
                    LeviathanStarfireTuning.TorchFamilyDamageMultiplier /
                    referenceRate *
                    resolved.DamageMultiplier *
                    damageFraction;
        }
        else
        {
            Launcher launcher = source as Launcher;
            nonCritDamage = launcher == null
                ? 0f
                : Mathf.Max(0f, launcher.Damage) *
                    LeviathanStarfireTuning.ThrowerDamageNormalization *
                    resolved.DamageMultiplier *
                    damageFraction;
        }

        float knockback = source.ApplyModifierToPercentage(
            Modifier.Type.Knockback,
            0f,
            true
        );
        float projectilesPerSecond = Mathf.Max(
            1f,
            family == StarfireSourceFamily.Thrower
                ? GetThrowerProjectilesPerSecond(source as Launcher)
                : GetProfileProjectilesPerSecond(profile) *
                    GetTorchRateFactor(source as Torch)
        );
        knockback /= projectilesPerSecond;

        Vector2 muzzle = GetMuzzlePosition(source);
        Vector2 forward = GetSourceDirection(source);
        Rigidbody2D ownerBody = owner.GetRigidBody();
        bool inheritVelocity = source is Launcher
            ? ((Launcher)source).inheritParentVelocity
            : profile.inheritParentVelocity;
        float sizeMultiplier =
            (family == StarfireSourceFamily.Thrower
                ? LeviathanStarfireTuning.ThrowerProjectileSizeMultiplier
                : 1f) *
            resolved.ProjectileSizeMultiplier;

        ProjectileIgnoreCache.Clear();
        ProjectileIgnoreCache.Add(owner.gameObject);
        if (owner.shield != null)
            ProjectileIgnoreCache.Add(owner.shield.gameObject);

        for (int i = 0; i < shotCount; i++)
        {
            float angle = UnityEngine.Random.Range(-halfAngle, halfAngle);
            Quaternion rotation = Quaternion.Euler(0f, 0f,
                Mathf.Atan2(forward.y, forward.x) * Mathf.Rad2Deg + angle);

            GameObject obj = PoolController.instance.GetObject(
                profile.projectilePrefab,
                muzzle,
                rotation,
                false
            );
            if (obj == null)
                continue;

            Projectile projectile;
            if (!obj.TryGetComponent<Projectile>(out projectile) ||
                !(projectile is FuzzyProjectile) ||
                projectile.rigidBody == null)
            {
                if (projectile != null)
                    projectile.PoolDestroy();
                continue;
            }

            StarfireProjectileContext context =
                new StarfireProjectileContext();
            context.source = source;
            context.owner = owner;
            context.family = family;
            context.nonCritDamage = Mathf.Max(0f, nonCritDamage);
            context.expectedDps = Mathf.Max(0f, expectedDps);
            context.critChance = critChance;
            context.critModifier = critModifier;
            context.statusChance = statusChance;
            context.knockback = Mathf.Max(0f, knockback);
            StarfireProjectiles[projectile] = context;

            FlameProjectile flame = projectile as FlameProjectile;
            float originalScatter = 0f;
            float originalBaseScale = 1f;
            if (flame != null)
            {
                originalScatter = flame.scatter;
                originalBaseScale = flame.baseScale;
                // The resolved Starfire cone replaces authored thrower scatter.
                // FlameProjectile itself supplies the native random distribution.
                flame.scatter = 0f;
                flame.baseScale = originalBaseScale * sizeMultiplier;
            }

            projectile.SetDestroyTime(lifetime, lifetime * 0.8f);
            if (inheritVelocity && ownerBody != null)
            {
                projectile.rigidBody.AddForce(
                    ownerBody.velocity,
                    ForceMode2D.Impulse
                );
            }
            projectile.rigidBody.AddForce(
                rotation * (Vector2.right * velocity),
                ForceMode2D.Impulse
            );

            try
            {
                projectile.Init(
                    0f,
                    false,
                    false,
                    parentLauncher,
                    owner,
                    ProjectileIgnoreCache,
                    null
                );
            }
            finally
            {
                if (flame != null)
                {
                    flame.scatter = originalScatter;
                    flame.baseScale = originalBaseScale;
                }
            }

            if (flame == null &&
                !Mathf.Approximately(sizeMultiplier, 1f))
            {
                projectile.transform.localScale *= sizeMultiplier;
            }

            if (projectile.hasResettableTrails)
                projectile.resettableTrails.ResetTrails();
        }
    }

    private static void SpawnTorchVolley(
        Torch torch,
        GameShip player,
        ResolvedStarfireState resolved,
        BreathState breath,
        float damageFraction,
        float lengthFraction,
        float widthFraction)
    {
        ThrowerProfile profile = GetThrowerProfile(torch);
        if (profile == null)
            return;

        float velocityMultiplier =
            GetVelocityFalloffMultiplier(damageFraction, resolved);

        SpawnProjectile(
            torch,
            player,
            StarfireSourceFamily.Torch,
            profile,
            resolved,
            damageFraction,
            lengthFraction,
            widthFraction,
            GetTorchProjectileVelocity(profile, breath) * velocityMultiplier,
            GetTorchShotCount(profile)
        );
    }

    public static bool ShouldRunNativeLauncherShot(Launcher launcher)
    {
        if (launcher == null || !IsThrowerLauncher(launcher))
            return true;

        GameShip player = launcher.parentShip;
        if (!IsStarfireActive(player))
            return true;

        if (!ReferenceEquals(FindSource(player), launcher))
            return false;

        ResolvedStarfireState resolved = GetResolvedStarfireState();
        BreathState breath = GetOrCreateBreathState(launcher, resolved);
        if (breath == null ||
            !IsSourceFiring(launcher) ||
            IsInsideStartupDelay(breath, resolved) ||
            resolved.BlastWaveEnabled ||
            breath.remainingSeconds <= 0.0001f)
        {
            return false;
        }

        return true;
    }

    public static void FinalizeNativeLauncherShot(
        Launcher launcher,
        GameObject muzzleObject,
        List<Projectile> projectiles)
    {
        if (launcher == null || projectiles == null || projectiles.Count == 0 ||
            !IsThrowerLauncher(launcher))
        {
            return;
        }

        GameShip player = launcher.parentShip;
        if (!IsStarfireActive(player) ||
            !ReferenceEquals(FindSource(player), launcher))
        {
            return;
        }

        ResolvedStarfireState resolved = GetResolvedStarfireState();
        BreathState breath = GetOrCreateBreathState(launcher, resolved);
        ThrowerProfile profile = GetThrowerProfile(launcher);
        if (breath == null || profile == null)
            return;

        float falloff = GetBreathFalloffProgress(breath, resolved);
        float damageFraction = EvaluateBreathComponent(
            falloff,
            resolved.MinimumDamageFraction,
            resolved.DamageFalloffCurveExponent
        );
        float lengthFraction = EvaluateBreathComponent(
            falloff,
            resolved.MinimumLengthFraction,
            resolved.LengthFalloffCurveExponent
        );
        float widthFraction = EvaluateBreathComponent(
            falloff,
            resolved.MinimumWidthFraction,
            resolved.WidthFalloffCurveExponent
        );

        float halfAngle = GetFiringHalfAngle(
            StarfireSourceFamily.Thrower,
            resolved,
            widthFraction
        );
        float lifetime = GetProjectileLifetime(
            launcher,
            profile,
            resolved,
            lengthFraction
        );
        float critChance = GetNormalizedCritChance(launcher);
        float critModifier = GetNormalizedCritModifier(launcher);
        float statusChance = GetNormalizedStatusChance(
            launcher,
            StarfireSourceFamily.Thrower,
            resolved
        );
        float normalizedDps = GetNormalizedActiveNonCritDps(
            launcher,
            StarfireSourceFamily.Thrower
        ) * resolved.DamageMultiplier * damageFraction;
        float expectedDps = normalizedDps *
            (1f + critChance * critModifier);

        // Native mirrored launchers split one weapon packet across both muzzles.
        // Keep that aggregate behavior while Starfire normalizes per-projectile damage.
        float mirrorPacketScale =
            !launcher.fireAlternate && launcher.mirrorGameObject != null
                ? 0.5f
                : 1f;
        float nonCritDamage = Mathf.Max(0f, launcher.Damage) *
            LeviathanStarfireTuning.ThrowerDamageNormalization *
            resolved.DamageMultiplier *
            damageFraction *
            mirrorPacketScale;

        float projectilesPerSecond = Mathf.Max(
            1f,
            GetThrowerProjectilesPerSecond(launcher)
        );
        float knockback = launcher.ApplyModifierToPercentage(
            Modifier.Type.Knockback,
            0f,
            true
        ) / projectilesPerSecond;

        Vector2 forward = muzzleObject == null
            ? GetSourceDirection(launcher)
            : (Vector2)muzzleObject.transform.right;
        if (forward.sqrMagnitude <= 0.0001f)
            forward = Vector2.right;
        else
            forward.Normalize();

        Rigidbody2D ownerBody = player == null ? null : player.GetRigidBody();
        Vector2 inheritedVelocity =
            launcher.inheritParentVelocity && ownerBody != null
                ? ownerBody.velocity
                : Vector2.zero;

        for (int i = 0; i < projectiles.Count; i++)
        {
            Projectile projectile = projectiles[i];
            if (projectile == null ||
                !(projectile is FuzzyProjectile) ||
                projectile.rigidBody == null)
            {
                continue;
            }

            StarfireProjectileContext context = new StarfireProjectileContext();
            context.source = launcher;
            context.owner = player;
            context.family = StarfireSourceFamily.Thrower;
            context.nonCritDamage = nonCritDamage;
            context.expectedDps = Mathf.Max(0f, expectedDps);
            context.critChance = critChance;
            context.critModifier = critModifier;
            context.statusChance = statusChance;
            context.knockback = Mathf.Max(0f, knockback);
            StarfireProjectiles[projectile] = context;

            // Keep native FlameProjectile velocity variance, pooling, animation and
            // lifetime behavior, but replace authored scatter with Starfire's cone.
            Vector2 shotVelocity = projectile.rigidBody.velocity - inheritedVelocity;
            float speed = shotVelocity.magnitude;
            if (speed <= 0.0001f)
                speed = Mathf.Max(0.01f, launcher.GetCurrentVelocity());

            speed *= GetVelocityFalloffMultiplier(damageFraction, resolved);

            float angle = UnityEngine.Random.Range(-halfAngle, halfAngle);
            Vector2 direction = Utils.RotateVector(forward, angle).normalized;
            projectile.rigidBody.velocity = inheritedVelocity + direction * speed;
            projectile.SetDestroyTime(lifetime, lifetime * 0.8f);

            FlameProjectile flame = projectile as FlameProjectile;
            if (flame != null && FlameStartScaleField != null)
            {
                object rawStartScale = FlameStartScaleField.GetValue(flame);
                if (rawStartScale is float)
                {
                    float sizeMultiplier =
                        LeviathanStarfireTuning.ThrowerProjectileSizeMultiplier *
                        resolved.ProjectileSizeMultiplier;
                    float scaledStart = (float)rawStartScale * sizeMultiplier;
                    FlameStartScaleField.SetValue(flame, scaledStart);
                    projectile.transform.localScale *= sizeMultiplier;
                }
            }
            else
            {
                // Base FuzzyProjectile variants do not have FlameProjectile's
                // startScale field, so their native transform scale is stable.
                projectile.transform.localScale *=
                    LeviathanStarfireTuning.ThrowerProjectileSizeMultiplier *
                    resolved.ProjectileSizeMultiplier;
            }
        }
    }

    public static void UpdateSource(Activatable source)
    {
        if (source == null)
            return;

        GameShip player;
        StarfireSourceFamily family;
        ResolvedStarfireState resolved;
        if (!TryGetSourceContext(
            source,
            out player,
            out family,
            out resolved))
        {
            BreathStates.Remove(source);
            BlastWaveState staleWave;
            if (BlastWaves.TryGetValue(source, out staleWave))
                DestroyBlastWave(source, staleWave);
            return;
        }

        BreathState state = GetOrCreateBreathState(source, resolved);
        if (state == null)
            return;

        float now = Time.time;
        float delta = Mathf.Clamp(now - state.lastUpdateTime, 0f, 0.25f);
        state.lastUpdateTime = now;
        bool firing = IsSourceFiring(source);
        float capacity = Mathf.Max(0f, state.capacitySeconds);

        UpdateBlastWave(source, resolved);

        if (firing)
        {
            state.idleStartedTime = -1f;
            state.activeSeconds += delta;
            bool insideStartup = IsInsideStartupDelay(state, resolved);

            if (!insideStartup && resolved.BlastWaveEnabled)
            {
                if (!state.blastFiredThisActivation &&
                    state.remainingSeconds > 0.0001f)
                {
                    FireBlastWave(
                        source,
                        player,
                        family,
                        resolved,
                        state
                    );
                    state.remainingSeconds = 0f;
                    state.blastFiredThisActivation = true;
                }
            }
            else if (!insideStartup && family == StarfireSourceFamily.Torch)
            {
                ThrowerProfile profile = GetThrowerProfile(source);
                if (profile != null)
                {
                    float interval = GetTorchVolleyInterval(
                        source as Torch,
                        profile
                    );
                    state.torchVolleyAccumulator += delta;
                    int volleys = 0;
                    while (state.torchVolleyAccumulator >= interval &&
                        volleys < 8)
                    {
                        state.torchVolleyAccumulator -= interval;
                        float falloff = GetBreathFalloffProgress(
                            state,
                            resolved
                        );
                        float damageFraction = EvaluateBreathComponent(
                            falloff,
                            resolved.MinimumDamageFraction,
                            resolved.DamageFalloffCurveExponent
                        );
                        float lengthFraction = EvaluateBreathComponent(
                            falloff,
                            resolved.MinimumLengthFraction,
                            resolved.LengthFalloffCurveExponent
                        );
                        float widthFraction = EvaluateBreathComponent(
                            falloff,
                            resolved.MinimumWidthFraction,
                            resolved.WidthFalloffCurveExponent
                        );
                        SpawnTorchVolley(
                            source as Torch,
                            player,
                            resolved,
                            state,
                            damageFraction,
                            lengthFraction,
                            widthFraction
                        );
                        volleys++;
                    }
                }
            }

            if (!insideStartup && delta > 0f)
            {
                state.remainingSeconds = Mathf.Max(
                    0f,
                    state.remainingSeconds -
                        delta * resolved.ActiveDrainRate
                );
            }
        }
        else
        {
            state.startupStartedTime = -1f;
            state.activeSeconds = 0f;
            state.torchVolleyAccumulator = 0f;
            state.blastFiredThisActivation = false;

            if (state.idleStartedTime < 0f)
                state.idleStartedTime = now;

            bool recoveryStarted =
                capacity > 0f &&
                state.remainingSeconds < capacity &&
                now - state.idleStartedTime >= resolved.RecoveryDelaySeconds;

            if (recoveryStarted && delta > 0f)
            {
                float missingFraction = Mathf.Clamp01(
                    1f - state.remainingSeconds / capacity
                );
                float curveScale = Mathf.Pow(
                    Mathf.Max(0.01f, missingFraction),
                    resolved.RecoveryCurveExponent - 1f
                );
                curveScale = Mathf.Clamp(curveScale, 0.05f, 20f);
                state.remainingSeconds = Mathf.Min(
                    capacity,
                    state.remainingSeconds +
                        delta *
                        resolved.RecoverySecondsPerSecond *
                        curveScale
                );
            }

            if (recoveryStarted &&
                state.remainingSeconds < capacity - 0.0001f &&
                resolved.RechargePullEnabled)
            {
                ApplyRechargePull(player, resolved);
            }
        }

        state.remainingSeconds = Mathf.Clamp(
            state.remainingSeconds,
            0f,
            capacity
        );
    }

    public static void RefreshTorchSuppression(Torch torch)
    {
        if (torch == null)
            return;

        GameShip player = torch.parentShip;
        bool suppress = IsStarfireActive(player) &&
            GetSourceFamily(torch) == StarfireSourceFamily.Torch;

        if (suppress)
        {
            SuppressedTorchSpikes.Add(torch);
            SetSpikeSuppressed(MainSpikeField == null
                ? null
                : MainSpikeField.GetValue(torch), true);
            SetSpikeSuppressed(MirrorSpikeField == null
                ? null
                : MirrorSpikeField.GetValue(torch), true);
        }
        else if (SuppressedTorchSpikes.Remove(torch))
        {
            SetSpikeSuppressed(MainSpikeField == null
                ? null
                : MainSpikeField.GetValue(torch), false);
            SetSpikeSuppressed(MirrorSpikeField == null
                ? null
                : MirrorSpikeField.GetValue(torch), false);
        }
    }

    private static void SetSpikeSuppressed(object nativeSpike, bool suppressed)
    {
        GameObject obj = GetSpikeGameObject(nativeSpike);
        if (obj == null)
            return;

        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].enabled = !suppressed;

        Collider2D[] colliders = obj.GetComponentsInChildren<Collider2D>(true);
        for (int i = 0; i < colliders.Length; i++)
            if (colliders[i] != null)
                colliders[i].enabled = !suppressed;
    }

    private static GameObject GetSpikeGameObject(object nativeSpike)
    {
        if (nativeSpike == null)
            return null;

        Type type = nativeSpike.GetType();
        FieldInfo named = AccessTools.Field(type, "obj");
        if (named != null)
        {
            GameObject direct = named.GetValue(nativeSpike) as GameObject;
            if (direct != null)
                return direct;
        }

        return null;
    }

    public static bool ShouldSuppressTorchDamage(Torch torch)
    {
        return torch != null &&
            IsStarfireActive(torch.parentShip) &&
            GetSourceFamily(torch) == StarfireSourceFamily.Torch;
    }

    public struct HeatRateState
    {
        public bool changed;
        public float originalHeatPerSecond;
    }

    public static HeatRateState PrepareShipHeatRate(GameShip player)
    {
        HeatRateState state = new HeatRateState();
        if (player == null ||
            GameShipHeatPerSecondField == null ||
            !IsStarfireActive(player))
        {
            return state;
        }

        Activatable source = FindSource(player);
        if (source == null)
            return state;

        object raw = GameShipHeatPerSecondField.GetValue(player);
        if (!(raw is float))
            return state;

        float original = (float)raw;
        float adjusted = original;

        if (player.slots != null)
        {
            for (int i = 0; i < player.slots.Length; i++)
            {
                Slot slot = player.slots[i];
                if (slot == null ||
                    slot.type != Item.Type.PrimaryWeapon ||
                    slot.equippable == null ||
                    slot.equippable.durability == 0 ||
                    ReferenceEquals(slot.equippable, source) ||
                    GetSourceFamily(slot.equippable) == StarfireSourceFamily.None)
                {
                    continue;
                }

                Activatable suppressed = slot.equippable as Activatable;
                if (suppressed != null)
                    adjusted -= suppressed.heatPerSecond;
            }
        }

        if (source.durability != 0)
        {
            // GameShip caches every equipped Activatable's heat contribution.
            // Replace the source-family authored value (Torch 5 / Thrower 3 for
            // the reference Cryo pair) with Starfire's common 4 heat/s baseline,
            // then apply the specialization heat scalar exactly once.
            ResolvedStarfireState resolved = GetResolvedStarfireState();
            adjusted -= source.heatPerSecond;
            adjusted += LeviathanStarfireTuning.BaselineHeatPerSecond *
                resolved.HeatMultiplier;
        }

        adjusted = Mathf.Max(0f, adjusted);
        if (Mathf.Approximately(adjusted, original))
            return state;

        state.changed = true;
        state.originalHeatPerSecond = original;
        GameShipHeatPerSecondField.SetValue(player, adjusted);
        return state;
    }

    public static void RestoreShipHeatRate(
        GameShip player,
        HeatRateState state)
    {
        if (!state.changed || player == null ||
            GameShipHeatPerSecondField == null)
        {
            return;
        }

        GameShipHeatPerSecondField.SetValue(
            player,
            state.originalHeatPerSecond
        );
    }

    private struct KnobAggregate
    {
        public float flat;
        public float percent;
        public float multiplier;
    }

    private static void AccumulateEffect(
        CoreSpecializationEffect effect,
        int rank,
        ref KnobAggregate aggregate)
    {
        if (effect == null || rank <= 0)
            return;

        float value = effect.GetAccumulatedValue(rank);
        switch (effect.Type)
        {
            case CoreSpecializationEffectType.Flat:
                aggregate.flat += value;
                break;
            case CoreSpecializationEffectType.Percent:
                aggregate.percent += value;
                break;
            case CoreSpecializationEffectType.Multiplier:
                aggregate.multiplier *= value;
                break;
        }
    }

    // Starfire resolves its own tree in one source-family context. Universal
    // effects and the active family's conditional effects contribute to the same
    // ordinary knob aggregate, so percentages remain additive before explicit
    // multipliers exactly like the generic specialization resolver.
    private static KnobAggregate AggregateStarfireKnob(
        Pilot pilot,
        StarfireSourceFamily family,
        CoreSpecializationKnob knob)
    {
        KnobAggregate aggregate = new KnobAggregate();
        aggregate.multiplier = 1f;

        if (pilot == null || knob == null)
            return aggregate;

        CoreSpecializationTree tree =
            CoreSpecializationRegistry.Get(LeviathanStarfireTree.TreeId);
        if (tree == null)
            return aggregate;

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            CoreSpecializationNode node = nodes[i];
            int rank = CoreSpecializationRuntime.GetEffectiveNodeRank(
                pilot,
                LeviathanStarfireTree.TreeId,
                node.Id
            );
            if (rank <= 0)
                continue;

            CoreSpecializationEffect[] effects = node.Effects;
            for (int effectIndex = 0; effectIndex < effects.Length; effectIndex++)
            {
                CoreSpecializationEffect effect = effects[effectIndex];
                if (effect != null && effect.Key == knob.Id)
                    AccumulateEffect(effect, rank, ref aggregate);
            }
        }

        FamilyKnobEffect[] familyEffects = LeviathanStarfireTree.FamilyEffects;
        for (int i = 0; i < familyEffects.Length; i++)
        {
            FamilyKnobEffect definition = familyEffects[i];
            if (definition == null ||
                definition.Family != family ||
                definition.Effect == null ||
                definition.Effect.Key != knob.Id)
            {
                continue;
            }

            int rank = CoreSpecializationRuntime.GetEffectiveNodeRank(
                pilot,
                LeviathanStarfireTree.TreeId,
                definition.NodeId
            );
            AccumulateEffect(definition.Effect, rank, ref aggregate);
        }

        return aggregate;
    }

    private static float ApplyKnob(
        Pilot pilot,
        StarfireSourceFamily family,
        CoreSpecializationKnob knob,
        float baseValue)
    {
        KnobAggregate aggregate = AggregateStarfireKnob(pilot, family, knob);
        return (baseValue + aggregate.flat) *
            (1f + aggregate.percent) *
            aggregate.multiplier;
    }

    private static float GetKnobMultiplier(
        Pilot pilot,
        StarfireSourceFamily family,
        CoreSpecializationKnob knob)
    {
        KnobAggregate aggregate = AggregateStarfireKnob(pilot, family, knob);
        return (1f + aggregate.percent) * aggregate.multiplier;
    }

    public static ResolvedStarfireState GetResolvedStarfireState()
    {
        GameShip player = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();
        Pilot pilot = player == null
            ? null
            : GameShip.GetPlayerSourcePilot(player);
        Activatable source = player == null ? null : FindSource(player);
        StarfireSourceFamily family = GetSourceFamily(source);
        int revision = CoreSpecializationRuntime.ConfigurationRevision;

        if (resolvedState != null &&
            resolvedStateRevision == revision &&
            ReferenceEquals(resolvedStatePilot, pilot) &&
            ReferenceEquals(resolvedStateSource, source) &&
            resolvedStateFamily == family)
        {
            return resolvedState;
        }

        ResolvedStarfireState state = new ResolvedStarfireState();
        state.SourceFamily = family;
        if (pilot == null)
            return state;

        state.HeatMultiplier = Mathf.Max(
            0f,
            LeviathanStarfireTuning.BaselineHeatMultiplier *
                GetKnobMultiplier(pilot, family, Knobs.HeatGeneration)
        );
        state.LengthMultiplier = Mathf.Max(
            0f,
            LeviathanStarfireTuning.BaselineLengthMultiplier *
                GetKnobMultiplier(pilot, family, Knobs.Length)
        );
        state.WidthMultiplier = Mathf.Max(
            0f,
            LeviathanStarfireTuning.BaselineWidthMultiplier *
                GetKnobMultiplier(pilot, family, Knobs.Width)
        );
        state.DamageMultiplier = Mathf.Max(
            0f,
            LeviathanStarfireTuning.BaselineDamageMultiplier *
                GetKnobMultiplier(pilot, family, Knobs.Damage)
        );
        state.StatusChanceBonus = ApplyKnob(
            pilot,
            family,
            Knobs.StatusChance,
            0f
        );
        state.ProjectileSizeMultiplier = Mathf.Max(
            0.01f,
            LeviathanStarfireTuning.BaselineProjectileSizeMultiplier *
                GetKnobMultiplier(pilot, family, Knobs.ProjectileSize)
        );
        state.FiringHalfAngleDegrees = Mathf.Max(
            0f,
            ApplyKnob(
                pilot,
                family,
                Knobs.FiringHalfAngleDegrees,
                LeviathanStarfireTuning.BaselineFiringHalfAngleDegrees
            )
        );
        state.StartupDelaySeconds = Mathf.Max(
            0f,
            ApplyKnob(
                pilot,
                family,
                Knobs.StartupDelaySeconds,
                LeviathanStarfireTuning.BaselineStartupDelaySeconds
            )
        );

        state.FullSizeHoldSeconds = Mathf.Max(
            0f,
            ApplyKnob(
                pilot,
                family,
                Knobs.FullSizeHoldSeconds,
                LeviathanStarfireTuning.BaselineFullSizeHoldSeconds
            )
        );
        state.RetreatSeconds = Mathf.Max(
            0f,
            ApplyKnob(
                pilot,
                family,
                Knobs.RetreatSeconds,
                LeviathanStarfireTuning.BaselineRetreatSeconds
            )
        );
        state.CapacitySeconds =
            state.FullSizeHoldSeconds + state.RetreatSeconds;
        state.ActiveDrainRate = Mathf.Max(
            0f,
            LeviathanStarfireTuning.BaselineActiveDrainRate *
                GetKnobMultiplier(pilot, family, Knobs.ActiveDrainRate)
        );
        state.RecoverySecondsPerSecond = Mathf.Max(
            0f,
            LeviathanStarfireTuning.BaselineRecoverySecondsPerSecond *
                GetKnobMultiplier(pilot, family, Knobs.RecoveryRate)
        );
        state.RecoveryDelaySeconds =
            LeviathanStarfireTuning.BaselineRecoveryDelaySeconds;
        state.RecoveryCurveExponent =
            LeviathanStarfireTuning.BaselineRecoveryCurveExponent;
        state.MinimumDamageFraction =
            LeviathanStarfireTuning.BaselineMinimumDamageFraction;
        state.MinimumLengthFraction =
            LeviathanStarfireTuning.BaselineMinimumLengthFraction;
        state.MinimumWidthFraction =
            LeviathanStarfireTuning.BaselineMinimumWidthFraction;
        state.MinimumVelocityFraction =
            LeviathanStarfireTuning.BaselineMinimumVelocityFraction;
        state.VelocityFalloffRate =
            LeviathanStarfireTuning.BaselineVelocityFalloffRate;
        state.DamageFalloffCurveExponent = Mathf.Max(
            0.01f,
            ApplyKnob(
                pilot,
                family,
                Knobs.DamageFalloffCurveExponent,
                LeviathanStarfireTuning.BaselineFalloffCurveExponent
            )
        );
        state.LengthFalloffCurveExponent = Mathf.Max(
            0.01f,
            ApplyKnob(
                pilot,
                family,
                Knobs.LengthFalloffCurveExponent,
                LeviathanStarfireTuning.BaselineFalloffCurveExponent
            )
        );
        state.WidthFalloffCurveExponent = Mathf.Max(
            0.01f,
            ApplyKnob(
                pilot,
                family,
                Knobs.WidthFalloffCurveExponent,
                LeviathanStarfireTuning.BaselineFalloffCurveExponent
            )
        );

        state.RechargePullEnabled = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.RechargePull
        );
        state.RechargePullRadius = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.RechargePullRadius, 0f)
        ) * WorldUnitsPerMeter;
        state.RechargePullStrength = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.RechargePullStrength, 0f)
        );
        state.RechargePullFalloffExponent = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.RechargePullFalloffExponent, 0f)
        );
        state.RechargePullMaxSpeed = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.RechargePullMaxSpeed, 0f)
        ) * WorldUnitsPerMeter;

        state.BlastWaveEnabled = CoreSpecializationRuntime.HasFlag(
            pilot,
            Flags.BlastWave
        );
        state.BlastWaveArcDegrees = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.BlastWaveArcDegrees, 0f)
        );
        state.BlastWaveDamageMultiplier = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.BlastWaveDamageMultiplier, 1f)
        );
        state.BlastWaveSpeed = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.BlastWaveSpeed, 0f)
        ) * WorldUnitsPerMeter;
        state.BlastWaveRange = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.BlastWaveRange, 0f)
        ) * WorldUnitsPerMeter;
        state.BlastWaveFrontThickness = Mathf.Max(
            0f,
            ApplyKnob(pilot, family, Knobs.BlastWaveWidth, 0f)
        ) * WorldUnitsPerMeter;
        state.BlastWaveVisualOpacity = Mathf.Clamp01(
            ApplyKnob(pilot, family, Knobs.BlastWaveVisualOpacity, 0f)
        );
        state.BlastWaveStartRadius = Mathf.Max(
            0.05f,
            ApplyKnob(pilot, family, Knobs.BlastWaveStartRadius, 0f) *
                WorldUnitsPerMeter
        );
        state.BlastWaveEndRadius = Mathf.Max(
            0.05f,
            ApplyKnob(pilot, family, Knobs.BlastWaveEndRadius, 0f) *
                WorldUnitsPerMeter
        );
        state.BlastWaveKnockback = 0f;

        resolvedStatePilot = pilot;
        resolvedStateSource = source;
        resolvedStateFamily = family;
        resolvedStateRevision =
            CoreSpecializationRuntime.ConfigurationRevision;
        resolvedState = state;
        return state;
    }

    private static void ApplyRechargePull(
        GameShip player,
        ResolvedStarfireState state)
    {
        if (player == null ||
            state == null ||
            state.RechargePullRadius <= 0.0001f ||
            state.RechargePullStrength <= 0f ||
            PhysicsController.instance == null)
        {
            return;
        }

        RechargePullTargets.Clear();
        Collider2D[] colliders = PhysicsController.instance.OverlapCircle(
            player.transform.position,
            state.RechargePullRadius
        );

        if (colliders == null)
            return;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider2D collider = colliders[i];
            if (!collider)
                continue;

            GameObject obj = collider.gameObject;
            if (obj.CompareTag("Shield") && collider.transform.parent != null)
                obj = collider.transform.parent.gameObject;

            if (!obj || obj == player.gameObject)
                continue;

            GameShip target;
            if (!GameShip.TryGetShip(obj, out target) ||
                !target ||
                target.IsDrone() ||
                !Faction.IsHostile(player.faction, target.faction) ||
                RechargePullTargets.Contains(target))
            {
                continue;
            }

            if (NetSession.InSession && target.IsNetRemote())
                continue;

            AIShip ai = AIController.instance == null
                ? null
                : AIController.instance.GetAIShip(target);
            if (ai is AttachedAIShip || ai is OrbitAIShip)
                continue;

            Rigidbody2D body = target.GetRigidBody();
            if (body == null)
                continue;

            RechargePullTargets.Add(target);

            Vector2 toPlayer =
                (Vector2)player.transform.position - body.position;
            float distance = toPlayer.magnitude;
            if (distance <= 0.0001f)
                continue;

            float normalizedDistance = Mathf.Clamp01(
                distance / state.RechargePullRadius
            );
            // A zero falloff exponent intentionally matches native Gladiator
            // Event Horizon: constant pull strength everywhere inside the radius.
            // Positive values opt into distance falloff for future tuning.
            float falloff = state.RechargePullFalloffExponent <= 0f
                ? 1f
                : Mathf.Pow(
                    1f - normalizedDistance,
                    state.RechargePullFalloffExponent
                );
            Vector2 direction = toPlayer / distance;

            float bossScale = target.GetShipClass() == Ship.Class.Boss
                ? 0.25f
                : 1f;
            float deltaSpeed =
                state.RechargePullStrength *
                Modifier.baseKnockback *
                Time.fixedDeltaTime *
                falloff *
                bossScale;

            if (state.RechargePullMaxSpeed > 0f)
            {
                float inwardSpeed = Vector2.Dot(body.velocity, direction);
                deltaSpeed = Mathf.Min(
                    deltaSpeed,
                    Mathf.Max(
                        0f,
                        state.RechargePullMaxSpeed - inwardSpeed
                    )
                );
            }

            if (deltaSpeed > 0f)
            {
                body.AddForce(
                    direction * deltaSpeed,
                    ForceMode2D.Impulse
                );
            }
        }
    }

    private static void FireBlastWave(
        Activatable source,
        GameShip player,
        StarfireSourceFamily family,
        ResolvedStarfireState state,
        BreathState breath)
    {
        if (source == null || player == null || state == null || breath == null)
            return;

        float damageSeconds = CalculateRemainingDamageSeconds(
            breath,
            state
        );
        if (damageSeconds <= 0.0001f)
            return;

        Vector2 origin = GetMuzzlePosition(source);
        Vector2 direction = GetSourceDirection(source);
        if (direction.sqrMagnitude <= 0.0001f)
            direction = player.transform.right;
        direction.Normalize();

        float maxRange = Mathf.Max(
            0.01f,
            state.BlastWaveRange * state.LengthMultiplier
        );
        float speed = Mathf.Max(0f, state.BlastWaveSpeed);

        BlastWaveState wave = new BlastWaveState();
        wave.sourceWeapon = source;
        wave.integratedDamageSeconds = Mathf.Max(
            0f,
            damageSeconds * state.BlastWaveDamageMultiplier
        );
        wave.owner = player;
        wave.origin = origin;
        wave.direction = direction;
        wave.startedTime = Time.time;
        wave.previousRadius = 0f;
        wave.maxRange = maxRange;
        wave.speed = speed;
        wave.radialThickness = Mathf.Max(
            0.05f,
            state.BlastWaveFrontThickness
        );
        wave.startRadius = Mathf.Max(
            0.05f,
            state.BlastWaveStartRadius
        );
        wave.endRadius = Mathf.Max(
            0.05f,
            state.BlastWaveEndRadius
        );
        wave.halfArcDegrees = Mathf.Clamp(
            state.BlastWaveArcDegrees * 0.5f,
            0f,
            180f
        );

        BlastWaveState previousWave;
        if (BlastWaves.TryGetValue(source, out previousWave))
            DestroyBlastWave(source, previousWave);

        BlastWaves[source] = wave;
        CreateBlastWaveVisual(wave, state);
    }

    private static void CreateBlastWaveVisual(
        BlastWaveState wave,
        ResolvedStarfireState state)
    {
        if (wave == null || state == null || state.BlastWaveVisualOpacity <= 0f)
            return;

        BlastWaveExplosionVisualSource source = ResolveBlastWaveExplosionSource();
        if (source == null || source.sprite == null)
        {
            CreateFallbackBlastWaveVisual(wave, state);
            return;
        }

        Shader fallbackShader = Shader.Find("Sprites/Default");
        if (source.material == null && fallbackShader == null)
        {
            CreateFallbackBlastWaveVisual(wave, state);
            return;
        }

        GameObject obj = new GameObject("Leviathan Starfire Blast Wave Explosion Crescent");
        obj.transform.position = wave.origin;
        float facingDegrees = Mathf.Atan2(wave.direction.y, wave.direction.x) *
            Mathf.Rad2Deg;
        obj.transform.rotation = Quaternion.Euler(0f, 0f, facingDegrees);
        obj.transform.localScale = Vector3.one;

        float visualRadius = Mathf.Max(
            0.05f,
            wave.startRadius
        );

        MeshFilter filter = obj.AddComponent<MeshFilter>();
        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        Mesh mesh = BuildExplosionCrescentMesh(
            wave,
            source.sprite,
            source.flipX,
            source.flipY,
            wave.halfArcDegrees,
            visualRadius,
            wave.radialThickness
        );

        if (mesh == null)
        {
            UnityEngine.Object.Destroy(obj);
            CreateFallbackBlastWaveVisual(wave, state);
            return;
        }

        Material material = source.material != null
            ? source.material
            : GetBlastWaveFallbackMaterial(fallbackShader);
        if (material == null)
        {
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(obj);
            CreateFallbackBlastWaveVisual(wave, state);
            return;
        }

        filter.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = source.sortingLayerID;
        renderer.sortingOrder = source.sortingOrder;

        // Hidden SpriteRenderer + Animator drive the explosion prefab's native
        // sprite animation. The cropped mesh simply follows whichever sprite frame
        // is current; none of the prefab's gameplay/sound/shake components run.
        GameObject animationObject = new GameObject(
            "Leviathan Starfire Blast Wave Explosion Animation"
        );
        animationObject.transform.SetParent(obj.transform, false);
        SpriteRenderer animationRenderer =
            animationObject.AddComponent<SpriteRenderer>();
        animationRenderer.sprite = source.sprite;
        animationRenderer.enabled = false;

        Animator animationAnimator = null;
        if (source.animatorController != null)
        {
            animationAnimator = animationObject.AddComponent<Animator>();
            animationAnimator.runtimeAnimatorController = source.animatorController;
            animationAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        SpriteRenderer sourceRenderer = GetSourceRenderer(wave.sourceWeapon);
        if (sourceRenderer != null)
        {
            renderer.sortingLayerID = sourceRenderer.sortingLayerID;
            renderer.sortingOrder = sourceRenderer.sortingOrder;
        }

        wave.visualObject = obj;
        wave.visualMesh = mesh;
        wave.visualRenderer = renderer;
        wave.visualAnimationRenderer = animationRenderer;
        wave.visualAnimationAnimator = animationAnimator;
        wave.visualLastSprite = source.sprite;
        wave.visualFlipX = source.flipX;
        wave.visualFlipY = source.flipY;
        wave.visualPropertyBlock = new MaterialPropertyBlock();

        Color tint = GetBlastWaveVisualColor(wave.sourceWeapon);
        tint.a = Mathf.Clamp01(state.BlastWaveVisualOpacity);
        ApplyBlastWaveVisualTint(
            renderer,
            wave.visualPropertyBlock,
            source.sprite,
            tint
        );

        UpdateBlastWaveVisual(wave, 0f, state);
    }

    private static Mesh BuildExplosionCrescentMesh(
        BlastWaveState wave,
        Sprite sprite,
        bool flipX,
        bool flipY,
        float halfArcDegrees,
        float visualRadius,
        float bandThickness)
    {
        if (wave == null || sprite == null)
            return null;

        const int arcSegments = 48;
        const int radialSegments = 8;

        int columns = arcSegments + 1;
        int rows = radialSegments + 1;
        wave.visualVertices = new Vector3[columns * rows];
        wave.visualUvs = new Vector2[wave.visualVertices.Length];

        int[] triangles = new int[arcSegments * radialSegments * 6];
        Color[] colors = new Color[wave.visualVertices.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;

        FillExplosionCrescentGeometry(
            wave.visualVertices,
            halfArcDegrees,
            visualRadius,
            bandThickness
        );

        int tri = 0;
        for (int radial = 0; radial < radialSegments; radial++)
        {
            for (int angular = 0; angular < arcSegments; angular++)
            {
                int a = radial * columns + angular;
                int b = a + 1;
                int c = (radial + 1) * columns + angular;
                int d = c + 1;

                triangles[tri++] = a;
                triangles[tri++] = c;
                triangles[tri++] = b;
                triangles[tri++] = b;
                triangles[tri++] = c;
                triangles[tri++] = d;
            }
        }

        float outerRadius = Mathf.Max(0.05f, visualRadius);
        FillExplosionCrescentUvs(
            wave.visualVertices,
            wave.visualUvs,
            sprite,
            flipX,
            flipY,
            outerRadius
        );

        Mesh mesh = new Mesh();
        mesh.name = "Starfire Explosion Crescent";
        mesh.MarkDynamic();
        mesh.vertices = wave.visualVertices;
        mesh.triangles = triangles;
        mesh.colors = colors;
        mesh.uv = wave.visualUvs;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void FillExplosionCrescentGeometry(
        Vector3[] vertices,
        float halfArcDegrees,
        float visualRadius,
        float bandThickness)
    {
        if (vertices == null)
            return;

        const int arcSegments = 48;
        const int radialSegments = 8;

        int columns = arcSegments + 1;
        int expectedVertices = columns * (radialSegments + 1);
        if (vertices.Length != expectedVertices)
            return;

        float outerRadius = Mathf.Max(0.05f, visualRadius);
        float thickness = Mathf.Clamp(
            bandThickness,
            0.01f,
            outerRadius * 0.95f
        );
        float innerRadius = Mathf.Max(0.01f, outerRadius - thickness);
        float clampedHalfArc = Mathf.Clamp(halfArcDegrees, 0.1f, 179f);

        for (int radial = 0; radial <= radialSegments; radial++)
        {
            float radialT = radial / (float)radialSegments;
            float radius = Mathf.Lerp(innerRadius, outerRadius, radialT);

            for (int angular = 0; angular <= arcSegments; angular++)
            {
                float angularT = angular / (float)arcSegments;
                float angle = Mathf.Lerp(
                    -clampedHalfArc,
                    clampedHalfArc,
                    angularT
                ) * Mathf.Deg2Rad;
                int index = radial * columns + angular;

                // Put the crescent's leading midpoint at local x=0. The circle
                // center sits behind it, producing a moon/arc that travels forward.
                vertices[index] = new Vector3(
                    -outerRadius + Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius,
                    0f
                );
            }
        }
    }

    private static void UpdateExplosionCrescentGeometry(
        BlastWaveState wave,
        float halfArcDegrees,
        float visualRadius,
        float bandThickness)
    {
        if (wave == null ||
            wave.visualMesh == null ||
            wave.visualVertices == null)
        {
            return;
        }

        FillExplosionCrescentGeometry(
            wave.visualVertices,
            halfArcDegrees,
            visualRadius,
            bandThickness
        );
        wave.visualMesh.vertices = wave.visualVertices;
        wave.visualMesh.RecalculateBounds();
    }

    private static float GetBlastWaveCurrentSizeRadius(
        BlastWaveState wave,
        float travelDistance)
    {
        if (wave == null)
            return 0.05f;

        float travel01 = wave.maxRange <= 0.0001f
            ? 1f
            : Mathf.Clamp01(travelDistance / wave.maxRange);

        return Mathf.Max(
            0.05f,
            Mathf.Lerp(wave.startRadius, wave.endRadius, travel01)
        );
    }

    private static void FillExplosionCrescentUvs(
        Vector3[] vertices,
        Vector2[] uv,
        Sprite sprite,
        bool flipX,
        bool flipY,
        float outerRadius)
    {
        if (vertices == null ||
            uv == null ||
            uv.Length != vertices.Length ||
            sprite == null ||
            outerRadius <= 0.0001f)
        {
            return;
        }

        Vector4 outerUv = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 vertex = vertices[i];
            float normalizedX = 0.5f +
                ((vertex.x + outerRadius) / outerRadius) * 0.5f;
            float normalizedY = 0.5f +
                (vertex.y / outerRadius) * 0.5f;

            normalizedX = Mathf.Clamp01(normalizedX);
            normalizedY = Mathf.Clamp01(normalizedY);

            if (flipX)
                normalizedX = 1f - normalizedX;
            if (flipY)
                normalizedY = 1f - normalizedY;

            switch (sprite.packingRotation)
            {
                case SpritePackingRotation.FlipHorizontal:
                    normalizedX = 1f - normalizedX;
                    break;
                case SpritePackingRotation.FlipVertical:
                    normalizedY = 1f - normalizedY;
                    break;
                case SpritePackingRotation.Rotate180:
                    normalizedX = 1f - normalizedX;
                    normalizedY = 1f - normalizedY;
                    break;
            }

            uv[i] = new Vector2(
                Mathf.Lerp(outerUv.x, outerUv.z, normalizedX),
                Mathf.Lerp(outerUv.y, outerUv.w, normalizedY)
            );
        }
    }

    private static void UpdateExplosionCrescentUvs(
        BlastWaveState wave,
        Sprite sprite,
        float outerRadius)
    {
        if (wave == null ||
            wave.visualMesh == null ||
            wave.visualVertices == null ||
            wave.visualUvs == null)
        {
            return;
        }

        FillExplosionCrescentUvs(
            wave.visualVertices,
            wave.visualUvs,
            sprite,
            wave.visualFlipX,
            wave.visualFlipY,
            outerRadius
        );
        wave.visualMesh.uv = wave.visualUvs;
    }

    private static BlastWaveExplosionVisualSource ResolveBlastWaveExplosionSource()
    {
        if (blastWaveExplosionVisualSource != null)
            return blastWaveExplosionVisualSource;

        GameObject prefab =
            LeviathanStellarConverter.GetCommonExplosiveAreaVisualPrefab();
        if (prefab == null)
            return null;

        SpriteRenderer renderer = prefab.GetComponent<SpriteRenderer>();
        if (renderer == null)
            renderer = prefab.GetComponentInChildren<SpriteRenderer>(true);
        if (renderer == null || renderer.sprite == null)
            return null;

        Animator animator = prefab.GetComponent<Animator>();
        if (animator == null)
            animator = prefab.GetComponentInChildren<Animator>(true);

        BlastWaveExplosionVisualSource source =
            new BlastWaveExplosionVisualSource();
        source.sprite = renderer.sprite;
        source.material = renderer.sharedMaterial;
        source.animatorController = animator == null
            ? null
            : animator.runtimeAnimatorController;
        source.flipX = renderer.flipX;
        source.flipY = renderer.flipY;
        source.sortingLayerID = renderer.sortingLayerID;
        source.sortingOrder = renderer.sortingOrder;

        blastWaveExplosionVisualSource = source;
        return source;
    }

    private static Color GetBlastWaveVisualColor(Activatable sourceWeapon)
    {
        SpriteRenderer renderer = GetSourceRenderer(sourceWeapon);
        if (renderer != null)
        {
            Color color = renderer.color;
            color.a = 1f;
            return color;
        }

        Color fallback = sourceWeapon == null
            ? Color.white
            : GetTorchDamageTypeColor(GetSourceDamageType(sourceWeapon));
        fallback.a = 1f;
        return fallback;
    }

    private static void ApplyBlastWaveVisualTint(
        MeshRenderer renderer,
        MaterialPropertyBlock block,
        Sprite sprite,
        Color tint)
    {
        if (renderer == null || block == null || sprite == null)
            return;

        renderer.GetPropertyBlock(block);
        block.SetTexture(MainTexShaderId, sprite.texture);
        block.SetColor(ColorShaderId, tint);
        block.SetColor(RendererColorShaderId, tint);
        renderer.SetPropertyBlock(block);
    }

    private static Material GetBlastWaveFallbackMaterial(Shader shader)
    {
        if (blastWaveFallbackMaterial != null)
            return blastWaveFallbackMaterial;

        if (shader == null)
            return null;

        blastWaveFallbackMaterial = new Material(shader);
        return blastWaveFallbackMaterial;
    }

    // Mirrors Torch.EditorGetDamageColor so Blast Wave always matches the source
    // Torch element even though the shared explosion art is element-neutral.
    private static Color GetTorchDamageTypeColor(Damageable.DamageType damageType)
    {
        switch (damageType)
        {
            case Damageable.DamageType.Cold:
                return new Color(0.4f, 0.98f, 0.98f, 1f);
            case Damageable.DamageType.Corrosive:
                return new Color(0.7f, 0.4f, 0.99f, 1f);
            case Damageable.DamageType.Electric:
                return new Color(0.99f, 0.92f, 0.4f, 1f);
            case Damageable.DamageType.Thermal:
                return new Color(0.99f, 0.4f, 0.43f, 1f);
            case Damageable.DamageType.Radiation:
                return new Color(0.4f, 0.99f, 0.43f, 1f);
            default:
                return Color.white;
        }
    }

    private static void CreateFallbackBlastWaveVisual(
        BlastWaveState wave,
        ResolvedStarfireState state)
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            return;

        GameObject obj = new GameObject("Leviathan Starfire Blast Wave Fallback");
        LineRenderer line = obj.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;
        line.positionCount = 25;
        line.widthMultiplier = wave.radialThickness;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        Material visualMaterial = GetBlastWaveFallbackMaterial(shader);
        if (visualMaterial == null)
        {
            UnityEngine.Object.Destroy(obj);
            return;
        }
        line.sharedMaterial = visualMaterial;

        Color color = GetBlastWaveVisualColor(wave.sourceWeapon);
        SpriteRenderer sourceRenderer = GetSourceRenderer(wave.sourceWeapon);
        if (sourceRenderer != null)
        {
            line.sortingLayerID = sourceRenderer.sortingLayerID;
            line.sortingOrder = sourceRenderer.sortingOrder;
        }

        color.a *= state.BlastWaveVisualOpacity;
        line.startColor = color;
        line.endColor = color;

        wave.visualObject = obj;
        wave.fallbackLineRenderer = line;
        UpdateBlastWaveVisual(wave, 0f, state);
    }

    private static void UpdateBlastWaveVisual(
        BlastWaveState wave,
        float travelDistance,
        ResolvedStarfireState state)
    {
        if (wave == null || state == null)
            return;

        float currentSizeRadius = GetBlastWaveCurrentSizeRadius(
            wave,
            travelDistance
        );
        Vector2 anchor =
            wave.origin + wave.direction * travelDistance;

        if (wave.visualObject != null &&
            wave.visualMesh != null &&
            wave.visualRenderer != null)
        {
            wave.visualObject.transform.position = anchor;
            wave.visualObject.transform.localScale = Vector3.one;

            UpdateExplosionCrescentGeometry(
                wave,
                wave.halfArcDegrees,
                currentSizeRadius,
                wave.radialThickness
            );

            if (wave.visualAnimationAnimator != null)
            {
                AnimatorStateInfo info =
                    wave.visualAnimationAnimator.GetCurrentAnimatorStateInfo(0);
                if (info.normalizedTime >= 1f && info.fullPathHash != 0)
                {
                    wave.visualAnimationAnimator.Play(
                        info.fullPathHash,
                        0,
                        info.normalizedTime - Mathf.Floor(info.normalizedTime)
                    );
                }
            }

            Sprite currentSprite = wave.visualAnimationRenderer == null
                ? wave.visualLastSprite
                : wave.visualAnimationRenderer.sprite;
            if (currentSprite != null)
            {
                bool spriteChanged = currentSprite != wave.visualLastSprite;
                if (spriteChanged)
                    wave.visualLastSprite = currentSprite;

                UpdateExplosionCrescentUvs(
                    wave,
                    currentSprite,
                    currentSizeRadius
                );

                Color tint = GetBlastWaveVisualColor(wave.sourceWeapon);
                tint.a = state.BlastWaveVisualOpacity;
                ApplyBlastWaveVisualTint(
                    wave.visualRenderer,
                    wave.visualPropertyBlock,
                    currentSprite,
                    tint
                );
            }

            return;
        }

        if (wave.fallbackLineRenderer == null)
            return;

        int count = Mathf.Max(2, wave.fallbackLineRenderer.positionCount);
        float baseAngle = Mathf.Atan2(wave.direction.y, wave.direction.x) *
            Mathf.Rad2Deg;
        Vector2 circleCenter =
            anchor - wave.direction * currentSizeRadius;

        wave.fallbackLineRenderer.widthMultiplier = wave.radialThickness;

        for (int i = 0; i < count; i++)
        {
            float t = count <= 1 ? 0.5f : (float)i / (count - 1);
            float angle = baseAngle + Mathf.Lerp(
                -wave.halfArcDegrees,
                wave.halfArcDegrees,
                t
            );
            Vector3 radial3 = Quaternion.Euler(0f, 0f, angle) * Vector3.right;
            Vector2 radial = new Vector2(radial3.x, radial3.y);
            wave.fallbackLineRenderer.SetPosition(
                i,
                circleCenter + radial * currentSizeRadius
            );
        }
    }

    private static void CleanupBlastWaveVisual(BlastWaveState wave)
    {
        if (wave == null)
            return;

        if (wave.visualObject)
            UnityEngine.Object.Destroy(wave.visualObject);

        if (wave.visualMesh)
            UnityEngine.Object.Destroy(wave.visualMesh);

        wave.visualObject = null;
        wave.visualMesh = null;
        wave.visualRenderer = null;
        wave.visualAnimationRenderer = null;
        wave.visualAnimationAnimator = null;
        wave.visualLastSprite = null;
        wave.visualVertices = null;
        wave.visualUvs = null;
        wave.visualPropertyBlock = null;
        wave.fallbackLineRenderer = null;
    }

    private static void DestroyBlastWave(
        Activatable source,
        BlastWaveState wave)
    {
        CleanupBlastWaveVisual(wave);

        if (source != null)
            BlastWaves.Remove(source);
    }

    private static void UpdateBlastWave(
        Activatable source,
        ResolvedStarfireState state)
    {
        BlastWaveState wave;
        if (source == null ||
            !BlastWaves.TryGetValue(source, out wave) ||
            wave == null)
        {
            return;
        }

        if (!wave.owner || wave.sourceWeapon == null)
        {
            DestroyBlastWave(source, wave);
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - wave.startedTime);
        float currentTravel = wave.speed <= 0.0001f
            ? wave.maxRange
            : Mathf.Min(wave.maxRange, elapsed * wave.speed);

        UpdateBlastWaveVisual(wave, currentTravel, state);

        float currentSizeRadius = GetBlastWaveCurrentSizeRadius(
            wave,
            currentTravel
        );
        Vector2 anchor =
            wave.origin + wave.direction * currentTravel;
        Vector2 circleCenter =
            anchor - wave.direction * currentSizeRadius;

        // The authored front thickness stays in meters while the crescent radius
        // grows from Start Radius to End Radius. Add the distance travelled since
        // the previous FixedUpdate only to the inner edge as anti-tunnelling sweep.
        float travelStep = Mathf.Max(
            0f,
            currentTravel - wave.previousRadius
        );
        float outerRadius = currentSizeRadius;
        float innerRadius = Mathf.Max(
            0f,
            currentSizeRadius -
                wave.radialThickness -
                travelStep
        );

        Collider2D[] colliders = PhysicsController.instance == null
            ? null
            : PhysicsController.instance.OverlapCircle(
                circleCenter,
                outerRadius
            );

        if (colliders != null)
        {
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider2D collider = colliders[i];
                if (!collider)
                    continue;

                GameObject obj = collider.gameObject;
                if (obj.CompareTag("Shield") && collider.transform.parent != null)
                    obj = collider.transform.parent.gameObject;

                if (!obj || obj == wave.owner.gameObject)
                    continue;

                GameShip target;
                if (!GameShip.TryGetShip(obj, out target) ||
                    !target ||
                    wave.hitShips.Contains(target) ||
                    !Faction.IsHostile(wave.owner.faction, target.faction) ||
                    !target.CanBeDamagedBy(wave.owner, false))
                {
                    continue;
                }

                Vector2 hitPoint = collider.ClosestPoint(circleCenter);
                Vector2 offset = hitPoint - circleCenter;
                float distance = offset.magnitude;
                if (distance < innerRadius || distance > outerRadius)
                    continue;

                if (distance > 0.0001f &&
                    Vector2.Angle(wave.direction, offset) > wave.halfArcDegrees)
                {
                    continue;
                }

                // The travelling front only gets one opportunity per target.
                wave.hitShips.Add(target);
                if (!target.IsDodging())
                {
                    ApplyBlastWaveDamage(
                        wave,
                        target,
                        hitPoint,
                        state
                    );
                }
            }
        }

        wave.previousRadius = currentTravel;

        if (currentTravel >= wave.maxRange - 0.0001f)
            DestroyBlastWave(source, wave);
    }

    private static void ApplyBlastWaveDamage(
        BlastWaveState wave,
        GameShip target,
        Vector2 hitPosition,
        ResolvedStarfireState state)
    {
        if (wave == null ||
            wave.sourceWeapon == null ||
            !wave.owner ||
            !target ||
            state == null ||
            wave.integratedDamageSeconds <= 0f)
        {
            return;
        }

        Activatable source = wave.sourceWeapon;
        StarfireSourceFamily family = GetSourceFamily(source);
        if (family == StarfireSourceFamily.None)
            return;

        float normalizedBaseDps = GetNormalizedActiveNonCritDps(
            source,
            family
        );
        float nonCritDamage =
            normalizedBaseDps *
            state.DamageMultiplier *
            wave.integratedDamageSeconds;
        if (nonCritDamage <= 0f)
            return;

        float critChance = GetNormalizedCritChance(source);
        float critModifier = GetNormalizedCritModifier(source);
        float statusChance = GetNormalizedStatusChance(source, family, state);
        float expectedDps =
            normalizedBaseDps *
            state.DamageMultiplier *
            (1f + critChance * critModifier);

        bool crit = Modifier.CritRoll(critChance, target);
        DamageData[] damage = BuildNormalizedDamagePacket(
            source,
            crit,
            nonCritDamage,
            expectedDps,
            critModifier
        );
        if (damage == null)
            return;

        bool bypassDamageLimit = source.HasCustomizer(
            Customizer.Type.BypassDamageLimit
        );
        target.SetLastDamageDirection(wave.direction);
        target.lastDamagedByWeaponName = source.GetName(false, false);
        target.lastDamagedByShipName = wave.owner.GetName();
        target.lastDamagedByFaction = wave.owner.faction;

        if (RouteDamageMethod == null)
        {
            Debug.LogError(
                "[Leviathan] Starfire Blast Wave could not resolve " +
                "NetCombat.RouteDamage."
            );
            return;
        }

        bool destroyed = RouteNormalizedDamage(
            target,
            GetSourceDamageType(source),
            damage,
            statusChance,
            crit,
            hitPosition,
            wave.owner,
            bypassDamageLimit,
            state.BlastWaveKnockback,
            source,
            0f
        );

        RelayNormalizedConduit(
            source,
            wave.owner,
            target,
            damage,
            hitPosition,
            bypassDamageLimit
        );

        ApplyTorchLeechIfApplicable(source, target, hitPosition);

        if (state.BlastWaveKnockback > 0f)
        {
            Modifier.Knockback(
                state.BlastWaveKnockback,
                target.GetRigidBody(),
                wave.origin,
                wave.owner
            );
        }

        if (!target.IsDrone() && destroyed)
        {
            float shedChance = source.ApplyModifierToPercentage(
                Modifier.Type.OnEnemyDeathShed,
                0f,
                true
            );
            if (shedChance > 0f)
                target.ShedStatusEffects(shedChance, true, wave.owner);

            Projectile.ProcessDeathSurge(
                source,
                wave.owner,
                target.transform.position,
                expectedDps
            );
        }
    }


}

[HarmonyPatch(typeof(ActivatableIcon), "Init")]
public static class LeviathanStarfireActivatableIconInitPatch
{
    public static void Prefix(
        Activatable activatable,
        out LeviathanStarfireRuntime.BreathIconInitState __state)
    {
        LeviathanStarfireRuntime.PrepareBreathIcon(
            activatable,
            out __state
        );
    }

    public static void Postfix(
        Activatable activatable,
        LeviathanStarfireRuntime.BreathIconInitState __state)
    {
        LeviathanStarfireRuntime.RestoreBreathIcon(
            activatable,
            __state
        );
    }
}

[HarmonyPatch(typeof(ActivatableIcon), "UpdateCooldownAndDuration")]
public static class LeviathanStarfireActivatableIconBreathPatch
{
    public static bool Prefix(
        ActivatableIcon __instance,
        Activatable ___activatable,
        Slider ___cooldownSlider,
        ref float ___cachedCooldownSlider,
        ref bool ___cachedCountdownActive)
    {
        float overlay;
        if (!LeviathanStarfireRuntime.TryGetBreathIconOverlay(
            ___activatable,
            out overlay))
        {
            return true;
        }

        if (__instance.countdown != null &&
            __instance.countdown.activeSelf)
        {
            __instance.countdown.SetActive(false);
        }
        ___cachedCountdownActive = false;

        if (___cooldownSlider != null &&
            Mathf.Abs(overlay - ___cachedCooldownSlider) >= 0.005f)
        {
            ___cachedCooldownSlider = overlay;
            ___cooldownSlider.value = overlay;
        }

        return false;
    }
}

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class LeviathanStarfireSceneCleanupBootstrapPatch
{
    public static void Postfix()
    {
        LeviathanStarfireRuntime.EnsureSceneCleanupHook();
    }
}

[HarmonyPatch(typeof(Torch), "FixedUpdate")]
public static class LeviathanStarfireTorchFixedUpdatePatch
{
    public static void Prefix(Torch __instance)
    {
        LeviathanStarfireRuntime.SuppressNonSourceActivation(__instance);
    }

    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchSuppression(__instance);
        LeviathanStarfireRuntime.UpdateSource(__instance);
    }
}

[HarmonyPatch(typeof(Launcher), "FixedUpdate")]
public static class LeviathanStarfireLauncherFixedUpdatePatch
{
    public static void Prefix(Launcher __instance)
    {
        LeviathanStarfireRuntime.SuppressNonSourceActivation(__instance);
    }

    public static void Postfix(Launcher __instance)
    {
        LeviathanStarfireRuntime.UpdateSource(__instance);
    }
}

[HarmonyPatch(typeof(Torch), "BuildSpikes")]
public static class LeviathanStarfireBuildSpikesPatch
{
    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchSuppression(__instance);
    }
}

[HarmonyPatch(typeof(Torch), "UpdateSpikeScale")]
public static class LeviathanStarfireUpdateSpikeScalePatch
{
    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchSuppression(__instance);
    }
}

[HarmonyPatch(typeof(Torch), "DoSpikeDamage")]
public static class LeviathanStarfireDoSpikeDamagePatch
{
    public static bool Prefix(Torch __instance)
    {
        return !LeviathanStarfireRuntime.ShouldSuppressTorchDamage(__instance);
    }
}

[HarmonyPatch]
public static class LeviathanStarfireLauncherShootProjectilePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Launcher),
            "ShootProjectile",
            new Type[]
            {
                typeof(SpriteRenderer),
                typeof(GameObject),
                typeof(ParticleSystem),
                typeof(ParticleSettingControl),
                typeof(bool)
            }
        );
    }

    public static bool Prefix(Launcher __instance)
    {
        return LeviathanStarfireRuntime.ShouldRunNativeLauncherShot(__instance);
    }

    public static void Postfix(
        Launcher __instance,
        GameObject __1,
        List<Projectile> __result)
    {
        LeviathanStarfireRuntime.FinalizeNativeLauncherShot(
            __instance,
            __1,
            __result
        );
    }
}

[HarmonyPatch(typeof(Projectile), "HitObject")]
public static class LeviathanStarfireProjectileHitPatch
{
    public static bool Prefix(
        Projectile __instance,
        GameObject hitObject,
        Vector2 hitPoint,
        ref bool __result)
    {
        bool result;
        if (!LeviathanStarfireRuntime.TryHandleProjectileHit(
            __instance,
            hitObject,
            hitPoint,
            out result))
        {
            return true;
        }

        __result = result;
        return false;
    }
}

[HarmonyPatch(typeof(Projectile), "PoolDestroy")]
public static class LeviathanStarfireProjectilePoolDestroyPatch
{
    public static void Prefix(Projectile __instance)
    {
        LeviathanStarfireRuntime.ForgetStarfireProjectile(__instance);
    }
}

// Torch-sourced Starfire borrows a matching thrower projectile only as its
// baseline delivery vehicle. Real Thrower sources retain their native Fuzzy
// subclass side effects; borrowed Torch projectiles do not gain them.
[HarmonyPatch]
public static class LeviathanStarfireBurningSpacePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(FuzzyProjectile), "RollBurningSpace");
    }

    public static bool Prefix(FuzzyProjectile __instance)
    {
        return !LeviathanStarfireRuntime.ShouldSuppressBorrowedProjectileSideEffects(
            __instance
        );
    }
}

[HarmonyPatch(typeof(GameShip), "UpdateHeat")]
public static class LeviathanStarfireGameShipUpdateHeatPatch
{
    public static void Prefix(
        GameShip __instance,
        out LeviathanStarfireRuntime.HeatRateState __state)
    {
        __state = LeviathanStarfireRuntime.PrepareShipHeatRate(__instance);
    }

    public static void Postfix(
        GameShip __instance,
        LeviathanStarfireRuntime.HeatRateState __state)
    {
        LeviathanStarfireRuntime.RestoreShipHeatRate(__instance, __state);
    }
}
