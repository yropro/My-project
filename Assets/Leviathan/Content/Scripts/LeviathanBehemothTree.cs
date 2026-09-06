public static class LeviathanBehemothTree
{
    public const string TreeId = "behemoth";
    public const string RootNodeId = "behemoth_root";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Behemoth",
            RootNodeId,
            40,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Behemoth",
            "Granted automatically when Behemoth is purchased in the Evolution tree."
        ));

        tree.Validate();
        return tree;
    }
}
