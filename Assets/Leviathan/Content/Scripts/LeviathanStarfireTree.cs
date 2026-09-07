using static LeviathanTreeDsl;
using Starfire = LeviathanStarfireRuntime;

/// <summary>
/// Starfire specialization content only.
///
/// This file intentionally contains no Starfire mechanics. To add or rebalance
/// an ordinary node, point it at a knob exposed by LeviathanStarfireRuntime and
/// edit the numbers here. Unique behavior is implemented in LeviathanStarfire_v2.cs
/// and exposed here as a flag.
/// </summary>
public static class LeviathanStarfireTree
{
    public const string TreeId = "starfire";
    public const string RootNodeId = "starfire";

    private const string BreathShape = "starfire_breath_shape";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(
            TreeId,
            "Starfire",
            RootNodeId,
            10
        );

        tree.Add(Root(
            "Starfire",
            "Transforms the first equipped Primary Torch into a broad breath weapon with persistent Breath Power."
        ));

        // =====================================================================
        // LUNGS / ENDURANCE
        // =====================================================================

        tree.Add(Node(
            "Expanded Lungs",
            Requires("Starfire"),
            Increment(Starfire.Knobs.Damage, 8f),
            Increment(Starfire.Knobs.FullSizeHoldSeconds, 0.15f),
            Increment(Starfire.Knobs.RetreatSeconds, 0.35f),
            Increment(Starfire.Knobs.DamageFalloffCurveExponent, 0.20f),
            Increment(Starfire.Knobs.LengthFalloffCurveExponent, 0.20f),
            Increment(Starfire.Knobs.WidthFalloffCurveExponent, 0.20f)
        ));

        tree.Add(Node(
            "Measured Breathing",
            Requires("Expanded Lungs"),
            Increment(Starfire.Knobs.HeatGeneration, -12f),
            Increment(Starfire.Knobs.RecoveryRate, 20f),
            Increment(Starfire.Knobs.Damage, -5f)
        ));

        tree.Add(Node(
            "Rapid Recovery",
            Requires("Expanded Lungs"),
            Increment(Starfire.Knobs.RecoveryRate, 50f)
        ));

        tree.Add(Node(
            "Sustained Flame",
            Requires("Expanded Lungs"),
            Increment(Starfire.Knobs.ActiveDrainRate, -20f),
            Increment(Starfire.Knobs.DamageFalloffCurveExponent, 0.50f),
            Increment(Starfire.Knobs.LengthFalloffCurveExponent, 0.50f),
            Increment(Starfire.Knobs.WidthFalloffCurveExponent, 0.50f)
        ));

        // Ranks(...) / MultiplyTotals(...) automatically make this a 3-rank node.
        tree.Add(Major(
            "Deep Breath",
            RequiresAll(
                Requires("Expanded Lungs"),
                Requires("Overpressure")
            ),
            Increment(Starfire.Knobs.StartupDelaySeconds, 0.60f),
            MultiplyTotals(Starfire.Knobs.Damage, 1.45f, 1.90f, 2.35f),
            MultiplyTotals(Starfire.Knobs.Length, 1.45f, 1.90f, 2.35f),
            MultiplyTotals(Starfire.Knobs.Width, 1.45f, 1.90f, 2.35f)
        ));

        tree.Add(Major(
            "Big Succ",
            RequiresAll(
                Requires("Rapid Recovery"),
                Requires("Measured Breathing")
            ),
            Enable(Starfire.Flags.RechargePull),
            Increment(Starfire.Knobs.RechargePullRadius, 100f),
            Increment(Starfire.Knobs.RechargePullStrength, 1.00f),
            Increment(Starfire.Knobs.RechargePullFalloffExponent, 0.00f),
            Increment(Starfire.Knobs.RechargePullMaxSpeed, 0f)
        ));

        // =====================================================================
        // GEOMETRY / BREATH SHAPE
        // =====================================================================

        tree.Add(Node(
            "Extended Flame",
            Requires("Starfire"),
            Increment(Starfire.Knobs.Length, 15f)
        ));

        tree.Add(ExclusiveNode(
            "Broad Breath",
            Requires("Extended Flame"),
            BreathShape,
            Increment(Starfire.Knobs.Width, 30f),
            Increment(Starfire.Knobs.BaseFanHalfAngleDegrees, 4f),
            Increment(Starfire.Knobs.Damage, -10f)
        ));

        tree.Add(Node(
            "Narrow Reach",
            Requires("Extended Flame"),
            Increment(Starfire.Knobs.Length, 25f),
            Increment(Starfire.Knobs.Width, -15f)
        ));

        tree.Add(ExclusiveMajor(
            "Focused Breath",
            RequiresAll(
                Requires("Narrow Reach"),
                Requires("Overpressure")
            ),
            BreathShape,
            Increment(Starfire.Knobs.BaseFanHalfAngleDegrees, -7.50f),
            Multiply(Starfire.Knobs.Damage, 1.35f)
        ));

        // =====================================================================
        // DAMAGE / PRESSURE
        // =====================================================================

        tree.Add(Node(
            "Intensified Flame",
            Requires("Starfire"),
            Increment(Starfire.Knobs.Damage, 12f),
            Increment(Starfire.Knobs.HeatGeneration, 8f)
        ));

        tree.Add(Node(
            "Overpressure",
            Requires("Intensified Flame"),
            Increment(Starfire.Knobs.Damage, 15f),
            Increment(Starfire.Knobs.Length, 10f),
            Increment(Starfire.Knobs.HeatGeneration, 10f)
        ));

        tree.Add(Major(
            "Powerful Exhalation",
            Requires("Overpressure"),
            Multiply(Starfire.Knobs.Damage, 1.50f),
            Increment(Starfire.Knobs.DamageFalloffCurveExponent, -1.50f)
        ));

        tree.Add(Keystone(
            "Blast Wave",
            RequiresAll(
                Requires("Powerful Exhalation"),
                Requires("Deep Breath")
            ),
            "Replaces Starfire's continuous attack with a travelling 120-degree blast that consumes all remaining Breath Power.",
            Enable(Starfire.Flags.BlastWave),
            Increment(Starfire.Knobs.BlastWaveArcDegrees, 120f),
            Multiply(Starfire.Knobs.BlastWaveDamageMultiplier, 1.10f),
            Increment(Starfire.Knobs.BlastWaveSpeed, 200f),
            Increment(Starfire.Knobs.BlastWaveRange, 900f),
            Increment(Starfire.Knobs.BlastWaveWidth, 20f),
            Increment(Starfire.Knobs.BlastWaveVisualOpacity, 100f),
            Increment(Starfire.Knobs.BlastWaveStartRadius, 30f),
            Increment(Starfire.Knobs.BlastWaveEndRadius, 120f)
        ));

        tree.Validate();
        return tree;
    }
}
