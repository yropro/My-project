public static class LeviathanStarfireTree
{
    public const string TreeId = "starfire";
    public const string RootNodeId = "starfire_root";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Starfire",
            RootNodeId,
            10,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Starfire",
            "Granted automatically when Starfire is purchased in the Evolution tree. Future Starfire nodes branch from here."
        ));

        // Example syntax for future nodes. These are intentionally NOT active yet.
        //
        // tree.Add(LeviathanNode.Passive(
        //     "broad_breath",
        //     "Broad Breath",
        //     3,
        //     LeviathanReq.Rank(RootNodeId),
        //     "Broadens the Starfire cone.",
        //     LeviathanFx.Ranks(LeviathanStarfireKnobs.Width, 5f, 5f, 10f)
        // ));
        //
        // tree.Add(LeviathanNode.Passive(
        //     "focused_destruction",
        //     "Focused Destruction",
        //     3,
        //     LeviathanReq.Rank(RootNodeId),
        //     "Narrows the Starfire cone.",
        //     LeviathanFx.Increment(LeviathanStarfireKnobs.Width, -5f)
        // ));

        tree.Validate();
        return tree;
    }
}
