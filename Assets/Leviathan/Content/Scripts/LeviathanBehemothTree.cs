using static LeviathanTreeDsl;

/// <summary>
/// Behemoth specialization definition.
///
/// Behemoth is currently only its native five-rank core skill. The tree keeps
/// a granted root so the Evolution unlock and future specialization branches
/// already have a stable tree identity, but there are no player-purchased
/// Behemoth specialization nodes in this basic refactor.
/// </summary>
public static class LeviathanBehemothTree
{
    public const string TreeId = "behemoth";
    public const string RootNodeId = "behemoth";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(
            TreeId,
            "Behemoth",
            RootNodeId,
            40
        );

        tree.Add(Root(
            RootNodeId,
            "Behemoth",
            "Behemoth's current behavior is provided by its five native ranks."
        ));

        tree.Validate();
        return tree;
    }
}
