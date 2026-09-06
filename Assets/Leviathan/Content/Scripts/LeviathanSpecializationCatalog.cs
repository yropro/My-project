public static class LeviathanSpecializationCatalog
{
    private static bool registered;

    public static void RegisterAll()
    {
        if (registered)
            return;

        registered = true;

        LeviathanSpecializationRegistry.Register(LeviathanEvolutionTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanStarfireTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanConstrictorTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanPredatorTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanBehemothTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanStellarConverterTree.Create());
    }
}
