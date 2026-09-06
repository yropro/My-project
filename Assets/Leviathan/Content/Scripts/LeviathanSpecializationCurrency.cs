using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Native Leviathan skill that converts one ordinary Star Vortex upgrade point
// into several specialization-only Growth Points.
public static class LeviathanSpecializationCurrency
{
    public const int UpgradeKeyValue = 87;
    public static readonly Upgrade.Key UpgradeKey = (Upgrade.Key)UpgradeKeyValue;

    public const int PointsPerRank = 3;
    public const int Levels = 5;
    public const string SkillName = "Evolution";
    public const string CurrencyName = "Growth Points";

    private static readonly FieldInfo UpgradesField =
        AccessTools.Field(typeof(Upgrade), "upgrades");

    private static readonly FieldInfo UpgradeLookupField =
        AccessTools.Field(typeof(Upgrade), "upgradeLookup");

    private static readonly FieldInfo UpgradeKeyField =
        AccessTools.Field(typeof(Upgrade), "key");

    private static readonly FieldInfo UpgradeCategoryField =
        AccessTools.Field(typeof(Upgrade), "category");

    private static readonly ConstructorInfo UpgradeConstructor =
        AccessTools.Constructor(
            typeof(Upgrade),
            new Type[]
            {
                typeof(Upgrade.Category),
                typeof(Upgrade.Key),
                typeof(int),
                typeof(bool),
                typeof(int),
                typeof(int),
                typeof(string)
            }
        );

    private static readonly MethodInfo GetUpgradeMethod =
        AccessTools.Method(
            typeof(Upgrade),
            "GetUpgrade",
            new Type[] { typeof(Upgrade.Key) }
        );

    private static Upgrade upgrade;

    public static Upgrade Upgrade
    {
        get
        {
            EnsureRegistered();
            return upgrade;
        }
    }

    public static void EnsureRegistered()
    {
        if (upgrade != null)
            return;

        if (UpgradesField == null ||
            UpgradeLookupField == null ||
            UpgradeKeyField == null ||
            UpgradeCategoryField == null ||
            UpgradeConstructor == null ||
            GetUpgradeMethod == null)
        {
            throw new Exception(
                "Could not resolve native Upgrade registration members for Evolution."
            );
        }

        Upgrade[] native = UpgradesField.GetValue(null) as Upgrade[];
        if (native == null)
            throw new Exception("Native Upgrade.upgrades array was null.");

        List<Upgrade> expanded = new List<Upgrade>(native);

        for (int i = 0; i < expanded.Count; i++)
        {
            Upgrade candidate = expanded[i];
            if (candidate == null)
                continue;

            Upgrade.Key key = (Upgrade.Key)UpgradeKeyField.GetValue(candidate);
            if (key != UpgradeKey)
                continue;

            Upgrade.Category category =
                (Upgrade.Category)UpgradeCategoryField.GetValue(candidate);

            if (category != LeviathanMod.LeviathanCategory)
            {
                throw new Exception(
                    "Upgrade key " + UpgradeKeyValue.ToString() +
                    " is already owned by another category."
                );
            }

            upgrade = candidate;
            break;
        }

        if (upgrade == null)
        {
            upgrade = UpgradeConstructor.Invoke(
                new object[]
                {
                    LeviathanMod.LeviathanCategory,
                    UpgradeKey,
                    0,
                    false,
                    PointsPerRank,
                    Levels,
                    SkillName
                }
            ) as Upgrade;

            if (upgrade == null)
                throw new Exception("Failed to construct Evolution Upgrade.");

            expanded.Add(upgrade);
            UpgradesField.SetValue(null, expanded.ToArray());

            Debug.Log(
                "[Leviathan] Registered native Evolution upgrade. Key = " +
                UpgradeKeyValue.ToString() + ", +" +
                PointsPerRank.ToString() + " Growth Points/rank."
            );
        }

        // The lookup may already have been materialized by LeviathanSkillSystem.
        // Clear it and force a rebuild containing key 87.
        UpgradeLookupField.SetValue(null, null);
        object rebuilt = GetUpgradeMethod.Invoke(
            null,
            new object[] { UpgradeKey }
        );

        if (rebuilt == null)
            throw new Exception("Upgrade lookup rebuilt without Evolution.");
    }
}

[HarmonyPatch]
public static class LeviathanEvolutionUpgradeNamePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetName",
            new Type[] { typeof(Upgrade.Key), typeof(bool) }
        );
    }

    public static bool Prefix(Upgrade.Key __0, ref string __result)
    {
        if (__0 != LeviathanSpecializationCurrency.UpgradeKey)
            return true;

        __result = LeviathanSpecializationCurrency.SkillName;
        return false;
    }
}

[HarmonyPatch]
public static class LeviathanEvolutionUpgradeDescriptionPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetDescription",
            new Type[] { typeof(Upgrade.Key), typeof(bool) }
        );
    }

    public static bool Prefix(Upgrade.Key __0, ref string __result)
    {
        if (__0 != LeviathanSpecializationCurrency.UpgradeKey)
            return true;

        __result =
            "Grants 3 Growth Points per rank. Growth Points are spent in " +
            "Leviathan skill specialization trees.";
        return false;
    }
}

[HarmonyPatch]
public static class LeviathanEvolutionUpgradeIsFreePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "IsFree",
            new Type[] { typeof(Upgrade.Key) }
        );
    }

    public static bool Prefix(Upgrade.Key __0, ref bool __result)
    {
        if (__0 != LeviathanSpecializationCurrency.UpgradeKey)
            return true;

        __result = false;
        return false;
    }
}

// Existing Leviathan UI code manually adds its known skills. Intercept the first
// AddUpgrade for category 17 and inject Evolution exactly once without requiring
// edits to LeviathanSkills.cs. Later, Evolution can be moved into the native list
// there and this bridge can simply be removed.
[HarmonyPatch(typeof(UpgradeClassDisplay), "AddUpgrade")]
public static class LeviathanEvolutionDisplayInjectionPatch
{
    [ThreadStatic]
    private static bool injecting;

    private static readonly FieldInfo UpgradeDisplaysField =
        AccessTools.Field(typeof(UpgradeClassDisplay), "upgradeDisplays");

    public static void Prefix(UpgradeClassDisplay __instance, Upgrade upgrade)
    {
        if (injecting || __instance == null || upgrade == null)
            return;

        if (__instance.GetCategory() != LeviathanMod.LeviathanCategory)
            return;

        LeviathanSpecializationCurrency.EnsureRegistered();

        if (upgrade.key == LeviathanSpecializationCurrency.UpgradeKey ||
            ContainsEvolution(__instance))
        {
            return;
        }

        injecting = true;
        try
        {
            __instance.AddUpgrade(LeviathanSpecializationCurrency.Upgrade);
        }
        finally
        {
            injecting = false;
        }
    }

    private static bool ContainsEvolution(UpgradeClassDisplay display)
    {
        if (UpgradeDisplaysField == null)
            return false;

        object raw = UpgradeDisplaysField.GetValue(display);
        System.Collections.IEnumerable enumerable =
            raw as System.Collections.IEnumerable;

        if (enumerable == null)
            return false;

        foreach (object item in enumerable)
        {
            UpgradeDisplay existing = item as UpgradeDisplay;
            if (existing != null &&
                existing.upgrade != null &&
                existing.upgrade.key == LeviathanSpecializationCurrency.UpgradeKey)
            {
                return true;
            }
        }

        return false;
    }
}

[HarmonyPatch(typeof(UpgradeDisplay), "Init")]
public static class LeviathanEvolutionIconPatch
{
    public static void Postfix(UpgradeDisplay __instance, Upgrade __0)
    {
        if (__instance == null ||
            __0 == null ||
            __0.key != LeviathanSpecializationCurrency.UpgradeKey ||
            __instance.iconImage == null ||
            WorldController.instance == null)
        {
            return;
        }

        __instance.iconImage.sprite =
            WorldController.instance.GetUpgradeCategoryIcon(
                LeviathanMod.LeviathanCategory
            );
    }
}

// Never permit the source skill to be refunded below the number of Growth Points
// already committed to specialization nodes.
[HarmonyPatch(typeof(Pilot), "SetUpgrade")]
public static class LeviathanEvolutionDowngradeGuardPatch
{
    public static bool Prefix(Pilot __instance, Upgrade.Key key, int level)
    {
        if (__instance == null ||
            key != LeviathanSpecializationCurrency.UpgradeKey)
        {
            return true;
        }

        int current = __instance.GetUpgradeLevel(key);
        if (level >= current)
            return true;

        int capacity = level * LeviathanSpecializationCurrency.PointsPerRank;
        int spent = LeviathanSpecializationRuntime.GetTotalSpentPoints(__instance);

        if (spent <= capacity)
            return true;

        Debug.LogWarning(
            "[Leviathan] Evolution downgrade blocked: " +
            spent.ToString() + " Growth Points are invested, but rank " +
            level.ToString() + " would provide only " + capacity.ToString() + "."
        );
        return false;
    }
}

[HarmonyPatch(typeof(CoreUpgrades), "CanDowngradeTooltip")]
public static class LeviathanEvolutionCoreDowngradeTooltipPatch
{
    public static void Postfix(Upgrade upgrade, ref string __result)
    {
        AddReason(upgrade, ref __result);
    }

    internal static void AddReason(Upgrade upgrade, ref string result)
    {
        if (result != null ||
            upgrade == null ||
            upgrade.key != LeviathanSpecializationCurrency.UpgradeKey)
        {
            return;
        }

        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (pilot == null)
            return;

        int current = pilot.GetUpgradeLevel(upgrade.key);
        if (current <= 0)
            return;

        int capacity = (current - 1) *
            LeviathanSpecializationCurrency.PointsPerRank;
        int spent = LeviathanSpecializationRuntime.GetTotalSpentPoints(pilot);

        if (spent > capacity)
            result = "Refund Growth Points from specialization trees first.";
    }
}

[HarmonyPatch(typeof(SciencePanel), "CanDowngradeTooltip")]
public static class LeviathanEvolutionScienceDowngradeTooltipPatch
{
    public static void Postfix(Upgrade upgrade, ref string __result)
    {
        LeviathanEvolutionCoreDowngradeTooltipPatch.AddReason(
            upgrade,
            ref __result
        );
    }
}

// A native full upgrade reset should also clear the externally persisted web.
[HarmonyPatch(typeof(Pilot), "ResetUpgrades")]
public static class LeviathanSpecializationResetWithNativePatch
{
    public static void Prefix(Pilot __instance)
    {
        if (__instance == null)
            return;

        string reason;
        LeviathanSpecializationRuntime.ResetAll(__instance, out reason);

        if (!string.IsNullOrEmpty(reason))
        {
            Debug.LogWarning(
                "[Leviathan] Could not persist specialization reset: " + reason
            );
        }
    }
}
