using StarVortex;
using System;

/// <summary>
/// Leviathan-specific progression and presentation plugged into the shared Core
/// specialization engine. Backend accounting remains generic Specialization
/// Points; the player-facing currency is Evolution Points.
/// </summary>
public sealed class LeviathanSpecializationPolicy : ICoreSpecializationPolicy
{
    public CoreClassId ClassId
    {
        get { return CoreClassId.Leviathan; }
    }

    public string ProgressionName
    {
        get { return LeviathanSpecializationCurrency.SkillName; }
    }

    public string PointCurrencyName
    {
        get { return LeviathanSpecializationCurrency.CurrencyName; }
    }

    public void EnsurePrerequisitesRegistered()
    {
        LeviathanSpecializationCurrency.EnsureRegistered();
    }

    public int GetProgressionRank(Pilot pilot)
    {
        if (pilot == null)
            return 0;

        EnsurePrerequisitesRegistered();
        return Math.Max(
            0,
            pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey));
    }

    public int GetGrantedPoints(Pilot pilot)
    {
        return GetProgressionRank(pilot) *
            LeviathanSpecializationCurrency.PointsPerRank;
    }

    public void RegisterTrees()
    {
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanEvolutionTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanGrowthTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanStarfireTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanConstrictorTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanPredatorTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanBehemothTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanStellarConverterTree.Create());
    }
}
