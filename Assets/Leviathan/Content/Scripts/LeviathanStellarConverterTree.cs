using static LeviathanTreeDsl;
using Converter = LeviathanStellarConverter;

/// <summary>
/// Stellar Converter specialization config only.
/// Gameplay baselines, knobs, flags and behavior live in LeviathanStellarConverter_v2.cs.
/// </summary>
public static class LeviathanStellarConverterTree
{
    public const string TreeId = "stellar_converter";
    public const string RootNodeId = "stellar_converter";

    private const string PrimaryPath = "stellar_converter.primary_path";
    private const string ManifestationPath = "stellar_converter.manifestation";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(
            TreeId,
            "Stellar Converter",
            RootNodeId,
            20
        );

        tree.Add(Root(
            RootNodeId,
            "Stellar Converter",
            "Baseline charged Converter profile."
        ));

        // Damage/range percentages modify the finished Converter baseline.
        // Example: +10% range means Baseline Converter Range * 1.10.
        // Multiple tree percentage bonuses add against that same baseline.

        // =====================================================================
        // RAPID / CONTINUOUS
        // =====================================================================

        tree.Add(ExclusiveMajor(
            "Accelerated Conversion",
            Requires("Stellar Converter"),
            PrimaryPath,
            Ranks(Converter.Knobs.ChargeTime, -0.30f, -0.30f),
            Ranks(Converter.Knobs.FinalDamagePercent, -30f, -30f),
            Ranks(Converter.Knobs.WidthMultiplier, -3.00f, -3.00f),
            Ranks(Converter.Knobs.StatusChance, 7.50f, 7.50f)
        ));

        tree.Add(Keystone(
            "Continuous Conversion",
            Requires("Accelerated Conversion", 2),
            "Removes charge/pulse cycling and fires continuously with its special continuous profile.",
            Enable(Converter.Flags.Continuous)
        ));

        // =====================================================================
        // CHAINING
        // =====================================================================

        tree.Add(Keystone(
            "Arc Cascade",
            Requires("Stellar Converter"),
            PrimaryPath,
            "Trades piercing and some damage for native chain behavior.",
            Increment(Converter.Knobs.FinalDamagePercent, -30f),
            Ranks(Converter.Knobs.ChainTargets, 2f),
            Enable(Converter.Flags.ArcCascade)
        ));

        tree.Add(Major(
            "Fractal Cascade",
            Requires("Arc Cascade"),
            Increment(Converter.Knobs.FinalDamagePercent, -10f),
            Ranks(Converter.Knobs.ChainTargets, 1f),
            Enable(Converter.Flags.FractalCascade)
        ));

        // =====================================================================
        // HEAVY / OVERCHARGE
        // =====================================================================

        tree.Add(ExclusiveMajor(
            "Power Accumulation",
            Requires("Stellar Converter"),
            PrimaryPath,
            Ranks(Converter.Knobs.ChargeTime, 0.25f, 0.25f),
            Ranks(Converter.Knobs.PulseDuration, 0.10f, 0.10f),
            Ranks(Converter.Knobs.FinalDamagePercent, 20f, 20f),
            Ranks(Converter.Knobs.WidthMultiplier, 5.00f, 5.00f),
            Ranks(Converter.Knobs.FinalRangePercent, 12.5f, 12.5f),
            Ranks(Converter.Knobs.Piercing, 0f, 1f)
        ));

        tree.Add(Major(
            "Deep Capacitors",
            Requires("Power Accumulation", 2),
            Ranks(Converter.Knobs.ChargeTime, 0.25f, 0.25f),
            Ranks(Converter.Knobs.PulseDuration, 0.10f, 0.10f),
            Ranks(
                Converter.Knobs.FinalDamagePercent,
                20f,
                25f
            ),
            Ranks(Converter.Knobs.WidthMultiplier, 5.00f, 10.00f),
            Ranks(Converter.Knobs.FinalRangePercent, 7.5f, 7.5f)
        ));

        tree.Add(Keystone(
            "Singularity",
            Requires("Deep Capacitors", 2),
            ManifestationPath,
            "Replaces the beam with a slow drifting gravity well. Its Halo deals sustained Converter damage while nearby enemies are pulled harder as they approach the center.",
            Enable(Converter.Flags.Singularity)
        ));

        tree.Add(Keystone(
            "Dying Star",
            Requires("Deep Capacitors", 2),
            ManifestationPath,
            "Replaces the beam with a slow gravity seed that gathers enemies. Hold after launch to mature it; release to detonate early with reduced damage and radius.",
            Enable(Converter.Flags.DyingStar)
        ));

        tree.Add(Keystone(
            "Event Horizon",
            Requires("Deep Capacitors", 2),
            ManifestationPath,
            "Keeps the Converter beam. An invisible gravity corridor grows down the beam and drags enemies toward its independently-moving tip.",
            Enable(Converter.Flags.EventHorizon)
        ));

        // =====================================================================
        // UTILITY
        // =====================================================================

        tree.Add(Node(
            "Resonant Optics",
            2,
            Requires("Stellar Converter"),
            Increment(Converter.Knobs.CritChance, 5f)
        ));

        tree.Add(Node(
            "Resonant Feedback",
            Requires("Resonant Optics", 1),
            Increment(Converter.Knobs.FinalDamagePercent, 5f),
            Increment(Converter.Knobs.CritChance, 2.5f)
        ));

        tree.Add(Node(
            "Ionized Focusing",
            2,
            Requires("Stellar Converter"),
            Increment(Converter.Knobs.StatusChance, 5f)
        ));

        tree.Add(Node(
            "Ionized Reach",
            Requires("Ionized Focusing", 1),
            Increment(Converter.Knobs.FinalRangePercent, 5f),
            Increment(Converter.Knobs.StatusChance, 2.5f)
        ));

        tree.Add(Node(
            "Long-Focus Lens",
            2,
            Requires("Stellar Converter"),
            Increment(Converter.Knobs.FinalRangePercent, 10f)
        ));

        tree.Add(Node(
            "Focal Compression",
            Requires("Long-Focus Lens", 1),
            Increment(Converter.Knobs.FinalRangePercent, -10f),
            Increment(Converter.Knobs.FinalDamagePercent, 15f)
        ));

        tree.Add(Node(
            "Thermal Overdrive",
            Requires("Long-Focus Lens", 2),
            Increment(Converter.Knobs.HeatGenerationPercent, 25f),
            Increment(Converter.Knobs.FinalRangePercent, 10f),
            Increment(Converter.Knobs.FinalDamagePercent, 10f)
        ));

        // =====================================================================
        // STRUCTURAL CONDUCTION
        // =====================================================================

        tree.Add(Node(
            "Conduction",
            1,
            Requires("Stellar Converter"),
            "Stellar Converter deals +1% damage per active body segment.",
            Enable(Converter.Flags.Conduction)
        ));

        tree.Add(Node(
            "Internal Heat Sinks",
            1,
            Requires("Conduction"),
            "Stellar Converter generates 10% less heat relative to its baseline.",
            Increment(Converter.Knobs.HeatGenerationPercent, -10f)
        ));

        tree.Add(Keystone(
            "Convergence",
            Requires("Internal Heat Sinks"),
            "Stellar Converter deals +10% damage per active tail. Each tail channels a secondary Converter beam into the head while charging and firing.",
            Enable(Converter.Flags.Convergence)
        ));

        tree.Validate();
        return tree;
    }
}
