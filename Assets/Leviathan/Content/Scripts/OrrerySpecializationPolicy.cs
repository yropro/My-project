using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using static CoreTreeDsl;

/// <summary>Minimal real second class. Content expansion follows the class design.</summary>
public sealed class OrrerySpecializationPolicy : ICoreSpecializationPolicy
{
    public const int UpgradeKeyValue = 88;
    public static readonly Upgrade.Key UpgradeKey = (Upgrade.Key)UpgradeKeyValue;
    private static Upgrade nativeUpgrade;
    public static readonly CoreSpecializationKnob CastPower = CoreSpecializationKnob.Percent("orrery.cast_power", "Spell Power");
    public static readonly CoreSpecializationFlag Focus = CoreSpecializationFlag.Create("orrery.focus", "Arcane Focus");
    public CoreClassId ClassId { get { return CoreClassId.Orrery; } }
    public string ProgressionName { get { return "Orrery"; } }
    public string PointCurrencyName { get { return "Orrery Points"; } }
    public int GetProgressionRank(Pilot pilot) { return pilot == null ? 0 : pilot.GetUpgradeLevel(UpgradeKey); }
    public int GetGrantedPoints(Pilot pilot) { return GetProgressionRank(pilot) * 2; }

    public void EnsurePrerequisitesRegistered()
    {
        if (nativeUpgrade != null) return;
        var field = AccessTools.Field(typeof(Upgrade), "upgrades");
        var lookup = AccessTools.Field(typeof(Upgrade), "upgradeLookup");
        var all = new List<Upgrade>((Upgrade[])field.GetValue(null));
        foreach (Upgrade item in all)
            if (item.key == UpgradeKey) throw new InvalidOperationException("Orrery upgrade key 88 is already registered.");
        nativeUpgrade = new Upgrade((Upgrade.Category)18, UpgradeKey, 0, false, 2, 999, "Orrery");
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
