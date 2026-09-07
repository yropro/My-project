using static LeviathanTreeDsl;

// Predator specialization config. Add nodes here; gameplay knobs/flags belong
// in LeviathanPredator.cs.
public static class LeviathanPredatorTree
{
    public const string TreeId = "predator";
    public const string RootNodeId = "predator";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(TreeId, "Predator", RootNodeId, 30);

        tree.Add(Root(
            RootNodeId,
            "Predator",
            "Granted automatically when Predator is unlocked in Evolution."
        ));

        tree.Validate();
        return tree;
    }
}
