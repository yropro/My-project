using System;

public static class LeviathanStarfireSpecialization
{
    public const string TreeId = "starfire";
    public const string RootNodeId = "starfire_root";

    public static class Stats
    {
        public const string Width = "starfire.width";
        public const string Length = "starfire.length";
        public const string Duration = "starfire.duration";
        public const string RechargeTime = "starfire.recharge_time";
        public const string Damage = "starfire.damage";
        public const string DebuffChance = "starfire.debuff_chance";
    }

    public static class Flags
    {
        public const string Enabled = "starfire.enabled";
        public const string DeepBreath = "starfire.deep_breath";
        public const string SteadyBreathing = "starfire.steady_breathing";
        public const string ForcefulExhalation = "starfire.forceful_exhalation";
    }

    public const string BreathStyleGroup = "starfire.breath_style";

    public static LeviathanSpecializationTree Create(int ownerUpgradeKey)
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Starfire",
            ownerUpgradeKey
        );

        // Starfire itself is now purchased inside the specialization web.
        // Evolution only supplies Growth Points; the old native Starfire skill is
        // not required for this path.
        tree.Add(new LeviathanSpecializationNode(
            RootNodeId,
            "Starfire Root",
            1,
            LeviathanSpecializationNodeType.Root,
            LeviathanReq.None,
            null,
            "Unlocks Starfire and opens its specialization web.",
            LeviathanSpecializationEffect.Flag(Flags.Enabled)
        ));

        // Initial prototype values are deliberately centralized here. Changing
        // balance or adding a branch should not require touching renderer code.

        tree.Add(new LeviathanSpecializationNode(
            "expanded_lungs",
            "Expanded Lungs",
            3,
            LeviathanSpecializationNodeType.Passive,
            LeviathanReq.Rank(RootNodeId, 1),
            null,
            "A broader initial Starfire breath.",
            LeviathanSpecializationEffect.Percent(Stats.Width, 0.10f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "long_reach",
            "Long Reach",
            3,
            LeviathanSpecializationNodeType.Passive,
            LeviathanReq.Rank(RootNodeId, 1),
            null,
            "Extends Starfire farther from the Leviathan.",
            LeviathanSpecializationEffect.Percent(Stats.Length, 0.10f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "sustained_flame",
            "Sustained Flame",
            3,
            LeviathanSpecializationNodeType.Major,
            LeviathanReq.Any(
                LeviathanReq.Rank("expanded_lungs", 1),
                LeviathanReq.Rank("long_reach", 1)
            ),
            null,
            "Trades a little peak output for a breath that remains useful longer.",
            LeviathanSpecializationEffect.Percent(Stats.Duration, 0.10f),
            LeviathanSpecializationEffect.Percent(Stats.Damage, -0.03f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "deep_breath",
            "Deep Breath",
            1,
            LeviathanSpecializationNodeType.Keystone,
            LeviathanReq.All(
                LeviathanReq.Rank("expanded_lungs", 2),
                LeviathanReq.Rank("long_reach", 2),
                LeviathanReq.Rank("sustained_flame", 2)
            ),
            BreathStyleGroup,
            "Adds a wind-up before Starfire fires, then heavily juices its geometry and output.",
            LeviathanSpecializationEffect.Percent(Stats.Width, 0.25f),
            LeviathanSpecializationEffect.Percent(Stats.Length, 0.20f),
            LeviathanSpecializationEffect.Percent(Stats.Duration, 0.25f),
            LeviathanSpecializationEffect.Percent(Stats.Damage, 0.20f),
            LeviathanSpecializationEffect.Flag(Flags.DeepBreath)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "rapid_recovery",
            "Rapid Recovery",
            3,
            LeviathanSpecializationNodeType.Passive,
            LeviathanReq.Rank(RootNodeId, 1),
            null,
            "Shortens the time before Starfire is ready again.",
            LeviathanSpecializationEffect.Percent(Stats.RechargeTime, -0.10f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "measured_breath",
            "Measured Breath",
            3,
            LeviathanSpecializationNodeType.Major,
            LeviathanReq.Any(
                LeviathanReq.Rank("rapid_recovery", 1),
                LeviathanReq.Rank("sustained_flame", 1)
            ),
            null,
            "Improves uptime and consistency at the expense of some peak damage.",
            LeviathanSpecializationEffect.Percent(Stats.Duration, 0.08f),
            LeviathanSpecializationEffect.Percent(Stats.RechargeTime, -0.06f),
            LeviathanSpecializationEffect.Percent(Stats.Damage, -0.04f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "steady_breathing",
            "Steady Breathing",
            1,
            LeviathanSpecializationNodeType.Keystone,
            LeviathanReq.All(
                LeviathanReq.Rank("rapid_recovery", 2),
                LeviathanReq.Rank("measured_breath", 2)
            ),
            BreathStyleGroup,
            "Detunes Starfire's peak profile in exchange for substantially steadier uptime.",
            LeviathanSpecializationEffect.Percent(Stats.Width, -0.10f),
            LeviathanSpecializationEffect.Percent(Stats.Length, -0.08f),
            LeviathanSpecializationEffect.Percent(Stats.Damage, -0.08f),
            LeviathanSpecializationEffect.Percent(Stats.Duration, 0.30f),
            LeviathanSpecializationEffect.Percent(Stats.RechargeTime, -0.20f),
            LeviathanSpecializationEffect.Flag(Flags.SteadyBreathing)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "pressure",
            "Pressure",
            3,
            LeviathanSpecializationNodeType.Passive,
            LeviathanReq.Rank(RootNodeId, 1),
            null,
            "Converts sustained breath into harder immediate output.",
            LeviathanSpecializationEffect.Percent(Stats.Damage, 0.12f),
            LeviathanSpecializationEffect.Percent(Stats.Duration, -0.06f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "searing_breath",
            "Searing Breath",
            3,
            LeviathanSpecializationNodeType.Major,
            LeviathanReq.Any(
                LeviathanReq.Rank("pressure", 1),
                LeviathanReq.Rank("measured_breath", 1)
            ),
            null,
            "A crossover node that improves both direct output and status application.",
            LeviathanSpecializationEffect.Percent(Stats.Damage, 0.06f),
            LeviathanSpecializationEffect.Percent(Stats.DebuffChance, 0.08f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "violent_release",
            "Violent Release",
            2,
            LeviathanSpecializationNodeType.Major,
            LeviathanReq.All(
                LeviathanReq.Rank("pressure", 2),
                LeviathanReq.Rank("searing_breath", 1)
            ),
            null,
            "Further compresses Starfire toward a short, violent attack window.",
            LeviathanSpecializationEffect.Percent(Stats.Damage, 0.15f),
            LeviathanSpecializationEffect.Percent(Stats.Duration, -0.12f),
            LeviathanSpecializationEffect.Percent(Stats.DebuffChance, 0.05f)
        ));

        tree.Add(new LeviathanSpecializationNode(
            "forceful_exhalation",
            "Forceful Exhalation",
            1,
            LeviathanSpecializationNodeType.Keystone,
            LeviathanReq.All(
                LeviathanReq.Rank("pressure", 3),
                LeviathanReq.Rank("violent_release", 2)
            ),
            BreathStyleGroup,
            "A very brief, rapidly collapsing Starfire blast with an outsized opening hit.",
            LeviathanSpecializationEffect.Percent(Stats.Damage, 0.25f),
            LeviathanSpecializationEffect.Percent(Stats.Duration, -0.35f),
            LeviathanSpecializationEffect.Flag(Flags.ForcefulExhalation)
        ));

        tree.Validate();
        return tree;
    }

    public static string GetStatName(string statId)
    {
        if (statId == Stats.Width) return "Width";
        if (statId == Stats.Length) return "Length";
        if (statId == Stats.Duration) return "Duration";
        if (statId == Stats.RechargeTime) return "Recharge Time";
        if (statId == Stats.Damage) return "Damage";
        if (statId == Stats.DebuffChance) return "Debuff Chance";
        if (statId == Flags.Enabled) return "Starfire";
        if (statId == Flags.DeepBreath) return "Deep Breath behavior";
        if (statId == Flags.SteadyBreathing) return "Steady Breathing behavior";
        if (statId == Flags.ForcefulExhalation) return "Forceful Exhalation behavior";
        return statId;
    }
}
