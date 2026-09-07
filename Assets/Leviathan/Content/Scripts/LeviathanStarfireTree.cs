public static class LeviathanStarfireTree
{
    public const string TreeId = "starfire";
    public const string RootNodeId = "starfire_root";

    public const string ExpandedLungsNodeId = "expanded_lungs";
    public const string MeasuredBreathingNodeId = "measured_breathing";
    public const string RapidRecoveryNodeId = "rapid_recovery";
    public const string SustainedFlameNodeId = "sustained_flame";
    public const string DeepBreathNodeId = "deep_breath";
    public const string BigSuccNodeId = "big_succ";

    public const string ExtendedFlameNodeId = "extended_flame";
    public const string BroadBreathNodeId = "broad_breath";
    public const string NarrowReachNodeId = "narrow_reach";
    public const string FocusedBreathNodeId = "focused_breath";

    public const string IntensifiedFlameNodeId = "intensified_flame";
    public const string OverpressureNodeId = "overpressure";
    public const string PowerfulExhalationNodeId = "powerful_exhalation";
    public const string BlastWaveNodeId = "blast_wave";

    private const string BreathShapeExclusiveGroup = "starfire_breath_shape";

    // -------------------------------------------------------------------------
    // NODE TUNING
    // -------------------------------------------------------------------------
    // All specialization balance lives here. Runtime Starfire code consumes only
    // named knobs/flags and never checks these node ids for behavior.

    // Lungs / endurance branch.
    private const float ExpandedLungsDamagePercent = 10f;
    private const float ExpandedLungsFullPowerSeconds = 0.15f;
    private const float ExpandedLungsFalloffSeconds = 0.35f;
    private const float ExpandedLungsFalloffCurve = 0.20f;

    private const float MeasuredBreathingHeatPercent = -15f;
    private const float MeasuredBreathingRecoveryPercent = 20f;
    private const float MeasuredBreathingDamagePercent = -5f;

    private const float RapidRecoveryPercent = 50f;

    private const float SustainedFlameDrainRatePercent = -20f;
    private const float SustainedFlameFalloffCurve = 0.50f;

    private const float DeepBreathWindupSecondsPerRank = 0.60f;
    private const float DeepBreathPowerPercentPerRank = 45f;

    private const float BigSuccRadiusMeters = 100f;
    private const float BigSuccStrength = 1.00f;
    private const float BigSuccFalloffExponent = 0.00f;
    private const float BigSuccMaxPullSpeedMetersPerSecond = 0f;

    // Geometry / breath-shape branch.
    private const float ExtendedFlameLengthPercent = 10f;

    private const float BroadBreathWidthPercent = 20f;
    private const float BroadBreathHalfAngleDegrees = 4f;
    private const float BroadBreathDamagePercent = -15f;

    private const float NarrowReachLengthPercent = 25f;
    private const float NarrowReachWidthPercent = -15f;

    private const float FocusedBreathHalfAngleDegrees = -7.50f;
    private const float FocusedBreathDamageMultiplier = 1.15f;

    // Damage / pressure branch.
    private const float IntensifiedFlameDamagePercent = 15f;
    private const float IntensifiedFlameHeatPercent = 10f;

    private const float OverpressureDamagePercent = 15f;
    private const float OverpressureLengthPercent = 10f;
    private const float OverpressureHeatPercent = 10f;

    private const float PowerfulExhalationDamageMultiplier = 1.50f;
    private const float PowerfulExhalationDamageFalloffCurve = -1.50f;

    // Blast Wave is a charge cash-out. The runtime integrates the actual
    // remaining resolved Starfire damage profile, then applies this multiplier.
    private const float BlastWaveArcDegrees = 120f;
    private const float BlastWaveDamageMultiplier = 0.80f;
    private const float BlastWaveSpeedMetersPerSecond = 180f;
    private const float BlastWaveFrontThicknessMeters = 20f;
    private const float BlastWaveVisualOpacityPercent = 45f;

    private static LeviathanSpecializationEffect DeepBreathPower(
        LeviathanSpecializationKnob knob)
    {
        float step = DeepBreathPowerPercentPerRank / 100f;
        return LeviathanFx.MultiplyTotals(
            knob,
            1f + step,
            1f + step * 2f,
            1f + step * 3f
        );
    }

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Starfire",
            RootNodeId,
            10,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Starfire",
            "Transforms the first equipped Primary Torch into a broad breath weapon."
        ));

        // =====================================================================
        // LUNGS / ENDURANCE
        // =====================================================================

        tree.Add(LeviathanNode.Passive(
            ExpandedLungsNodeId,
            "Expanded Lungs",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Slightly strengthens Starfire, extends the full-power portion, and makes its depleted tail last longer and fall off more gently.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.Damage, ExpandedLungsDamagePercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.FullSizeHoldSeconds, ExpandedLungsFullPowerSeconds),
            LeviathanFx.Increment(LeviathanStarfireKnobs.RetreatSeconds, ExpandedLungsFalloffSeconds),
            LeviathanFx.Increment(LeviathanStarfireKnobs.DamageFalloffCurveExponent, ExpandedLungsFalloffCurve),
            LeviathanFx.Increment(LeviathanStarfireKnobs.LengthFalloffCurveExponent, ExpandedLungsFalloffCurve),
            LeviathanFx.Increment(LeviathanStarfireKnobs.WidthFalloffCurveExponent, ExpandedLungsFalloffCurve)
        ));

        tree.Add(LeviathanNode.Passive(
            MeasuredBreathingNodeId,
            "Measured Breathing",
            1,
            LeviathanReq.Rank(ExpandedLungsNodeId),
            "Burns cooler and recovers Breath Power more efficiently, at a small cost to raw damage.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.HeatGeneration, MeasuredBreathingHeatPercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.RecoveryRate, MeasuredBreathingRecoveryPercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.Damage, MeasuredBreathingDamagePercent)
        ));

        tree.Add(LeviathanNode.Passive(
            RapidRecoveryNodeId,
            "Rapid Recovery",
            1,
            LeviathanReq.Rank(ExpandedLungsNodeId),
            "Breath Power recovers substantially faster while Starfire is not in use.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.RecoveryRate, RapidRecoveryPercent)
        ));

        tree.Add(LeviathanNode.Passive(
            SustainedFlameNodeId,
            "Sustained Flame",
            1,
            LeviathanReq.Rank(ExpandedLungsNodeId),
            "Consumes Breath Power more slowly and preserves damage, range, and width deeper into the depleted portion of the breath.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.ActiveDrainRate, SustainedFlameDrainRatePercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.DamageFalloffCurveExponent, SustainedFlameFalloffCurve),
            LeviathanFx.Increment(LeviathanStarfireKnobs.LengthFalloffCurveExponent, SustainedFlameFalloffCurve),
            LeviathanFx.Increment(LeviathanStarfireKnobs.WidthFalloffCurveExponent, SustainedFlameFalloffCurve)
        ));

        tree.Add(LeviathanNode.Major(
            DeepBreathNodeId,
            "Deep Breath",
            3,
            LeviathanReq.All(
                LeviathanReq.Rank(ExpandedLungsNodeId),
                LeviathanReq.Rank(OverpressureNodeId)
            ),
            "Adds 0.6 seconds of windup per rank. After all other Starfire modifiers are resolved, damage, range, and width are multiplied to 1.45x / 1.90x / 2.35x at ranks 1-3.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.StartupDelaySeconds, DeepBreathWindupSecondsPerRank),
            DeepBreathPower(LeviathanStarfireKnobs.Damage),
            DeepBreathPower(LeviathanStarfireKnobs.Length),
            DeepBreathPower(LeviathanStarfireKnobs.Width)
        ));

        tree.Add(LeviathanNode.Major(
            BigSuccNodeId,
            "Big Succ",
            1,
            LeviathanReq.All(
                LeviathanReq.Rank(RapidRecoveryNodeId),
                LeviathanReq.Rank(MeasuredBreathingNodeId)
            ),
            "While Breath Power is actively recovering, pulls hostile ships within 100 m toward the Leviathan using Gladiator Event Horizon-style force. The pull stops immediately at full Breath Power.",
            LeviathanFx.Flag(LeviathanStarfireFlags.RechargePull),
            LeviathanFx.Increment(LeviathanStarfireKnobs.RechargePullRadius, BigSuccRadiusMeters),
            LeviathanFx.Increment(LeviathanStarfireKnobs.RechargePullStrength, BigSuccStrength),
            LeviathanFx.Increment(LeviathanStarfireKnobs.RechargePullFalloffExponent, BigSuccFalloffExponent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.RechargePullMaxSpeed, BigSuccMaxPullSpeedMetersPerSecond)
        ));

        // =====================================================================
        // GEOMETRY / BREATH SHAPE
        // =====================================================================

        tree.Add(LeviathanNode.Passive(
            ExtendedFlameNodeId,
            "Extended Flame",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Increases Starfire range.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.Length, ExtendedFlameLengthPercent)
        ));

        tree.Add(LeviathanNode.PassiveExclusive(
            BroadBreathNodeId,
            "Broad Breath",
            1,
            LeviathanReq.Rank(ExtendedFlameNodeId),
            BreathShapeExclusiveGroup,
            "Spreads Starfire into a much broader cone at the cost of some damage. Mutually exclusive with Focused Breath.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.Width, BroadBreathWidthPercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.BaseFanHalfAngleDegrees, BroadBreathHalfAngleDegrees),
            LeviathanFx.Increment(LeviathanStarfireKnobs.Damage, BroadBreathDamagePercent)
        ));

        tree.Add(LeviathanNode.Passive(
            NarrowReachNodeId,
            "Narrow Reach",
            1,
            LeviathanReq.Rank(ExtendedFlameNodeId),
            "Greatly increases Starfire range at the cost of breath width.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.Length, NarrowReachLengthPercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.Width, NarrowReachWidthPercent)
        ));

        tree.Add(LeviathanNode.MajorExclusive(
            FocusedBreathNodeId,
            "Focused Breath",
            1,
            LeviathanReq.All(
                LeviathanReq.Rank(NarrowReachNodeId),
                LeviathanReq.Rank(OverpressureNodeId)
            ),
            BreathShapeExclusiveGroup,
            "Greatly narrows Starfire's fan angle and multiplies final resolved damage. Mutually exclusive with Broad Breath.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.BaseFanHalfAngleDegrees, FocusedBreathHalfAngleDegrees),
            LeviathanFx.Multiply(LeviathanStarfireKnobs.Damage, FocusedBreathDamageMultiplier)
        ));

        // =====================================================================
        // DAMAGE / PRESSURE
        // =====================================================================

        tree.Add(LeviathanNode.Passive(
            IntensifiedFlameNodeId,
            "Intensified Flame",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Burns hotter for increased damage, generating slightly more heat.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.Damage, IntensifiedFlameDamagePercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.HeatGeneration, IntensifiedFlameHeatPercent)
        ));

        tree.Add(LeviathanNode.Passive(
            OverpressureNodeId,
            "Overpressure",
            1,
            LeviathanReq.Rank(IntensifiedFlameNodeId),
            "Pushes the Torch harder for more damage and reach, at the cost of additional heat generation.",
            LeviathanFx.Increment(LeviathanStarfireKnobs.Damage, OverpressureDamagePercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.Length, OverpressureLengthPercent),
            LeviathanFx.Increment(LeviathanStarfireKnobs.HeatGeneration, OverpressureHeatPercent)
        ));

        tree.Add(LeviathanNode.Major(
            PowerfulExhalationNodeId,
            "Powerful Exhalation",
            1,
            LeviathanReq.Rank(OverpressureNodeId),
            "Greatly increases initial Starfire damage, but damage collapses much faster once the full-power portion is exhausted.",
            LeviathanFx.Multiply(LeviathanStarfireKnobs.Damage, PowerfulExhalationDamageMultiplier),
            LeviathanFx.Increment(LeviathanStarfireKnobs.DamageFalloffCurveExponent, PowerfulExhalationDamageFalloffCurve)
        ));

        tree.Add(LeviathanNode.Keystone(
            BlastWaveNodeId,
            "Blast Wave",
            LeviathanReq.All(
                LeviathanReq.Rank(PowerfulExhalationNodeId),
                LeviathanReq.Rank(DeepBreathNodeId)
            ),
            string.Empty,
            "Replaces Starfire's continuous attack with a travelling 120-degree blast wave. Firing consumes all remaining Breath Power and deals 80% of the total damage that the consumed, fully-modified breath would have produced normally.",
            LeviathanFx.Flag(LeviathanStarfireFlags.BlastWave),
            LeviathanFx.Increment(LeviathanStarfireKnobs.BlastWaveArcDegrees, BlastWaveArcDegrees),
            LeviathanFx.Multiply(LeviathanStarfireKnobs.BlastWaveDamageMultiplier, BlastWaveDamageMultiplier),
            LeviathanFx.Increment(LeviathanStarfireKnobs.BlastWaveSpeed, BlastWaveSpeedMetersPerSecond),
            LeviathanFx.Increment(LeviathanStarfireKnobs.BlastWaveWidth, BlastWaveFrontThicknessMeters),
            LeviathanFx.Increment(LeviathanStarfireKnobs.BlastWaveVisualOpacity, BlastWaveVisualOpacityPercent)
        ));

        tree.Validate();
        return tree;
    }
}
