public static class LeviathanEvolutionTree
{
    public const string TreeId = "evolution";
    public const string RootNodeId = "evolution_root";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Evolution",
            RootNodeId,
            0,
            LeviathanTreeUnlockKind.NativeUpgrade,
            LeviathanSpecializationCurrency.UpgradeKeyValue
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Evolution",
            "The root of Leviathan specialization. Evolution ranks grant Growth Points; this tree spends them to unlock individual Leviathan skill trees."
        ));

        tree.Add(LeviathanNode.Passive(
            "unlock_starfire",
            "Starfire",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Unlocks Starfire and grants the root of its specialization tree.",
            LeviathanFx.UnlockTree(LeviathanStarfireTree.TreeId)
        ));

        tree.Add(LeviathanNode.Passive(
            "unlock_constrictor",
            "Constrictor",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Unlocks Constrictor and grants the root of its specialization tree.",
            LeviathanFx.UnlockTree(LeviathanConstrictorTree.TreeId)
        ));

        tree.Add(LeviathanNode.Passive(
            "unlock_predator",
            "Predator",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Unlocks Predator and grants the root of its specialization tree.",
            LeviathanFx.UnlockTree(LeviathanPredatorTree.TreeId)
        ));

        tree.Add(LeviathanNode.Passive(
            "unlock_behemoth",
            "Behemoth",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Unlocks Behemoth and grants the root of its specialization tree.",
            LeviathanFx.UnlockTree(LeviathanBehemothTree.TreeId)
        ));

        tree.Add(LeviathanNode.Passive(
            "unlock_stellar_converter",
            "Stellar Converter",
            1,
            LeviathanReq.Rank(RootNodeId),
            "Unlocks Stellar Converter and grants the root of its specialization tree.",
            LeviathanFx.UnlockTree(LeviathanStellarConverterTree.TreeId)
        ));

        tree.Validate();
        return tree;
    }
}
