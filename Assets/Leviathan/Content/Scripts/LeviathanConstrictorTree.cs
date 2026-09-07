using static LeviathanTreeDsl;

// Constrictor specialization config. Add nodes here; gameplay knobs/flags belong
// in LeviathanConstrictor.cs.
public static class LeviathanConstrictorTree
{
    public const string TreeId = "constrictor";
    public const string RootNodeId = "constrictor";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(TreeId, "Constrictor", RootNodeId, 20);

        tree.Add(Root(
            RootNodeId,
            "Constrictor",
            "Granted automatically when Constrictor is unlocked in Evolution."
        ));

        tree.Validate();
        return tree;
    }
}
