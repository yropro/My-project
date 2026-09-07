// Named Starfire knobs exposed to specialization tree definitions.
// Tree files only describe intent; Starfire owns the implementation details.
public static class LeviathanStarfireKnobs
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

    public static readonly LeviathanSpecializationKnob BlastWaveKnockback =
        LeviathanSpecializationKnob.Flat(
            "starfire.blast_wave_knockback",
            "Blast Wave Knockback"
        );
}

// Typed feature switches. Runtime functionality consumes these without knowing
// which specialization tree granted them.
public static class LeviathanStarfireFlags
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
