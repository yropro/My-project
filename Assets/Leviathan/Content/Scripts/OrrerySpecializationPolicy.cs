using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using static CoreTreeDsl;

/// <summary>Minimal real second class. Content expansion follows the class design.</summary>
public sealed class OrrerySpecializationPolicy : ICoreSpecializationPolicy, ICoreProgressionRankPolicy
{
    public const int UpgradeKeyValue = 88;
    public static readonly Upgrade.Key UpgradeKey = (Upgrade.Key)UpgradeKeyValue;
    public static readonly Upgrade.Category OrreryCategory = (Upgrade.Category)18;
    private static Upgrade nativeUpgrade;
    public static readonly CoreSpecializationKnob CastPower = CoreSpecializationKnob.Percent("orrery.cast_power", "Spell Power");
    public static readonly CoreSpecializationFlag Focus = CoreSpecializationFlag.Create("orrery.focus", "Arcane Focus");
    public CoreClassId ClassId { get { return CoreClassId.Orrery; } }
    public string ProgressionName { get { return "Orrery"; } }
    public string PointCurrencyName { get { return "Orrery Points"; } }
    public Upgrade.Key ProgressionUpgradeKey { get { return UpgradeKey; } }
    public Upgrade.Category ClassCategory { get { return OrreryCategory; } }

    /// <summary>
    /// Native progression Upgrade used by the vanilla class panels. Registration
    /// remains owned by the policy; UI code only consumes the resulting object.
    /// </summary>
    public static Upgrade Upgrade
    {
        get
        {
            EnsureNativeUpgradeRegistered();
            return nativeUpgrade;
        }
    }

    public int GetProgressionRank(Pilot pilot) { return pilot == null ? 0 : pilot.GetUpgradeLevel(UpgradeKey); }
    public int GetGrantedPoints(Pilot pilot) { return GetGrantedPointsForProgressionRank(pilot, GetProgressionRank(pilot)); }
    public int GetGrantedPointsForProgressionRank(Pilot pilot, int progressionRank) { return Math.Max(0, progressionRank) * 2; }

    public void EnsurePrerequisitesRegistered()
    {
        EnsureNativeUpgradeRegistered();
    }

    private static void EnsureNativeUpgradeRegistered()
    {
        if (nativeUpgrade != null) return;
        var field = AccessTools.Field(typeof(Upgrade), "upgrades");
        var lookup = AccessTools.Field(typeof(Upgrade), "upgradeLookup");
        if (field == null || lookup == null)
            throw new InvalidOperationException("Could not resolve native Upgrade registration members for Orrery.");

        var all = new List<Upgrade>((Upgrade[])field.GetValue(null));
        foreach (Upgrade item in all)
        {
            if (item == null || item.key != UpgradeKey)
                continue;
            if (item.category != OrreryCategory)
                throw new InvalidOperationException("Orrery upgrade key 88 is already registered by another category.");

            nativeUpgrade = item;
            nativeUpgrade.requiredLevel = 0;
            nativeUpgrade.percentage = false;
            nativeUpgrade.value = 2;
            nativeUpgrade.levels = 999;
            nativeUpgrade.name = "Orrery";
            return;
        }

        nativeUpgrade = new Upgrade(OrreryCategory, UpgradeKey, 0, false, 2, 999, "Orrery");
        all.Add(nativeUpgrade);
        field.SetValue(null, all.ToArray());
        lookup.SetValue(null, null);
        Upgrade.GetUpgrade(UpgradeKey);
    }

    public void RegisterTrees()
    {
        var tree = new CoreSpecializationTree("orrery.foundation", "Orrery", "orrery.root", 0,
            CoreTreeUnlockKind.NativeUpgrade, UpgradeKeyValue);
        tree.Add(new CoreSpecializationNode("orrery.root", "Orrery", 1, CoreSpecializationNodeType.Root,
            CoreReq.None, "", "Assemble elemental formulas with your satellites.", 0, true));
        tree.Add(new CoreSpecializationNode("orrery.focus", "Arcane Focus", 1, CoreSpecializationNodeType.Passive,
            new CoreRankRequirement("orrery.root", 1), "", "Gain 10% spell power and Arcane Focus.",
            CoreSpecializationEffect.KnobIncrement(CastPower, 10f), CoreSpecializationEffect.Flag(Focus)));
        CoreSpecializationPolicies.RegisterTree(ClassId, tree);
    }
}
