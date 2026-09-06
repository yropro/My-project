public static class LeviathanPredatorTree
{
    public const string TreeId = "predator";
    public const string RootNodeId = "predator_root";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Predator",
            RootNodeId,
            30,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Predator",
            "Granted automatically when Predator is purchased in the Evolution tree."
        ));

        tree.Validate();
        return tree;
    }
}
