using HarmonyLib;
using static LeviathanTreeDsl;
using Converter = LeviathanStellarConverter;

/// <summary>
/// Stellar Converter specialization configuration.
/// Gameplay baselines, runtime mechanics, knobs and flags live in
/// LeviathanStellarConverter.cs. This file owns player choices and layout only.
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
        // Multiple percentage effects add against that same baseline.

        // =====================================================================
        // RAPID / CONTINUOUS
        // =====================================================================

        tree.Add(Major(
            "Accelerated Conversion",
            Requires("Stellar Converter"),
            Ranks(Converter.Knobs.ChargeTime, -0.30f, -0.30f),
            Ranks(Converter.Knobs.FinalDamagePercent, -30f, -30f),
            Ranks(Converter.Knobs.WidthMultiplier, -3.00f, -3.00f),
            Ranks(Converter.Knobs.StatusChance, 7.50f, 7.50f)
        ));

        tree.Add(Keystone(
            "Continuous Conversion",
            Requires("Accelerated Conversion", 2),
            PrimaryPath,
            "Removes charge/pulse cycling and fires continuously.",
            // Preserve the former Continuous baseline through ordinary knobs.
            // The Continuous flag itself now owns behavior only.
            Increment(Converter.Knobs.ChargeTime, -0.40f),
            Increment(Converter.Knobs.FinalDamagePercent, -8.75f),
            Increment(Converter.Knobs.WidthMultiplier, -2.00f),
            Increment(Converter.Knobs.CritChanceMultiplier, -0.10f),
            Increment(Converter.Knobs.RandomBasicStatusChance, 5f),
            Enable(Converter.Flags.Continuous)
        ));

        tree.Add(Node(
            "Spectrum Saturation",
            1,
            Requires("Continuous Conversion"),
            "Raises Continuous Conversion's random basic-status roll to about 35% per second at the native 5 Hz tick rate, and adds a separate 2.5% per-tick roll for a random basic status other than the firing laser's damage type.",
            // 8.254944% per tick produces 35% chance of at least one proc across
            // five independent native beam ticks: 1 - (1 - p)^5 = 0.35.
            Increment(Converter.Knobs.RandomBasicStatusChance, 3.254944f),
            Increment(Converter.Knobs.OffElementRandomStatusChance, 2.5f),
            Enable(Converter.Flags.SpectrumSaturation)
        ));

        // =====================================================================
        // CHAINING
        // =====================================================================

        tree.Add(Keystone(
            "Arc Cascade",
            Requires("Stellar Converter"),
            ManifestationPath,
            "Trades piercing and some damage for native chain behavior.",
            Increment(Converter.Knobs.FinalDamagePercent, -25f),
            Ranks(Converter.Knobs.ChainTargets, 2f),
            Enable(Converter.Flags.ArcCascade)
        ));

        tree.Add(Major(
            "Fractal Cascade",
            Requires("Arc Cascade"),
            Increment(Converter.Knobs.FinalDamagePercent, -5f),
            Ranks(Converter.Knobs.ChainTargets, 1f),
            Enable(Converter.Flags.FractalCascade)
        ));

        tree.Add(Keystone(
            "Forking",
            Requires("Fractal Cascade"),
            "Replaces the single native chain with three 70% fork beams in a cone. Each fork receives half the final resolved chain count rounded up. Across the whole fork graph, a previously hit target may be revisited once; then a globally new target must be hit before any branch may revisit again.",
            Increment(Converter.Knobs.ForkTargets, 3f),
            Increment(Converter.Knobs.ForkDamage, 0.70f),
            Increment(Converter.Knobs.ForkChainFraction, 0.5f),
            Increment(Converter.Knobs.ForkConeDegrees, 90f),
            Enable(Converter.Flags.Forking)
        ));

        // =====================================================================
        // STRUCTURAL CONDUCTION
        // =====================================================================

        tree.Add(Node(
            "Conduction",
            1,
            Requires("Stellar Converter"),
            "Stellar Converter deals +1% damage per active body segment.",
            Increment(Converter.Knobs.BodySegmentDamagePercent, 1f),
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
            Increment(Converter.Knobs.TailDamagePercent, 10f),
            Enable(Converter.Flags.Convergence)
        ));

        // =====================================================================
        // STATUS
        // =====================================================================

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
            "Contagion",
            1,
            Requires("Ionized Reach"),
            "Converter kills gain +10 percentage points of native negative-status shedding, spreading transferable debuffs to nearby allies of the slain target.",
            Increment(Converter.Knobs.DebuffSpreadOnKillChance, 10f)
        ));

        // =====================================================================
        // RANGE / HEAT
        // =====================================================================

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
            Ranks(Converter.Knobs.FinalDamagePercent, 20f, 25f),
            Ranks(Converter.Knobs.WidthMultiplier, 5.00f, 10.00f),
            Ranks(Converter.Knobs.FinalRangePercent, 7.5f, 7.5f)
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

        tree.Add(Keystone(
            "Singularity",
            Requires("Deep Capacitors", 2),
            ManifestationPath,
            "Replaces the beam with a slow drifting gravity well. Its Halo deals sustained Converter damage while nearby enemies are pulled harder as they approach the center.",
            Enable(Converter.Flags.Singularity)
        ));

        // =====================================================================
        // CRITICAL
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

        tree.Validate();
        return tree;
    }

    /// <summary>
    /// Applies the authored Converter layout exported from the tree workbench.
    /// Auto-layout still owns edge construction; this only replaces coordinates.
    /// </summary>
    public static void ApplyAuthoredLayout(LeviathanSpecializationLayout layout)
    {
        if (layout == null)
            return;

        SetPosition(layout, RootNodeId, 0f, 0f);

        SetPosition(layout, "accelerated_conversion", 250f, -495f);
        SetPosition(layout, "continuous_conversion", 510f, -570f);
        SetPosition(layout, "spectrum_saturation", 760f, -570f);

        SetPosition(layout, "arc_cascade", 250f, -330f);
        SetPosition(layout, "fractal_cascade", 500f, -412f);
        SetPosition(layout, "forking", 750f, -412f);

        SetPosition(layout, "conduction", 250f, -170f);
        SetPosition(layout, "internal_heat_sinks", 500f, -247f);
        SetPosition(layout, "convergence", 750f, -250f);

        SetPosition(layout, "ionized_focusing", 250f, 0f);
        SetPosition(layout, "ionized_reach", 500f, -82f);
        SetPosition(layout, "contagion", 750f, -82f);

        SetPosition(layout, "long_focus_lens", 250f, 165f);
        SetPosition(layout, "focal_compression", 500f, 83f);
        SetPosition(layout, "thermal_overdrive", 500f, 250f);

        SetPosition(layout, "power_accumulation", 250f, 330f);
        SetPosition(layout, "deep_capacitors", 500f, 413f);
        SetPosition(layout, "dying_star", 740f, -90f);
        SetPosition(layout, "event_horizon", 750f, 60f);
        SetPosition(layout, "singularity", 750f, 210f);

        SetPosition(layout, "resonant_optics", 250f, 495f);
        SetPosition(layout, "resonant_feedback", 500f, 578f);

        // Leave comfortable margins around the authored coordinate range.
        layout.Width = 1180f;
        layout.Height = 1420f;
    }

    private static void SetPosition(
        LeviathanSpecializationLayout layout,
        string nodeId,
        float x,
        float y)
    {
        LeviathanSpecializationLayoutNode node;
        if (layout.Nodes.TryGetValue(nodeId, out node) && node != null)
        {
            node.Position = new LeviathanLayoutPoint(x, y);
        }
    }
}

/// <summary>
/// The generic framework remains generic. Converter supplies its own authored
/// presentation coordinates after the normal auto-layout has built nodes/edges.
/// </summary>
[HarmonyPatch(typeof(LeviathanSpecializationAutoLayout), "Build")]
public static class LeviathanStellarConverterLayoutPatch
{
    public static void Postfix(
        LeviathanSpecializationTree __0,
        ref LeviathanSpecializationLayout __result)
    {
        if (__0 == null ||
            __result == null ||
            __0.Id != LeviathanStellarConverterTree.TreeId)
        {
            return;
        }

        LeviathanStellarConverterTree.ApplyAuthoredLayout(__result);
    }
}
