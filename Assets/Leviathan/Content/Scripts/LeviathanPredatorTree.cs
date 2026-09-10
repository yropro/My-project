using static LeviathanTreeDsl;
using Predator = LeviathanPredatorRuntime;

/// <summary>
/// Temporary Predator specialization config while the skill mechanics are being
/// redesigned. One investable node, five ranks; gameplay implementation will
/// consume Predator.Knobs later.
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

        tree.Add(Node(
            "Predatory Instinct",
            5,
            Requires("Predator"),
            Increment(Predator.Knobs.Damage, 10f),
            Increment(Predator.Knobs.LungeDistance, 5f),
            Increment(Predator.Knobs.LungeDuration, -5f),
            Increment(Predator.Knobs.Cooldown, -5f),
            Increment(Predator.Knobs.CritChance, 2f)
        ));

        tree.Validate();
        return tree;
    }
}
