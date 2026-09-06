public static class LeviathanStellarConverterTree
{
    public const string TreeId = "stellar_converter";
    public const string RootNodeId = "stellar_converter_root";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = new LeviathanSpecializationTree(
            TreeId,
            "Stellar Converter",
            RootNodeId,
            50,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );

        tree.Add(LeviathanNode.GrantedRoot(
            RootNodeId,
            "Stellar Converter",
            "Granted automatically when Stellar Converter is purchased in the Evolution tree."
        ));

        tree.Validate();
        return tree;
    }
}
