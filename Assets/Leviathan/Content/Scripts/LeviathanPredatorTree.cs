using static LeviathanTreeDsl;
using Predator = LeviathanPredatorRuntime;

/// <summary>
/// Predator tree placeholder while the full 30-40 node topology is designed.
///
/// Predator.cs already exposes the intended long-term knobs/flags. This tree
/// deliberately remains one simple five-rank node so today's scaffolding does
/// not prematurely lock in branch layout, exclusivity, or crossover placement.
/// </summary>
public static class LeviathanPredatorTree
{
    public const string TreeId = "predator";
    public const string RootNodeId = "predator";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(
            TreeId,
            "Predator",
            RootNodeId,
            30
        );

        tree.Add(Root(
            RootNodeId,
            "Predator",
            "Granted automatically when Predator is unlocked in Evolution."
        ));

        // Temporary progression only. The agreed Predator baseline lives in
        // Predator.Tuning; these increments exist so the current five-rank tree
        // remains testable until the full topology replaces it.
        tree.Add(Node(
            "Predatory Instinct",
            5,
            Requires("Predator"),
            Increment(Predator.Knobs.Damage, 10f),
            Increment(Predator.Knobs.LungeDistance, 5f),
            Increment(Predator.Knobs.LungeSpeed, 5f),
            Increment(Predator.Knobs.Cooldown, -5f),
            Increment(Predator.Knobs.CritChance, 2f)
        ));

        tree.Validate();
        return tree;
    }
}
