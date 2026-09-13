using static CoreTreeDsl;

// Constrictor specialization configuration only. Mechanics, tuning semantics,
// native integration and runtime state live in LeviathanConstrictor.cs.
public static class LeviathanConstrictorTree
{
    public const string TreeId = "constrictor";
    public const string RootNodeId = "constrictor";
    public const string MasteryNodeId = "constrictor_mastery";

    public static CoreSpecializationTree Create()
    {
        CoreSpecializationTree tree =
            Tree(TreeId, "Constrictor", RootNodeId, 20);

        // The framework requires an auto-granted structural root for an unlocked
        // specialization tree. It grants no Constrictor mechanics by itself;
        // Constrictor Mastery below is the one paid gameplay node.
        tree.Add(Root(
            RootNodeId,
            "Constrictor",
            "Unlocks the Constrictor specialization. Invest in Constrictor Mastery to convert an equipped Assault into Leviathan-wide contact damage."
        ));

        tree.Add(new CoreSpecializationNode(
            MasteryNodeId,
            "Constrictor Mastery",
            5,
            CoreSpecializationNodeType.Passive,
            CoreReq.Rank(RootNodeId),
            null,
            "Rank 1 converts the first equipped Assault into passive Leviathan-wide contact damage: two simultaneous section contacts equal one full native passive Assault aggregate. Additional contacts have symmetric diminishing returns and a complete wrap approaches two native passive aggregates. Ranks 2-5 add 25% Constrictor damage each. Every rank adds 2.5 percentage points Critical Chance, 5% Acceleration, Boost, Turn Speed and Maneuverability, and reduces Leviathan high-speed resistance by 10%. Passive status inheritance is 20% / 25% / 30% / 35% / 40% of the source Assault's resolved status chance.",
            1,
            false,

            // Semantic rank consumed by the mechanics resolver.
            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.Rank,
                1f,
                1f,
                1f,
                1f,
                1f
            ),

            // Rank totals: 1.00x / 1.25x / 1.50x / 1.75x / 2.00x.
            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.FinalDamagePercent,
                0f,
                25f,
                25f,
                25f,
                25f
            ),

            // Additive percentage points on the fully resolved source crit.
            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.CritChancePoints,
                2.5f,
                2.5f,
                2.5f,
                2.5f,
                2.5f
            ),

            // Absolute source-status fractions by accumulated rank:
            // .20 / .25 / .30 / .35 / .40.
            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.PassiveStatusFraction,
                0.20f,
                0.05f,
                0.05f,
                0.05f,
                0.05f
            ),

            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.AccelerationPercent,
                5f,
                5f,
                5f,
                5f,
                5f
            ),

            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.BoostPercent,
                5f,
                5f,
                5f,
                5f,
                5f
            ),

            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.TurnSpeedPercent,
                5f,
                5f,
                5f,
                5f,
                5f
            ),

            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.ManeuverabilityPercent,
                5f,
                5f,
                5f,
                5f,
                5f
            ),

            CoreFx.Ranks(
                LeviathanConstrictor.Knobs.AirResistanceReductionPoints,
                10f,
                10f,
                10f,
                10f,
                10f
            )
        ));

        tree.Validate();
        return tree;
    }
}
