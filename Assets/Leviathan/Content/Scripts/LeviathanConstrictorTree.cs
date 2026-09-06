public static class LeviathanConstrictorTree
{
    public const string TreeId = "constrictor";
    public const string RootNodeId = "constrictor_root";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Constrictor",
            RootNodeId,
            20,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Constrictor",
            "Granted automatically when Constrictor is purchased in the Evolution tree."
        ));

        tree.Validate();
        return tree;
    }
}
