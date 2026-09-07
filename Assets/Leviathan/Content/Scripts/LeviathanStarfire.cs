using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using static StarVortex.Damageable;

/// <summary>
/// Starfire transforms one equipped Torch Primary weapon into a broad Leviathan
/// breath attack. The first equipped Primary Torch in native slot order is the source;
/// all other equipped Primary Torches are suppressed while Starfire is active.
///
/// The source Torch remains the source of truth for native damage packet
/// construction, damage type, legendary/customizer behavior, native
/// charge/tick timing and attribution. Starfire reshapes the native spike and
/// exposes neutral tuning hooks for damage, crits, debuffs and future charge
/// profiles while scaling heat/geometry by rank.
/// </summary>
public static class LeviathanStarfireRuntime
{
    // Star Vortex displays Unity world distance at 20 meters per world unit.
    // User-facing meter/meter-per-second knobs are converted once when the
    // resolved Starfire state is built.
    private const float WorldUnitsPerMeter = 1f / 20f;

    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5. These are the main Starfire balance knobs.

    // Multiplier on native Torch heat generation from the one Starfire source.
    // Rank 1 starts at double heat. Rank 5 is 50% lower than that starting
    // burden and returns to the source Torch's native heat generation.
    private static readonly float[] HeatMultiplierByRank =
    {
        1.50f, // Rank 1
        1.45f, // Rank 2
        1.40f, // Rank 3
        1.35f, // Rank 4
        1.30f  // Rank 5
    };

    // Multiplier on the selected Torch's native MaxRange result. This is applied at
    // the same Equippable.ApplyModifier(MaxRange) boundary used by Striker, so
    // Torch's own charge, VFX length and collider length all inherit it natively.
    private static readonly float[] LengthMultiplierByRank =
    {
        1.15f, // Rank 1
        1.25f, // Rank 2
        1.3f, // Rank 3
        1.4f, // Rank 4
        1.5f  // Rank 5
    };

    // Multiplier on Starfire's base fan angle. The native Torch body stays at
    // Y scale 1; width is now encoded in one trapezoid collider plus cosmetic
    // beam strips. With BaseFanHalfAngleDegrees = 10, these values produce
    // half-angles of 13 / 15 / 17 / 18 / 20 degrees by rank.
    private static readonly float[] WidthMultiplierByRank =
    {
        1.30f, // Rank 1
        1.50f, // Rank 2
        1.70f, // Rank 3
        1.80f, // Rank 4
        2.00f  // Rank 5
    };

    // Cosmetic fan only. All strips share one generated mesh per native Torch
    // SpriteRenderer layer and never receive colliders or damage logic.
    private const int BaseVisualBeamCount = 16;

    // Rank spread = this angle * WidthMultiplierByRank. This is the main cone
    // angle knob. Example: 10 degrees * Rank 5's 2.0 = 20 degree half-angle.
    private const float BaseFanHalfAngleDegrees = 15.0f;

    // Width at the muzzle as a fraction of the native Torch half-width. The
    // far end then expands according to the fan angle and current beam length.
    private const float BaseMuzzleHalfWidthMultiplier = 0.65f;

    // 1.0 means neighboring cosmetic strips just touch at their center spacing.
    // Below 1 leaves visible seams; above 1 deliberately overlaps neighboring
    // strips. 1.35 is intentionally dense so ten beams read as one plume.
    private const float BaseVisualBeamFillFraction = 1.6f;

    // Never let an individual strip become thinner than this fraction of the
    // native Torch half-width. 1.00 keeps each cosmetic strip approximately
    // native-beam thickness instead of squeezing ten skinny ribbons into the fan.
    private const float BaseVisualMinimumHalfWidthMultiplier = 1.20f;

    // Slightly pad the single mechanical trapezoid beyond the visible fan to be
    // forgiving of motion/netcode without adding extra overlap queries.
    private const float BaseHitboxWidthPaddingMultiplier = 1.05f;
    private const float BaseHitboxLengthPaddingMultiplier = 1.00f;

    // Global opacity of the cosmetic Starfire plume. 1.00 is fully opaque;
    // 0.50 is half opacity. This affects visuals only, never hit detection.
    private const float BaseVisualOpacity = 0.70f;

    // Terminal shaping shared by visuals and the single mechanical collider.
    // The outermost beam pair keeps the base breath length while progressively
    // more central pairs extend farther. With 10 beams, pair weights are
    // 0 / 25 / 50 / 75 / 100 percent of this bonus.
    private const float BaseVisualCenterLengthBonusFraction = 0.10f;

    // Starfire AoE timing is intentionally independent of native Torch charge.
    // Arrays are Rank 1 -> Rank 5 so each rank can tune the sustained full-size
    // phase without changing the geometry/damage implementation.
    private static readonly float[] FullSizeHoldSecondsByRank =
    {
        1.50f, // Rank 1
        2.00f, // Rank 2
        2.00f, // Rank 3
        2.00f, // Rank 4
        2.00f  // Rank 5
    };

    // Time spent shrinking from full range to the minimum breath-length fraction.
    private static readonly float[] BreathRetreatSecondsByRank =
    {
        2.00f, // Rank 1
        2.00f, // Rank 2
        2.00f, // Rank 3
        2.00f, // Rank 4
        2.00f  // Rank 5
    };

    // Remaining longitudinal breath length after the retreat completes. Baseline
    // reaches zero at an empty reservoir; talents may raise the floor through knobs.
    private const float BaseBreathMinimumLengthFraction = 0.00f;

    // Damage, length and angular width fall with the same persistent breath reserve.
    // Baseline reaches zero for all three at exhaustion, while each component keeps
    // its own floor and curve knob so talents can reshape the profile independently.
    private const float BaseBreathMinimumDamageFraction = 0.00f;
    private const float BaseBreathMinimumWidthFraction = 0.00f;

    // Baseline Starfire restores half a breath-second per real idle second. A
    // 3.5-second empty reservoir therefore takes roughly seven seconds to refill.
    private const float BaseBreathRecoverySecondsPerSecond = 0.50f;
    private const float BaseBreathRecoveryDelaySeconds = 0.00f;
    private const float BaseBreathRecoveryCurveExponent = 1.00f;
    private const float BaseBreathActiveDrainRate = 1.00f;

    // Shapes retreat progress after the full-size hold. 1 = linear; values above
    // 1 keep the breath large longer, then make it collapse faster near the end.
    private static readonly float[] BreathRetreatCurveExponentByRank =
    {
        2.00f, // Rank 1
        2.00f, // Rank 2
        2.00f, // Rank 3
        2.00f, // Rank 4
        2.00f  // Rank 5
    };

    // Only the terminal edge is alpha-feathered. The rest of the breath remains
    // at the configured visual opacity for the full firing cycle. 0.10 softens the final 10% of
    // each cosmetic ribbon without narrowing it.
    private const float BaseVisualEndFeatherFraction = 0.10f;

    // The ribbons are linear, so 16 longitudinal sections are enough for the
    // endpoint alpha feather while cutting dynamic vertex work roughly in half.
    private const int BaseVisualLengthSegments = 16;

    // Cosmetic-only startup grow. Mechanics immediately use the resolved breath
    // size; the plume rapidly extends to that size instead of popping in fully formed.
    private const float BaseVisualStartupExtendSeconds = 0.25f;

    // Direct multiplier on the source Torch's native DamageData[] packet.
    // Neutral for now; future Starfire branches can change this independently.
    private static readonly float[] DamageMultiplierByRank =
    {
        0.80f, // Rank 1
        1.20f, // Rank 2
        1.30f, // Rank 3
        1.35f, // Rank 4
        1.40f  // Rank 5
    };

    // Additive bonus to the equipped Torch's native crit chance. Values are
    // decimal percentage points: 0.05 means native 10% -> Starfire 15%.
    private static readonly float[] CritChanceBonusByRank =
    {
        0.00f, // Rank 1
        0.00f, // Rank 2
        0.00f, // Rank 3
        0.00f, // Rank 4
        0.00f  // Rank 5
    };

    // Additive bonus to the BONUS portion of native crit damage. 0.05 means a
    // native +50% crit modifier becomes +55%, not 1.05x the final hit.
    private static readonly float[] CritDamageBonusByRank =
    {
        0.00f, // Rank 1
        0.00f, // Rank 2
        0.00f, // Rank 3
        0.00f, // Rank 4
        0.00f  // Rank 5
    };

    // Additive bonus to native Torch status/debuff proc chance. Damage type and
    // the actual status applied remain entirely inherited from the source Torch.
    private static readonly float[] StatusChanceBonusByRank =
    {
        0.00f, // Rank 1
        0.00f, // Rank 2
        0.00f, // Rank 3
        0.00f, // Rank 4
        0.00f  // Rank 5
    };

    // Future Deep Breath / charge-profile hooks. These are intentionally neutral
    // today. StartupDelaySeconds is an explicit wind-up before Starfire can deal
    // damage; native charge/VFX can still build during that telegraph.
    private static readonly float[] StartupDelaySecondsByRank =
    {
        0.00f, // Rank 1
        0.00f, // Rank 2
        0.00f, // Rank 3
        0.00f, // Rank 4
        0.00f  // Rank 5
    };

    // Multiplier on native Torch charge speed. 2.00 = half native chargeTime;
    // 0.50 = double native chargeTime. Native charge still affects Torch damage and
    // weapon behavior, but Starfire AoE geometry now uses its separate breath timer.
    private static readonly float[] ChargeRampSpeedMultiplierByRank =
    {
        0.90f, // Rank 1
        0.80f, // Rank 2
        0.70f, // Rank 3
        0.60f, // Rank 4
        0.30f  // Rank 5
    };


    // =========================================================================
    // SPECIALIZATION INTERFACE
    // =========================================================================
    // Tree definitions point at these named knobs/flags. Starfire owns what the
    // values mean and where they are applied. Ordinary tree edits should never
    // require new Harmony/UI/framework code.

    public static class Knobs
    {
        // -------------------------------------------------------------------------
        // Core gameplay
        // -------------------------------------------------------------------------

        public static readonly LeviathanSpecializationKnob HeatGeneration =
            LeviathanSpecializationKnob.Percent(
                "starfire.heat_generation",
                "Heat Generation"
            );

        public static readonly LeviathanSpecializationKnob Length =
            LeviathanSpecializationKnob.Percent(
                "starfire.length",
                "Length"
            );

        public static readonly LeviathanSpecializationKnob Width =
            LeviathanSpecializationKnob.Percent(
                "starfire.width",
                "Width"
            );

        public static readonly LeviathanSpecializationKnob Damage =
            LeviathanSpecializationKnob.Percent(
                "starfire.damage",
                "Damage"
            );

        // Percentage-points are additive. +5 on a 10% source weapon becomes 15%.
        public static readonly LeviathanSpecializationKnob CritChance =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.crit_chance",
                "Critical Chance"
            );

        // Native Torch crit modifier is the bonus portion only. +5 means +5
        // percentage points of crit damage, e.g. +50% -> +55%.
        public static readonly LeviathanSpecializationKnob CritDamage =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.crit_damage",
                "Critical Damage"
            );

        public static readonly LeviathanSpecializationKnob StatusChance =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.status_chance",
                "Status Chance"
            );

        public static readonly LeviathanSpecializationKnob DebuffChance =
            StatusChance;

        // -------------------------------------------------------------------------
        // Persistent breath reservoir
        // -------------------------------------------------------------------------

        // Common multiplier on both full-power and falloff capacity.
        public static readonly LeviathanSpecializationKnob Duration =
            LeviathanSpecializationKnob.Percent(
                "starfire.duration",
                "Breath Capacity"
            );

        public static readonly LeviathanSpecializationKnob FullSizeHoldSeconds =
            LeviathanSpecializationKnob.Flat(
                "starfire.full_size_hold_seconds",
                "Full-Power Capacity",
                "s"
            );

        public static readonly LeviathanSpecializationKnob RetreatSeconds =
            LeviathanSpecializationKnob.Flat(
                "starfire.retreat_seconds",
                "Falloff Capacity",
                "s"
            );

        // Multiplier on reservoir consumption while actively breathing.
        public static readonly LeviathanSpecializationKnob ActiveDrainRate =
            LeviathanSpecializationKnob.Percent(
                "starfire.active_drain_rate",
                "Breath Drain Rate"
            );

        // Multiplier on the baseline amount of reservoir restored per idle second.
        public static readonly LeviathanSpecializationKnob RecoveryRate =
            LeviathanSpecializationKnob.Percent(
                "starfire.recovery_rate",
                "Breath Recovery Rate"
            );

        // Low-level flat adjustment to the baseline breath-seconds restored each
        // second. Exposed in addition to RecoveryRate for future exact tuning.
        public static readonly LeviathanSpecializationKnob RecoverySecondsPerSecond =
            LeviathanSpecializationKnob.Flat(
                "starfire.recovery_seconds_per_second",
                "Breath Recovery",
                " breath-s/s"
            );

        public static readonly LeviathanSpecializationKnob RecoveryDelaySeconds =
            LeviathanSpecializationKnob.Flat(
                "starfire.recovery_delay_seconds",
                "Breath Recovery Delay",
                "s"
            );

        // 1 = linear recovery. >1 recovers quickly when empty and slows near full.
        public static readonly LeviathanSpecializationKnob RecoveryCurveExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.recovery_curve_exponent",
                "Recovery Curve Exponent"
            );

        // Common falloff curve retained as a master shaping knob.
        public static readonly LeviathanSpecializationKnob RetreatCurveExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.retreat_curve_exponent",
                "Falloff Curve Exponent"
            );

        public static readonly LeviathanSpecializationKnob DamageFalloffCurveExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.damage_falloff_curve_exponent",
                "Damage Falloff Curve Exponent"
            );

        public static readonly LeviathanSpecializationKnob LengthFalloffCurveExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.length_falloff_curve_exponent",
                "Length Falloff Curve Exponent"
            );

        public static readonly LeviathanSpecializationKnob WidthFalloffCurveExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.width_falloff_curve_exponent",
                "Width Falloff Curve Exponent"
            );

        public static readonly LeviathanSpecializationKnob MinimumDamage =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.minimum_damage",
                "Minimum Damage"
            );

        public static readonly LeviathanSpecializationKnob MinimumLength =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.minimum_length",
                "Minimum Breath Length"
            );

        public static readonly LeviathanSpecializationKnob MinimumWidth =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.minimum_width",
                "Minimum Breath Width"
            );

        // -------------------------------------------------------------------------
        // Windup / native Torch timing
        // -------------------------------------------------------------------------

        public static readonly LeviathanSpecializationKnob StartupDelaySeconds =
            LeviathanSpecializationKnob.Flat(
                "starfire.startup_delay_seconds",
                "Startup Delay",
                "s"
            );

        public static readonly LeviathanSpecializationKnob NativeChargeRampSpeed =
            LeviathanSpecializationKnob.Percent(
                "starfire.native_charge_ramp_speed",
                "Native Charge Ramp Speed"
            );

        // Compatibility alias for previous node examples/code.
        public static readonly LeviathanSpecializationKnob ChargeRampSpeed =
            NativeChargeRampSpeed;

        // Common multiplier on native Activatable cooldown/recharge values.
        public static readonly LeviathanSpecializationKnob NativeRecoveryTime =
            LeviathanSpecializationKnob.Percent(
                "starfire.native_recovery_time",
                "Native Recovery Time"
            );

        public static readonly LeviathanSpecializationKnob RechargeTime =
            NativeRecoveryTime;

        public static readonly LeviathanSpecializationKnob Cooldown =
            LeviathanSpecializationKnob.Percent(
                "starfire.cooldown",
                "Native Cooldown"
            );

        public static readonly LeviathanSpecializationKnob RechargeSeconds =
            LeviathanSpecializationKnob.Percent(
                "starfire.recharge_seconds",
                "Native Recharge Time"
            );

        // -------------------------------------------------------------------------
        // Cone geometry / hitbox
        // -------------------------------------------------------------------------

        public static readonly LeviathanSpecializationKnob BaseFanHalfAngleDegrees =
            LeviathanSpecializationKnob.Flat(
                "starfire.base_fan_half_angle_degrees",
                "Base Fan Half-Angle",
                "°"
            );

        public static readonly LeviathanSpecializationKnob MuzzleWidth =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.muzzle_width",
                "Muzzle Width"
            );

        public static readonly LeviathanSpecializationKnob HitboxWidthPadding =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.hitbox_width_padding",
                "Hitbox Width Scale"
            );

        public static readonly LeviathanSpecializationKnob HitboxLengthPadding =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.hitbox_length_padding",
                "Hitbox Length Scale"
            );

        public static readonly LeviathanSpecializationKnob CenterLengthBonus =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.center_length_bonus",
                "Center Length Bonus"
            );

        // -------------------------------------------------------------------------
        // Cosmetic plume
        // -------------------------------------------------------------------------

        public static readonly LeviathanSpecializationKnob VisualOpacity =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.visual_opacity",
                "Visual Opacity"
            );

        public static readonly LeviathanSpecializationKnob VisualBeamFill =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.visual_beam_fill",
                "Visual Beam Fill"
            );

        public static readonly LeviathanSpecializationKnob VisualMinimumBeamWidth =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.visual_minimum_beam_width",
                "Visual Minimum Beam Width"
            );

        public static readonly LeviathanSpecializationKnob VisualEndFeather =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.visual_end_feather",
                "Visual End Feather"
            );

        public static readonly LeviathanSpecializationKnob VisualStartupExtendSeconds =
            LeviathanSpecializationKnob.Flat(
                "starfire.visual_startup_extend_seconds",
                "Visual Startup Extend Time",
                "s"
            );

        public static readonly LeviathanSpecializationKnob VisualBeamCount =
            LeviathanSpecializationKnob.Flat(
                "starfire.visual_beam_count",
                "Visual Beam Count"
            );

        public static readonly LeviathanSpecializationKnob VisualLengthSegments =
            LeviathanSpecializationKnob.Flat(
                "starfire.visual_length_segments",
                "Visual Length Segments"
            );

        // -------------------------------------------------------------------------
        // Recharge-pull / Big Succ feature
        // -------------------------------------------------------------------------

        public static readonly LeviathanSpecializationKnob RechargePullRadius =
            LeviathanSpecializationKnob.Flat(
                "starfire.recharge_pull_radius",
                "Recharge Pull Radius",
                "m"
            );

        public static readonly LeviathanSpecializationKnob RechargePullStrength =
            LeviathanSpecializationKnob.Flat(
                "starfire.recharge_pull_strength",
                "Recharge Pull Strength"
            );

        public static readonly LeviathanSpecializationKnob RechargePullFalloffExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.recharge_pull_falloff_exponent",
                "Recharge Pull Falloff Exponent"
            );

        public static readonly LeviathanSpecializationKnob RechargePullMaxSpeed =
            LeviathanSpecializationKnob.Flat(
                "starfire.recharge_pull_max_speed",
                "Recharge Pull Max Speed",
                "m/s"
            );

        // -------------------------------------------------------------------------
        // Blast Wave mode
        // -------------------------------------------------------------------------

        public static readonly LeviathanSpecializationKnob BlastWaveArcDegrees =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_arc_degrees",
                "Blast Wave Arc",
                "°"
            );

        // Reserved compatibility knob. The current Blast Wave implementation does
        // not use an authored damage-duration value; it integrates the actual
        // remaining resolved Breath Power damage profile instead.
        public static readonly LeviathanSpecializationKnob BlastWaveDamageSeconds =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_damage_seconds",
                "Blast Wave Damage Seconds",
                "s"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveDamageMultiplier =
            LeviathanSpecializationKnob.Multiplier(
                "starfire.blast_wave_damage_multiplier",
                "Blast Wave Damage"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveSpeed =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_speed",
                "Blast Wave Speed",
                "m/s"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveRange =
            LeviathanSpecializationKnob.Percent(
                "starfire.blast_wave_range",
                "Blast Wave Range"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveWidth =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_width",
                "Blast Wave Front Thickness",
                "m"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveDuration =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_duration",
                "Blast Wave Duration",
                "s"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveFalloffExponent =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_falloff_exponent",
                "Blast Wave Falloff Exponent"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveVisualOpacity =
            LeviathanSpecializationKnob.PercentagePoints(
                "starfire.blast_wave_visual_opacity",
                "Blast Wave Visual Opacity"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveStartRadius =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_start_radius",
                "Blast Wave Start Radius",
                "m"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveEndRadius =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_end_radius",
                "Blast Wave End Radius",
                "m"
            );

        public static readonly LeviathanSpecializationKnob BlastWaveKnockback =
            LeviathanSpecializationKnob.Flat(
                "starfire.blast_wave_knockback",
                "Blast Wave Knockback"
            );
    }

    public static class Flags
    {
        public static readonly LeviathanSpecializationFlag RechargePull =
            LeviathanSpecializationFlag.Create(
                "starfire.recharge_pull",
                "Recharge Pull"
            );

        public static readonly LeviathanSpecializationFlag BlastWave =
            LeviathanSpecializationFlag.Create(
                "starfire.blast_wave",
                "Blast Wave"
            );
    }

    // =========================================================================
    // NATIVE ACCESS
    // =========================================================================

    private static readonly FieldInfo ParentShipField =
        AccessTools.Field(typeof(Equippable), "parentShip");

    private static readonly FieldInfo MainSpikeField =
        AccessTools.Field(typeof(Torch), "mainSpike");

    private static readonly FieldInfo MirrorSpikeField =
        AccessTools.Field(typeof(Torch), "mirrorSpike");

    // Verified native Torch fields: UpdatePower moves `charge` toward 0/1 at
    // deltaTime / chargeTime, and both UpdateSpikeScale/GetDamageData multiply by
    // `charge`. Temporarily changing chargeTime therefore cleanly changes ramp speed.
    private static readonly FieldInfo ChargeField =
        AccessTools.Field(typeof(Torch), "charge");

    private static readonly FieldInfo ChargeTimeField =
        AccessTools.Field(typeof(Torch), "chargeTime");

    // Native Torch.GetDamageData halves each packet whenever the equipped weapon
    // has a mirrorGameObject, because vanilla subsequently applies both spikes.
    // Starfire suppresses the mirror, so its one surviving breath recombines this.
    private static readonly FieldInfo MirrorGameObjectField =
        AccessTools.Field(typeof(Equippable), "mirrorGameObject");

    // GameShip caches the aggregate heat-per-second contribution of equipped
    // activatables in this field. GameShip.AddHeat() has no amount parameter; it
    // only opens a short heat-latency window. Starfire therefore adjusts this
    // cached rate only while GameShip.UpdateHeat() executes.
    private static readonly FieldInfo GameShipHeatPerSecondField =
        AccessTools.Field(typeof(GameShip), "heatPerSecond");

    private static readonly FieldInfo ActivatableActiveField =
        AccessTools.Field(typeof(Activatable), "active");

    // Internal native combat helpers are resolved by shape so Starfire can route
    // custom Blast Wave packets through exactly the same network/damage path as
    // native Torch hits without taking a compile-time dependency on IDamageable.
    private static readonly MethodInfo RouteDamageMethod =
        typeof(NetCombat)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "RouteDamage" &&
                     m.GetParameters().Length == 14 &&
                     m.GetParameters()[2].ParameterType == typeof(DamageData[])
            );

    private static readonly MethodInfo ConduitRelayHitMethod =
        typeof(Conduit)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "RelayHit" &&
                     m.GetParameters().Length == 6 &&
                     m.GetParameters()[3].ParameterType == typeof(DamageData[])
            );

    private static readonly HashSet<Torch> TouchedTorches =
        new HashSet<Torch>();

    private static readonly HashSet<Torch> FullySuppressedTorches =
        new HashSet<Torch>();

    // Source Torch instance -> activation start time for the current windup.
    private static readonly Dictionary<Torch, float> StartupBeginTimes =
        new Dictionary<Torch, float>();

    private sealed class BreathState
    {
        public bool initialized;
        public float remainingSeconds;
        public float capacitySeconds;
        public float lastUpdateTime;
        public float idleStartedTime = -1f;
        public bool blastFiredThisActivation;
        public bool wasEmitting;
        public float visualEmissionStartedTime = -1f;
    }

    // Persistent lung state survives trigger release. It is initialized full,
    // drains while Starfire is actually breathing, and recovers only while idle.
    private static readonly Dictionary<Torch, BreathState> BreathStates =
        new Dictionary<Torch, BreathState>();

    private sealed class BlastWaveState
    {
        public Torch sourceTorch;
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
        public float damageTickEquivalent;
        public GameObject visualObject;
        public Mesh visualMesh;
        public MeshRenderer visualRenderer;
        public GameObject visualAnimationObject;
        public SpriteRenderer visualAnimationRenderer;
        public Animator visualAnimationAnimator;
        public Sprite visualLastSprite;
        public bool visualFlipX;
        public bool visualFlipY;
        public float visualRadius;
        public LineRenderer fallbackLineRenderer;
        public Material visualMaterial;
        public readonly HashSet<GameShip> hitShips = new HashSet<GameShip>();
    }

    private sealed class BlastWaveExplosionVisualSource
    {
        public SpriteRenderer sourceRenderer;
        public Sprite sprite;
        public Material material;
        public RuntimeAnimatorController animatorController;
        public bool flipX;
        public bool flipY;
        public int sortingLayerID;
        public int sortingOrder;
    }

    private static readonly Dictionary<Torch, BlastWaveState> BlastWaves =
        new Dictionary<Torch, BlastWaveState>();

    // Blast Wave reuses the same common vanilla ExplosiveArea prefab selected by
    // Event Horizon. Only its sprite/material/animation are borrowed; none of the
    // ExplosiveArea damage, sound, shake or pooling behavior is instantiated.
    private static BlastWaveExplosionVisualSource blastWaveExplosionVisualSource;

    private static readonly HashSet<GameShip> RechargePullTargets =
        new HashSet<GameShip>();

    // Custom Starfire damage (Blast Wave) uses the same native Torch packet and
    // RouteDamage hooks as normal Starfire, but supplies an integrated breath
    // scale instead of the instantaneous reservoir scale.
    private static float currentDamageScaleOverride = -1f;

    private static bool sceneCleanupRegistered;

    private static bool warnedNoSpikeFields;
    private static bool warnedNoSpikeObject;
    private static bool warnedNoFanCollider;
    private static bool warnedNoFanVisual;

    private sealed class FanVisualLayer
    {
        public SpriteRenderer sourceRenderer;
        public bool sourceWasEnabled;
        public GameObject objectInstance;
        public Mesh mesh;
        public Material material;
        public float uMin;
        public float uMax;
        public float vMin;
        public float vMax;
        public float nativeHalfWidth;
        public int beamCount;
        public int lengthSegments;
        public Vector3[] vertices;
        public Color[] colors;
        public Color lastSourceColor;
        public float lastVisualOpacity = -1f;
        public float lastEndFeatherFraction = -1f;
        public bool colorsInitialized;
    }

    private sealed class FanState
    {
        public object nativeSpike;
        public GameObject spikeObject;
        public Transform body;
        public FieldInfo colliderField;
        public Collider2D originalCollider;
        public bool originalColliderEnabled;
        public GameObject fanColliderObject;
        public PolygonCollider2D fanCollider;
        public float minX;
        public float maxX;
        public float centerY;
        public float baseHalfWidth;
        public readonly Vector2[] colliderPoints = new Vector2[5];
        public bool colliderShapeInitialized;
        public float lastColliderNearHalfWidth;
        public float lastColliderFarHalfWidth;
        public float lastColliderTipX;
        public bool visualGeometryInitialized;
        public float lastVisualNearHalfWidth;
        public float lastVisualFarHalfWidth;
        public float lastVisualBreathScaleX;
        public float lastVisualExtendScale;
        public float maxVisualRangeMetric;
        public float maxVisualWidthMetric;
        public readonly List<FanVisualLayer> visualLayers =
            new List<FanVisualLayer>();
    }

    private static readonly Dictionary<Torch, FanState> FanStates =
        new Dictionary<Torch, FanState>();


    // Narrower context used only while the source Torch's native DoSpikeDamage
    // runs, so the DamageData[] RouteDamage packet can be scaled without touching
    // unrelated damage that may occur elsewhere during a Torch update.
    [ThreadStatic]
    private static Torch currentDamageTorch;

    [ThreadStatic]
    private static int damageContextDepth;

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

    // Static Torch dictionaries otherwise survive scene changes. Explicitly
    // release generated Mesh/Material objects so Unity can reclaim GPU/native
    // resources before the next sector allocates its render targets.
    public static void CleanupTransientState()
    {
        foreach (KeyValuePair<Torch, FanState> pair in FanStates)
            CleanupFanState(pair.Value);
        FanStates.Clear();

        foreach (KeyValuePair<Torch, BlastWaveState> pair in BlastWaves)
            CleanupBlastWaveVisual(pair.Value);
        BlastWaves.Clear();
        blastWaveExplosionVisualSource = null;

        BreathStates.Clear();
        StartupBeginTimes.Clear();
        TouchedTorches.Clear();
        FullySuppressedTorches.Clear();
        RechargePullTargets.Clear();

        currentDamageTorch = null;
        damageContextDepth = 0;
        currentDamageScaleOverride = -1f;

        resolvedStatePilot = null;
        resolvedStateRank = -1;
        resolvedStateRevision = -1;
        resolvedState = null;
    }

    // =========================================================================
    // STARFIRE STATE / SOURCE SELECTION
    // =========================================================================

    public static bool TryGetStarfireRank(GameShip player, out int rank)
    {
        rank = 0;

        if (player == null ||
            LeviathanMod.Controller == null ||
            !IsCurrentPlayer(player))
        {
            return false;
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1 ||
            LeviathanMod.Controller.GetActiveSectionCount(player) <
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2)
        {
            return false;
        }

        // The Evolution-tree Starfire unlock is the canonical v2 ownership path.
        // It grants the rank-1 baseline; specialization nodes then modify named
        // knobs rather than masquerading as native Starfire ranks.
        if (LeviathanSpecializationRuntime.IsTreeActive(
            pilot,
            LeviathanStarfireTree.TreeId))
        {
            rank = 1;
            return true;
        }

        // Legacy fallback while existing saves/native Starfire registration still
        // exist. This can be removed after the specialization migration settles.
        rank = pilot.GetUpgradeLevel(LeviathanMod.StarfireUpgrade);
        return rank >= 1;
    }

    internal static bool IsCurrentStarfireSource(Torch torch)
    {
        if (torch == null || WorldController.instance == null)
            return false;

        GameShip player = WorldController.instance.GetCurrentPlayerShip();
        if (player == null || player.slots == null)
            return false;

        int rank;
        if (!TryGetStarfireRank(player, out rank))
            return false;

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];
            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            Torch candidate = slot.equippable as Torch;
            if (candidate != null && candidate.type == Item.Type.PrimaryWeapon)
                return ReferenceEquals(candidate, torch);
        }

        return false;
    }

    internal static float GetNativeRecoveryMultiplier(
        LeviathanSpecializationKnob specificKnob)
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (pilot == null)
            return 1f;

        float common = LeviathanSpecializationRuntime.GetKnobMultiplier(
            pilot,
            Knobs.RechargeTime);
        float specific = LeviathanSpecializationRuntime.GetKnobMultiplier(
            pilot,
            specificKnob);

        return Mathf.Max(0f, common * specific);
    }

    private static bool TryGetStarfireContext(
        Torch torch,
        out GameShip player,
        out int rank)
    {
        player = GetParentShip(torch);

        // These Harmony hooks run for every Torch in the sector. Reject NPC
        // Torches before scanning their slots or touching specialization state.
        if (!IsCurrentPlayer(player) ||
            !IsPrimaryTorch(torch, player))
        {
            rank = 0;
            return false;
        }

        return TryGetStarfireRank(player, out rank);
    }

    private static GameShip GetParentShip(Torch torch)
    {
        if (torch == null || ParentShipField == null)
            return null;

        return ParentShipField.GetValue(torch) as GameShip;
    }

    private static Torch FindSourceTorch(GameShip player)
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

            Torch torch = slot.equippable as Torch;

            if (torch != null && torch.type == Item.Type.PrimaryWeapon)
                return torch;
        }

        return null;
    }

    private static bool IsPrimaryTorch(Torch torch, GameShip player)
    {
        if (torch == null ||
            player == null ||
            player.slots == null ||
            torch.type != Item.Type.PrimaryWeapon)
        {
            return false;
        }

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];

            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            if (ReferenceEquals(slot.equippable, torch))
                return true;
        }

        return false;
    }

    private static bool IsSourceTorch(Torch torch, GameShip player)
    {
        return torch != null &&
            player != null &&
            ReferenceEquals(FindSourceTorch(player), torch);
    }

    private static bool IsCurrentPlayer(GameShip player)
    {
        return player != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == player;
    }

    // =========================================================================
    // GEOMETRY / VISUAL SUPPRESSION
    // =========================================================================

    public static void RefreshTorchGeometry(Torch torch, bool applyScale)
    {
        if (torch == null)
            return;

        GameShip player;
        int rank;
        if (!TryGetStarfireContext(torch, out player, out rank))
        {
            RestoreStarfireFan(torch);
            BreathStates.Remove(torch);
            StartupBeginTimes.Remove(torch);
            BlastWaveState activeWave;
            if (BlastWaves.TryGetValue(torch, out activeWave))
                DestroyBlastWave(torch, activeWave);

            if (TouchedTorches.Remove(torch))
            {
                if (FullySuppressedTorches.Remove(torch))
                    SetSpikeSuppressed(GetMainSpike(torch), false);

                SetSpikeSuppressed(GetMirrorSpike(torch), false);
            }

            return;
        }

        TouchedTorches.Add(torch);

        bool source = IsSourceTorch(torch, player);
        object mainSpike = GetMainSpike(torch);
        object mirrorSpike = GetMirrorSpike(torch);

        if (!source)
        {
            RestoreStarfireFan(torch);
            BreathStates.Remove(torch);
            StartupBeginTimes.Remove(torch);
            BlastWaveState activeWave;
            if (BlastWaves.TryGetValue(torch, out activeWave))
                DestroyBlastWave(torch, activeWave);
            FullySuppressedTorches.Add(torch);
            SetSpikeSuppressed(mainSpike, true);
            SetSpikeSuppressed(mirrorSpike, true);
            return;
        }

        ResolvedStarfireState resolved = GetResolvedStarfireState(rank);

        // Blast Wave replaces the Torch completely for this activation. Keep the
        // native Torch spike and Starfire plume hidden; only the travelling wave
        // visual is allowed to render. The source spike remains available as an
        // origin/reference object but contributes no renderer or collider.
        if (resolved.BlastWaveEnabled)
        {
            FullySuppressedTorches.Add(torch);
            SetSpikeSuppressed(mainSpike, true);
            SetSpikeSuppressed(mirrorSpike, true);
            SetStarfireFanActive(torch, false);
            return;
        }

        if (FullySuppressedTorches.Remove(torch))
            SetSpikeSuppressed(mainSpike, false);

        // Starfire still owns exactly one native damage spike. The mirror and
        // every extra Primary Torch remain mechanically suppressed.
        SetSpikeSuppressed(mirrorSpike, true);

        // Native Torch charge eases back toward zero after release. Starfire should
        // not remain visible during that discharge tail. Releasing only hides the
        // breath; persistent Breath Power is preserved and recovers in FixedUpdate.
        bool firing = torch.IsActive();
        bool emitting = firing && !IsInsideStartupDelay(torch, rank);
        SetStarfireFanActive(torch, emitting);

        if (!firing)
        {
            StartupBeginTimes.Remove(torch);
            return;
        }

        if (!emitting)
            return;

        if (applyScale)
            ApplyStarfireFan(torch, mainSpike, rank);
    }

    private static object GetMainSpike(Torch torch)
    {
        if (MainSpikeField == null)
        {
            WarnNoSpikeFields();
            return null;
        }

        return MainSpikeField.GetValue(torch);
    }

    private static object GetMirrorSpike(Torch torch)
    {
        if (MirrorSpikeField == null)
            return null;

        return MirrorSpikeField.GetValue(torch);
    }

    private static void ApplyStarfireFan(
        Torch torch,
        object nativeSpike,
        int rank)
    {
        if (torch == null || nativeSpike == null)
            return;

        FanState state = EnsureStarfireFan(torch, nativeSpike);
        if (state == null)
            return;

        float localLength = Mathf.Max(0.0001f, state.maxX - state.minX);
        float fullRangeScaleX = Mathf.Max(0.0001f, Mathf.Abs(torch.MaxRange));
        float x0 = 0f;
        float x1 = localLength;

        // Geometry reads the persistent reservoir rather than a per-trigger
        // timer. The first FullSizeHoldSeconds of capacity stay at full power;
        // once remaining capacity enters the retreat portion, length and width
        // independently fall toward their configured floors.
        float falloffProgress = GetBreathFalloffProgress(torch, rank);
        float remainingLengthFraction = EvaluateBreathComponent(
            falloffProgress,
            GetBreathMinimumLengthFraction(),
            GetBreathLengthFalloffCurveExponent(rank)
        );
        float remainingWidthFraction = EvaluateBreathComponent(
            falloffProgress,
            GetBreathMinimumWidthFraction(),
            GetBreathWidthFalloffCurveExponent(rank)
        );

        float fanHalfAngle = Mathf.Deg2Rad *
            GetBaseFanHalfAngleDegrees() *
            GetWidthMultiplier(rank) *
            remainingWidthFraction;

        float breathScaleX = Mathf.Max(
            0.0001f,
            fullRangeScaleX * remainingLengthFraction
        );

        // Override the native growing X scale after Torch.UpdateSpikeScale. This one
        // transform drives the real collider range; generated visuals use the same
        // scale explicitly, so cosmetic and mechanical reach stay synchronized.
        Vector3 bodyScale = state.body.localScale;
        bodyScale.x = breathScaleX;
        bodyScale.y = 1f;
        state.body.localScale = bodyScale;

        float scaledLength = localLength * breathScaleX;
        float nearHalfWidth = Mathf.Max(
            state.baseHalfWidth *
                GetMuzzleHalfWidthMultiplier() *
                remainingWidthFraction,
            0.0001f
        );
        float farHalfWidth =
            nearHalfWidth + Mathf.Tan(fanHalfAngle) * scaledLength;
        farHalfWidth = Mathf.Max(farHalfWidth, nearHalfWidth);

        float cy = state.centerY;

        // Still exactly one PolygonCollider2D / one native OverlapCollider call.
        // Use a cheap five-point envelope rather than rebaking a beam-by-beam
        // silhouette every falloff tick.
        UpdateMechanicalFanCollider(
            state,
            x0,
            x1,
            cy,
            nearHalfWidth,
            farHalfWidth
        );

        state.originalCollider.enabled = false;
        state.fanCollider.enabled = true;

        if (state.colliderField != null &&
            !ReferenceEquals(
                state.colliderField.GetValue(state.nativeSpike),
                state.fanCollider))
        {
            state.colliderField.SetValue(
                state.nativeSpike,
                state.fanCollider
            );
        }

        float visualExtendScale = GetVisualStartupExtendScale(torch, rank);
        UpdateFanVisuals(
            torch,
            state,
            nearHalfWidth,
            farHalfWidth,
            x0,
            x1,
            cy,
            breathScaleX,
            visualExtendScale
        );
    }

    private static float GetVisualStartupExtendScale(Torch torch, int rank)
    {
        ResolvedStarfireState resolved = GetResolvedStarfireState(rank);
        float seconds = Mathf.Max(0f, resolved.VisualStartupExtendSeconds);
        if (seconds <= 0.0001f || torch == null)
            return 1f;

        BreathState breath = GetOrCreateBreathState(torch, rank);
        if (breath == null || breath.visualEmissionStartedTime < 0f)
            return 1f;

        float t = Mathf.Clamp01(
            (Time.time - breath.visualEmissionStartedTime) / seconds
        );
        return Mathf.SmoothStep(0f, 1f, t);
    }

    private static void UpdateMechanicalFanCollider(
        FanState state,
        float x0,
        float x1,
        float centerY,
        float nearHalfWidth,
        float farHalfWidth)
    {
        if (state == null || state.fanCollider == null)
            return;

        // Keep the mechanical envelope cheap. The previous collider traced each
        // cosmetic beam and forced PolygonCollider2D to rebake a large changing
        // polygon every falloff tick. Five points preserve the cone and center tip.
        float widthPadding = GetHitboxWidthPaddingMultiplier();
        float lengthPadding = GetHitboxLengthPaddingMultiplier();
        float centerBonus = Mathf.Max(0f, GetVisualCenterLengthBonusFraction());
        int beamCount = Mathf.Max(1, GetVisualBeamCount());
        float beamFill = Mathf.Max(0f, GetVisualBeamFillFraction());
        float minimumHalfThickness = Mathf.Max(
            0.0001f,
            state.baseHalfWidth * GetVisualMinimumHalfWidthMultiplier()
        );
        float nearSpacing = beamCount > 1
            ? (nearHalfWidth * 2f) / (beamCount - 1)
            : nearHalfWidth * 2f;
        float farSpacing = beamCount > 1
            ? (farHalfWidth * 2f) / (beamCount - 1)
            : farHalfWidth * 2f;
        float nearThickness = Mathf.Max(
            minimumHalfThickness,
            nearSpacing * beamFill * 0.5f
        );
        float farThickness = Mathf.Max(
            minimumHalfThickness,
            farSpacing * beamFill * 0.5f
        );
        float visibleEndT = 1f - Mathf.Clamp(
            GetVisualEndFeatherFraction(),
            0f,
            0.50f
        ) * 0.5f;

        float near = Mathf.Max(
            0.0001f,
            (nearHalfWidth + nearThickness) * widthPadding
        );
        float far = Mathf.Max(
            near,
            (farHalfWidth + farThickness) * widthPadding
        );
        float outerX = x0 +
            (x1 - x0) * visibleEndT * lengthPadding;
        float tipX = x0 +
            (x1 - x0) * visibleEndT * (1f + centerBonus) * lengthPadding;

        bool changed = !state.colliderShapeInitialized ||
            Mathf.Abs(state.lastColliderNearHalfWidth - near) > 0.0005f ||
            Mathf.Abs(state.lastColliderFarHalfWidth - far) > 0.0005f ||
            Mathf.Abs(state.lastColliderTipX - tipX) > 0.0005f;

        if (!changed)
            return;

        Vector2[] points = state.colliderPoints;
        points[0] = new Vector2(x0, centerY + near);
        points[1] = new Vector2(outerX, centerY + far);
        points[2] = new Vector2(tipX, centerY);
        points[3] = new Vector2(outerX, centerY - far);
        points[4] = new Vector2(x0, centerY - near);
        state.fanCollider.points = points;

        state.lastColliderNearHalfWidth = near;
        state.lastColliderFarHalfWidth = far;
        state.lastColliderTipX = tipX;
        state.colliderShapeInitialized = true;
    }

    private static float GetCenterLengthWeight(int beam, int beamCount)
    {
        if (beamCount <= 1)
            return 1f;

        int pairDepth = Mathf.Min(
            beam,
            beamCount - 1 - beam
        );
        int maxPairDepth = Mathf.Max(
            1,
            (beamCount - 1) / 2
        );

        return pairDepth / (float)maxPairDepth;
    }

    private static FanState EnsureStarfireFan(
        Torch torch,
        object nativeSpike)
    {
        GameObject spikeObject = GetSpikeGameObject(nativeSpike);
        Transform body = GetSpikeBodyTransform(nativeSpike);

        if (spikeObject == null || body == null)
            return null;

        FanState existing;
        if (FanStates.TryGetValue(torch, out existing))
        {
            if (existing != null &&
                existing.spikeObject == spikeObject &&
                ReferenceEquals(existing.nativeSpike, nativeSpike) &&
                existing.fanCollider != null)
            {
                return existing;
            }

            CleanupFanState(existing);
            FanStates.Remove(torch);
        }

        FieldInfo colliderField =
            AccessTools.Field(nativeSpike.GetType(), "collider");
        Collider2D originalCollider = colliderField == null
            ? null
            : colliderField.GetValue(nativeSpike) as Collider2D;

        if (originalCollider == null)
            originalCollider =
                spikeObject.GetComponentInChildren<Collider2D>(true);

        float minX;
        float maxX;
        float centerY;
        float halfWidth;

        if (originalCollider == null ||
            !TryGetColliderLocalProfile(
                originalCollider,
                out minX,
                out maxX,
                out centerY,
                out halfWidth))
        {
            if (!warnedNoFanCollider)
            {
                warnedNoFanCollider = true;
                Debug.LogError(
                    "[Leviathan] Starfire could not resolve the native Torch " +
                    "collider profile; fan geometry will remain native."
                );
            }

            return null;
        }

        FanState state = new FanState();
        state.nativeSpike = nativeSpike;
        state.spikeObject = spikeObject;
        state.body = body;
        state.colliderField = colliderField;
        state.originalCollider = originalCollider;
        state.originalColliderEnabled = originalCollider.enabled;
        state.minX = minX;
        state.maxX = maxX;
        state.centerY = centerY;
        state.baseHalfWidth = halfWidth;

        // Add the polygon to an empty child, not the native SpriteRenderer object.
        // Unity otherwise attempts sprite-outline generation against Star Vortex's
        // non-readable textures when PolygonCollider2D is created at runtime.
        GameObject colliderObject =
            new GameObject("Starfire Fan Collider");
        colliderObject.layer = originalCollider.gameObject.layer;
        colliderObject.transform.SetParent(originalCollider.transform, false);
        colliderObject.transform.localPosition = Vector3.zero;
        colliderObject.transform.localRotation = Quaternion.identity;
        colliderObject.transform.localScale = Vector3.one;
        state.fanColliderObject = colliderObject;

        PolygonCollider2D fanCollider =
            colliderObject.AddComponent<PolygonCollider2D>();
        fanCollider.isTrigger = originalCollider.isTrigger;
        fanCollider.sharedMaterial = originalCollider.sharedMaterial;
        fanCollider.usedByEffector = originalCollider.usedByEffector;
        fanCollider.enabled = true;
        state.fanCollider = fanCollider;

        originalCollider.enabled = false;
        if (colliderField != null)
            colliderField.SetValue(nativeSpike, fanCollider);

        BuildFanVisualLayers(state);
        FanStates[torch] = state;
        return state;
    }

    private static void BuildFanVisualLayers(FanState state)
    {
        if (state == null || state.spikeObject == null)
            return;

        SpriteRenderer[] renderers =
            state.spikeObject.GetComponentsInChildren<SpriteRenderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            SpriteRenderer source = renderers[i];
            if (source == null || source.sprite == null)
                continue;

            Shader shader = source.sharedMaterial == null
                ? Shader.Find("Sprites/Default")
                : source.sharedMaterial.shader;

            if (shader == null)
                continue;

            FanVisualLayer layer = new FanVisualLayer();
            layer.sourceRenderer = source;
            layer.sourceWasEnabled = source.enabled;
            layer.nativeHalfWidth = GetRendererHalfWidthInBodySpace(
                state,
                source
            );

            // Fall back to the native collider profile only if this particular
            // sprite layer cannot report usable geometry. Normally the sprite
            // bounds are the correct source of truth for vanilla visual width.
            if (layer.nativeHalfWidth <= 0.0001f)
                layer.nativeHalfWidth = state.baseHalfWidth;

            GameObject meshObject =
                new GameObject("Starfire Fan Visual");
            meshObject.transform.SetParent(state.spikeObject.transform, false);
            meshObject.transform.localPosition = Vector3.zero;
            meshObject.transform.localRotation = Quaternion.identity;
            meshObject.transform.localScale = Vector3.one;
            layer.objectInstance = meshObject;

            MeshFilter filter = meshObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();

            Mesh mesh = new Mesh();
            mesh.name = "Starfire Fan Mesh";
            mesh.MarkDynamic();
            layer.mesh = mesh;
            filter.sharedMesh = mesh;

            Material material = source.sharedMaterial == null
                ? new Material(shader)
                : new Material(source.sharedMaterial);
            layer.material = material;

            if (material.HasProperty("_MainTex"))
                material.mainTexture = source.sprite.texture;

            renderer.sharedMaterial = material;
            renderer.sortingLayerID = source.sortingLayerID;
            renderer.sortingOrder = source.sortingOrder;

            Vector2[] spriteUv = source.sprite.uv;
            layer.uMin = float.PositiveInfinity;
            layer.uMax = float.NegativeInfinity;
            layer.vMin = float.PositiveInfinity;
            layer.vMax = float.NegativeInfinity;

            for (int uvIndex = 0; uvIndex < spriteUv.Length; uvIndex++)
            {
                Vector2 value = spriteUv[uvIndex];
                layer.uMin = Mathf.Min(layer.uMin, value.x);
                layer.uMax = Mathf.Max(layer.uMax, value.x);
                layer.vMin = Mathf.Min(layer.vMin, value.y);
                layer.vMax = Mathf.Max(layer.vMax, value.y);
            }

            if (spriteUv.Length == 0)
            {
                layer.uMin = 0f;
                layer.uMax = 1f;
                layer.vMin = 0f;
                layer.vMax = 1f;
            }

            if (source.flipX)
            {
                float temp = layer.uMin;
                layer.uMin = layer.uMax;
                layer.uMax = temp;
            }

            if (source.flipY)
            {
                float temp = layer.vMin;
                layer.vMin = layer.vMax;
                layer.vMax = temp;
            }

            RebuildFanVisualMesh(
                layer,
                GetVisualBeamCount(),
                GetVisualLengthSegments()
            );

            source.enabled = false;
            state.visualLayers.Add(layer);
        }

        if (state.visualLayers.Count == 0 && !warnedNoFanVisual)
        {
            warnedNoFanVisual = true;
            Debug.LogWarning(
                "[Leviathan] Starfire did not find a SpriteRenderer on the " +
                "Torch spike prefab. The fan hitbox will still work, but the " +
                "cosmetic beam fan cannot be generated automatically."
            );
        }
    }

    private static void RebuildFanVisualMesh(
        FanVisualLayer layer,
        int beamCount,
        int lengthSegments)
    {
        if (layer == null || layer.mesh == null)
            return;

        beamCount = Mathf.Max(1, beamCount);
        lengthSegments = Mathf.Max(2, lengthSegments);

        int vertsPerBeam = (lengthSegments + 1) * 2;
        int trisPerBeam = lengthSegments * 6;
        Vector3[] vertices =
            new Vector3[beamCount * vertsPerBeam];
        Vector2[] uv = new Vector2[beamCount * vertsPerBeam];
        int[] triangles = new int[beamCount * trisPerBeam];
        Color[] colors = new Color[beamCount * vertsPerBeam];
        Color sourceColor = layer.sourceRenderer == null
            ? Color.white
            : layer.sourceRenderer.color;

        for (int beam = 0; beam < beamCount; beam++)
        {
            int vBase = beam * vertsPerBeam;
            int triBase = beam * trisPerBeam;

            for (int section = 0;
                 section <= lengthSegments;
                 section++)
            {
                float t = section / (float)lengthSegments;
                float u = Mathf.Lerp(layer.uMin, layer.uMax, t);
                int v = vBase + section * 2;

                uv[v + 0] = new Vector2(u, layer.vMax);
                uv[v + 1] = new Vector2(u, layer.vMin);
                colors[v + 0] = sourceColor;
                colors[v + 1] = sourceColor;
            }

            for (int section = 0;
                 section < lengthSegments;
                 section++)
            {
                int v = vBase + section * 2;
                int tri = triBase + section * 6;

                triangles[tri + 0] = v + 0;
                triangles[tri + 1] = v + 1;
                triangles[tri + 2] = v + 2;
                triangles[tri + 3] = v + 2;
                triangles[tri + 4] = v + 1;
                triangles[tri + 5] = v + 3;
            }
        }

        layer.mesh.Clear();
        layer.mesh.vertices = vertices;
        layer.mesh.uv = uv;
        layer.mesh.triangles = triangles;
        layer.mesh.colors = colors;
        layer.mesh.RecalculateBounds();

        layer.vertices = vertices;
        layer.colors = colors;
        layer.lastSourceColor = sourceColor;
        layer.lastVisualOpacity = -1f;
        layer.lastEndFeatherFraction = -1f;
        layer.colorsInitialized = false;
        layer.beamCount = beamCount;
        layer.lengthSegments = lengthSegments;
    }

    private static void UpdateFanVisuals(
        Torch torch,
        FanState state,
        float nearHalfWidth,
        float farHalfWidth,
        float x0,
        float x1,
        float centerY,
        float breathScaleX,
        float visualExtendScale)
    {
        if (torch == null ||
            state == null ||
            state.spikeObject == null ||
            state.body == null)
        {
            return;
        }

        int beamCount = GetVisualBeamCount();
        int lengthSegments = GetVisualLengthSegments();
        int vertsPerBeam = (lengthSegments + 1) * 2;
        float nearSpacing = beamCount > 1
            ? (nearHalfWidth * 2f) / (beamCount - 1)
            : nearHalfWidth * 2f;
        float farSpacing = beamCount > 1
            ? (farHalfWidth * 2f) / (beamCount - 1)
            : farHalfWidth * 2f;

        float centerLengthBonus = Mathf.Max(
            0f,
            GetVisualCenterLengthBonusFraction()
        );
        float endFeatherFraction = Mathf.Clamp(
            GetVisualEndFeatherFraction(),
            0.001f,
            0.50f
        );
        float endFeatherStart = 1f - endFeatherFraction;
        float minimumWidthMultiplier =
            GetVisualMinimumHalfWidthMultiplier();
        float beamFillFraction = GetVisualBeamFillFraction();
        float visualOpacity = GetVisualOpacity();
        visualExtendScale = Mathf.Clamp01(visualExtendScale);

        bool geometryChanged = !state.visualGeometryInitialized ||
            Mathf.Abs(state.lastVisualNearHalfWidth - nearHalfWidth) > 0.0005f ||
            Mathf.Abs(state.lastVisualFarHalfWidth - farHalfWidth) > 0.0005f ||
            Mathf.Abs(state.lastVisualBreathScaleX - breathScaleX) > 0.0005f ||
            Mathf.Abs(state.lastVisualExtendScale - visualExtendScale) > 0.002f;

        float rangeMetric = breathScaleX * visualExtendScale;
        float widthMetric = farHalfWidth * visualExtendScale;
        bool boundsMayExpand = !state.visualGeometryInitialized ||
            rangeMetric > state.maxVisualRangeMetric + 0.0005f ||
            widthMetric > state.maxVisualWidthMetric + 0.0005f;

        for (int layerIndex = 0;
             layerIndex < state.visualLayers.Count;
             layerIndex++)
        {
            FanVisualLayer layer = state.visualLayers[layerIndex];
            if (layer == null || layer.mesh == null)
                continue;

            bool layerGeometryChanged = geometryChanged;
            if (layer.beamCount != beamCount ||
                layer.lengthSegments != lengthSegments)
            {
                RebuildFanVisualMesh(
                    layer,
                    beamCount,
                    lengthSegments
                );
                layerGeometryChanged = true;
            }

            if (layer.sourceRenderer != null)
                layer.sourceRenderer.enabled = false;

            // Fan spacing is expressed in native collider/body coordinates while
            // cosmetic strips display the actual Torch sprite. Correct thickness
            // by real vanilla sprite width so 1.00 remains one vanilla beam wide.
            float nativeHalfWidth = Mathf.Max(
                0.0001f,
                layer.nativeHalfWidth
            );
            float colliderHalfWidth = Mathf.Max(
                0.0001f,
                state.baseHalfWidth
            );
            float visualWidthScale = nativeHalfWidth / colliderHalfWidth;

            float minimumHalfThickness =
                nativeHalfWidth * minimumWidthMultiplier;
            float nearHalfThickness = Mathf.Max(
                minimumHalfThickness,
                nearSpacing * beamFillFraction * 0.5f *
                    visualWidthScale
            );
            float farHalfThickness = Mathf.Max(
                minimumHalfThickness,
                farSpacing * beamFillFraction * 0.5f *
                    visualWidthScale
            );

            Vector3[] vertices = layer.vertices;
            Color[] colors = layer.colors;
            if (vertices == null ||
                vertices.Length != beamCount * vertsPerBeam ||
                colors == null ||
                colors.Length != vertices.Length)
            {
                continue;
            }

            Color sourceColor = layer.sourceRenderer == null
                ? Color.white
                : layer.sourceRenderer.color;
            float baseAlpha = sourceColor.a * visualOpacity;
            bool colorsChanged = !layer.colorsInitialized ||
                layer.lastSourceColor != sourceColor ||
                Mathf.Abs(layer.lastVisualOpacity - visualOpacity) > 0.0005f ||
                Mathf.Abs(layer.lastEndFeatherFraction - endFeatherFraction) > 0.0005f;

            if (!layerGeometryChanged && !colorsChanged)
                continue;

            Matrix4x4 bodyToSpike = Matrix4x4.identity;
            if (layerGeometryChanged)
            {
                bodyToSpike =
                    state.spikeObject.transform.worldToLocalMatrix *
                    state.body.localToWorldMatrix;
            }

            for (int beam = 0; beam < beamCount; beam++)
            {
                float beamT = beamCount == 1
                    ? 0f
                    : Mathf.Lerp(
                        -1f,
                        1f,
                        beam / (beamCount - 1f)
                    );
                // Shape the plume's terminal silhouette by staggering whole-beam
                // lengths rather than narrowing/fading the end of each ribbon.
                // For 10 beams, pairDepth is 0/1/2/3/4/4/3/2/1/0, matching the
                // desired outer +0 through center +4 style progression.
                float centerLengthWeight = GetCenterLengthWeight(
                    beam,
                    beamCount
                );
                float beamLengthScale =
                    1f + centerLengthBonus * centerLengthWeight;
                float beamEndX =
                    x0 + (x1 - x0) * beamLengthScale;

                int vBase = beam * vertsPerBeam;

                for (int section = 0;
                     section <= lengthSegments;
                     section++)
                {
                    float t = section / (float)lengthSegments;
                    float grownT = t * visualExtendScale;
                    float fanT = grownT * beamLengthScale;
                    float x = Mathf.Lerp(x0, beamEndX, grownT);
                    float halfWidth =
                        nearHalfWidth +
                        (farHalfWidth - nearHalfWidth) * fanT;
                    float halfThickness =
                        nearHalfThickness +
                        (farHalfThickness - nearHalfThickness) * fanT;
                    float center = centerY + halfWidth * beamT;

                    // Keep brightness static across the active plume. Only the
                    // last portion of each ribbon fades to transparent, which
                    // softens the stepped ten-beam silhouette without narrowing
                    // the ribbon or adding more cosmetic Torch strips.
                    float endAlpha = t <= endFeatherStart
                        ? 1f
                        : 1f - Mathf.SmoothStep(
                            0f,
                            1f,
                            (t - endFeatherStart) / endFeatherFraction
                        );

                    float alpha = baseAlpha * endAlpha;
                    int v = vBase + section * 2;

                    if (layerGeometryChanged)
                    {
                        vertices[v + 0] = bodyToSpike.MultiplyPoint3x4(
                            new Vector3(
                                x,
                                center + halfThickness,
                                0f
                            )
                        );
                        vertices[v + 1] = bodyToSpike.MultiplyPoint3x4(
                            new Vector3(
                                x,
                                center - halfThickness,
                                0f
                            )
                        );
                    }

                    if (colorsChanged)
                    {
                        Color vertexColor = sourceColor;
                        vertexColor.a = alpha;
                        colors[v + 0] = vertexColor;
                        colors[v + 1] = vertexColor;
                    }
                }
            }

            if (layerGeometryChanged)
            {
                layer.mesh.vertices = vertices;
                // Once full-size bounds have been observed, retracting geometry
                // remains inside them. Avoid scanning every vertex for bounds on
                // every decay tick; only expand bounds when the plume grows beyond
                // a previously observed extent.
                if (boundsMayExpand)
                    layer.mesh.RecalculateBounds();
            }

            if (colorsChanged)
            {
                layer.mesh.colors = colors;
                layer.lastSourceColor = sourceColor;
                layer.lastVisualOpacity = visualOpacity;
                layer.lastEndFeatherFraction = endFeatherFraction;
                layer.colorsInitialized = true;
            }
        }

        if (geometryChanged)
        {
            state.lastVisualNearHalfWidth = nearHalfWidth;
            state.lastVisualFarHalfWidth = farHalfWidth;
            state.lastVisualBreathScaleX = breathScaleX;
            state.lastVisualExtendScale = visualExtendScale;
            state.maxVisualRangeMetric = Mathf.Max(
                state.maxVisualRangeMetric,
                rangeMetric
            );
            state.maxVisualWidthMetric = Mathf.Max(
                state.maxVisualWidthMetric,
                widthMetric
            );
            state.visualGeometryInitialized = true;
        }
    }

    private static float GetRendererHalfWidthInBodySpace(
        FanState state,
        SpriteRenderer renderer)
    {
        if (state == null ||
            state.body == null ||
            renderer == null ||
            renderer.sprite == null)
        {
            return 0f;
        }

        Bounds bounds = renderer.sprite.bounds;
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        Vector3[] corners = new Vector3[]
        {
            new Vector3(min.x, min.y, 0f),
            new Vector3(min.x, max.y, 0f),
            new Vector3(max.x, min.y, 0f),
            new Vector3(max.x, max.y, 0f)
        };

        float lowY = float.PositiveInfinity;
        float highY = float.NegativeInfinity;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 world = renderer.transform.TransformPoint(corners[i]);
            Vector3 bodyLocal = state.body.InverseTransformPoint(world);
            lowY = Mathf.Min(lowY, bodyLocal.y);
            highY = Mathf.Max(highY, bodyLocal.y);
        }

        if (float.IsInfinity(lowY) || float.IsInfinity(highY))
            return 0f;

        return Mathf.Max(0f, (highY - lowY) * 0.5f);
    }

    private static Vector3 BodyPointToSpikeLocal(
        FanState state,
        Vector3 bodyLocalPoint)
    {
        Vector3 world = state.body.TransformPoint(bodyLocalPoint);
        return state.spikeObject.transform.InverseTransformPoint(world);
    }

    private static Vector3 BodyPointToSpikeLocalWithXScale(
        FanState state,
        Vector3 bodyLocalPoint,
        float xScale)
    {
        if (state == null || state.body == null || state.spikeObject == null)
            return Vector3.zero;

        Transform body = state.body;
        Vector3 scale = body.localScale;
        scale.x = xScale;

        Vector3 parentLocal =
            body.localPosition +
            body.localRotation * Vector3.Scale(bodyLocalPoint, scale);
        Vector3 world = body.parent == null
            ? parentLocal
            : body.parent.TransformPoint(parentLocal);

        return state.spikeObject.transform.InverseTransformPoint(world);
    }

    private static void SetStarfireFanActive(Torch torch, bool active)
    {
        if (torch == null)
            return;

        FanState state;
        if (!FanStates.TryGetValue(torch, out state) || state == null)
            return;

        if (state.fanCollider != null)
            state.fanCollider.enabled = active;

        for (int i = 0; i < state.visualLayers.Count; i++)
        {
            FanVisualLayer layer = state.visualLayers[i];
            if (layer != null && layer.objectInstance != null)
                layer.objectInstance.SetActive(active);
        }
    }

    public static void RestoreStarfireFan(Torch torch)
    {
        if (torch == null)
            return;

        FanState state;
        if (!FanStates.TryGetValue(torch, out state))
            return;

        CleanupFanState(state);
        FanStates.Remove(torch);
    }

    public static void ForgetStarfireFan(Torch torch)
    {
        RestoreStarfireFan(torch);
    }

    private static void CleanupFanState(FanState state)
    {
        if (state == null)
            return;

        if (state.colliderField != null &&
            state.nativeSpike != null &&
            state.originalCollider != null)
        {
            try
            {
                state.colliderField.SetValue(
                    state.nativeSpike,
                    state.originalCollider
                );
            }
            catch
            {
            }
        }

        if (state.originalCollider != null)
            state.originalCollider.enabled = state.originalColliderEnabled;

        if (state.fanCollider != null)
            state.fanCollider.enabled = false;

        if (state.fanColliderObject != null)
            UnityEngine.Object.Destroy(state.fanColliderObject);

        for (int i = 0; i < state.visualLayers.Count; i++)
        {
            FanVisualLayer layer = state.visualLayers[i];
            if (layer == null)
                continue;

            if (layer.sourceRenderer != null)
                layer.sourceRenderer.enabled = layer.sourceWasEnabled;

            if (layer.mesh != null)
                UnityEngine.Object.Destroy(layer.mesh);
            if (layer.material != null)
                UnityEngine.Object.Destroy(layer.material);
            if (layer.objectInstance != null)
                UnityEngine.Object.Destroy(layer.objectInstance);
        }

        state.visualLayers.Clear();
    }

    private static bool TryGetColliderLocalProfile(
        Collider2D collider,
        out float minX,
        out float maxX,
        out float centerY,
        out float halfWidth)
    {
        minX = 0f;
        maxX = 0f;
        centerY = 0f;
        halfWidth = 0f;

        if (collider == null)
            return false;

        BoxCollider2D box = collider as BoxCollider2D;
        if (box != null)
        {
            minX = box.offset.x - box.size.x * 0.5f;
            maxX = box.offset.x + box.size.x * 0.5f;
            centerY = box.offset.y;
            halfWidth = Mathf.Abs(box.size.y) * 0.5f;
            return maxX > minX && halfWidth > 0f;
        }

        CapsuleCollider2D capsule = collider as CapsuleCollider2D;
        if (capsule != null)
        {
            minX = capsule.offset.x - capsule.size.x * 0.5f;
            maxX = capsule.offset.x + capsule.size.x * 0.5f;
            centerY = capsule.offset.y;
            halfWidth = Mathf.Abs(capsule.size.y) * 0.5f;
            return maxX > minX && halfWidth > 0f;
        }

        CircleCollider2D circle = collider as CircleCollider2D;
        if (circle != null)
        {
            minX = circle.offset.x - circle.radius;
            maxX = circle.offset.x + circle.radius;
            centerY = circle.offset.y;
            halfWidth = Mathf.Abs(circle.radius);
            return maxX > minX && halfWidth > 0f;
        }

        PolygonCollider2D polygon = collider as PolygonCollider2D;
        if (polygon != null && polygon.pathCount > 0)
        {
            float lowX = float.PositiveInfinity;
            float highX = float.NegativeInfinity;
            float lowY = float.PositiveInfinity;
            float highY = float.NegativeInfinity;

            for (int pathIndex = 0;
                 pathIndex < polygon.pathCount;
                 pathIndex++)
            {
                Vector2[] path = polygon.GetPath(pathIndex);
                for (int i = 0; i < path.Length; i++)
                {
                    Vector2 point = path[i] + polygon.offset;
                    lowX = Mathf.Min(lowX, point.x);
                    highX = Mathf.Max(highX, point.x);
                    lowY = Mathf.Min(lowY, point.y);
                    highY = Mathf.Max(highY, point.y);
                }
            }

            if (!float.IsInfinity(lowX) &&
                highX > lowX &&
                highY > lowY)
            {
                minX = lowX;
                maxX = highX;
                centerY = (lowY + highY) * 0.5f;
                halfWidth = (highY - lowY) * 0.5f;
                return true;
            }
        }

        EdgeCollider2D edge = collider as EdgeCollider2D;
        if (edge != null && edge.points != null && edge.points.Length > 1)
        {
            float lowX = float.PositiveInfinity;
            float highX = float.NegativeInfinity;
            float lowY = float.PositiveInfinity;
            float highY = float.NegativeInfinity;

            for (int i = 0; i < edge.points.Length; i++)
            {
                Vector2 point = edge.points[i] + edge.offset;
                lowX = Mathf.Min(lowX, point.x);
                highX = Mathf.Max(highX, point.x);
                lowY = Mathf.Min(lowY, point.y);
                highY = Mathf.Max(highY, point.y);
            }

            if (highX > lowX && highY > lowY)
            {
                minX = lowX;
                maxX = highX;
                centerY = (lowY + highY) * 0.5f;
                halfWidth = (highY - lowY) * 0.5f;
                return true;
            }
        }

        Bounds bounds = collider.bounds;
        Vector3[] worldCorners =
        {
            new Vector3(
                bounds.min.x,
                bounds.min.y,
                collider.transform.position.z),
            new Vector3(
                bounds.min.x,
                bounds.max.y,
                collider.transform.position.z),
            new Vector3(
                bounds.max.x,
                bounds.min.y,
                collider.transform.position.z),
            new Vector3(
                bounds.max.x,
                bounds.max.y,
                collider.transform.position.z)
        };

        float fallbackMinX = float.PositiveInfinity;
        float fallbackMaxX = float.NegativeInfinity;
        float fallbackMinY = float.PositiveInfinity;
        float fallbackMaxY = float.NegativeInfinity;

        for (int i = 0; i < worldCorners.Length; i++)
        {
            Vector3 local =
                collider.transform.InverseTransformPoint(worldCorners[i]);
            fallbackMinX = Mathf.Min(fallbackMinX, local.x);
            fallbackMaxX = Mathf.Max(fallbackMaxX, local.x);
            fallbackMinY = Mathf.Min(fallbackMinY, local.y);
            fallbackMaxY = Mathf.Max(fallbackMaxY, local.y);
        }

        if (fallbackMaxX <= fallbackMinX ||
            fallbackMaxY <= fallbackMinY)
        {
            return false;
        }

        minX = fallbackMinX;
        maxX = fallbackMaxX;
        centerY = (fallbackMinY + fallbackMaxY) * 0.5f;
        halfWidth = (fallbackMaxY - fallbackMinY) * 0.5f;
        return halfWidth > 0f;
    }

    private static Transform GetSpikeBodyTransform(object nativeSpike)
    {
        if (nativeSpike == null)
            return null;

        FieldInfo bodyField = AccessTools.Field(nativeSpike.GetType(), "body");
        if (bodyField == null)
            return null;

        return bodyField.GetValue(nativeSpike) as Transform;
    }

    private static void SetSpikeSuppressed(object nativeSpike, bool suppressed)
    {
        GameObject spikeObject = GetSpikeGameObject(nativeSpike);
        if (spikeObject == null)
            return;

        Renderer[] renderers =
            spikeObject.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].enabled = !suppressed;
        }

        Collider2D[] colliders =
            spikeObject.GetComponentsInChildren<Collider2D>(true);

        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = !suppressed;
        }
    }

    private static GameObject GetSpikeGameObject(object nativeSpike)
    {
        if (nativeSpike == null)
            return null;

        Type type = nativeSpike.GetType();

        FieldInfo named = AccessTools.Field(type, "obj");
        GameObject result = GetGameObjectFromValue(
            named == null ? null : named.GetValue(nativeSpike)
        );

        if (result != null)
            return result;

        FieldInfo[] fields = type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic
        );

        for (int i = 0; i < fields.Length; i++)
        {
            object value;

            try
            {
                value = fields[i].GetValue(nativeSpike);
            }
            catch
            {
                continue;
            }

            result = GetGameObjectFromValue(value);
            if (result != null)
                return result;
        }

        if (!warnedNoSpikeObject)
        {
            warnedNoSpikeObject = true;
            Debug.LogError(
                "[Leviathan] Starfire found Torch.Spike but could not resolve " +
                "its native GameObject; breath geometry will remain unchanged."
            );
        }

        return null;
    }

    private static GameObject GetGameObjectFromValue(object value)
    {
        GameObject gameObject = value as GameObject;
        if (gameObject != null)
            return gameObject;

        Component component = value as Component;
        return component == null ? null : component.gameObject;
    }

    private static void WarnNoSpikeFields()
    {
        if (warnedNoSpikeFields)
            return;

        warnedNoSpikeFields = true;
        Debug.LogError(
            "[Leviathan] Starfire could not resolve Torch.mainSpike; " +
            "breath geometry will remain native."
        );
    }

    // Mirrors the native Striker Laser/Bolt/Torch Range path. Torch.MaxRange
    // calls Equippable.ApplyModifier(MaxRange, ..., includeParentShip: true);
    // scale only the selected Starfire source at that native stat boundary.
    public static void ScaleMaxRange(
        Equippable equippable,
        Modifier.Type modifierType,
        bool includeParentShip,
        ref float value)
    {
        if (modifierType != Modifier.Type.MaxRange || !includeParentShip)
            return;

        Torch torch = equippable as Torch;
        GameShip player;
        int rank;

        if (torch == null ||
            !TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return;
        }

        value *= GetLengthMultiplier(rank);
    }

    // =========================================================================
    // PERSISTENT BREATH POWER
    // =========================================================================

    private static BreathState GetOrCreateBreathState(
        Torch torch,
        int rank)
    {
        if (torch == null)
            return null;

        float capacity = GetBreathCapacitySeconds(rank);
        BreathState state;
        if (!BreathStates.TryGetValue(torch, out state) || state == null)
        {
            state = new BreathState();
            BreathStates[torch] = state;
        }

        if (!state.initialized)
        {
            state.initialized = true;
            state.capacitySeconds = capacity;
            state.remainingSeconds = capacity;
            state.lastUpdateTime = Time.time;
            state.idleStartedTime = torch.IsActive() ? -1f : Time.time;
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

    // Called once from Torch.FixedUpdate. This is the authoritative reservoir
    // clock; visual/update patches only read the resulting state.
    public static void UpdateBreathPower(Torch torch)
    {
        if (torch == null)
            return;

        GameShip player;
        int rank;
        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            BreathStates.Remove(torch);
            BlastWaveState activeWave;
            if (BlastWaves.TryGetValue(torch, out activeWave))
                DestroyBlastWave(torch, activeWave);
            return;
        }

        BreathState state = GetOrCreateBreathState(torch, rank);
        if (state == null)
            return;

        float now = Time.time;
        float delta = Mathf.Clamp(
            now - state.lastUpdateTime,
            0f,
            0.25f
        );
        state.lastUpdateTime = now;

        float capacity = Mathf.Max(0f, state.capacitySeconds);
        bool firing = torch.IsActive();
        ResolvedStarfireState resolved = GetResolvedStarfireState(rank);

        // A previously launched wave keeps travelling independently of trigger
        // state and breath recovery.
        UpdateBlastWave(torch, resolved);

        if (firing)
        {
            state.idleStartedTime = -1f;

            // Windup is preparation, not exhalation. Breath capacity begins
            // draining only after the Starfire startup delay has completed.
            bool insideStartup = IsInsideStartupDelay(torch, rank);
            bool emitting = !insideStartup && !resolved.BlastWaveEnabled;
            if (emitting && !state.wasEmitting)
                state.visualEmissionStartedTime = now;
            state.wasEmitting = emitting;

            if (!insideStartup)
            {
                if (resolved.BlastWaveEnabled)
                {
                    if (!state.blastFiredThisActivation &&
                        state.remainingSeconds > 0.0001f)
                    {
                        FireBlastWave(
                            torch,
                            player,
                            rank,
                            resolved,
                            state
                        );
                        state.remainingSeconds = 0f;
                        state.blastFiredThisActivation = true;
                    }
                }
                else if (delta > 0f)
                {
                    state.remainingSeconds = Mathf.Max(
                        0f,
                        state.remainingSeconds -
                            delta * resolved.ActiveDrainRate
                    );
                }
            }
        }
        else
        {
            StartupBeginTimes.Remove(torch);
            state.blastFiredThisActivation = false;
            state.wasEmitting = false;
            state.visualEmissionStartedTime = -1f;

            if (state.idleStartedTime < 0f)
                state.idleStartedTime = now;

            float recoveryDelay = resolved.RecoveryDelaySeconds;
            bool recoveryStarted =
                capacity > 0f &&
                state.remainingSeconds < capacity &&
                now - state.idleStartedTime >= recoveryDelay;

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

            // Big Succ is explicitly a recovery effect: it begins only when
            // breath recovery itself begins, and stops the instant capacity is full.
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

    public static float GetBreathPower01(Torch torch)
    {
        if (torch == null)
            return 1f;

        GameShip player;
        int rank;
        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return 1f;
        }

        BreathState state = GetOrCreateBreathState(torch, rank);
        if (state == null || state.capacitySeconds <= 0.0001f)
            return 1f;

        return Mathf.Clamp01(
            state.remainingSeconds / state.capacitySeconds
        );
    }

    public static float GetBreathRemainingSeconds(Torch torch)
    {
        if (torch == null)
            return 0f;

        GameShip player;
        int rank;
        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return 0f;
        }

        BreathState state = GetOrCreateBreathState(torch, rank);
        return state == null ? 0f : Mathf.Max(0f, state.remainingSeconds);
    }

    public static bool IsBreathRecovering(Torch torch)
    {
        if (torch == null || torch.IsActive())
            return false;

        GameShip player;
        int rank;
        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return false;
        }

        BreathState state = GetOrCreateBreathState(torch, rank);
        if (state == null ||
            state.capacitySeconds <= 0.0001f ||
            state.remainingSeconds >= state.capacitySeconds - 0.0001f)
        {
            return false;
        }

        return state.idleStartedTime >= 0f &&
            Time.time - state.idleStartedTime >=
                GetBreathRecoveryDelaySeconds();
    }

    private static float GetBreathFalloffProgress(Torch torch, int rank)
    {
        BreathState state = GetOrCreateBreathState(torch, rank);
        if (state == null)
            return 0f;

        float retreatCapacity = Mathf.Max(
            0f,
            GetBreathRetreatSeconds(rank)
        );

        if (retreatCapacity <= 0.0001f)
            return state.remainingSeconds <= 0.0001f ? 1f : 0f;

        if (state.remainingSeconds >= retreatCapacity)
            return 0f;

        return Mathf.Clamp01(
            1f - state.remainingSeconds / retreatCapacity
        );
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

    // Integral of the remaining normal Starfire damage profile, expressed as
    // seconds of full-power Starfire damage. Blast Wave consumes this value rather
    // than inventing a separate damage formula, so every resolved damage/capacity/
    // falloff modifier automatically changes the wave payout.
    public static float CalculateRemainingBreathDamageSeconds(
        Torch torch,
        int rank)
    {
        BreathState breath = GetOrCreateBreathState(torch, rank);
        if (breath == null || breath.remainingSeconds <= 0.0001f)
            return 0f;

        ResolvedStarfireState state = GetResolvedStarfireState(rank);
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

        // Integral of:
        // 1 - (1-minimum) * (1-r/retreat)^exponent, r=[0, remaining].
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
        Torch torch,
        GameShip player,
        int rank,
        ResolvedStarfireState state,
        BreathState breath)
    {
        if (torch == null || player == null || state == null || breath == null)
            return;

        float damageSeconds = CalculateRemainingBreathDamageSeconds(
            torch,
            rank
        );
        if (damageSeconds <= 0.0001f)
            return;

        float tickRate = player.ApplyModifier(
            Modifier.Type.BeamTickRate,
            Item.Category.None,
            0.2f,
            true
        );
        tickRate = Mathf.Max(0.0001f, tickRate);

        Vector2 origin = player.transform.position;
        Vector2 direction = player.transform.right;
        FanState fan;
        if (FanStates.TryGetValue(torch, out fan) &&
            fan != null &&
            fan.body != null)
        {
            origin = fan.body.position;
            direction = fan.body.right;
        }

        direction.Normalize();

        float maxRange = Mathf.Max(
            0.01f,
            Mathf.Abs(torch.MaxRange) *
                Mathf.Max(0.01f, state.BlastWaveRangeMultiplier)
        );
        float durationSpeed = state.BlastWaveDuration > 0.0001f
            ? maxRange / state.BlastWaveDuration
            : 0f;
        float speed = durationSpeed > 0f
            ? durationSpeed
            : state.BlastWaveSpeed;

        BlastWaveState wave = new BlastWaveState();
        wave.sourceTorch = torch;
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
        wave.damageTickEquivalent = Mathf.Max(
            0f,
            damageSeconds /
                tickRate *
                state.BlastWaveDamageMultiplier
        );

        BlastWaveState previousWave;
        if (BlastWaves.TryGetValue(torch, out previousWave))
            DestroyBlastWave(torch, previousWave);

        BlastWaves[torch] = wave;
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
            ? new Material(source.material)
            : new Material(fallbackShader);
        if (material.HasProperty("_MainTex"))
            material.mainTexture = source.sprite.texture;
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);

        filter.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = source.sortingLayerID;
        renderer.sortingOrder = source.sortingOrder;

        Color tint = GetBlastWaveVisualColor(wave.sourceTorch);
        tint.a = Mathf.Clamp01(state.BlastWaveVisualOpacity);
        ApplyBlastWaveVisualTint(renderer, source.sprite, tint);

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

        // Prefer the Torch draw order over the source explosion's draw order.
        FanState fan;
        if (wave.sourceTorch != null &&
            FanStates.TryGetValue(wave.sourceTorch, out fan) &&
            fan != null &&
            fan.visualLayers.Count > 0 &&
            fan.visualLayers[0].sourceRenderer)
        {
            SpriteRenderer torchRenderer = fan.visualLayers[0].sourceRenderer;
            renderer.sortingLayerID = torchRenderer.sortingLayerID;
            renderer.sortingOrder = torchRenderer.sortingOrder;
        }

        wave.visualObject = obj;
        wave.visualMesh = mesh;
        wave.visualRenderer = renderer;
        wave.visualAnimationObject = animationObject;
        wave.visualAnimationRenderer = animationRenderer;
        wave.visualAnimationAnimator = animationAnimator;
        wave.visualLastSprite = source.sprite;
        wave.visualFlipX = source.flipX;
        wave.visualFlipY = source.flipY;
        wave.visualRadius = visualRadius;
        wave.visualMaterial = material;
        UpdateBlastWaveVisual(wave, 0f);
    }

    private static Mesh BuildExplosionCrescentMesh(
        Sprite sprite,
        bool flipX,
        bool flipY,
        float halfArcDegrees,
        float visualRadius,
        float bandThickness)
    {
        if (sprite == null)
            return null;

        const int arcSegments = 48;
        const int radialSegments = 8;

        float outerRadius = Mathf.Max(0.05f, visualRadius);
        float thickness = Mathf.Clamp(
            bandThickness,
            0.01f,
            outerRadius * 0.95f
        );
        float innerRadius = Mathf.Max(0.01f, outerRadius - thickness);
        float clampedHalfArc = Mathf.Clamp(halfArcDegrees, 0.1f, 179f);

        int columns = arcSegments + 1;
        int rows = radialSegments + 1;
        Vector3[] vertices = new Vector3[columns * rows];
        int[] triangles = new int[arcSegments * radialSegments * 6];
        Color[] colors = new Color[vertices.Length];

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
                colors[index] = Color.white;
            }
        }

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

        Mesh mesh = new Mesh();
        mesh.name = "Starfire Explosion Crescent";
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.colors = colors;
        UpdateExplosionCrescentUvs(
            mesh,
            sprite,
            flipX,
            flipY,
            outerRadius
        );
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void UpdateExplosionCrescentGeometry(
        Mesh mesh,
        float halfArcDegrees,
        float visualRadius,
        float bandThickness)
    {
        if (mesh == null)
            return;

        const int arcSegments = 48;
        const int radialSegments = 8;

        float outerRadius = Mathf.Max(0.05f, visualRadius);
        float thickness = Mathf.Clamp(
            bandThickness,
            0.01f,
            outerRadius * 0.95f
        );
        float innerRadius = Mathf.Max(0.01f, outerRadius - thickness);
        float clampedHalfArc = Mathf.Clamp(halfArcDegrees, 0.1f, 179f);

        int columns = arcSegments + 1;
        int expectedVertices = columns * (radialSegments + 1);
        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length != expectedVertices)
            return;

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

                vertices[index] = new Vector3(
                    -outerRadius + Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius,
                    0f
                );
            }
        }

        mesh.vertices = vertices;
        mesh.RecalculateBounds();
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

    private static void UpdateExplosionCrescentUvs(
        Mesh mesh,
        Sprite sprite,
        bool flipX,
        bool flipY,
        float outerRadius)
    {
        if (mesh == null || sprite == null || outerRadius <= 0.0001f)
            return;

        Vector3[] vertices = mesh.vertices;
        Vector2[] uv = new Vector2[vertices.Length];
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

        mesh.uv = uv;
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
        source.sourceRenderer = renderer;
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

    private static Color GetBlastWaveVisualColor(Torch torch)
    {
        FanState fan;
        if (torch != null &&
            FanStates.TryGetValue(torch, out fan) &&
            fan != null &&
            fan.visualLayers.Count > 0 &&
            fan.visualLayers[0].sourceRenderer)
        {
            Color color = fan.visualLayers[0].sourceRenderer.color;
            color.a = 1f;
            return color;
        }

        Color fallback = torch == null
            ? Color.white
            : GetTorchDamageTypeColor(torch.damageType);
        fallback.a = 1f;
        return fallback;
    }

    private static void ApplyBlastWaveVisualTint(
        MeshRenderer renderer,
        Sprite sprite,
        Color tint)
    {
        if (renderer == null || sprite == null)
            return;

        MaterialPropertyBlock block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        block.SetTexture(Shader.PropertyToID("_MainTex"), sprite.texture);
        block.SetColor(Shader.PropertyToID("_Color"), tint);
        block.SetColor(Shader.PropertyToID("_RendererColor"), tint);
        renderer.SetPropertyBlock(block);
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
        Material visualMaterial = new Material(shader);
        line.sharedMaterial = visualMaterial;

        Color color = GetTorchDamageTypeColor(
            wave.sourceTorch == null
                ? Damageable.DamageType.Kinetic
                : wave.sourceTorch.damageType
        );
        FanState fan;
        if (wave.sourceTorch != null &&
            FanStates.TryGetValue(wave.sourceTorch, out fan) &&
            fan != null &&
            fan.visualLayers.Count > 0 &&
            fan.visualLayers[0].sourceRenderer)
        {
            SpriteRenderer source = fan.visualLayers[0].sourceRenderer;
            color = source.color;
            line.sortingLayerID = source.sortingLayerID;
            line.sortingOrder = source.sortingOrder;
        }

        color.a *= state.BlastWaveVisualOpacity;
        line.startColor = color;
        line.endColor = color;

        wave.visualObject = obj;
        wave.fallbackLineRenderer = line;
        wave.visualMaterial = visualMaterial;
        UpdateBlastWaveVisual(wave, 0f);
    }

    private static void UpdateBlastWaveVisual(
        BlastWaveState wave,
        float travelDistance)
    {
        if (wave == null)
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
                wave.visualMesh,
                wave.halfArcDegrees,
                currentSizeRadius,
                wave.radialThickness
            );
            wave.visualRadius = currentSizeRadius;

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
                    wave.visualMesh,
                    currentSprite,
                    wave.visualFlipX,
                    wave.visualFlipY,
                    currentSizeRadius
                );

                if (spriteChanged &&
                    wave.visualMaterial != null &&
                    wave.visualMaterial.HasProperty("_MainTex"))
                {
                    wave.visualMaterial.mainTexture = currentSprite.texture;
                }

                Color tint = GetBlastWaveVisualColor(wave.sourceTorch);
                tint.a = GetResolvedStarfireState().BlastWaveVisualOpacity;
                ApplyBlastWaveVisualTint(
                    wave.visualRenderer,
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

        if (wave.visualMaterial)
            UnityEngine.Object.Destroy(wave.visualMaterial);

        wave.visualObject = null;
        wave.visualMesh = null;
        wave.visualRenderer = null;
        wave.visualAnimationObject = null;
        wave.visualAnimationRenderer = null;
        wave.visualAnimationAnimator = null;
        wave.visualLastSprite = null;
        wave.fallbackLineRenderer = null;
        wave.visualMaterial = null;
    }

    private static void DestroyBlastWave(
        Torch torch,
        BlastWaveState wave)
    {
        CleanupBlastWaveVisual(wave);

        if (torch != null)
            BlastWaves.Remove(torch);
    }

    private static void UpdateBlastWave(
        Torch torch,
        ResolvedStarfireState state)
    {
        BlastWaveState wave;
        if (torch == null ||
            !BlastWaves.TryGetValue(torch, out wave) ||
            wave == null)
        {
            return;
        }

        if (!wave.owner || !wave.sourceTorch)
        {
            DestroyBlastWave(torch, wave);
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - wave.startedTime);
        float currentTravel = wave.speed <= 0.0001f
            ? wave.maxRange
            : Mathf.Min(wave.maxRange, elapsed * wave.speed);

        UpdateBlastWaveVisual(wave, currentTravel);

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
            DestroyBlastWave(torch, wave);
    }

    private static void ApplyBlastWaveDamage(
        BlastWaveState wave,
        GameShip target,
        Vector2 hitPosition,
        ResolvedStarfireState state)
    {
        if (wave == null ||
            !wave.sourceTorch ||
            !wave.owner ||
            !target ||
            wave.damageTickEquivalent <= 0f)
        {
            return;
        }

        Torch torch = wave.sourceTorch;
        float originalCharge = ReadCharge(torch);
        Torch previousTorch = currentDamageTorch;
        int previousDepth = damageContextDepth;
        float previousOverride = currentDamageScaleOverride;

        try
        {
            currentDamageTorch = torch;
            damageContextDepth = Math.Max(1, damageContextDepth + 1);
            currentDamageScaleOverride = wave.damageTickEquivalent;

            if (ChargeField != null)
                ChargeField.SetValue(torch, 1f);

            bool crit = Modifier.CritRoll(torch.GetCritChance(), target);
            DamageData[] damage = torch.GetDamageData(crit);
            bool bypassDamageLimit = torch.HasCustomizer(
                Customizer.Type.BypassDamageLimit
            );

            target.SetLastDamageDirection(wave.direction);
            target.lastDamagedByWeaponName = torch.GetName(false, false);
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

            RouteDamageMethod.Invoke(
                null,
                new object[]
                {
                    target,
                    torch.damageType,
                    damage,
                    torch.GetStatusEffectChance(),
                    crit,
                    hitPosition,
                    wave.owner,
                    bypassDamageLimit,
                    state.BlastWaveKnockback,
                    torch,
                    0f,
                    0f,
                    false,
                    0f
                }
            );

            if (ConduitRelayHitMethod != null)
            {
                ConduitRelayHitMethod.Invoke(
                    null,
                    new object[]
                    {
                        torch,
                        wave.owner,
                        target,
                        damage,
                        hitPosition,
                        bypassDamageLimit
                    }
                );
            }

            if (state.BlastWaveKnockback > 0f)
            {
                Rigidbody2D body = target.GetRigidBody();
                Modifier.Knockback(
                    state.BlastWaveKnockback,
                    body,
                    wave.origin,
                    wave.owner
                );
            }
        }
        finally
        {
            if (ChargeField != null)
                ChargeField.SetValue(torch, originalCharge);

            currentDamageScaleOverride = previousOverride;
            damageContextDepth = previousDepth;
            currentDamageTorch = previousTorch;
        }
    }

    // =========================================================================
    // HEAT / EXTRA-TORCH SUPPRESSION
    // =========================================================================

    public struct HeatRateState
    {
        public bool changed;
        public float originalHeatPerSecond;
    }

    // Starfire owns exactly one Primary Torch. Extra Primary Torches are true
    // disabled weapons, not merely invisible damage sources. This runs before
    // Torch.FixedUpdate -> Activatable.FixedUpdate -> UpdateHeat.
    public static void SuppressNonSourceTorchActivation(Torch torch)
    {
        if (torch == null || ActivatableActiveField == null)
            return;

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank) ||
            IsSourceTorch(torch, player))
        {
            return;
        }

        ActivatableActiveField.SetValue(torch, false);
    }

    // Native heat flow:
    //   Activatable.UpdateHeat() -> GameShip.AddHeat() [no args, latency flag]
    //   GameShip.UpdateHeat() -> heat += cached heatPerSecond * deltaTime
    //
    // Temporarily rewrite only that cached aggregate while UpdateHeat executes.
    // Suppressed Primary Torches contribute zero. The source Torch keeps its
    // native contribution, with the Starfire multiplier applied only while the
    // source itself is active. Every non-Torch contribution remains untouched.
    public static HeatRateState PrepareShipHeatRate(GameShip player)
    {
        HeatRateState state = new HeatRateState();

        if (player == null || GameShipHeatPerSecondField == null)
            return state;

        int rank;
        if (!TryGetStarfireRank(player, out rank))
            return state;

        Torch source = FindSourceTorch(player);
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
                    slot.equippable.durability == 0)
                {
                    continue;
                }

                Torch torch = slot.equippable as Torch;
                if (torch == null || ReferenceEquals(torch, source))
                    continue;

                // GameShip.RegenerateModifiers included this value in its cached
                // aggregate. Remove it because Starfire disables this Torch.
                adjusted -= torch.heatPerSecond;
            }
        }

        if (source.durability != 0 && source.IsActive())
        {
            float multiplier = GetHeatMultiplier(rank);
            adjusted += source.heatPerSecond * (multiplier - 1f);
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
        if (!state.changed ||
            player == null ||
            GameShipHeatPerSecondField == null)
        {
            return;
        }

        GameShipHeatPerSecondField.SetValue(
            player,
            state.originalHeatPerSecond
        );
    }

    // =========================================================================
    // CHARGE / STARTUP
    // =========================================================================

    public static bool PrepareChargeRamp(Torch torch, out float originalChargeTime)
    {
        originalChargeTime = 0f;

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player) ||
            ChargeTimeField == null)
        {
            return false;
        }

        object raw = ChargeTimeField.GetValue(torch);
        if (!(raw is float))
            return false;

        originalChargeTime = (float)raw;
        float speed = GetChargeRampSpeedMultiplier(rank);

        if (Mathf.Approximately(speed, 1f))
            return false;

        // Native UpdatePower uses deltaTime / chargeTime. Divide chargeTime by
        // the requested speed multiplier so >1 charges faster and <1 slower.
        float safeSpeed = Mathf.Max(0.0001f, speed);
        ChargeTimeField.SetValue(
            torch,
            originalChargeTime <= 0f
                ? originalChargeTime
                : originalChargeTime / safeSpeed
        );

        return true;
    }

    public static void RestoreChargeRamp(
        Torch torch,
        bool changed,
        float originalChargeTime)
    {
        if (changed && torch != null && ChargeTimeField != null)
            ChargeTimeField.SetValue(torch, originalChargeTime);

        // A fully discharged Torch begins a new Starfire startup cycle next time.
        if (torch != null && ReadCharge(torch) <= 0.0001f)
            StartupBeginTimes.Remove(torch);
    }

    private static float ReadCharge(Torch torch)
    {
        if (torch == null || ChargeField == null)
            return 0f;

        object raw = ChargeField.GetValue(torch);
        return raw is float ? (float)raw : 0f;
    }

    private static bool IsInsideStartupDelay(Torch torch, int rank)
    {
        float delay = GetStartupDelaySeconds(rank);
        if (delay <= 0f || torch == null)
            return false;

        float started;

        if (!StartupBeginTimes.TryGetValue(torch, out started))
        {
            started = Time.time;
            StartupBeginTimes[torch] = started;
        }

        return Time.time - started < delay;
    }

    // =========================================================================
    // DAMAGE
    // =========================================================================

    public static bool BeginSpikeDamage(
        Torch torch,
        object[] args,
        out bool suppress)
    {
        suppress = false;

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank))
            return false;

        if (!IsSourceTorch(torch, player))
        {
            suppress = true;
            return false;
        }

        if (IsInsideStartupDelay(torch, rank))
        {
            suppress = true;
            return false;
        }

        // Blast Wave replaces Starfire's native continuous DoT entirely. Its
        // custom travelling front routes one integrated native Torch packet.
        if (GetResolvedStarfireState(rank).BlastWaveEnabled)
        {
            suppress = true;
            return false;
        }

        object mirrorSpike = GetMirrorSpike(torch);

        // Native Torch may call DoSpikeDamage once for each spike. If the method
        // exposes the Spike as an argument, explicitly reject the mirror packet.
        if (mirrorSpike != null && args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ReferenceEquals(args[i], mirrorSpike))
                {
                    suppress = true;
                    return false;
                }
            }
        }

        if (damageContextDepth == 0)
            currentDamageTorch = torch;

        damageContextDepth++;
        return true;
    }

    public static void EndSpikeDamage(bool entered)
    {
        if (!entered || damageContextDepth <= 0)
            return;

        damageContextDepth--;

        if (damageContextDepth == 0)
            currentDamageTorch = null;
    }

    public static void ScaleNativeTorchDamage(object[] args)
    {
        Torch torch = currentDamageTorch;

        if (damageContextDepth <= 0 ||
            torch == null ||
            args == null ||
            args.Length < 10 ||
            !ReferenceEquals(args[9], torch))
        {
            return;
        }

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return;
        }

        DamageData[] source = args[2] as DamageData[];
        if (source == null || source.Length == 0)
            return;

        float breathDamageScale = currentDamageScaleOverride >= 0f
            ? currentDamageScaleOverride
            : GetCurrentBreathDamageScale(torch, rank);

        float packetMultiplier =
            GetDamageMultiplier(rank) *
            breathDamageScale;

        // Native mirrored Torches split one weapon's damage across two spikes.
        // Starfire emits exactly one spike, so restore the source weapon's full
        // aggregate packet before applying any Starfire damage multiplier.
        if (HasNativeMirror(torch))
            packetMultiplier *= 2f;

        if (Mathf.Approximately(packetMultiplier, 1f))
            return;

        DamageData[] scaled = new DamageData[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            DamageData datum = source[i];
            datum.damage *= packetMultiplier;
            datum.dps *= packetMultiplier;
            scaled[i] = datum;
        }

        args[2] = scaled;
    }

    public static void ScaleNativeCritChance(Torch torch, ref float value)
    {
        GameShip player;
        int rank;

        if (!IsCurrentDamageSource(torch) ||
            !TryGetStarfireContext(torch, out player, out rank))
        {
            return;
        }

        value = Mathf.Clamp01(
            value + GetCritChanceBonus(rank)
        );
    }

    public static void ScaleNativeCritModifier(Torch torch, ref float value)
    {
        GameShip player;
        int rank;

        if (!IsCurrentDamageSource(torch) ||
            !TryGetStarfireContext(torch, out player, out rank))
        {
            return;
        }

        // Native GetDamageData uses (1 + GetCritModifier()). Add directly to
        // the modifier so specialization values are true percentage points.
        value += GetCritDamageBonus(rank);
    }

    public static void ScaleNativeDebuffChance(Torch torch, ref float value)
    {
        GameShip player;
        int rank;

        if (!IsCurrentDamageSource(torch) ||
            !TryGetStarfireContext(torch, out player, out rank))
        {
            return;
        }

        value = Mathf.Clamp01(
            value + GetStatusChanceBonus(rank)
        );
    }

    private static bool IsCurrentDamageSource(Torch torch)
    {
        if (damageContextDepth <= 0 ||
            torch == null ||
            !ReferenceEquals(currentDamageTorch, torch))
        {
            return false;
        }

        GameShip player = GetParentShip(torch);
        return IsSourceTorch(torch, player);
    }

    private static bool HasNativeMirror(Torch torch)
    {
        if (torch == null || MirrorGameObjectField == null)
            return false;

        UnityEngine.Object mirror =
            MirrorGameObjectField.GetValue(torch) as UnityEngine.Object;

        return mirror != null;
    }

    // =========================================================================
    // PUBLIC TUNING HELPERS
    // =========================================================================

    private static float ApplySpecializationKnob(
        Pilot pilot,
        LeviathanSpecializationKnob knob,
        float baseValue)
    {
        return pilot == null
            ? baseValue
            : LeviathanSpecializationRuntime.ApplyKnob(
                pilot,
                knob,
                baseValue
            );
    }

    private static float GetSpecializationMultiplier(
        Pilot pilot,
        LeviathanSpecializationKnob knob)
    {
        return pilot == null
            ? 1f
            : LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                knob
            );
    }

    // One authoritative post-modifier configuration for Starfire. Every ordinary
    // breath subsystem consumes this resolved state instead of reinterpreting node
    // effects independently. It is rebuilt at most once per rendered frame for the
    // current Pilot/rank, so tree changes are reflected immediately without a manual
    // invalidation path.
    public sealed class ResolvedStarfireState
    {
        public int Rank;

        public float HeatMultiplier;
        public float LengthMultiplier;
        public float WidthMultiplier;
        public float DamageMultiplier;
        public float CritChanceBonus;
        public float CritDamageBonus;
        public float StatusChanceBonus;

        public float StartupDelaySeconds;
        public float NativeChargeRampSpeedMultiplier;

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
        public float RetreatCurveExponent;
        public float DamageFalloffCurveExponent;
        public float LengthFalloffCurveExponent;
        public float WidthFalloffCurveExponent;

        public float BaseFanHalfAngleDegrees;
        public float MuzzleHalfWidthMultiplier;
        public float HitboxWidthPaddingMultiplier;
        public float HitboxLengthPaddingMultiplier;
        public float VisualCenterLengthBonusFraction;
        public float VisualOpacity;
        public float VisualBeamFillFraction;
        public float VisualMinimumHalfWidthMultiplier;
        public float VisualEndFeatherFraction;
        public float VisualStartupExtendSeconds;
        public int VisualBeamCount;
        public int VisualLengthSegments;

        public bool RechargePullEnabled;
        public float RechargePullRadius;
        public float RechargePullStrength;
        public float RechargePullFalloffExponent;
        public float RechargePullMaxSpeed;

        public bool BlastWaveEnabled;
        public float BlastWaveArcDegrees;
        public float BlastWaveDamageMultiplier;
        public float BlastWaveSpeed;
        public float BlastWaveRangeMultiplier;
        public float BlastWaveFrontThickness;
        public float BlastWaveDuration;
        public float BlastWaveFalloffExponent;
        public float BlastWaveVisualOpacity;
        public float BlastWaveStartRadius;
        public float BlastWaveEndRadius;
        public float BlastWaveKnockback;
    }

    private static Pilot resolvedStatePilot;
    private static int resolvedStateRank = -1;
    private static int resolvedStateRevision = -1;
    private static ResolvedStarfireState resolvedState;

    private static int ResolveCurrentStarfireRank()
    {
        if (WorldController.instance != null)
        {
            GameShip player = WorldController.instance.GetCurrentPlayerShip();
            int rank;
            if (player != null && TryGetStarfireRank(player, out rank))
                return Mathf.Max(1, rank);
        }

        return 1;
    }

    public static ResolvedStarfireState GetResolvedStarfireState(int rank)
    {
        rank = Mathf.Max(1, rank);
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        int revision = LeviathanSpecializationRuntime.ConfigurationRevision;

        if (resolvedState != null &&
            resolvedStateRevision == revision &&
            resolvedStateRank == rank &&
            ReferenceEquals(resolvedStatePilot, pilot))
        {
            return resolvedState;
        }

        ResolvedStarfireState state = new ResolvedStarfireState();
        state.Rank = rank;

        state.HeatMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.HeatGeneration,
            GetRankValue(HeatMultiplierByRank, rank)));
        state.LengthMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.Length,
            GetRankValue(LengthMultiplierByRank, rank)));
        state.WidthMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.Width,
            GetRankValue(WidthMultiplierByRank, rank)));
        state.DamageMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.Damage,
            GetRankValue(DamageMultiplierByRank, rank)));
        state.CritChanceBonus = ApplySpecializationKnob(
            pilot,
            Knobs.CritChance,
            GetRankValue(CritChanceBonusByRank, rank));
        state.CritDamageBonus = ApplySpecializationKnob(
            pilot,
            Knobs.CritDamage,
            GetRankValue(CritDamageBonusByRank, rank));
        state.StatusChanceBonus = ApplySpecializationKnob(
            pilot,
            Knobs.StatusChance,
            GetRankValue(StatusChanceBonusByRank, rank));

        state.StartupDelaySeconds = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.StartupDelaySeconds,
            GetRankValue(StartupDelaySecondsByRank, rank)));
        state.NativeChargeRampSpeedMultiplier = Mathf.Max(0.0001f, ApplySpecializationKnob(
            pilot,
            Knobs.NativeChargeRampSpeed,
            GetRankValue(ChargeRampSpeedMultiplierByRank, rank)));

        float durationMultiplier = Mathf.Max(0f, GetSpecializationMultiplier(
            pilot,
            Knobs.Duration));
        state.FullSizeHoldSeconds = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.FullSizeHoldSeconds,
            GetRankValue(FullSizeHoldSecondsByRank, rank)) * durationMultiplier);
        state.RetreatSeconds = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RetreatSeconds,
            GetRankValue(BreathRetreatSecondsByRank, rank)) * durationMultiplier);
        state.CapacitySeconds = Mathf.Max(0f,
            state.FullSizeHoldSeconds + state.RetreatSeconds);
        state.ActiveDrainRate = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.ActiveDrainRate,
            BaseBreathActiveDrainRate));

        float recoveryRateMultiplier = Mathf.Max(0f, GetSpecializationMultiplier(
            pilot,
            Knobs.RecoveryRate));
        state.RecoverySecondsPerSecond = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RecoverySecondsPerSecond,
            BaseBreathRecoverySecondsPerSecond) * recoveryRateMultiplier);
        state.RecoveryDelaySeconds = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RecoveryDelaySeconds,
            BaseBreathRecoveryDelaySeconds));
        state.RecoveryCurveExponent = Mathf.Max(0.05f, ApplySpecializationKnob(
            pilot,
            Knobs.RecoveryCurveExponent,
            BaseBreathRecoveryCurveExponent));

        state.MinimumDamageFraction = Mathf.Clamp01(ApplySpecializationKnob(
            pilot,
            Knobs.MinimumDamage,
            BaseBreathMinimumDamageFraction));
        state.MinimumLengthFraction = Mathf.Clamp01(ApplySpecializationKnob(
            pilot,
            Knobs.MinimumLength,
            BaseBreathMinimumLengthFraction));
        state.MinimumWidthFraction = Mathf.Clamp01(ApplySpecializationKnob(
            pilot,
            Knobs.MinimumWidth,
            BaseBreathMinimumWidthFraction));
        state.RetreatCurveExponent = Mathf.Max(0.01f, ApplySpecializationKnob(
            pilot,
            Knobs.RetreatCurveExponent,
            GetRankValue(BreathRetreatCurveExponentByRank, rank)));
        state.DamageFalloffCurveExponent = Mathf.Max(0.01f, ApplySpecializationKnob(
            pilot,
            Knobs.DamageFalloffCurveExponent,
            state.RetreatCurveExponent));
        state.LengthFalloffCurveExponent = Mathf.Max(0.01f, ApplySpecializationKnob(
            pilot,
            Knobs.LengthFalloffCurveExponent,
            state.RetreatCurveExponent));
        state.WidthFalloffCurveExponent = Mathf.Max(0.01f, ApplySpecializationKnob(
            pilot,
            Knobs.WidthFalloffCurveExponent,
            state.RetreatCurveExponent));

        state.BaseFanHalfAngleDegrees = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BaseFanHalfAngleDegrees,
            BaseFanHalfAngleDegrees));
        state.MuzzleHalfWidthMultiplier = Mathf.Max(0.01f, ApplySpecializationKnob(
            pilot,
            Knobs.MuzzleWidth,
            BaseMuzzleHalfWidthMultiplier));
        state.HitboxWidthPaddingMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.HitboxWidthPadding,
            BaseHitboxWidthPaddingMultiplier));
        state.HitboxLengthPaddingMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.HitboxLengthPadding,
            BaseHitboxLengthPaddingMultiplier));
        state.VisualCenterLengthBonusFraction = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.CenterLengthBonus,
            BaseVisualCenterLengthBonusFraction));
        state.VisualOpacity = Mathf.Clamp01(ApplySpecializationKnob(
            pilot,
            Knobs.VisualOpacity,
            BaseVisualOpacity));
        state.VisualBeamFillFraction = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.VisualBeamFill,
            BaseVisualBeamFillFraction));
        state.VisualMinimumHalfWidthMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.VisualMinimumBeamWidth,
            BaseVisualMinimumHalfWidthMultiplier));
        state.VisualEndFeatherFraction = Mathf.Clamp(ApplySpecializationKnob(
            pilot,
            Knobs.VisualEndFeather,
            BaseVisualEndFeatherFraction), 0f, 0.50f);
        state.VisualStartupExtendSeconds = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.VisualStartupExtendSeconds,
            BaseVisualStartupExtendSeconds));
        state.VisualBeamCount = Mathf.Max(1, Mathf.RoundToInt(ApplySpecializationKnob(
            pilot,
            Knobs.VisualBeamCount,
            BaseVisualBeamCount)));
        state.VisualLengthSegments = Mathf.Max(2, Mathf.RoundToInt(ApplySpecializationKnob(
            pilot,
            Knobs.VisualLengthSegments,
            BaseVisualLengthSegments)));

        state.RechargePullEnabled = LeviathanSpecializationRuntime.HasFlag(
            Flags.RechargePull);
        state.RechargePullRadius = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RechargePullRadius, 0f)) * WorldUnitsPerMeter;
        state.RechargePullStrength = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RechargePullStrength, 0f));
        state.RechargePullFalloffExponent = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RechargePullFalloffExponent, 0f));
        state.RechargePullMaxSpeed = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.RechargePullMaxSpeed, 0f)) * WorldUnitsPerMeter;

        state.BlastWaveEnabled = LeviathanSpecializationRuntime.HasFlag(
            Flags.BlastWave);
        state.BlastWaveArcDegrees = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveArcDegrees, 0f));
        state.BlastWaveDamageMultiplier = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveDamageMultiplier, 1f));
        state.BlastWaveSpeed = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveSpeed, 0f)) * WorldUnitsPerMeter;
        state.BlastWaveRangeMultiplier = Mathf.Max(0f, GetSpecializationMultiplier(
            pilot,
            Knobs.BlastWaveRange));
        state.BlastWaveFrontThickness = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveWidth, 0f)) * WorldUnitsPerMeter;
        state.BlastWaveDuration = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveDuration, 0f));
        state.BlastWaveFalloffExponent = Mathf.Max(0.01f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveFalloffExponent, 1f));
        state.BlastWaveVisualOpacity = Mathf.Clamp01(ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveVisualOpacity, 0f));
        state.BlastWaveStartRadius = Mathf.Max(0.05f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveStartRadius, 0f)) * WorldUnitsPerMeter;
        state.BlastWaveEndRadius = Mathf.Max(0.05f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveEndRadius, 0f)) * WorldUnitsPerMeter;
        state.BlastWaveKnockback = Mathf.Max(0f, ApplySpecializationKnob(
            pilot,
            Knobs.BlastWaveKnockback, 0f));

        resolvedStatePilot = pilot;
        resolvedStateRank = rank;
        // Synchronizing an auto-granted root during resolution can itself bump
        // the revision, so capture the final value after all knob aggregation.
        resolvedStateRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;
        resolvedState = state;
        return state;
    }

    public static ResolvedStarfireState GetResolvedStarfireState()
    {
        return GetResolvedStarfireState(ResolveCurrentStarfireRank());
    }

    public static float GetHeatMultiplier(int rank) { return GetResolvedStarfireState(rank).HeatMultiplier; }
    public static float GetLengthMultiplier(int rank) { return GetResolvedStarfireState(rank).LengthMultiplier; }
    public static float GetWidthMultiplier(int rank) { return GetResolvedStarfireState(rank).WidthMultiplier; }
    public static float GetDamageMultiplier(int rank) { return GetResolvedStarfireState(rank).DamageMultiplier; }
    public static float GetCritChanceBonus(int rank) { return GetResolvedStarfireState(rank).CritChanceBonus; }
    public static float GetCritDamageBonus(int rank) { return GetResolvedStarfireState(rank).CritDamageBonus; }
    public static float GetStatusChanceBonus(int rank) { return GetResolvedStarfireState(rank).StatusChanceBonus; }
    public static float GetStartupDelaySeconds(int rank) { return GetResolvedStarfireState(rank).StartupDelaySeconds; }
    public static float GetChargeRampSpeedMultiplier(int rank) { return GetResolvedStarfireState(rank).NativeChargeRampSpeedMultiplier; }
    public static float GetFullSizeHoldSeconds(int rank) { return GetResolvedStarfireState(rank).FullSizeHoldSeconds; }
    public static float GetBreathRetreatSeconds(int rank) { return GetResolvedStarfireState(rank).RetreatSeconds; }
    public static float GetBreathCapacitySeconds(int rank) { return GetResolvedStarfireState(rank).CapacitySeconds; }
    public static float GetBreathActiveDrainRate() { return GetResolvedStarfireState().ActiveDrainRate; }
    public static float GetBreathRecoverySecondsPerSecond() { return GetResolvedStarfireState().RecoverySecondsPerSecond; }
    public static float GetBreathRecoveryDelaySeconds() { return GetResolvedStarfireState().RecoveryDelaySeconds; }
    public static float GetBreathRecoveryCurveExponent() { return GetResolvedStarfireState().RecoveryCurveExponent; }
    public static float GetBreathMinimumDamageFraction() { return GetResolvedStarfireState().MinimumDamageFraction; }
    public static float GetBreathMinimumLengthFraction() { return GetResolvedStarfireState().MinimumLengthFraction; }
    public static float GetBreathMinimumWidthFraction() { return GetResolvedStarfireState().MinimumWidthFraction; }
    public static float GetBreathRetreatCurveExponent(int rank) { return GetResolvedStarfireState(rank).RetreatCurveExponent; }
    public static float GetBreathDamageFalloffCurveExponent(int rank) { return GetResolvedStarfireState(rank).DamageFalloffCurveExponent; }
    public static float GetBreathLengthFalloffCurveExponent(int rank) { return GetResolvedStarfireState(rank).LengthFalloffCurveExponent; }
    public static float GetBreathWidthFalloffCurveExponent(int rank) { return GetResolvedStarfireState(rank).WidthFalloffCurveExponent; }

    public static float GetCurrentBreathDamageScale(Torch torch, int rank)
    {
        ResolvedStarfireState state = GetResolvedStarfireState(rank);
        return EvaluateBreathComponent(
            GetBreathFalloffProgress(torch, rank),
            state.MinimumDamageFraction,
            state.DamageFalloffCurveExponent
        );
    }

    public static float GetBaseFanHalfAngleDegrees() { return GetResolvedStarfireState().BaseFanHalfAngleDegrees; }
    public static float GetMuzzleHalfWidthMultiplier() { return GetResolvedStarfireState().MuzzleHalfWidthMultiplier; }
    public static float GetHitboxWidthPaddingMultiplier() { return GetResolvedStarfireState().HitboxWidthPaddingMultiplier; }
    public static float GetHitboxLengthPaddingMultiplier() { return GetResolvedStarfireState().HitboxLengthPaddingMultiplier; }
    public static float GetVisualCenterLengthBonusFraction() { return GetResolvedStarfireState().VisualCenterLengthBonusFraction; }
    public static float GetVisualOpacity() { return GetResolvedStarfireState().VisualOpacity; }
    public static float GetVisualBeamFillFraction() { return GetResolvedStarfireState().VisualBeamFillFraction; }
    public static float GetVisualMinimumHalfWidthMultiplier() { return GetResolvedStarfireState().VisualMinimumHalfWidthMultiplier; }
    public static float GetVisualEndFeatherFraction() { return GetResolvedStarfireState().VisualEndFeatherFraction; }
    public static float GetVisualStartupExtendSeconds() { return GetResolvedStarfireState().VisualStartupExtendSeconds; }
    public static int GetVisualBeamCount() { return GetResolvedStarfireState().VisualBeamCount; }
    public static int GetVisualLengthSegments() { return GetResolvedStarfireState().VisualLengthSegments; }

    public static bool IsRechargePullEnabled() { return GetResolvedStarfireState().RechargePullEnabled; }
    public static float GetRechargePullRadius() { return GetResolvedStarfireState().RechargePullRadius; }
    public static float GetRechargePullStrength() { return GetResolvedStarfireState().RechargePullStrength; }
    public static float GetRechargePullFalloffExponent() { return GetResolvedStarfireState().RechargePullFalloffExponent; }
    public static float GetRechargePullMaxSpeed() { return GetResolvedStarfireState().RechargePullMaxSpeed; }

    public static bool IsBlastWaveEnabled() { return GetResolvedStarfireState().BlastWaveEnabled; }
    public static float GetBlastWaveArcDegrees() { return GetResolvedStarfireState().BlastWaveArcDegrees; }
    public static float GetBlastWaveDamageMultiplier() { return GetResolvedStarfireState().BlastWaveDamageMultiplier; }
    public static float GetBlastWaveSpeed() { return GetResolvedStarfireState().BlastWaveSpeed; }
    public static float GetBlastWaveRangeMultiplier() { return GetResolvedStarfireState().BlastWaveRangeMultiplier; }
    public static float GetBlastWaveFrontThickness() { return GetResolvedStarfireState().BlastWaveFrontThickness; }
    public static float GetBlastWaveDuration() { return GetResolvedStarfireState().BlastWaveDuration; }
    public static float GetBlastWaveFalloffExponent() { return GetResolvedStarfireState().BlastWaveFalloffExponent; }
    public static float GetBlastWaveVisualOpacity() { return GetResolvedStarfireState().BlastWaveVisualOpacity; }
    public static float GetBlastWaveStartRadius() { return GetResolvedStarfireState().BlastWaveStartRadius; }
    public static float GetBlastWaveEndRadius() { return GetResolvedStarfireState().BlastWaveEndRadius; }
    public static float GetBlastWaveKnockback() { return GetResolvedStarfireState().BlastWaveKnockback; }

    private static float GetRankValue(float[] values, int rank)
    {
        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }
}

// Native Striker range uses Equippable.ApplyModifier(MaxRange) and category
// modifiers. Hook the same stat boundary, but only for Starfire's one source.
[HarmonyPatch]
public static class LeviathanStarfireMaxRangePatch
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
        LeviathanStarfireRuntime.ScaleMaxRange(
            __instance,
            __0,
            __3,
            ref __result
        );
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

// -----------------------------------------------------------------------------
// Native Torch hooks
// -----------------------------------------------------------------------------

// Native BuildSpikes starts by destroying the previous spike. Clear generated
// fan state first so collider/material references cannot survive a rebuild.
[HarmonyPatch(typeof(Torch), "DestroySpikes")]
public static class LeviathanStarfireDestroySpikesPatch
{
    public static void Prefix(Torch __instance)
    {
        LeviathanStarfireRuntime.ForgetStarfireFan(__instance);
    }
}

[HarmonyPatch(typeof(Torch), "BuildSpikes")]
public static class LeviathanStarfireBuildSpikesPatch
{

    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, true);
    }
}

[HarmonyPatch(typeof(Torch), "FollowSpikes")]
public static class LeviathanStarfireFollowSpikesPatch
{

    public static void Postfix(Torch __instance)
    {
        // Generated fan objects are children of the native spike and follow its
        // transform automatically. No mesh/collider rebuild is needed here.
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, false);
    }
}

[HarmonyPatch(typeof(Torch), "UpdateSpikeScale")]
public static class LeviathanStarfireUpdateSpikeScalePatch
{

    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, true);
    }
}

// Extra Primary Torches must be inactive before Activatable.FixedUpdate performs
// duration/cooldown/heat work. The selected source Torch is untouched.
[HarmonyPatch(typeof(Torch), "FixedUpdate")]
public static class LeviathanStarfireTorchFixedUpdatePatch
{
    public static void Prefix(Torch __instance)
    {
        LeviathanStarfireRuntime.UpdateBreathPower(__instance);
        LeviathanStarfireRuntime.SuppressNonSourceTorchActivation(__instance);
    }
}

// GameShip.AddHeat() is only a no-argument latency flag. Actual weapon heat is
// applied here from GameShip's cached heatPerSecond field, so this is the narrow
// native point where Starfire can change only Torch contributions.
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

[HarmonyPatch(typeof(Torch), "UpdatePower")]
public static class LeviathanStarfireChargeRampPatch
{

    public static void Prefix(
        Torch __instance,
        out Tuple<bool, float> __state)
    {
        float original;
        bool changed = LeviathanStarfireRuntime.PrepareChargeRamp(
            __instance,
            out original
        );

        __state = Tuple.Create(changed, original);
    }

    public static void Postfix(
        Torch __instance,
        Tuple<bool, float> __state)
    {
        LeviathanStarfireRuntime.RestoreChargeRamp(
            __instance,
            __state != null && __state.Item1,
            __state == null ? 0f : __state.Item2
        );
    }
}

/// <summary>
/// Suppress every non-source Torch's native damage, and suppress the source
/// Torch's mirror spike if native DoSpikeDamage exposes that Spike as an arg.
/// For the one allowed source packet, establish a narrow context so RouteDamage
/// can apply the Starfire damage multiplier without recreating Torch mechanics.
/// </summary>
[HarmonyPatch(typeof(Torch), "DoSpikeDamage")]
public static class LeviathanStarfireDoSpikeDamagePatch
{

    public static bool Prefix(
        Torch __instance,
        object[] __args,
        out bool __state)
    {
        bool suppress;
        __state = LeviathanStarfireRuntime.BeginSpikeDamage(
            __instance,
            __args,
            out suppress
        );

        return !suppress;
    }

    public static void Postfix(bool __state)
    {
        LeviathanStarfireRuntime.EndSpikeDamage(__state);
    }
}

[HarmonyPatch(typeof(Torch), "GetCritChance")]
public static class LeviathanStarfireCritChancePatch
{

    public static void Postfix(Torch __instance, ref float __result)
    {
        LeviathanStarfireRuntime.ScaleNativeCritChance(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(Torch), "GetCritModifier")]
public static class LeviathanStarfireCritModifierPatch
{

    public static void Postfix(Torch __instance, ref float __result)
    {
        LeviathanStarfireRuntime.ScaleNativeCritModifier(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(Torch), "GetStatusEffectChance")]
public static class LeviathanStarfireDebuffChancePatch
{

    public static void Postfix(Torch __instance, ref float __result)
    {
        LeviathanStarfireRuntime.ScaleNativeDebuffChance(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch]
public static class LeviathanStarfireRouteDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return typeof(NetCombat)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "RouteDamage" &&
                     m.GetParameters().Length == 14 &&
                     m.GetParameters()[2].ParameterType == typeof(DamageData[])
            );
    }

    public static void Prefix(object[] __args)
    {
        LeviathanStarfireRuntime.ScaleNativeTorchDamage(__args);
    }
}

// Native Activatable timing belongs to Starfire even though the values live on
// Star Vortex's Activatable base class. Kept in this file so the skill owns all
// implementation needed by the knobs exposed above.
[HarmonyPatch(typeof(Activatable), "get_Cooldown")]
public static class LeviathanStarfireSpecializationCooldownPatch
{
    public static void Postfix(Activatable __instance, ref float __result)
    {
        Torch torch = __instance as Torch;
        if (!LeviathanStarfireRuntime.IsCurrentStarfireSource(torch))
            return;

        __result *= LeviathanStarfireRuntime.GetNativeRecoveryMultiplier(
            LeviathanStarfireRuntime.Knobs.Cooldown);
    }
}

[HarmonyPatch(typeof(Activatable), "get_RechargeSeconds")]
public static class LeviathanStarfireSpecializationRechargePatch
{
    public static void Postfix(Activatable __instance, ref float __result)
    {
        Torch torch = __instance as Torch;
        if (!LeviathanStarfireRuntime.IsCurrentStarfireSource(torch))
            return;

        __result *= LeviathanStarfireRuntime.GetNativeRecoveryMultiplier(
            LeviathanStarfireRuntime.Knobs.RechargeSeconds);
    }
}
