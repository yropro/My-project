using static LeviathanTreeDsl;
using Behemoth = LeviathanBehemoth;

// Behemoth specialization config only. Temporal Dive implementation, state,
// networking and tuning ownership remain in LeviathanBehemoth.cs.
public static class LeviathanBehemothTree
{
    public const string TreeId = "behemoth";
    public const string RootNodeId = "behemoth";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(TreeId, "Behemoth", RootNodeId, 40);

        tree.Add(Root(
            RootNodeId,
            "Behemoth",
            "Granted automatically when Behemoth is unlocked in Evolution."
        ));

        tree.Add(Keystone(
            "Temporal Dive",
            Requires("Behemoth"),
            Enable(Behemoth.Flags.TemporalDive)
        ));

        tree.Validate();
        return tree;
    }
}
