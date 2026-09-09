using static LeviathanTreeDsl;

// Evolution is the root specialization tree. Native Evolution ranks grant Evolution
// Points. Rank 1 automatically activates the Leviathan chassis and Growth tree;
// nodes here spend Evolution Points to unlock the other Leviathan trees.
public static class LeviathanEvolutionTree
{
    public const string TreeId = "evolution";
    public const string RootNodeId = "evolution";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = NativeTree(
            TreeId,
            "Evolution",
            RootNodeId,
            0,
            LeviathanSpecializationCurrency.UpgradeKeyValue
        );

        tree.Add(Root(
            RootNodeId,
            "Evolution",
            "Evolution ranks grant Evolution Points. Evolution rank 1 automatically awakens the Leviathan chassis and Growth tree; spend Evolution Points here to unlock the other Leviathan skill trees."
        ));


        tree.Add(NodeId(
            "unlock_starfire",
            "Starfire",
            Requires("Evolution"),
            UnlockTree(LeviathanStarfireTree.TreeId)
        ));

        tree.Add(NodeId(
            "unlock_constrictor",
            "Constrictor",
            Requires("Evolution"),
            UnlockTree(LeviathanConstrictorTree.TreeId)
        ));

        tree.Add(NodeId(
            "unlock_predator",
            "Predator",
            Requires("Evolution"),
            UnlockTree(LeviathanPredatorTree.TreeId)
        ));

        tree.Add(NodeId(
            "unlock_behemoth",
            "Behemoth",
            Requires("Evolution"),
            UnlockTree(LeviathanBehemothTree.TreeId)
        ));

        tree.Add(NodeId(
            "unlock_stellar_converter",
            "Stellar Converter",
            Requires("Evolution"),
            UnlockTree(LeviathanStellarConverterTree.TreeId)
        ));

        tree.Validate();
        return tree;
    }
}
